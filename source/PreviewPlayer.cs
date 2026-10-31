using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ASMRVolumeBalancer
{
    public sealed class WaveFileInfo
    {
        public int Channels, SampleRate, BitsPerSample, BlockAlign;
        public long DataOffset, DataLength;
        public int DurationMilliseconds;

        public static WaveFileInfo Read(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.ASCII))
            {
                if (stream.Length < 44 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
                    throw new IOException("试听音频不是有效的 WAV，请重新生成试听。");
                reader.ReadUInt32();
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
                    throw new IOException("试听音频格式无效，请重新生成试听。");
                WaveFileInfo info = new WaveFileInfo(); bool formatFound = false, dataFound = false;
                while (stream.Position + 8 <= stream.Length)
                {
                    string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    uint size = reader.ReadUInt32(); long chunk = stream.Position;
                    if (size > stream.Length - chunk) throw new IOException("试听音频尚未完整写入，请重新生成。");
                    if (id == "fmt ")
                    {
                        if (size < 16) throw new IOException("试听音频格式块不完整。");
                        ushort format = reader.ReadUInt16();
                        info.Channels = reader.ReadUInt16(); info.SampleRate = reader.ReadInt32();
                        uint byteRate = reader.ReadUInt32(); info.BlockAlign = reader.ReadUInt16(); info.BitsPerSample = reader.ReadUInt16();
                        if (format == 0xfffe && size >= 40)
                        {
                            reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32();
                            if (new Guid(reader.ReadBytes(16)) == new Guid("00000001-0000-0010-8000-00aa00389b71")) format = 1;
                        }
                        if (format != 1 || info.BitsPerSample != 16 || info.Channels < 1 || info.Channels > 2 ||
                            info.SampleRate <= 0 || info.BlockAlign != info.Channels * 2 || byteRate != (long)info.SampleRate * info.BlockAlign)
                            throw new IOException("试听播放器需要单声道或双声道 16 位 PCM WAV，请重新生成试听。");
                        formatFound = true;
                    }
                    else if (id == "data")
                    {
                        info.DataOffset = chunk; info.DataLength = size; dataFound = true;
                    }
                    if (formatFound && dataFound) break;
                    stream.Position = Math.Min(stream.Length, chunk + size + size % 2);
                }
                if (!formatFound || !dataFound || info.DataLength <= 0 || info.DataLength % info.BlockAlign != 0)
                    throw new IOException("试听音频不完整或没有有效声音数据，请重新生成。");
                long duration = info.DataLength * 1000 / ((long)info.SampleRate * info.BlockAlign);
                if (duration < 1 || duration > int.MaxValue) throw new IOException("试听音频时长无效。");
                info.DurationMilliseconds = (int)duration; return info;
            }
        }
    }

    public interface IPreviewAudioDevice : IDisposable
    {
        void Open(string path);
        void Close();
        void PlayFrom(int milliseconds);
        void Pause();
        void Stop();
        void Seek(int milliseconds);
        int PositionMilliseconds { get; }
        bool IsPlaying { get; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    internal struct WaveTime
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Value;
    }

    // Small seam for regression tests of the real PCM reader, native buffers and
    // device state machine. Production always uses the WinMM implementation below.
    internal interface IWaveOutApi
    {
        uint Open(out IntPtr device, ref WaveFormat format);
        uint Close(IntPtr device);
        uint Prepare(IntPtr device, IntPtr header, uint size);
        uint Unprepare(IntPtr device, IntPtr header, uint size);
        uint Write(IntPtr device, IntPtr header, uint size);
        uint Reset(IntPtr device);
        uint Pause(IntPtr device);
        uint Restart(IntPtr device);
        uint Position(IntPtr device, ref WaveTime time, uint size);
        string ErrorText(uint error);
    }

    internal sealed class WinMmWaveOutApi : IWaveOutApi
    {
        [DllImport("winmm.dll", ExactSpelling = true)]
        static extern uint waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, UIntPtr callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll", ExactSpelling = true)] static extern uint waveOutClose(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)] static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)] static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)] static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)] static extern uint waveOutReset(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)] static extern uint waveOutPause(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)] static extern uint waveOutRestart(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)] static extern uint waveOutGetPosition(IntPtr device, ref WaveTime time, uint size);
        [DllImport("winmm.dll", EntryPoint = "waveOutGetErrorTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint waveOutGetErrorText(uint error, StringBuilder text, uint size);

        public uint Open(out IntPtr device, ref WaveFormat format) { return waveOutOpen(out device, uint.MaxValue, ref format, UIntPtr.Zero, UIntPtr.Zero, 0); }
        public uint Close(IntPtr device) { return waveOutClose(device); }
        public uint Prepare(IntPtr device, IntPtr header, uint size) { return waveOutPrepareHeader(device, header, size); }
        public uint Unprepare(IntPtr device, IntPtr header, uint size) { return waveOutUnprepareHeader(device, header, size); }
        public uint Write(IntPtr device, IntPtr header, uint size) { return waveOutWrite(device, header, size); }
        public uint Reset(IntPtr device) { return waveOutReset(device); }
        public uint Pause(IntPtr device) { return waveOutPause(device); }
        public uint Restart(IntPtr device) { return waveOutRestart(device); }
        public uint Position(IntPtr device, ref WaveTime time, uint size) { return waveOutGetPosition(device, ref time, size); }
        public string ErrorText(uint error)
        {
            StringBuilder text = new StringBuilder(512);
            return waveOutGetErrorText(error, text, 512) == 0 ? text.ToString() : "Windows 音频设备错误";
        }
    }

    // Read Unicode/long paths using .NET, then send PCM memory to Windows. No
    // filename or MCI command string is passed to the audio driver (no 8.3 names).
    // Only the selected A/B file is held in memory: about 55 MiB for 5 min/48k/stereo.
    public sealed class WaveOutPreviewAudioDevice : IPreviewAudioDevice
    {
        readonly IWaveOutApi api;
        readonly uint headerSize = (uint)Marshal.SizeOf(typeof(WaveHeader));
        IntPtr device, pcm, header;
        WaveFileInfo info;
        int offsetBytes;
        bool prepared, playing;

        public WaveOutPreviewAudioDevice() : this(new WinMmWaveOutApi()) { }
        internal WaveOutPreviewAudioDevice(IWaveOutApi audioApi) { api = audioApi; }

        void Check(uint error, string operation)
        {
            if (error == 0) return;
            throw new IOException("试听播放失败（" + operation + "，代码 " + error.ToString(CultureInfo.InvariantCulture) + "）：" + api.ErrorText(error) +
                "\r\n请在 Windows 中确认默认输出设备可用；若仍失败，请通过“查看日志”保留完整错误。");
        }
        WaveHeader ReadHeader() { return (WaveHeader)Marshal.PtrToStructure(header, typeof(WaveHeader)); }

        public void Open(string path)
        {
            Close();
            if (api is WinMmWaveOutApi && Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("这个试听播放器需要 Windows。");
            WaveFileInfo fileInfo = WaveFileInfo.Read(path);
            if (fileInfo.DataLength > int.MaxValue)
                throw new IOException("试听音频过大，请缩短试听长度。");
            try
            {
                info = fileInfo;
                pcm = Marshal.AllocHGlobal((int)info.DataLength);
                header = Marshal.AllocHGlobal((int)headerSize);
                using (FileStream stream = File.OpenRead(path))
                {
                    stream.Position = info.DataOffset;
                    byte[] block = new byte[65536]; int copied = 0;
                    while (copied < info.DataLength)
                    {
                        int count = stream.Read(block, 0, (int)Math.Min(block.Length, info.DataLength - copied));
                        if (count == 0) throw new IOException("试听音频未完整读取，请重新生成试听。");
                        Marshal.Copy(block, 0, IntPtr.Add(pcm, copied), count); copied += count;
                    }
                }
                WaveFormat format = new WaveFormat { FormatTag = 1, Channels = (ushort)info.Channels,
                    SamplesPerSecond = (uint)info.SampleRate, AverageBytesPerSecond = (uint)(info.SampleRate * info.BlockAlign),
                    BlockAlign = (ushort)info.BlockAlign, BitsPerSample = 16, ExtraSize = 0 };
                IntPtr opened;
                uint error = api.Open(out opened, ref format);
                if (error == 0) device = opened;
                Check(error, "打开输出设备");
                if (device == IntPtr.Zero) throw new IOException("Windows 未返回有效的试听输出设备。");
            }
            catch { try { Close(); } catch { } throw; }
        }

        void ReleaseBuffer()
        {
            if (device == IntPtr.Zero) return;
            // Reset synchronously returns queued buffers before unprepare/free.
            Check(api.Reset(device), "停止输出"); playing = false;
            if (prepared)
            {
                Check(api.Unprepare(device, header, headerSize), "释放播放缓冲区");
                prepared = false;
            }
        }
        public void Close()
        {
            if (device != IntPtr.Zero)
            {
                // If a driver fails to return a buffer, retain its memory rather
                // than freeing memory the driver might still be reading.
                ReleaseBuffer(); Check(api.Close(device), "关闭输出设备"); device = IntPtr.Zero;
            }
            if (header != IntPtr.Zero) { Marshal.FreeHGlobal(header); header = IntPtr.Zero; }
            if (pcm != IntPtr.Zero) { Marshal.FreeHGlobal(pcm); pcm = IntPtr.Zero; }
            info = null; offsetBytes = 0; prepared = playing = false;
        }
        public void Seek(int milliseconds)
        {
            if (info == null) return;
            ReleaseBuffer();
            int target = Math.Max(0, Math.Min(info.DurationMilliseconds, milliseconds));
            offsetBytes = target >= info.DurationMilliseconds ? (int)info.DataLength :
                (int)Math.Min(info.DataLength, ((long)target * info.SampleRate / 1000) * info.BlockAlign);
        }
        public void PlayFrom(int milliseconds)
        {
            if (device == IntPtr.Zero) throw new InvalidOperationException("请先生成试听。");
            Seek(milliseconds);
            if (offsetBytes >= info.DataLength) return;
            WaveHeader buffer = new WaveHeader { Data = IntPtr.Add(pcm, offsetBytes), BufferLength = (uint)(info.DataLength - offsetBytes) };
            Marshal.StructureToPtr(buffer, header, false);
            Check(api.Prepare(device, header, headerSize), "准备播放缓冲区"); prepared = true;
            try
            {
                Check(api.Write(device, header, headerSize), "提交音频");
                // Reset does not guarantee clearing a device's paused state.
                Check(api.Restart(device), "开始播放"); playing = true;
            }
            catch { try { ReleaseBuffer(); } catch { } throw; }
        }
        public void Pause()
        {
            if (device == IntPtr.Zero || !playing) return;
            Check(api.Pause(device), "暂停播放"); playing = false;
        }
        public void Stop() { Seek(0); }
        public int PositionMilliseconds
        {
            get
            {
                if (info == null) return 0;
                if (prepared && (ReadHeader().Flags & 1) != 0) return info.DurationMilliseconds;
                long bytes = offsetBytes;
                if (prepared)
                {
                    WaveTime time = new WaveTime { Type = 4 }; // TIME_BYTES
                    Check(api.Position(device, ref time, (uint)Marshal.SizeOf(typeof(WaveTime))), "读取播放进度");
                    if (time.Type == 4) bytes += time.Value;
                    else if (time.Type == 2) bytes += (long)time.Value * info.BlockAlign; // TIME_SAMPLES
                    else if (time.Type == 1) bytes += (long)time.Value * info.SampleRate * info.BlockAlign / 1000; // TIME_MS
                    else throw new IOException("音频设备未返回受支持的播放进度格式。");
                }
                long bytesPerSecond = (long)info.SampleRate * info.BlockAlign;
                return (int)Math.Min(info.DurationMilliseconds, (Math.Max(0, bytes) * 1000 + bytesPerSecond / 2) / bytesPerSecond);
            }
        }
        public bool IsPlaying
        {
            get
            {
                if (playing && prepared && (ReadHeader().Flags & 1) != 0) playing = false;
                return playing;
            }
        }
        public void Dispose() { Close(); }
    }

    // One shared timeline for A and B. Device operations are synchronous and
    // this controller is used only on the Windows Forms UI thread.
    public sealed class PreviewPlayback : IDisposable
    {
        readonly IPreviewAudioDevice device;
        string original, processed, openPath;
        int position;
        public int DurationMilliseconds { get; private set; }
        public int PositionMilliseconds { get { return position; } }
        public bool IsPlaying { get; private set; }
        public bool ProcessedSelected { get; private set; }
        public bool HasPreview { get { return original != null && processed != null; } }

        public PreviewPlayback(IPreviewAudioDevice audioDevice) { device = audioDevice; }
        public void Load(string before, string after)
        {
            WaveFileInfo a = WaveFileInfo.Read(before), b = WaveFileInfo.Read(after);
            if (a.SampleRate != b.SampleRate || a.Channels != b.Channels || Math.Abs(a.DurationMilliseconds - b.DurationMilliseconds) > 25)
                throw new IOException("原音与处理后试听的格式或长度不一致，请重新生成试听。");
            Unload(); original = before; processed = after;
            DurationMilliseconds = Math.Min(a.DurationMilliseconds, b.DurationMilliseconds);
        }
        public void Poll()
        {
            if (!IsPlaying) return;
            position = Math.Max(0, Math.Min(DurationMilliseconds, device.PositionMilliseconds));
            bool active = device.IsPlaying;
            if (!active || position >= DurationMilliseconds)
            {
                if (active) device.Stop();
                IsPlaying = false; position = DurationMilliseconds;
            }
        }
        public void Play(bool useProcessed)
        {
            if (!HasPreview) throw new InvalidOperationException("请先生成 A/B 试听。");
            Poll();
            if (IsPlaying && ProcessedSelected == useProcessed) return;
            string path = useProcessed ? processed : original;
            if (position >= DurationMilliseconds) position = 0;
            if (openPath != path)
            {
                device.Close(); openPath = null;
                device.Open(path); openPath = path;
            }
            device.PlayFrom(position); ProcessedSelected = useProcessed; IsPlaying = true;
        }
        public void Pause()
        {
            if (!IsPlaying) return;
            Poll();
            if (IsPlaying) device.Pause();
            IsPlaying = false;
        }
        public void Seek(int milliseconds)
        {
            if (!HasPreview) return;
            int target = Math.Max(0, Math.Min(DurationMilliseconds, milliseconds));
            bool resume = IsPlaying;
            if (target >= DurationMilliseconds)
            {
                if (openPath != null) device.Stop(); IsPlaying = false;
            }
            else if (openPath != null)
            {
                if (resume) device.PlayFrom(target); else device.Seek(target);
            }
            position = target;
        }
        public void Stop()
        {
            try { if (openPath != null) device.Stop(); }
            finally { IsPlaying = false; position = 0; }
        }
        public void Unload()
        {
            try { device.Close(); }
            finally { original = processed = openPath = null; position = 0; DurationMilliseconds = 0; IsPlaying = false; ProcessedSelected = false; }
        }
        public void Dispose() { try { Unload(); } finally { device.Dispose(); } }
    }
}
