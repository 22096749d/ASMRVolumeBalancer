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

    // Windows' built-in waveform-audio device streams the generated PCM WAV.
    // Seeking does not copy or regenerate a five-minute clip.
    public sealed class MciPreviewAudioDevice : IPreviewAudioDevice
    {
        readonly string alias = "asmr_" + Guid.NewGuid().ToString("N");
        bool opened;
        [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint SendString(string command, StringBuilder result, uint size, IntPtr callback);
        [DllImport("winmm.dll", EntryPoint = "mciGetErrorStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ErrorString(uint error, StringBuilder text, uint size);

        string Command(string command, bool response = false)
        {
            StringBuilder result = response ? new StringBuilder(256) : null;
            uint error = SendString(command, result, response ? 256U : 0U, IntPtr.Zero);
            if (error != 0)
            {
                StringBuilder message = new StringBuilder(512);
                ErrorString(error, message, 512);
                throw new IOException("无法播放试听：" + (message.Length > 0 ? message.ToString() : "Windows 音频错误 " + error) +
                    "\r\n请确认耳机或扬声器已连接，并在 Windows 中选好了输出设备。");
            }
            return result == null ? "" : result.ToString().Trim();
        }
        public void Open(string path)
        {
            Close();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("这个试听播放器需要 Windows；生成和音频处理模块可单独测试。");
            if (!File.Exists(path) || path.IndexOf('"') >= 0) throw new IOException("找不到试听音频，请重新生成。");
            try
            {
                Command("open \"" + path + "\" type waveaudio alias " + alias + " wait"); opened = true;
                Command("set " + alias + " time format milliseconds");
            }
            catch { Close(); throw; }
        }
        public void Close()
        {
            if (!opened) return;
            try { Command("close " + alias + " wait"); } catch (IOException) { } finally { opened = false; }
        }
        public void Seek(int milliseconds) { if (opened) Command("seek " + alias + " to " + Math.Max(0, milliseconds).ToString(CultureInfo.InvariantCulture) + " wait"); }
        public void PlayFrom(int milliseconds)
        {
            if (!opened) throw new InvalidOperationException("请先生成试听。");
            Seek(milliseconds); Command("play " + alias);
        }
        public void Pause() { if (opened) Command("pause " + alias); }
        public void Stop() { if (opened) Command("stop " + alias); }
        public int PositionMilliseconds
        {
            get
            {
                if (!opened) return 0;
                int value;
                if (!int.TryParse(Command("status " + alias + " position", true), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    throw new IOException("无法读取试听播放位置。");
                return value;
            }
        }
        public bool IsPlaying { get { return opened && Command("status " + alias + " mode", true).Equals("playing", StringComparison.OrdinalIgnoreCase); } }
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
