using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace ASMRVolumeBalancer
{
    public sealed class MediaInfo
    {
        public double Duration;
        public int SampleRate;
        public int Channels;
        public int AudioTracks;
        public bool HasVideo;
    }

    public enum BalanceMode { ReduceMouth = 0, ReduceWhisper = 1, Automatic = 2 }

    public sealed class ProcessingSettings
    {
        public BalanceMode Mode = BalanceMode.ReduceMouth;
        public int Strength = 1;
        public bool AutomaticReference = true;
        public double ReferenceDb = -36;
        public double GainDb = 0;
        public string OutputFolder = "";

        public ProcessingSettings Copy() { return (ProcessingSettings)MemberwiseClone(); }
    }

    public sealed class AnalysisResult
    {
        public double ReferenceDb;
        public int ActiveFrames;
        public int TotalFrames;
        public bool UsedFallback;
    }

    public sealed class ProcessResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
    }

    // All subprocesses run directly, without cmd.exe / a shell. Windows argument
    // quoting below follows the CommandLineToArgvW / CRT backslash convention.
    public sealed class AudioEngine
    {
        public readonly string Ffmpeg;
        public readonly string Ffprobe;
        readonly Action<string> log;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public AudioEngine(string ffmpeg, string ffprobe, Action<string> logger)
        {
            Ffmpeg = ffmpeg; Ffprobe = ffprobe; log = logger ?? delegate { };
        }

        public static string Number(double value) { return value.ToString("0.########", Inv); }
        public static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }

        public static string Quote(string value)
        {
            if (value == null) value = "";
            StringBuilder b = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') b.Append('\\', slashes * 2 + 1);
                else b.Append('\\', slashes);
                b.Append(c); slashes = 0;
            }
            b.Append('\\', slashes * 2); b.Append('"');
            return b.ToString();
        }

        static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
        {
            return new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(" ", args.Select(Quote).ToArray()),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        static void Stop(Process p)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        public ProcessResult Run(string exe, IEnumerable<string> args, CancellationToken cancel,
            Action<string> onOutput = null, int timeoutSeconds = 0)
        {
            cancel.ThrowIfCancellationRequested();
            using (Process p = new Process())
            {
                p.StartInfo = StartInfo(exe, args);
                StringBuilder output = new StringBuilder(), error = new StringBuilder();
                object outputLock = new object(), errorLock = new object();
                p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (outputLock)
                    {
                        if (output.Length < 1000000) output.AppendLine(e.Data);
                    }
                    if (onOutput != null) onOutput(e.Data);
                };
                p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (errorLock)
                    {
                        error.AppendLine(e.Data);
                        if (error.Length > 40000) error.Remove(0, error.Length - 30000);
                    }
                };
                p.Start();
                using (cancel.Register(delegate { Stop(p); }))
                {
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    Stopwatch clock = Stopwatch.StartNew();
                    bool timedOut = false;
                    while (!p.WaitForExit(100))
                    {
                        if (timeoutSeconds > 0 && clock.Elapsed.TotalSeconds > timeoutSeconds)
                        { timedOut = true; Stop(p); break; }
                    }
                    p.WaitForExit(); // Flush asynchronous output handlers.
                    cancel.ThrowIfCancellationRequested();
                    if (timedOut) throw new IOException("读取媒体超时，请确认文件及所在硬盘可以正常访问。");
                    return new ProcessResult { ExitCode = p.ExitCode, Output = output.ToString(), Error = error.ToString() };
                }
            }
        }

        static void RequireSuccess(ProcessResult result, string message)
        {
            if (result.ExitCode != 0)
                throw new IOException(message + "\r\n" + result.Error.Trim());
        }

        public void CheckTools(CancellationToken cancel)
        {
            if (!File.Exists(Ffmpeg) || !File.Exists(Ffprobe))
                throw new FileNotFoundException("音频组件不完整。请先完整解压 ZIP，保留 bin 文件夹，再打开程序。");
            RequireSuccess(Run(Ffmpeg, new[] { "-version" }, cancel, null, 20), "无法启动 FFmpeg。");
            RequireSuccess(Run(Ffprobe, new[] { "-version" }, cancel, null, 20), "无法启动 FFprobe。");
        }

        public MediaInfo Probe(string path, CancellationToken cancel)
        {
            ProcessResult r = Run(Ffprobe, new[] { "-v", "error", "-show_entries",
                "format=duration:stream=index,codec_type,codec_name,sample_rate,channels", "-of", "xml", path }, cancel, null, 45);
            RequireSuccess(r, "无法读取媒体信息。");
            XmlDocument xml = new XmlDocument(); xml.LoadXml(r.Output);
            MediaInfo info = new MediaInfo();
            XmlNode format = xml.SelectSingleNode("/ffprobe/format");
            double duration;
            if (format == null || !double.TryParse(format.Attributes["duration"] == null ? "" : format.Attributes["duration"].Value,
                NumberStyles.Float, Inv, out duration) || duration <= 0 || double.IsInfinity(duration) || double.IsNaN(duration))
                throw new IOException("无法获得有效时长，请先确认录播文件可以正常播放。");
            info.Duration = duration;
            foreach (XmlNode stream in xml.SelectNodes("/ffprobe/streams/stream"))
            {
                string type = stream.Attributes["codec_type"] == null ? "" : stream.Attributes["codec_type"].Value;
                if (type == "video") info.HasVideo = true;
                if (type != "audio") continue;
                info.AudioTracks++;
                if (info.AudioTracks != 1) continue;
                int rate, channels;
                if (!int.TryParse(stream.Attributes["sample_rate"] == null ? "" : stream.Attributes["sample_rate"].Value, out rate) || rate <= 0)
                    throw new IOException("音轨采样率无效。");
                if (!int.TryParse(stream.Attributes["channels"] == null ? "" : stream.Attributes["channels"].Value, out channels))
                    throw new IOException("无法读取音轨声道数。");
                info.SampleRate = rate; info.Channels = channels;
            }
            if (info.AudioTracks == 0) throw new IOException("这个文件没有音轨。");
            if (info.Channels < 1 || info.Channels > 2)
                throw new IOException("当前版本支持单声道和双声道 ASMR，暂不处理多于两个声道的音轨。");
            return info;
        }

        public List<double> MeasureSegment(string path, MediaInfo media, double start, double duration,
            CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            List<string> args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-ss", Number(Math.Max(0, start)),
                "-i", path, "-t", Number(duration), "-map", "0:a:0", "-vn", "-sn", "-dn", "-ar", media.SampleRate.ToString(Inv),
                "-ac", media.Channels.ToString(Inv), "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1" };
            byte[] pcm;
            using (Process p = new Process())
            {
                p.StartInfo = StartInfo(Ffmpeg, args); p.Start();
                using (cancel.Register(delegate { Stop(p); }))
                {
                    var errorTask = p.StandardError.ReadToEndAsync();
                    using (MemoryStream memory = new MemoryStream())
                    {
                        byte[] buffer = new byte[32768]; int read;
                        int cap = (int)((duration + 2) * media.SampleRate * media.Channels * 4);
                        while ((read = p.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (memory.Length + read > cap) { Stop(p); throw new IOException("分析音频超出预期长度。"); }
                            memory.Write(buffer, 0, read);
                        }
                        p.WaitForExit();
                        string error = errorTask.GetAwaiter().GetResult();
                        cancel.ThrowIfCancellationRequested();
                        if (p.ExitCode != 0) throw new IOException("分析片段失败。\r\n" + error);
                        pcm = memory.ToArray();
                    }
                }
            }
            int window = Math.Max(1, media.SampleRate / 10), channelsCount = media.Channels; // 100 ms, original bandwidth.
            List<double> levels = new List<double>();
            double[] sums = new double[channelsCount]; int count = 0;
            for (int i = 0; i + channelsCount * 4 <= pcm.Length; i += channelsCount * 4)
            {
                for (int c = 0; c < channelsCount; c++)
                {
                    double value = BitConverter.ToSingle(pcm, i + c * 4);
                    if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                    sums[c] += value * value;
                }
                count++;
                if (count < window) continue;
                // Use the louder ear to avoid averaging away one-sided ASMR.
                double power = sums.Max() / count;
                levels.Add(power > 1e-14 ? 10 * Math.Log10(power) : -140);
                Array.Clear(sums, 0, sums.Length); count = 0;
            }
            return levels;
        }

        public static double Percentile(IList<double> sorted, double fraction)
        {
            if (sorted.Count == 0) return -36;
            double index = (sorted.Count - 1) * fraction;
            int low = (int)Math.Floor(index), high = (int)Math.Ceiling(index);
            return sorted[low] * (high - index) + sorted[high] * (index - low) + (low == high ? sorted[low] : 0);
        }

        public static AnalysisResult EstimateReference(IEnumerable<double> measurements, bool knownWhisper)
        {
            List<double> all = measurements.Where(x => x > -100 && x < 1 && !double.IsNaN(x)).OrderBy(x => x).ToList();
            if (all.Count < 10) return new AnalysisResult { ReferenceDb = -36, UsedFallback = true, TotalFrames = all.Count };
            double floor = knownWhisper ? -72 : -58;
            double gate = Math.Max(floor, Percentile(all, .90) - (knownWhisper ? 30 : 36));
            List<double> active = all.Where(x => x > gate).ToList();
            if (active.Count < 10) return new AnalysisResult { ReferenceDb = -36, UsedFallback = true, TotalFrames = all.Count };
            // A robust quiet-active quantile, NOT a semantic whisper detector.
            double reference = Percentile(active, knownWhisper ? .50 : .35);
            return new AnalysisResult { ReferenceDb = Clamp(reference, -55, -12), ActiveFrames = active.Count, TotalFrames = all.Count };
        }

        public AnalysisResult Analyze(string path, MediaInfo media, CancellationToken cancel, Action<double> progress = null)
        {
            return Analyze(path, media, BalanceMode.ReduceMouth, cancel, progress);
        }

        public static AnalysisResult EstimateBalancedReference(IEnumerable<double> measurements)
        {
            List<double> all = measurements.Where(x => x > -100 && x < 1 && !double.IsNaN(x)).OrderBy(x => x).ToList();
            if (all.Count < 10) return new AnalysisResult { ReferenceDb = -32, UsedFallback = true, TotalFrames = all.Count };
            double gate = Math.Max(-62, Percentile(all, .90) - 36);
            List<double> active = all.Where(x => x > gate).ToList();
            if (active.Count < 10) return new AnalysisResult { ReferenceDb = -32, UsedFallback = true, TotalFrames = all.Count };
            // The centre of quiet and loud active regions, in dB. Do not use a
            // quiet reference for the bidirectional mode: weak sounds must be
            // allowed to rise towards a middle level while loud regions fall.
            double reference = (Percentile(active, .25) + Percentile(active, .80)) / 2;
            return new AnalysisResult { ReferenceDb = Clamp(reference, -55, -16), ActiveFrames = active.Count, TotalFrames = all.Count };
        }

        public AnalysisResult Analyze(string path, MediaInfo media, BalanceMode mode, CancellationToken cancel, Action<double> progress = null)
        {
            double segmentLength = Math.Min(15, media.Duration);
            int count = media.Duration < 45 ? 1 : media.Duration < 180 ? 3 : 6;
            List<double> levels = new List<double>();
            for (int i = 0; i < count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                double start = count == 1 ? 0 : (media.Duration - segmentLength) * (.06 + .88 * i / (count - 1.0));
                levels.AddRange(MeasureSegment(path, media, start, segmentLength, cancel));
                if (progress != null) progress((i + 1.0) / count);
            }
            AnalysisResult result = mode == BalanceMode.Automatic ? EstimateBalancedReference(levels) : EstimateReference(levels, false);
            log(ModeName(mode) + "；自动参考 " + Number(result.ReferenceDb) + " dBFS；有效窗口 " + result.ActiveFrames + "/" + result.TotalFrames +
                (result.UsedFallback ? "；有效声音不足，已使用默认参考，请试听。" : "；音量统计估计，不识别声音类别，请用参考片段或试听确认。"));
            return result;
        }

        public static string ModeName(BalanceMode mode)
        {
            return mode == BalanceMode.ReduceMouth ? "口腔音平衡" : mode == BalanceMode.ReduceWhisper ? "轻语平衡" : "自动平衡";
        }

        public static string BuildFilter(ProcessingSettings settings, double referenceDb)
        {
            int profile = Math.Max(0, Math.Min(2, settings.Strength));
            double[] margin = { 5, 2, 0.5 }, ratio = { 3, 8, 16 }, release = { 280, 220, 180 };
            double reference = Clamp(referenceDb, -55, -12);
            string levelAndLimit = "volume=" + Number(Clamp(settings.GainDb, -12, 12)) + "dB," +
                "alimiter=limit=0.70794578:attack=5:release=100:level=false:latency=true";
            if (settings.Mode == BalanceMode.Automatic)
            {
                double[] maxBoostDb = { 3, 7, 12 };
                int[] smoothingFrames = { 17, 11, 7 };
                double[] crestAllowance = { 16, 14, 12 };
                double activeThreshold = Clamp(reference - 25, -62, -38);
                double masterGain = Clamp(settings.GainDb, -12, 12);
                double ceilingDb = Math.Min(-3, reference + crestAllowance[profile] + masterGain);
                // Keep brief clicks near the reference too, rather than only
                // balancing 100 ms RMS. Scale around alimiter for ceilings
                // below its native -24 dB parameter floor, then scale back.
                double limiterScale = Math.Max(0, -24 - ceilingDb);
                string autoFinal = "volume=" + Number(masterGain + limiterScale) + "dB," +
                    "alimiter=limit=" + Number(Math.Pow(10, (ceilingDb + limiterScale) / 20)) +
                    ":attack=5:release=100:level=false:latency=true" +
                    (limiterScale > 0 ? ",volume=" + Number(-limiterScale) + "dB" : "");
                // A real bidirectional, stereo-linked segment normalizer.
                // Quiet active audio can rise, but only within the selected
                // boost cap. Sub-threshold near-silent frames are not boosted.
                return "dynaudnorm=f=100:g=" + smoothingFrames[profile].ToString(Inv) +
                    ":p=0.70794578:m=" + Number(Math.Pow(10, maxBoostDb[profile] / 20)) +
                    ":r=" + Number(Math.Pow(10, reference / 20)) + ":n=true:t=" +
                    Number(Math.Pow(10, activeThreshold / 20)) + ":b=true," + autoFinal;
            }
            double thresholdDb = Clamp(reference + margin[profile], -58, -3);
            bool reduceWhisper = settings.Mode == BalanceMode.ReduceWhisper;
            double peakThresholdDb = Clamp(reference + (reduceWhisper ? 18 : 12), -50, -2);
            double attack = reduceWhisper ? 18 : 3;
            double[] peakRatio = reduceWhisper ? new double[] { 2, 4, 8 } : new double[] { 3, 6, 10 };
            // Mode 1 reacts quickly to bursts; mode 2 uses a slower envelope
            // for sustained speech and leaves more short trigger transients.
            // These are reference/envelope policies, NOT semantic separation.
            // No upward normalization is used in either reference-down mode.
            return "acompressor=threshold=" + Number(Math.Pow(10, thresholdDb / 20)) +
                ":ratio=" + Number(ratio[profile]) + ":attack=" + Number(attack) + ":release=" + Number(release[profile] + (reduceWhisper ? 100 : 0)) +
                ":knee=1.6:makeup=1:link=maximum:detection=rms," +
                "acompressor=threshold=" + Number(Math.Pow(10, peakThresholdDb / 20)) +
                ":ratio=" + Number(peakRatio[profile]) + ":attack=" + (reduceWhisper ? "1" : "0.3") +
                ":release=80:knee=1.4:makeup=1:link=maximum:detection=peak," + levelAndLimit;
        }

        void Convert(List<string> args, CancellationToken cancel, double duration, Action<double> progress)
        {
            DateTime last = DateTime.MinValue;
            ProcessResult r = Run(Ffmpeg, args, cancel, delegate(string line)
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
                long us;
                if (!long.TryParse(line.Substring(12), NumberStyles.Integer, Inv, out us)) return;
                if ((DateTime.UtcNow - last).TotalMilliseconds < 150) return;
                last = DateTime.UtcNow;
                if (progress != null) progress(Clamp(us / 1000000.0 / Math.Max(.01, duration), 0, .995));
            });
            RequireSuccess(r, "音频处理失败。原文件未被更改。");
            if (!string.IsNullOrWhiteSpace(r.Error)) log("FFmpeg 提示：" + r.Error.Trim());
        }

        public void RenderPreview(string source, MediaInfo media, double start, double duration, string output,
            ProcessingSettings settings, double reference, bool processed, CancellationToken cancel, Action<double> progress)
        {
            double preRoll = processed ? Math.Min(2, start) : 0;
            List<string> args = new List<string> { "-hide_banner", "-v", "warning", "-nostats", "-nostdin", "-n", "-ss", Number(start - preRoll),
                "-i", source, "-t", Number(duration), "-map", "0:a:0", "-vn", "-sn", "-dn" };
            long previewSamples = Math.Max(1, (long)Math.Round(duration * media.SampleRate));
            string previewFilter = (processed ? BuildFilter(settings, reference) + "," : "") +
                "atrim=start=" + Number(preRoll) + ":duration=" + Number(duration) +
                ",apad=whole_len=" + previewSamples.ToString(Inv) + ",atrim=end_sample=" + previewSamples.ToString(Inv) +
                ",asetpts=N/SR/TB";
            args.AddRange(new[] { "-af", previewFilter });
            args.AddRange(new[] { "-ar", media.SampleRate.ToString(Inv), "-c:a", "pcm_s16le", "-progress", "pipe:1", output });
            try { Convert(args, cancel, duration, progress); }
            catch { TryDelete(output); throw; }
        }

        public static string NextOutputPath(string source, string outputFolder, string presetTag = null)
        {
            string folder = string.IsNullOrWhiteSpace(outputFolder) ? Path.Combine(Path.GetDirectoryName(source), "ASMR_均衡输出") : outputFolder;
            Directory.CreateDirectory(folder);
            string stem = Path.GetFileNameWithoutExtension(source) + "_音量均衡" + (string.IsNullOrEmpty(presetTag) ? "" : "_" + presetTag);
            string path = Path.Combine(folder, stem + ".mp4"); int suffix = 1;
            while (File.Exists(path)) path = Path.Combine(folder, stem + "_" + (suffix++).ToString("000", Inv) + ".mp4");
            return path;
        }

        public string ProcessFile(string source, MediaInfo media, ProcessingSettings settings, double reference,
            CancellationToken cancel, Action<double> progress)
        {
            string presetTag = "模式" + ((int)settings.Mode + 1).ToString(Inv) + "_" + new[] { "温和", "标准", "较强" }[Math.Max(0, Math.Min(2, settings.Strength))];
            string destination = NextOutputPath(source, settings.OutputFolder, presetTag);
            string partial = Path.Combine(Path.GetDirectoryName(destination), ".asmr_" + Guid.NewGuid().ToString("N") + ".partial.mp4");
            try
            {
                try
                {
                    DriveInfo drive = new DriveInfo(Path.GetPathRoot(destination));
                    if (drive.IsReady && drive.AvailableFreeSpace < new FileInfo(source).Length + 64L * 1024 * 1024)
                        throw new IOException("输出硬盘剩余空间不足。建议预留至少与原文件相当的可用空间。");
                }
                catch (ArgumentException) { } // Some UNC paths have no DriveInfo.
                List<string> args = new List<string> { "-hide_banner", "-v", "warning", "-nostats", "-nostdin", "-n", "-i", source,
                    "-map", "0:v:0?", "-map", "0:a:0", "-map", "0:s?", "-map_metadata", "0", "-map_chapters", "0",
                    "-c:v", "copy", "-c:s", "copy", "-af", BuildFilter(settings, reference), "-c:a", "aac", "-b:a", "256k",
                    "-ar", media.SampleRate.ToString(Inv), "-movflags", "+faststart", "-max_muxing_queue_size", "2048",
                    "-progress", "pipe:1", partial };
                log("处理：" + source + "\r\n模式：" + ModeName(settings.Mode) + "；参考 " + Number(reference) + " dBFS；强度 " + settings.Strength + "；整体增益 " + Number(settings.GainDb) + " dB");
                if (media.AudioTracks > 1) log("注意：源文件含多条音轨，输出仅保留处理后的第一条音轨。");
                Convert(args, cancel, media.Duration, progress);
                cancel.ThrowIfCancellationRequested();
                MediaInfo verify = Probe(partial, cancel);
                if (Math.Abs(verify.Duration - media.Duration) > Math.Max(1, media.Duration * .0001))
                    throw new IOException("输出时长与原文件差异过大，已取消保存，请检查日志。");
                if (verify.Channels != media.Channels || verify.SampleRate != media.SampleRate || (media.HasVideo && !verify.HasVideo))
                    throw new IOException("输出媒体校验未通过，已取消保存。");
                cancel.ThrowIfCancellationRequested();
                if (File.Exists(destination)) destination = NextOutputPath(source, settings.OutputFolder, presetTag);
                File.Move(partial, destination);
                log("完成：" + destination);
                if (progress != null) progress(1);
                return destination;
            }
            finally { TryDelete(partial); }
        }

        public static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
