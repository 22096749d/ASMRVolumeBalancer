using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

[assembly: AssemblyTitle("轻声 ASMR 音量均衡")]
[assembly: AssemblyDescription("保留双耳空间感的本地批量音量均衡工具")]
[assembly: AssemblyCompany("ASMR Volume Balancer")]
[assembly: AssemblyProduct("ASMR Volume Balancer")]
[assembly: AssemblyVersion("2.1.1.0")]
[assembly: AssemblyFileVersion("2.1.1.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace ASMRVolumeBalancer
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Fatal(e.Exception); };
            try { Application.Run(new MainForm(args)); }
            catch (Exception e) { Fatal(e); }
        }

        static void Fatal(Exception e)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASMRVolumeBalancer", "logs");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "startup-error.txt"), DateTime.Now + "\r\n" + e + "\r\n", Encoding.UTF8);
            }
            catch { }
            string guidance = e is MissingMethodException
                ? "这是程序与 .NET Framework 的兼容性错误，请使用最新修复版。"
                : "请保留这段报错，并通过“查看日志”获取详细信息。";
            MessageBox.Show("程序遇到错误：\r\n" + e.Message + "\r\n\r\n" + guidance, "轻声 ASMR", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    sealed class QueueItem
    {
        public string Path;
        public string Output;
        public string Error;
        public MediaInfo Media;
        public DataGridViewRow Row;
    }

    public sealed class MainForm : Form
    {
        readonly Color ink = Color.FromArgb(31, 48, 62);
        readonly Color muted = Color.FromArgb(101, 117, 128);
        readonly Color accent = Color.FromArgb(17, 121, 121);
        readonly List<QueueItem> items = new List<QueueItem>();
        readonly List<Control> disableWhenBusy = new List<Control>();
        readonly object logLock = new object();
        readonly string appFolder, dataFolder, logFolder, logFile, previewFolder, settingsFile;
        readonly string[] initialFiles;
        readonly AudioEngine engine;
        DataGridView grid;
        ComboBox strength, balanceMode;
        CheckBox automatic;
        NumericUpDown manualReference, gain, previewLength;
        TextBox outputFolder, previewStart;
        Label status, queueCount, profileDescription, previewStatus, referenceName, playbackClock;
        TrackBar previewSeek;
        System.Windows.Forms.Timer playbackTimer;
        ProgressBar progress;
        Button cancelButton, originalButton, processedButton, stopButton, startButton, measureButton, pauseButton;
        readonly double[] modeReferences = { -36, -36, -30 };
        readonly bool[] modeAutomatic = { true, true, true };
        int previousMode = 2;
        PreviewPlayback playback;
        bool seekDragging, resumeAfterSeek;
        CancellationTokenSource cancellation;
        string originalPreview, processedPreview;
        bool busy, closeWhenDone, initialized;

        public MainForm(string[] files)
        {
            initialFiles = files;
            appFolder = AppDomain.CurrentDomain.BaseDirectory;
            dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASMRVolumeBalancer");
            logFolder = Path.Combine(dataFolder, "logs");
            previewFolder = Path.Combine(dataFolder, "previews", Guid.NewGuid().ToString("N"));
            settingsFile = Path.Combine(dataFolder, "settings-v2.xml");
            Directory.CreateDirectory(logFolder); Directory.CreateDirectory(previewFolder);
            logFile = Path.Combine(logFolder, "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            engine = new AudioEngine(Path.Combine(appFolder, "bin", "ffmpeg.exe"), Path.Combine(appFolder, "bin", "ffprobe.exe"), Log);
            playback = new PreviewPlayback(new WaveOutPreviewAudioDevice());
            Text = "轻声 · ASMR 音量均衡 2.1.1";
            Font = new Font("Microsoft YaHei UI", 9.5F);
            ForeColor = ink; BackColor = Color.FromArgb(244, 248, 249);
            ClientSize = new Size(1140, 900); MinimumSize = new Size(1060, 850);
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Font;
            try { Icon = new Icon(Path.Combine(appFolder, "app.ico")); } catch { }
            BuildInterface();
            LoadSettings(); initialized = true;
            UpdateProfile(); UpdateCount();
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e) { if (!busy) AddPaths((string[])e.Data.GetData(DataFormats.FileDrop)); };
            FormClosing += OnClosing;
            Shown += delegate
            {
                if (initialFiles.Length > 0) AddPaths(initialFiles);
                if (!File.Exists(engine.Ffmpeg) || !File.Exists(engine.Ffprobe))
                {
                    status.Text = "组件缺失：请完整解压 ZIP，保留 bin 文件夹。";
                    MessageBox.Show(this, "未找到 bin 文件夹中的音频组件。\r\n请把整个 ZIP 解压到一个文件夹，再双击 ASMRVolumeBalancer.exe。", "请完整解压", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            Log("轻声 ASMR 音量均衡 2.1.1 启动。试听使用直接 PCM / waveOut 播放。仅本地读写，不连接网络。");
        }

        Label MakeLabel(string text, int width, int height = 26)
        {
            return new Label { Text = text, Width = width, Height = height, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 8, 0), ForeColor = muted };
        }

        Button MakeButton(string text, int width, EventHandler click, bool primary = false)
        {
            Button b = new Button { Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat,
                BackColor = primary ? accent : Color.White, ForeColor = primary ? Color.White : ink, Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = primary ? accent : Color.FromArgb(208, 220, 224);
            b.Click += click;
            return b;
        }

        void AddBusyControl(Control control) { disableWhenBusy.Add(control); }
        void Ui(Action action)
        {
            if (IsDisposed || Disposing) return;
            try { if (InvokeRequired) BeginInvoke(action); else action(); } catch (InvalidOperationException) { }
        }
        void SetStatus(string message, double fraction)
        {
            Ui(delegate { status.Text = message; progress.Value = (int)AudioEngine.Clamp(fraction * 1000, 0, 1000); });
        }
        void Log(string text)
        {
            lock (logLock)
            {
                try { File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\r\n", Encoding.UTF8); } catch { }
            }
        }

        void BuildInterface()
        {
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), ColumnCount = 1, RowCount = 7 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 192));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            Controls.Add(root);

            Panel header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            header.Controls.Add(new Label { Text = "轻声", Font = new Font(Font.FontFamily, 25, FontStyle.Bold), Location = new Point(0, 0), Size = new Size(110, 45), ForeColor = accent });
            header.Controls.Add(new Label { Text = "ASMR 音量均衡", Font = new Font(Font.FontFamily, 17, FontStyle.Regular), Location = new Point(113, 8), Size = new Size(460, 34) });
            header.Controls.Add(new Label { Text = "三种平衡模式 × 三档力度，让轻语、口腔音与触发音更均衡。保留左右耳方向感。", Location = new Point(2, 50), Size = new Size(900, 25), ForeColor = muted });
            Button shortcut = MakeButton("创建桌面图标", 130, CreateShortcut);
            Panel shortcutArea = new Panel { Dock = DockStyle.Right, Width = 142, Padding = new Padding(0, 8, 0, 0) };
            shortcut.Dock = DockStyle.Top; shortcutArea.Controls.Add(shortcut); header.Controls.Add(shortcutArea);
            root.Controls.Add(header, 0, 0);

            FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            Button add = MakeButton("＋ 添加 MP4", 140, AddFiles);
            Button folder = MakeButton("添加文件夹", 125, AddFolder);
            Button remove = MakeButton("移除所选", 105, delegate { RemoveSelected(); });
            Button clear = MakeButton("清空列表", 105, delegate { items.Clear(); grid.Rows.Clear(); UpdateCount(); InvalidatePreview(); });
            queueCount = MakeLabel("", 420, 34);
            toolbar.Controls.AddRange(new Control[] { add, folder, remove, clear, queueCount });
            foreach (Control c in new Control[] { add, folder, remove, clear }) AddBusyControl(c);
            root.Controls.Add(toolbar, 0, 1);

            grid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
                ReadOnly = true, MultiSelect = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Color.FromArgb(235, 239, 241),
                EnableHeadersVisualStyles = false, Margin = new Padding(0, 0, 0, 8), AllowDrop = true };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(230, 240, 241), ForeColor = ink,
                Font = new Font(Font, FontStyle.Bold), Padding = new Padding(5, 0, 0, 0) };
            grid.ColumnHeadersHeight = 36; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle = new DataGridViewCellStyle { Font = Font, ForeColor = ink, BackColor = Color.White, SelectionBackColor = Color.FromArgb(218, 239, 239), SelectionForeColor = ink, Padding = new Padding(5, 0, 0, 0) };
            grid.RowsDefaultCellStyle.Font = Font;
            grid.RowTemplate.DefaultCellStyle.Font = Font;
            grid.RowTemplate.Height = 34;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "file", HeaderText = "录播文件", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 52, MinimumWidth = 220, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "duration", HeaderText = "时长", Width = 100, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "reference", HeaderText = "参考 dBFS", Width = 105, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "状态", Width = 145, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "output", HeaderText = "输出文件", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 48, MinimumWidth = 180, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.SelectionChanged += delegate { if (!busy) InvalidatePreview(); };
            grid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                QueueItem item = (QueueItem)grid.Rows[e.RowIndex].Tag;
                if (!string.IsNullOrEmpty(item.Error)) MessageBox.Show(this, item.Error, "文件处理信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else if (!string.IsNullOrEmpty(item.Output) && File.Exists(item.Output)) OpenPath(Path.GetDirectoryName(item.Output));
            };
            grid.DragEnter += delegate(object s, DragEventArgs e) { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            grid.DragDrop += delegate(object s, DragEventArgs e) { if (!busy) AddPaths((string[])e.Data.GetData(DataFormats.FileDrop)); };
            root.Controls.Add(grid, 0, 2);

            TableLayoutPanel output = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 2, 0, 7) };
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118)); output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            output.Controls.Add(MakeLabel("输出目录", 70, 32), 0, 0);
            outputFolder = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 10, 0) };
            Button browseOutput = MakeButton("选择目录", 108, delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择输出文件夹；留空时，每个原文件旁新建 ASMR_均衡输出" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) outputFolder.Text = dialog.SelectedPath;
            });
            Button defaultOutput = MakeButton("恢复默认目录", 120, delegate { outputFolder.Clear(); });
            output.Controls.Add(outputFolder, 1, 0); output.Controls.Add(browseOutput, 2, 0); output.Controls.Add(defaultOutput, 3, 0);
            foreach (Control c in new Control[] { outputFolder, browseOutput, defaultOutput }) AddBusyControl(c);
            root.Controls.Add(output, 0, 3);

            GroupBox settings = new GroupBox { Text = "处理设置", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(13, 18, 13, 5) };
            TableLayoutPanel options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, 65)); options.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel modeBox = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            modeBox.Controls.Add(MakeLabel("平衡模式", 340, 23));
            balanceMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, Margin = new Padding(0, 4, 0, 0) };
            balanceMode.Items.AddRange(new object[] { "1. 削弱口腔音 · 与轻语平衡", "2. 削弱轻语 · 与触发音平衡", "3. 自动平衡 · 削强补弱" }); balanceMode.SelectedIndex = 2;
            balanceMode.SelectedIndexChanged += delegate { OnModeChanged(); };
            modeBox.Controls.Add(balanceMode); options.Controls.Add(modeBox, 0, 0);
            FlowLayoutPanel strengthBox = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            strengthBox.Controls.Add(MakeLabel("平衡力度（三种模式均可选择）", 275, 23));
            strength = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245, Margin = new Padding(0, 4, 0, 0) };
            strength.Items.AddRange(new object[] { "温和 · 保留更多起伏", "标准 · 推荐先试听", "较强 · 更接近参考音量" }); strength.SelectedIndex = 1;
            strength.SelectedIndexChanged += delegate { UpdateProfile(); InvalidatePreview(); };
            strengthBox.Controls.Add(strength); options.Controls.Add(strengthBox, 1, 0);

            FlowLayoutPanel referenceRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 35, WrapContents = false, Margin = Padding.Empty };
            automatic = new CheckBox { Text = "每个文件自动估计参考", Checked = true, Width = 210, Height = 30, Margin = new Padding(0, 0, 8, 0) };
            referenceName = MakeLabel("目标参考", 90, 30);
            manualReference = new NumericUpDown { Minimum = -55, Maximum = -12, Value = -30, DecimalPlaces = 1, Increment = 1, Width = 76, Enabled = false, Margin = new Padding(0, 3, 5, 0) };
            referenceRow.Controls.AddRange(new Control[] { automatic, referenceName, manualReference, MakeLabel("dBFS", 58, 30), MakeLabel("可用下方按钮测量；每种模式分别记住参考设置", 485, 30) });
            options.Controls.Add(referenceRow, 0, 1); options.SetColumnSpan(referenceRow, 3);
            automatic.CheckedChanged += delegate { manualReference.Enabled = !busy && !automatic.Checked; InvalidatePreview(); };
            manualReference.ValueChanged += delegate { InvalidatePreview(); };

            FlowLayoutPanel gainBox = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            gainBox.Controls.Add(MakeLabel("处理后整体增益", 260, 23));
            FlowLayoutPanel gainRow = new FlowLayoutPanel { Width = 265, Height = 37, WrapContents = false, Margin = Padding.Empty };
            gain = new NumericUpDown { Minimum = -12, Maximum = 12, Value = 0, Width = 72, Margin = new Padding(0, 4, 5, 0) };
            gain.ValueChanged += delegate { InvalidatePreview(); };
            gainRow.Controls.Add(gain); gainRow.Controls.Add(MakeLabel("dB（负数可整体变小）", 185, 33));
            gainBox.Controls.Add(gainRow); options.Controls.Add(gainBox, 2, 0);
            profileDescription = new Label { Dock = DockStyle.Fill, ForeColor = muted, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft };
            options.Controls.Add(profileDescription, 0, 2); options.SetColumnSpan(profileDescription, 3);
            settings.Controls.Add(options); root.Controls.Add(settings, 0, 4);
            foreach (Control c in new Control[] { balanceMode, strength, automatic, manualReference, gain }) AddBusyControl(c);

            GroupBox preview = new GroupBox { Text = "试听对比 · 最长 5 分钟 · A/B 共享播放位置", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(13, 18, 13, 3) };
            TableLayoutPanel previewBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            previewBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            previewBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); previewBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); previewBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel previewRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 40, WrapContents = false, Margin = Padding.Empty };
            previewStart = new TextBox { Text = "00:00:00", Width = 86, Margin = new Padding(0, 5, 8, 0) };
            previewStart.TextChanged += delegate { InvalidatePreview(); };
            previewLength = new NumericUpDown { Minimum = 10, Maximum = 300, Value = 30, Increment = 10, Width = 58, Margin = new Padding(0, 5, 8, 0) };
            previewLength.ValueChanged += delegate { InvalidatePreview(); };
            measureButton = MakeButton("测量目标参考", 128, MeasureWhisper);
            Button generate = MakeButton("生成 A/B 试听", 132, GeneratePreview);
            originalButton = MakeButton("播放原音", 95, delegate { PlayPreview(false); });
            processedButton = MakeButton("播放处理后", 106, delegate { PlayPreview(true); });
            pauseButton = MakeButton("播放/继续", 85, delegate { TogglePreviewPlayback(); });
            stopButton = MakeButton("停止", 65, delegate { StopPlayback(); });
            previewRow.Controls.AddRange(new Control[] { MakeLabel("起点", 38, 34), previewStart, MakeLabel("长度 / 秒", 72, 34), previewLength, measureButton, generate, originalButton, processedButton, pauseButton, stopButton });
            TableLayoutPanel timeline = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            timeline.Controls.Add(MakeLabel("播放位置", 76, 30), 0, 0);
            previewSeek = new TrackBar { Dock = DockStyle.Fill, AutoSize = false, Height = 30, Minimum = 0, Maximum = 1, TickStyle = TickStyle.None,
                SmallChange = 1000, LargeChange = 5000, TickFrequency = 1000, Enabled = false, Margin = new Padding(0, 0, 9, 0), AccessibleName = "试听播放进度" };
            playbackClock = MakeLabel("00:00 / 00:00", 132, 30); playbackClock.TextAlign = ContentAlignment.MiddleRight;
            timeline.Controls.Add(previewSeek, 1, 0); timeline.Controls.Add(playbackClock, 2, 0);
            previewSeek.ValueChanged += delegate { UpdatePlaybackClock(); };
            previewSeek.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) BeginPreviewSeek(); };
            previewSeek.MouseUp += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) EndPreviewSeek(); };
            previewSeek.MouseCaptureChanged += delegate { if (seekDragging && !previewSeek.Capture) EndPreviewSeek(); };
            previewSeek.Scroll += delegate
            {
                if (!seekDragging && !busy && playback.HasPreview)
                {
                    try { playback.Seek(previewSeek.Value); RefreshPlaybackControls(); }
                    catch (Exception e) { PlaybackFailed(e); }
                }
            };
            previewSeek.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Space) { TogglePreviewPlayback(); e.SuppressKeyPress = true; } };
            previewStatus = new Label { Dock = DockStyle.Fill, ForeColor = muted, Margin = Padding.Empty,
                Text = "填写试听起点和长度，生成后可拖动播放位置；切换原音 / 处理后会保留当前位置。" };
            previewBody.Controls.Add(previewRow, 0, 0); previewBody.Controls.Add(timeline, 0, 1); previewBody.Controls.Add(previewStatus, 0, 2);
            preview.Controls.Add(previewBody); root.Controls.Add(preview, 0, 5);
            foreach (Control c in new Control[] { previewStart, previewLength, measureButton, generate }) AddBusyControl(c);
            originalButton.Enabled = processedButton.Enabled = pauseButton.Enabled = stopButton.Enabled = false;
            playbackTimer = new System.Windows.Forms.Timer { Interval = 100 };
            playbackTimer.Tick += delegate
            {
                try
                {
                    bool wasPlaying = playback.IsPlaying;
                    playback.Poll(); RefreshPlaybackControls();
                    if (wasPlaying && !playback.IsPlaying) previewStatus.Text = "试听已播放完毕；可拖动回看，或点击播放从开头重新开始。";
                }
                catch (Exception e) { PlaybackFailed(e); }
            };

            Panel footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            progress = new ProgressBar { Dock = DockStyle.Top, Height = 15, Maximum = 1000, Style = ProgressBarStyle.Continuous };
            status = new Label { Location = new Point(0, 24), Height = 26, AutoSize = false, Width = 1050, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ForeColor = muted,
                Text = "就绪。输出目录留空时，会在原文件旁新建 ASMR_均衡输出 文件夹。" };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, WrapContents = false };
            startButton = MakeButton("开始批量处理", 180, ProcessQueue, true);
            cancelButton = MakeButton("停止处理", 110, delegate { if (cancellation != null) { cancellation.Cancel(); status.Text = "正在停止并清理未完成文件…"; cancelButton.Enabled = false; } });
            cancelButton.Enabled = false;
            Button openOutput = MakeButton("打开输出目录", 140, delegate { OpenOutput(); });
            Button logs = MakeButton("查看日志", 105, delegate { OpenPath(logFolder); });
            Button help = MakeButton("使用说明", 105, delegate { OpenPath(Path.Combine(appFolder, "使用说明.html")); });
            actions.Controls.AddRange(new Control[] { startButton, cancelButton, openOutput, logs, help, MakeLabel("原文件保留 · 视频直接复制", 250, 34) });
            footer.Controls.Add(progress); footer.Controls.Add(status); footer.Controls.Add(actions); root.Controls.Add(footer, 0, 6);
            AddBusyControl(startButton);
        }

        void UpdateProfile()
        {
            if (profileDescription == null || strength == null || balanceMode == null) return;
            int selected = Math.Max(0, balanceMode.SelectedIndex);
            string[] names = { "轻语参考", "触发音参考", "目标参考" };
            string[] descriptions = {
                "模式 1：以正常轻语为基准，快速压低突出的口腔音 / 大声。建议先用“测量轻语参考”选取纯轻语片段。",
                "模式 2：以较小触发音为基准，平滑压低持续偏大的轻语。建议先用“测量触发音参考”选取较安静的触发音片段。",
                "模式 3：自动估计中间音量，压低大声、有限度抬升弱声；温和 / 标准 / 较强的最大补偿约为 +3 / +7 / +12 dB。"
            };
            referenceName.Text = names[selected];
            if (measureButton != null) measureButton.Text = "测量" + names[selected];
            profileDescription.Text = descriptions[selected] + "\r\n自动参考依据音量统计，不识别声音类别；重叠声音会一起调整。三种模式都保留双耳方向感。";
        }

        void CaptureModeSettings()
        {
            modeReferences[previousMode] = (double)manualReference.Value;
            modeAutomatic[previousMode] = automatic.Checked;
        }

        void OnModeChanged()
        {
            if (initialized)
            {
                CaptureModeSettings();
                previousMode = Math.Max(0, balanceMode.SelectedIndex);
                automatic.Checked = modeAutomatic[previousMode];
                manualReference.Value = (decimal)modeReferences[previousMode];
            }
            UpdateProfile(); InvalidatePreview();
        }

        void InvalidatePreview()
        {
            if (!initialized || busy) return;
            ClearPreviews();
            previewStatus.Text = "选中一个文件，生成 A/B 试听后可切换播放；修改设置后需要重新生成。";
        }

        void ClearPreviews()
        {
            if (playbackTimer != null) playbackTimer.Stop();
            seekDragging = resumeAfterSeek = false;
            try { playback.Unload(); } catch (Exception e) { Log("关闭试听提示：" + e.Message); }
            string before = originalPreview, after = processedPreview;
            originalPreview = processedPreview = null;
            AudioEngine.TryDelete(before); AudioEngine.TryDelete(after);
            RefreshPlaybackControls();
        }

        void StopPlayback()
        {
            if (playbackTimer != null) playbackTimer.Stop();
            seekDragging = resumeAfterSeek = false;
            try { if (playback != null) playback.Stop(); } catch (Exception e) { Log("停止试听提示：" + e.Message); }
            RefreshPlaybackControls();
            if (playback != null && playback.HasPreview && !busy && previewStatus != null) previewStatus.Text = "已停止，播放位置已回到开头。";
        }

        void PlayPreview(bool processed)
        {
            if (busy || !playback.HasPreview) return;
            try
            {
                playback.Play(processed); RefreshPlaybackControls();
                previewStatus.Text = processed ? "正在播放：处理后。切换原音会从同一位置继续，可拖动进度条定位。" : "正在播放：原音。切换处理后会从同一位置继续，可拖动进度条定位。";
            }
            catch (Exception e) { PlaybackFailed(e); }
        }

        static string PreviewTime(int milliseconds)
        {
            int seconds = Math.Max(0, milliseconds) / 1000;
            return (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
        }
        void UpdatePlaybackClock()
        {
            if (playbackClock != null && previewSeek != null)
                playbackClock.Text = PreviewTime(previewSeek.Value) + " / " + PreviewTime(playback.DurationMilliseconds);
        }
        void RefreshPlaybackControls()
        {
            if (previewSeek == null || playback == null) return;
            bool ready = playback.HasPreview && !busy;
            originalButton.Enabled = processedButton.Enabled = pauseButton.Enabled = previewSeek.Enabled = ready;
            stopButton.Enabled = ready && (playback.IsPlaying || playback.PositionMilliseconds > 0);
            pauseButton.Text = playback.IsPlaying ? "暂停" : "播放/继续";
            originalButton.BackColor = ready && !playback.ProcessedSelected ? Color.FromArgb(218, 239, 239) : Color.White;
            processedButton.BackColor = ready && playback.ProcessedSelected ? Color.FromArgb(218, 239, 239) : Color.White;
            if (!seekDragging)
            {
                previewSeek.Maximum = Math.Max(1, playback.DurationMilliseconds);
                previewSeek.Value = Math.Max(0, Math.Min(previewSeek.Maximum, playback.PositionMilliseconds));
            }
            UpdatePlaybackClock();
            if (playbackTimer != null)
            {
                if (ready && playback.IsPlaying && !seekDragging) playbackTimer.Start(); else playbackTimer.Stop();
            }
        }
        void TogglePreviewPlayback()
        {
            if (busy || !playback.HasPreview) return;
            try
            {
                if (playback.IsPlaying)
                {
                    playback.Pause(); previewStatus.Text = "已暂停；可拖动定位，点击“播放/继续”或 A/B 按钮从当前位置播放。";
                }
                else
                {
                    playback.Play(playback.ProcessedSelected);
                    previewStatus.Text = playback.ProcessedSelected ? "正在播放：处理后。可拖动定位，切换原音会保留当前位置。" : "正在播放：原音。可拖动定位，切换处理后会保留当前位置。";
                }
                RefreshPlaybackControls();
            }
            catch (Exception e) { PlaybackFailed(e); }
        }
        void BeginPreviewSeek()
        {
            if (busy || !playback.HasPreview) return;
            try
            {
                seekDragging = true; resumeAfterSeek = playback.IsPlaying;
                if (resumeAfterSeek) playback.Pause();
                playbackTimer.Stop();
            }
            catch (Exception e) { seekDragging = false; PlaybackFailed(e); }
        }
        void EndPreviewSeek()
        {
            if (!seekDragging) return;
            int target = previewSeek.Value; bool resume = resumeAfterSeek;
            seekDragging = resumeAfterSeek = false;
            try
            {
                playback.Seek(target);
                if (resume && target < playback.DurationMilliseconds) playback.Play(playback.ProcessedSelected);
                RefreshPlaybackControls();
                previewStatus.Text = playback.IsPlaying
                    ? (playback.ProcessedSelected ? "正在播放：处理后。" : "正在播放：原音。") + "已跳转到所选位置。"
                    : "已定位到所选位置；点击播放即可从这里试听。";
            }
            catch (Exception e) { PlaybackFailed(e); }
        }
        void PlaybackFailed(Exception error)
        {
            StopPlayback();
            ShowError("试听播放失败", error);
        }

        void AddFiles(object sender, EventArgs args)
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Multiselect = true, Filter = "MP4 录播|*.mp4;*.m4v", Title = "选择一个或多个 ASMR 录播" })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames);
        }

        void AddFolder(object sender, EventArgs args)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "添加这个文件夹内的 MP4（不含子文件夹）" })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dialog.SelectedPath });
        }

        void AddPaths(IEnumerable<string> paths)
        {
            int rejected = 0;
            try
            {
                foreach (string value in paths)
                {
                    IEnumerable<string> files = Directory.Exists(value) ? Directory.EnumerateFiles(value, "*", SearchOption.TopDirectoryOnly) : new[] { value };
                    foreach (string file in files)
                    {
                        string extension = Path.GetExtension(file);
                        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)) { rejected++; continue; }
                        string path = Path.GetFullPath(file);
                        if (!File.Exists(path) || items.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                        QueueItem item = new QueueItem { Path = path };
                        int index = grid.Rows.Add(Path.GetFileName(path), "待检测", "—", "等待处理", "—");
                        item.Row = grid.Rows[index]; item.Row.Tag = item; item.Row.Cells["file"].ToolTipText = path; items.Add(item);
                    }
                }
                UpdateCount();
                status.Text = "已添加 " + items.Count + " 个 MP4。先选一个代表片段试听，再开始批量处理。" + (rejected > 0 ? "（仅接收 MP4 / M4V）" : "");
            }
            catch (Exception e) { ShowError("添加文件失败", e); }
        }

        void RemoveSelected()
        {
            foreach (DataGridViewRow row in grid.SelectedRows.Cast<DataGridViewRow>().ToArray()) { items.Remove((QueueItem)row.Tag); grid.Rows.Remove(row); }
            UpdateCount(); InvalidatePreview();
        }
        void UpdateCount() { queueCount.Text = items.Count + " 个文件 · 支持拖入 · 双击失败行查看原因"; }
        QueueItem SelectedItem()
        {
            if (grid.CurrentRow == null) throw new InvalidOperationException("请先添加并选中一个 MP4 文件。");
            return (QueueItem)grid.CurrentRow.Tag;
        }
        double StartSeconds()
        {
            string[] pieces = previewStart.Text.Trim().Split(new char[] { ':' }, StringSplitOptions.None);
            double result = 0;
            if (pieces.Length > 3 || pieces.Length == 0) throw new FormatException("试听起点请填 00:12:30，或直接填写秒数。");
            for (int i = 0; i < pieces.Length; i++)
            {
                double value;
                if (!double.TryParse(pieces[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) || value < 0 || double.IsInfinity(value) || double.IsNaN(value) || (i > 0 && value >= 60))
                    throw new FormatException("试听起点请填 00:12:30，或直接填写秒数。");
                result = result * 60 + value;
            }
            return result;
        }
        ProcessingSettings GetSettings()
        {
            string folder = outputFolder.Text.Trim();
            if (folder.Length > 0 && !Path.IsPathRooted(folder)) throw new InvalidOperationException("输出目录请使用完整路径，或点击“选择目录”。");
            return new ProcessingSettings { Mode = (BalanceMode)balanceMode.SelectedIndex, Strength = strength.SelectedIndex, AutomaticReference = automatic.Checked,
                ReferenceDb = (double)manualReference.Value, GainDb = (double)gain.Value, OutputFolder = folder };
        }
        void SetBusy(bool value, string text = null)
        {
            busy = value;
            foreach (Control control in disableWhenBusy) control.Enabled = !value;
            manualReference.Enabled = !value && !automatic.Checked;
            grid.Enabled = !value;
            cancelButton.Enabled = value;
            if (value)
            {
                StopPlayback(); progress.Value = 0;
                if (text != null) status.Text = text;
                cancellation = new CancellationTokenSource();
            }
            else
            {
                if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
                if (closeWhenDone) BeginInvoke(new Action(Close));
            }
            RefreshPlaybackControls();
        }

        MediaInfo EnsureMedia(QueueItem item, CancellationToken token)
        {
            // Re-probe every operation in case a recording was replaced externally.
            MediaInfo media = engine.Probe(item.Path, token); item.Media = media;
            Ui(delegate { item.Row.Cells["duration"].Value = FormatDuration(media.Duration); });
            return media;
        }
        static string FormatDuration(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }
        void ShowReference(QueueItem item, double value)
        {
            Ui(delegate { item.Row.Cells["reference"].Value = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); });
        }

        async void MeasureWhisper(object sender, EventArgs args)
        {
            if (busy) return;
            QueueItem item; double start;
            try { item = SelectedItem(); start = StartSeconds(); }
            catch (Exception e) { ShowError("无法测量", e); return; }
            string referenceLabel = referenceName.Text;
            ClearPreviews();
            SetBusy(true, "正在测量所选片段的" + referenceLabel + "…");
            CancellationToken token = cancellation.Token;
            try
            {
                AnalysisResult measured = await Task.Run(delegate
                {
                    MediaInfo media = EnsureMedia(item, token);
                    if (start >= media.Duration - .5) throw new InvalidOperationException("试听起点已超过文件时长。");
                    return AudioEngine.EstimateReference(engine.MeasureSegment(item.Path, media, start, Math.Min(20, media.Duration - start), token), true);
                }, token);
                if (measured.UsedFallback) throw new InvalidOperationException("这段有效声音太少，请把起点移动到希望保留的参考声音处，再测量。");
                automatic.Checked = false; manualReference.Value = (decimal)Math.Round(measured.ReferenceDb, 1);
                ShowReference(item, measured.ReferenceDb);
                originalPreview = processedPreview = null;
                previewStatus.Text = "已测量" + referenceLabel + " " + measured.ReferenceDb.ToString("0.0") + " dBFS；仅用于当前模式的整个队列，请先生成 A/B 试听。";
                SetStatus(referenceLabel + "已测量。不同录播音量差异很大时，请分别处理，或使用自动参考。", 1);
                Log("用户指定" + referenceLabel + " " + measured.ReferenceDb.ToString("0.0") + " dBFS，起点 " + start + " 秒。");
            }
            catch (OperationCanceledException) { SetStatus("已停止测量。", 0); }
            catch (Exception e) { ShowError("参考测量失败", e); }
            finally { SetBusy(false); }
        }

        async void GeneratePreview(object sender, EventArgs args)
        {
            if (busy) return;
            QueueItem item; double start, length; ProcessingSettings settings;
            try { item = SelectedItem(); start = StartSeconds(); length = (double)previewLength.Value; settings = GetSettings(); }
            catch (Exception e) { ShowError("无法生成试听", e); return; }
            ClearPreviews();
            SetBusy(true, "正在准备 A/B 试听…");
            CancellationToken token = cancellation.Token;
            string prefix = Guid.NewGuid().ToString("N");
            string before = Path.Combine(previewFolder, prefix + "_a.wav"), after = Path.Combine(previewFolder, prefix + "_b.wav");
            try
            {
                double reference = await Task.Run(delegate
                {
                    MediaInfo media = EnsureMedia(item, token);
                    if (start >= media.Duration - .5) throw new InvalidOperationException("试听起点已超过文件时长。");
                    double actualLength = Math.Min(length, media.Duration - start);
                    double refDb = settings.AutomaticReference ? engine.Analyze(item.Path, media, settings.Mode, token, p => SetStatus("正在抽样估计当前模式的参考…", p * .35)).ReferenceDb : settings.ReferenceDb;
                    ShowReference(item, refDb);
                    engine.RenderPreview(item.Path, media, start, actualLength, before, settings, refDb, false, token, p => SetStatus("正在生成原音试听…", .35 + .25 * p));
                    engine.RenderPreview(item.Path, media, start, actualLength, after, settings, refDb, true, token, p => SetStatus("正在生成处理后试听…", .60 + .40 * p));
                    return refDb;
                }, token);
                playback.Load(before, after);
                originalPreview = before; processedPreview = after;
                previewStatus.Text = AudioEngine.ModeName(settings.Mode) + "试听已就绪；可拖动定位，切换 A/B 保留当前位置。参考 " + reference.ToString("0.0") + " dBFS。";
                SetStatus("A/B 试听已生成。满意后点击“开始批量处理”。", 1);
            }
            catch (OperationCanceledException) { AudioEngine.TryDelete(before); AudioEngine.TryDelete(after); SetStatus("已停止生成试听。", 0); }
            catch (Exception e) { AudioEngine.TryDelete(before); AudioEngine.TryDelete(after); ShowError("生成试听失败", e); }
            finally { SetBusy(false); }
        }

        async void ProcessQueue(object sender, EventArgs args)
        {
            if (busy) return;
            ProcessingSettings settings;
            try
            {
                if (items.Count == 0) throw new InvalidOperationException("请先添加 MP4 录播。");
                settings = GetSettings();
            }
            catch (Exception e) { ShowError("无法开始", e); return; }
            QueueItem[] queue = items.ToArray();
            foreach (QueueItem item in queue) { item.Error = null; item.Row.Cells["state"].Value = "等待处理"; item.Row.DefaultCellStyle.ForeColor = ink; }
            SaveSettings(); SetBusy(true, "正在检查处理组件…");
            CancellationToken token = cancellation.Token;
            int success = 0, failed = 0;
            try
            {
                await Task.Run(delegate
                {
                    engine.CheckTools(token);
                    for (int i = 0; i < queue.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        QueueItem item = queue[i]; int index = i;
                        try
                        {
                            Ui(delegate { item.Row.Cells["state"].Value = "读取信息…"; });
                            SetStatus("[" + (i + 1) + "/" + queue.Length + "] 读取 " + Path.GetFileName(item.Path), i / (double)queue.Length);
                            MediaInfo media = EnsureMedia(item, token);
                            double reference = settings.ReferenceDb;
                            if (settings.AutomaticReference)
                            {
                                Ui(delegate { item.Row.Cells["state"].Value = "估计参考…"; });
                                reference = engine.Analyze(item.Path, media, settings.Mode, token, p => SetStatus("[" + (index + 1) + "/" + queue.Length + "] 正在估计" + AudioEngine.ModeName(settings.Mode) + "参考…", (index + p * .08) / queue.Length)).ReferenceDb;
                            }
                            ShowReference(item, reference);
                            Ui(delegate { item.Row.Cells["state"].Value = "处理中…"; });
                            string output = engine.ProcessFile(item.Path, media, settings, reference, token, p =>
                            {
                                SetStatus("[" + (index + 1) + "/" + queue.Length + "] " + (p >= .99 ? "正在封装并校验…" : "处理 " + (p * 100).ToString("0") + "% · " + Path.GetFileName(item.Path)), (index + .08 + p * .92) / queue.Length);
                                Ui(delegate { item.Row.Cells["state"].Value = p >= .99 ? "封装 / 校验…" : "处理中 " + (p * 100).ToString("0") + "%"; });
                            });
                            item.Output = output; success++;
                            Ui(delegate { item.Row.Cells["state"].Value = "已完成"; item.Row.Cells["output"].Value = Path.GetFileName(output); item.Row.Cells["output"].ToolTipText = output; item.Row.DefaultCellStyle.ForeColor = accent; });
                        }
                        catch (OperationCanceledException) { Ui(delegate { item.Row.Cells["state"].Value = "已停止"; }); throw; }
                        catch (Exception e)
                        {
                            failed++; item.Error = e.Message; Log("失败：" + item.Path + "\r\n" + e);
                            Ui(delegate { item.Row.Cells["state"].Value = "失败（双击查看）"; item.Row.Cells["state"].ToolTipText = e.Message; item.Row.DefaultCellStyle.ForeColor = Color.FromArgb(174, 60, 57); });
                        }
                    }
                }, token);
                SetStatus("队列处理结束：成功 " + success + " 个，失败 " + failed + " 个。" + (failed > 0 ? "双击失败行可查看原因。" : "点击“打开输出目录”查看结果。"), 1);
            }
            catch (OperationCanceledException) { SetStatus("已停止。已完成 " + success + " 个；完成的文件保留，未完成的临时文件已清理。", progress.Value / 1000.0); }
            catch (Exception e) { ShowError("处理无法继续", e); }
            finally { SetBusy(false); }
        }

        void ShowError(string title, Exception error)
        {
            Log(title + "\r\n" + error);
            string firstLine = error.Message;
            int lineEnd = firstLine.IndexOf('\n');
            if (lineEnd >= 0) firstLine = firstLine.Substring(0, lineEnd);
            status.Text = title + "：" + firstLine;
            if (!closeWhenDone) MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { ShowError("无法打开", e); }
        }
        void OpenOutput()
        {
            QueueItem item = grid.CurrentRow == null ? null : (QueueItem)grid.CurrentRow.Tag;
            string path;
            if (item != null && !string.IsNullOrEmpty(item.Output)) path = Path.GetDirectoryName(item.Output);
            else if (!string.IsNullOrWhiteSpace(outputFolder.Text)) path = outputFolder.Text.Trim();
            else if (item != null) path = Path.Combine(Path.GetDirectoryName(item.Path), "ASMR_均衡输出");
            else { status.Text = "请先添加文件或选择输出目录。"; return; }
            if (!Directory.Exists(path)) { status.Text = "输出目录尚未生成；处理成功后会自动创建。"; return; }
            OpenPath(path);
        }
        void CreateShortcut(object sender, EventArgs args)
        {
            object shell = null, link = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell"); shell = Activator.CreateInstance(shellType);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string shortcut = Path.Combine(desktop, "轻声 ASMR 音量均衡.lnk");
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcut });
                Type type = link.GetType();
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { appFolder });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath + ",0" });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "轻声 ASMR 音量均衡" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                status.Text = "桌面图标已创建。请保留当前程序文件夹；移动文件夹后需要重新创建图标。";
            }
            catch (Exception e) { ShowError("创建桌面图标失败", e); }
            finally
            {
                if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
        void LoadSettings()
        {
            try
            {
                bool legacy = !File.Exists(settingsFile);
                string path = legacy ? Path.Combine(dataFolder, "settings.xml") : settingsFile;
                if (!File.Exists(path)) return;
                XmlDocument doc = new XmlDocument(); doc.Load(path);
                XmlElement e = doc.DocumentElement; decimal value; int profile;
                if (int.TryParse(e.GetAttribute("strength"), out profile)) strength.SelectedIndex = Math.Max(0, Math.Min(2, profile));
                outputFolder.Text = e.GetAttribute("output");
                int previewSeconds;
                if (int.TryParse(e.GetAttribute("preview_seconds"), out previewSeconds)) previewLength.Value = Math.Max(10, Math.Min(300, previewSeconds));
                // On the first v2 launch, retain the user's folder and strength,
                // but start the more general auto mode at 0 dB overall gain.
                if (legacy) return;
                for (int i = 0; i < 3; i++)
                {
                    modeAutomatic[i] = e.GetAttribute("auto" + i) != "false";
                    if (decimal.TryParse(e.GetAttribute("ref" + i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
                        modeReferences[i] = (double)Math.Max(-55, Math.Min(-12, value));
                }
                int selected;
                if (!int.TryParse(e.GetAttribute("mode"), out selected)) selected = 2;
                balanceMode.SelectedIndex = Math.Max(0, Math.Min(2, selected));
                previousMode = balanceMode.SelectedIndex;
                automatic.Checked = modeAutomatic[previousMode];
                manualReference.Value = (decimal)modeReferences[previousMode];
                if (decimal.TryParse(e.GetAttribute("gain"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) gain.Value = Math.Max(-12, Math.Min(12, value));
            }
            catch { }
        }
        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(dataFolder);
                CaptureModeSettings();
                XmlDocument doc = new XmlDocument(); XmlElement e = doc.CreateElement("settings"); doc.AppendChild(e);
                e.SetAttribute("schema", "2"); e.SetAttribute("mode", balanceMode.SelectedIndex.ToString());
                e.SetAttribute("preview_seconds", previewLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                e.SetAttribute("strength", strength.SelectedIndex.ToString()); e.SetAttribute("gain", AudioEngine.Number((double)gain.Value));
                for (int i = 0; i < 3; i++)
                {
                    e.SetAttribute("auto" + i, modeAutomatic[i] ? "true" : "false");
                    e.SetAttribute("ref" + i, AudioEngine.Number(modeReferences[i]));
                }
                e.SetAttribute("output", outputFolder.Text.Trim()); doc.Save(settingsFile);
            }
            catch { }
        }
        void OnClosing(object sender, FormClosingEventArgs args)
        {
            StopPlayback();
            if (busy) { args.Cancel = true; closeWhenDone = true; cancellation.Cancel(); status.Text = "正在停止处理，完成清理后关闭…"; return; }
            SaveSettings();
            ClearPreviews(); playback.Dispose(); playbackTimer.Dispose();
            try { Directory.Delete(previewFolder, true); } catch { }
        }
    }
}
