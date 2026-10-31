using System;
using System.IO;
using System.Runtime.InteropServices;
using ASMRVolumeBalancer;

// Emulates the WinMM buffer ownership/clock contract; reads the actual unmanaged
// PCM buffer submitted by production code. Does not emulate filename handling:
// the API deliberately has no filename parameter.
sealed class FakeWaveApi : IWaveOutApi
{
    public bool Opened, Paused, Prepared, Queued;
    public IntPtr Header;
    public uint Bytes, ClockType = 4;
    public int BlockAlign, Rate, Opens, Closes, Writes;
    public string FailOperation;
    public uint Open(out IntPtr device, ref WaveFormat format)
    {
        device = IntPtr.Zero;
        if (FailOperation == "open") return 2;
        if (format.FormatTag != 1 || format.BitsPerSample != 16 || format.ExtraSize != 0 || format.BlockAlign != format.Channels * 2)
            throw new Exception("Bad WAVEFORMATEX");
        Opened = true; BlockAlign = format.BlockAlign; Rate = (int)format.SamplesPerSecond;
        Opens++; device = new IntPtr(123); return 0;
    }
    WaveHeader Read() { return (WaveHeader)Marshal.PtrToStructure(Header, typeof(WaveHeader)); }
    void Flags(uint flags) { WaveHeader h = Read(); h.Flags = flags; Marshal.StructureToPtr(h, Header, false); }
    public uint Close(IntPtr device)
    {
        if (Prepared || Queued) throw new Exception("Closed before unprepare");
        Opened = false; Closes++; return 0;
    }
    public uint Prepare(IntPtr device, IntPtr header, uint size)
    {
        if (!Opened || Prepared || size != Marshal.SizeOf(typeof(WaveHeader))) throw new Exception("Bad prepare");
        if (FailOperation == "prepare") return 7;
        Header = header;
        WaveHeader h = Read();
        if (h.Flags != 0 || h.Data == IntPtr.Zero || h.BufferLength % BlockAlign != 0) throw new Exception("Bad buffer");
        Prepared = true; Flags(2); return 0;
    }
    public uint Unprepare(IntPtr device, IntPtr header, uint size)
    {
        if (Queued) return 33; // WAVERR_STILLPLAYING
        if (FailOperation == "unprepare") return 33;
        if (header != Header || !Prepared) throw new Exception("Bad unprepare");
        Flags(0); Prepared = false; Header = IntPtr.Zero; return 0;
    }
    public uint Write(IntPtr device, IntPtr header, uint size)
    {
        if (!Prepared || Queued || header != Header) throw new Exception("Bad write");
        if (FailOperation == "write") return 7;
        Queued = true; Bytes = 0; Flags(2 | 16); Writes++; return 0;
    }
    public uint Reset(IntPtr device)
    {
        if (FailOperation == "reset") return 1;
        if (Prepared) Flags(2 | 1);
        Bytes = 0; Queued = false; return 0;
    }
    public uint Pause(IntPtr device) { Paused = true; return 0; }
    public uint Restart(IntPtr device)
    {
        if (FailOperation == "restart") return 1;
        Paused = false; return 0;
    }
    public uint Position(IntPtr device, ref WaveTime time, uint size)
    {
        if (size != 12 || time.Type != 4) throw new Exception("Bad MMTIME");
        time.Type = ClockType;
        time.Value = ClockType == 2 ? Bytes / (uint)BlockAlign : ClockType == 1 ? (uint)((long)Bytes * 1000 / (Rate * BlockAlign)) : Bytes;
        return 0;
    }
    public string ErrorText(uint error) { return "test audio device error"; }
    public void Advance(int ms)
    {
        if (Queued && !Paused)
        {
            Bytes = (uint)Math.Min(Read().BufferLength, Bytes + (long)ms * Rate * BlockAlign / 1000);
            if (Bytes >= Read().BufferLength) { Queued = false; Flags(2 | 1); }
        }
    }
    public short FirstLeftSample { get { return Marshal.ReadInt16(Read().Data); } }
}

class WaveOutTests
{
    static int passed;
    static void Assert(bool ok, string text) { if (!ok) throw new Exception(text); passed++; Console.WriteLine("PASS " + text); }
    static void Wave(string path, short value, int rate = 48000, int channels = 2)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        const int seconds = 3;
        int size = rate * channels * 2 * seconds;
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(size + 36); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(size);
            for (int i = 0; i < rate * seconds; i++) { w.Write((short)(value + i / rate)); if (channels == 2) w.Write((short)(-value - i / rate)); }
        }
    }
    static int Main(string[] args)
    {
        try
        {
            Assert(Marshal.SizeOf(typeof(WaveHeader)) == (IntPtr.Size == 8 ? 48 : 32), "Pointer-size-correct WAVEHDR layout");
            Assert(Marshal.SizeOf(typeof(WaveFormat)) == 18 && Marshal.SizeOf(typeof(WaveTime)) == 12, "WinMM format and time ABI sizes");
            string folder = Path.Combine(Path.GetFullPath(args[0]), "用户名 中文 & spaces (试听)' ♥");
            for (int i = 0; i < 6; i++) folder = Path.Combine(folder, "长路径测试_abcdefghijklmnopqrstuvwxyz_" + i);
            string a = Path.Combine(folder, "123456789abcdef_原音.wav"), b = Path.Combine(folder, "123456789abcdef_处理后.wav");
            Wave(a, 100); Wave(b, 200);
            Assert(a.Length > 260, "Regression fixture includes Unicode and a path longer than 260 characters");
            FakeWaveApi api = new FakeWaveApi();
            using (WaveOutPreviewAudioDevice device = new WaveOutPreviewAudioDevice(api))
            using (PreviewPlayback player = new PreviewPlayback(device))
            {
                player.Load(a, b); player.Play(false);
                Assert(api.Opened && api.FirstLeftSample == 100 && player.IsPlaying, "Long Unicode WAV is read and actual PCM is submitted, without filename-based audio APIs");
                api.Advance(1200); player.Poll(); Assert(player.PositionMilliseconds == 1200, "Device byte clock advances the timeline");
                player.Play(true);
                Assert(api.FirstLeftSample == 201 && player.PositionMilliseconds == 1200 && player.IsPlaying && api.Opens == 2 && api.Closes == 1, "A/B switching submits the correct PCM frame at the shared position and releases the old device");
                api.Advance(100); player.Pause(); api.Advance(500);
                Assert(player.PositionMilliseconds == 1300 && !device.IsPlaying && device.PositionMilliseconds == 1300, "Pause holds native audio and timeline");
                player.Seek(2000); Assert(!player.IsPlaying && device.PositionMilliseconds == 2000, "Paused seek uses a block-aligned absolute position");
                player.Play(false); Assert(api.FirstLeftSample == 102 && !api.Paused, "Resume after paused A/B switch restarts the device at the requested sample");
                api.Advance(200);
                api.ClockType = 2; Assert(device.PositionMilliseconds == 2200, "TIME_SAMPLES driver fallback is interpreted correctly");
                api.ClockType = 1; Assert(device.PositionMilliseconds == 2200, "TIME_MS driver fallback is interpreted correctly");
                api.ClockType = 4;
                player.Seek(1000); Assert(api.FirstLeftSample == 101 && player.IsPlaying, "Playing seek resets and requeues the requested PCM frame");
                api.Advance(3000); player.Poll(); Assert(!player.IsPlaying && player.PositionMilliseconds == 3000, "WHDR_DONE ends playback at the exact preview duration");
                player.Play(true); Assert(api.FirstLeftSample == 200 && player.PositionMilliseconds == 0, "Replay after completion starts from zero");
                player.Stop(); Assert(!api.Queued && !api.Prepared && player.PositionMilliseconds == 0, "Stop returns the buffer before unpreparing and resets the timeline");
                for (int i = 0; i < 50; i++) { player.Play(i % 2 == 0); player.Seek(1700); player.Pause(); player.Play(i % 2 == 0); }
                player.Unload(); Assert(!api.Opened && !api.Prepared && !api.Queued && api.Opens == api.Closes, "Repeated switching, dragging and unloading release every device and buffer");
            }
            foreach (string operation in new[] { "open", "prepare", "write", "restart" })
            {
                FakeWaveApi failing = new FakeWaveApi { FailOperation = operation };
                using (WaveOutPreviewAudioDevice device = new WaveOutPreviewAudioDevice(failing))
                {
                    bool rejected = false;
                    try { device.Open(a); device.PlayFrom(0); } catch (IOException e) { rejected = e.Message.Contains("代码"); }
                    device.Close();
                    Assert(rejected && !failing.Opened && !failing.Prepared && !failing.Queued, operation + " failure reports the operation/code and cleans up safely");
                }
            }
            FakeWaveApi retained = new FakeWaveApi();
            using (WaveOutPreviewAudioDevice device = new WaveOutPreviewAudioDevice(retained))
            {
                device.Open(a); device.PlayFrom(0); retained.FailOperation = "unprepare";
                bool rejected = false;
                try { device.Close(); } catch (IOException) { rejected = true; }
                Assert(rejected && retained.Opened && retained.FirstLeftSample == 100, "Driver cleanup failure retains valid PCM memory instead of freeing an owned buffer");
                retained.FailOperation = null; device.Close();
                Assert(!retained.Opened && !retained.Prepared, "Cleanup can be safely retried after a driver error");
            }
            string mono = Path.Combine(folder, "单声道44100.wav"); Wave(mono, 300, 44100, 1);
            FakeWaveApi monoApi = new FakeWaveApi();
            using (WaveOutPreviewAudioDevice device = new WaveOutPreviewAudioDevice(monoApi))
            {
                device.Open(mono); device.PlayFrom(1234);
                Assert(monoApi.BlockAlign == 2 && device.PositionMilliseconds == 1234, "44.1k mono retains sample-aligned millisecond seeks without cumulative rounding loss");
            }
            if (args.Length > 1)
            {
                FakeWaveApi fullApi = new FakeWaveApi();
                using (WaveOutPreviewAudioDevice device = new WaveOutPreviewAudioDevice(fullApi))
                {
                    device.Open(args[1]); device.PlayFrom(299000); fullApi.Advance(2000);
                    Assert(device.PositionMilliseconds == 300000 && !device.IsPlaying, "Full 300-second real PCM WAV is buffered and can seek to its final second");
                }
            }
            Console.WriteLine("ALL " + passed + " WAVEOUT REGRESSION CHECKS PASSED. Native Windows sound hardware not exercised.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
