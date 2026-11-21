using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using CancellationTokenSource = System.Threading.CancellationTokenSource;
using OperationCanceledException = System.OperationCanceledException;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InDeWindow());
    }
}

internal sealed class InDeWindow : Form
{
    private static readonly Color Canvas = Color.FromArgb(13, 19, 33);
    private static readonly Color Surface = Color.FromArgb(23, 32, 51);
    private static readonly Color Field = Color.FromArgb(16, 24, 41);
    private static readonly Color Border = Color.FromArgb(51, 66, 93);
    private static readonly Color Foreground = Color.FromArgb(231, 239, 252);
    private static readonly Color Muted = Color.FromArgb(163, 184, 217);
    private static readonly Color Accent = Color.FromArgb(147, 194, 255);
    private static readonly Color Mint = Color.FromArgb(125, 225, 202);
    private readonly TextBox input = new TextBox();
    private readonly TextBox output = new TextBox();
    private readonly TextBox backend = new TextBox();
    private readonly NumericUpDown sharpening = Number(0m, 0.70m, 0.10m, 2, 0.01m);
    private readonly NumericUpDown halos = Number(-0.50m, 0.50m, 0m, 2, 0.01m);
    private readonly NumericUpDown noise = Number(0m, 1m, 0.50m, 2, 0.01m);
    private readonly NumericUpDown iterations = Number(1m, 5m, 1m, 0, 1m);
    private readonly NumericUpDown color = Number(0m, 1m, 0.85m, 2, 0.01m);
    private readonly CheckBox separateColor = new CheckBox();
    private readonly Label noiseLabel = Caption("降噪强度");
    private readonly Label status = new Label();
    private readonly ProgressTrack progress = new ProgressTrack();
    private readonly TextBox log = new TextBox();
    private readonly Timer timer = new Timer { Interval = 150 };
    private readonly Timer previewTimer = new Timer { Interval = 250 };
    private readonly PictureBox inputPreview = new PictureBox();
    private readonly Label previewMessage = new Label();
    private readonly Label previewSize = new Label();
    private readonly Button start = Button("开始处理");
    private readonly Button result = Button("打开合成 TIFF");
    private readonly Button comparison = Button("查看对比");
    private readonly Button folder = Button("打开结果文件夹");
    private readonly Button cancel = Button("取消");
    private readonly Button downloadBackend = Button("下载官方后端");
    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
    private Process process;
    private CancellationTokenSource setupCancellation;
    private Process previewProcess;
    private int previewVersion;
    private string resultPath;
    private bool cancelled;
    private bool closeWhenFinished;
    private int stageStart;
    private int stageEnd;
    private const string CompletionFile = "05_InDe_Composite.tif";
    private const string ComparisonFile = "InDe_Comparison.jpg";
    private Image headerIcon;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public InDeWindow()
    {
        Text = "InDe 星空处理";
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Canvas;
        ForeColor = Foreground;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1060, 686);
        MinimumSize = new Size(840, 632);
        AllowDrop = true;
        DragEnter += AcceptImageDrag;
        DragDrop += DropImage;

        Panel viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Controls.Add(viewport);
        TableLayoutPanel layout = new TableLayoutPanel {
            Dock = DockStyle.Top, Padding = new Padding(24, 12, 24, 20), ColumnCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        viewport.Controls.Add(layout);
        viewport.ClientSizeChanged += delegate { layout.MinimumSize = new Size(0, viewport.ClientSize.Height); };
        layout.MinimumSize = new Size(0, viewport.ClientSize.Height);
        Add(layout, BuildHeader(), 74);

        TableLayoutPanel dashboard = FitTable(2);
        dashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        dashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        dashboard.RowCount = 1;
        dashboard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        TableLayoutPanel left = FitTable(1);
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.Margin = new Padding(0, 0, 12, 0);
        dashboard.Controls.Add(left, 0, 0);

        TableLayoutPanel files;
        AddAuto(left, CreateCard(out files, true));
        AddAuto(files, Section("图像与文件"));
        AddAuto(files, PathRow("输入图像", input, Button("选择图像", PickImage)));
        AddAuto(files, PathRow("保存到", output, Button("选择位置", PickOutput)));
        AddAuto(files, PathRow("InDe 后端", backend, Button("选择后端", PickBackend)));
        FlowLayoutPanel backendActions = Flow();
        backendActions.AutoSize = true;
        backendActions.Controls.Add(downloadBackend);
        AddAuto(files, backendActions);
        downloadBackend.Click += async delegate { await DownloadBackend(); };
        output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "InDe 结果");
        backend.Text = FindBackend();

        TableLayoutPanel settings;
        AddAuto(left, CreateCard(out settings, true));
        AddAuto(settings, Section("处理参数"));
        TableLayoutPanel parameters = FitTable(4);
        for (int column = 0; column < 4; column++)
            parameters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        parameters.RowCount = 2;
        parameters.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        parameters.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddParameter(parameters, 0, Caption("星点锐化"), sharpening);
        AddParameter(parameters, 1, Caption("星晕调节"), halos);
        AddParameter(parameters, 2, noiseLabel, noise);
        AddParameter(parameters, 3, Caption("降噪轮数"), iterations);
        AddAuto(settings, parameters);
        separateColor.Text = "单独设置色彩降噪";
        separateColor.AutoSize = true;
        separateColor.ForeColor = Muted;
        separateColor.Anchor = AnchorStyles.Left;
        separateColor.Margin = new Padding(0, 0, 12, 0);
        separateColor.CheckedChanged += delegate {
            color.Enabled = separateColor.Checked && process == null;
            noiseLabel.Text = separateColor.Checked ? "亮度降噪" : "降噪强度";
        };
        color.Enabled = false;
        color.Anchor = AnchorStyles.Left;
        color.Margin = Padding.Empty;
        TableLayoutPanel colorRow = FitTable(3);
        colorRow.Margin = new Padding(0, 9, 0, 3);
        colorRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        colorRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        colorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        colorRow.Controls.Add(separateColor, 0, 0);
        colorRow.Controls.Add(color, 1, 0);
        AddAuto(settings, colorRow);

        dashboard.Controls.Add(BuildPreview(), 1, 0);
        AddAuto(layout, dashboard);

        FlowLayoutPanel actions = Flow();
        actions.Controls.AddRange(new Control[] { start, cancel, result, comparison, folder });
        start.BackColor = Accent;
        start.ForeColor = Canvas;
        start.FlatAppearance.BorderSize = 0;
        start.FlatAppearance.MouseOverBackColor = Color.FromArgb(183, 216, 255);
        start.MinimumSize = new Size(116, 37);
        Add(layout, actions, 52);
        start.Click += delegate { StartProcessing(); };
        cancel.Click += delegate { CancelProcessing(); };
        result.Click += delegate { OpenResult(CompletionFile); };
        comparison.Click += delegate { OpenResult(ComparisonFile); };
        folder.Click += delegate { OpenResult(""); };
        cancel.Enabled = false;
        EnableResults(false);

        status.Text = BackendInstaller.IsUsable(backend.Text)
            ? "选择或拖入一张 RGB TIFF / PNG"
            : "首次使用请点击「下载官方后端」，或选择已有的后端。";
        status.ForeColor = Muted;
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        Add(layout, status, 28);
        progress.Dock = DockStyle.Fill;
        Add(layout, progress, 12);

        TableLayoutPanel activity;
        Control activityCard = CreateCard(out activity, false);
        activityCard.Margin = new Padding(0, 10, 0, 0);
        activityCard.MinimumSize = new Size(0, 104);
        Add(layout, activityCard, null);
        AddAuto(activity, Section("处理日志"));
        StyleField(log);
        log.Dock = DockStyle.Fill;
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.Margin = new Padding(0);
        Add(activity, log, null);

        previewTimer.Tick += async delegate {
            previewTimer.Stop();
            await LoadInputPreview();
        };
        input.TextChanged += delegate { QueuePreview(); };
        backend.TextChanged += delegate { QueuePreview(); };
        QueuePreview();
        timer.Tick += delegate { ReadProgress(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (process == null && setupCancellation == null) return;
            closeWhenFinished = true;
            CancelProcessing();
            e.Cancel = true;
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            previewTimer.Dispose();
            previewVersion++;
            StopPreviewProcess();
            ClearPreviewImage();
            if (headerIcon != null) headerIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private Control BuildHeader()
    {
        Panel header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        using (Stream artwork = typeof(InDeWindow).Assembly.GetManifestResourceStream("InDe.Header"))
        {
            if (artwork == null)
            {
                if (Icon != null) headerIcon = Icon.ToBitmap();
            }
            else
            {
                using (Image decoded = Image.FromStream(artwork)) headerIcon = new Bitmap(decoded);
            }
        }
        if (headerIcon != null)
        {
            header.Controls.Add(new PictureBox { Image = headerIcon, SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(53, 53), Location = new Point(0, 5) });
        }
        header.Controls.Add(new Label { Text = "InDe 星空处理", AutoSize = true,
            Font = new Font(Font.FontFamily, 21, FontStyle.Bold), Location = new Point(66, 2) });
        header.Controls.Add(new Label { Text = "反卷积  /  星点分离  /  降噪", AutoSize = true,
            ForeColor = Muted, Location = new Point(70, 43) });
        return header;
    }

    private static Label Section(string text)
    {
        return new Label { Text = text, AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), ForeColor = Foreground,
            Margin = new Padding(0, 0, 0, 3) };
    }

    private static Control CreateCard(out TableLayoutPanel table, bool autoSize)
    {
        RoundedCard card = new RoundedCard {
            BackColor = Surface, Dock = DockStyle.Fill, Padding = new Padding(15, 9, 15, 9),
            Margin = new Padding(0, 0, 0, 9), AutoSize = autoSize,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        table = autoSize ? FitTable(1) : new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(table);
        return card;
    }

    private Control BuildPreview()
    {
        TableLayoutPanel section;
        Control card = CreateCard(out section, false);
        card.MinimumSize = new Size(0, 240);
        AddAuto(section, Section("输入预览"));
        Panel imageWell = new Panel { BackColor = Field, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        inputPreview.Dock = DockStyle.Fill;
        inputPreview.SizeMode = PictureBoxSizeMode.Zoom;
        inputPreview.AccessibleName = "输入图像预览";
        imageWell.Controls.Add(inputPreview);
        previewMessage.Text = "选择或拖入图像后预览";
        previewMessage.ForeColor = Muted;
        previewMessage.TextAlign = ContentAlignment.MiddleCenter;
        previewMessage.Dock = DockStyle.Fill;
        imageWell.Controls.Add(previewMessage);
        previewMessage.BringToFront();
        Add(section, imageWell, null);
        previewSize.AutoSize = true;
        previewSize.ForeColor = Muted;
        previewSize.Margin = new Padding(0, 6, 0, 0);
        AddAuto(section, previewSize);
        return card;
    }

    private static void AddParameter(TableLayoutPanel table, int column, Label label, NumericUpDown number)
    {
        label.Margin = new Padding(0, 4, 0, 3);
        number.Margin = new Padding(0, 0, 10, 0);
        number.Dock = DockStyle.Top;
        table.Controls.Add(label, column, 0);
        table.Controls.Add(number, column, 1);
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value, int places, decimal step)
    {
        return new NumericUpDown { Minimum = min, Maximum = max, Value = value,
            DecimalPlaces = places, Increment = step, Width = 80, TextAlign = HorizontalAlignment.Right,
            BackColor = Field, ForeColor = Foreground, BorderStyle = BorderStyle.FixedSingle };
    }

    private static Button Button(string caption, EventHandler handler = null)
    {
        Button button = new FlatButton { Text = caption, AutoSize = true, Padding = new Padding(9, 0, 9, 0),
            MinimumSize = new Size(0, 34), Margin = new Padding(0, 2, 8, 2), FlatStyle = FlatStyle.Flat,
            BackColor = Surface, ForeColor = Foreground, UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 55, 80);
        if (handler != null) button.Click += handler;
        return button;
    }

    private static Label Caption(string text)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 7, 0, 0) };
    }

    private static FlowLayoutPanel Flow()
    {
        return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
    }

    private static TableLayoutPanel FitTable(int columns)
    {
        return new TableLayoutPanel { ColumnCount = columns, Dock = DockStyle.Fill, Margin = Padding.Empty,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    }

    private static void AddAuto(TableLayoutPanel layout, Control control)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(control, 0, row);
    }

    private static void Add(TableLayoutPanel layout, Control control, int? height)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(height.HasValue
            ? new RowStyle(SizeType.Absolute, height.Value)
            : new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(control, 0, row);
    }

    private static Control PathRow(string label, TextBox box, Button button)
    {
        TableLayoutPanel row = FitTable(3);
        row.RowCount = 1;
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
        StyleField(box);
        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        box.Margin = new Padding(0, 0, 8, 0);
        button.Dock = DockStyle.Top;
        row.Controls.Add(Caption(label), 0, 0);
        row.Controls.Add(box, 1, 0);
        row.Controls.Add(button, 2, 0);
        return row;
    }

    private static void StyleField(TextBox box)
    {
        box.BackColor = Field;
        box.ForeColor = Foreground;
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    private sealed class RoundedCard : Panel
    {
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width < 20 || Height < 20) return;
            using (GraphicsPath shape = new GraphicsPath())
            {
                const int curve = 18;
                shape.AddArc(0, 0, curve, curve, 180, 90);
                shape.AddArc(Width - curve, 0, curve, curve, 270, 90);
                shape.AddArc(Width - curve, Height - curve, curve, curve, 0, 90);
                shape.AddArc(0, Height - curve, curve, curve, 90, 90);
                shape.CloseFigure();
                Region old = Region;
                Region = new Region(shape);
                if (old != null) old.Dispose();
            }
        }
    }

    private sealed class ProgressTrack : Control
    {
        private int value;

        public ProgressTrack()
        {
            DoubleBuffered = true;
            AccessibleRole = AccessibleRole.ProgressBar;
            AccessibleName = "处理进度";
        }

        public int Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int y = Math.Max(0, (Height - 5) / 2);
            using (Brush track = new SolidBrush(Border))
            using (Brush fill = new SolidBrush(Mint))
            {
                e.Graphics.FillRectangle(track, 0, y, Width, 5);
                e.Graphics.FillRectangle(fill, 0, y, Width * Value / 100, 5);
            }
        }
    }

    private sealed class FlatButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled)
            {
                base.OnPaint(e);
                return;
            }
            e.Graphics.Clear(Surface);
            using (Pen outline = new Pen(Border))
                e.Graphics.DrawRectangle(outline, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                Color.FromArgb(124, 146, 178),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    private static string FindBackend()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string standard = Path.Combine(local, "Programs", "InDe", "InDeBackend");
        string profile = Environment.GetEnvironmentVariable("USERPROFILE");
        string fallback = String.IsNullOrEmpty(profile) ? null
            : Path.Combine(profile, "AppData", "Local", "Programs", "InDe", "InDeBackend");
        foreach (string candidate in new string[] {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "InDeBackend"),
            standard, BackendInstaller.ManagedPath, fallback
        })
            if (BackendInstaller.IsUsable(candidate)) return candidate;
        return BackendInstaller.ManagedPath;
    }

    private static bool IsImage(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".tif" || extension == ".tiff" || extension == ".png";
    }

    private void AcceptImageDrag(object sender, DragEventArgs e)
    {
        string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (process == null && setupCancellation == null && files != null && files.Length == 1 && IsImage(files[0]))
            e.Effect = DragDropEffects.Copy;
    }

    private void DropImage(object sender, DragEventArgs e)
    {
        string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (process == null && setupCancellation == null && files != null && files.Length == 1 && IsImage(files[0]))
            SelectImage(files[0]);
    }

    private void PickImage(object sender, EventArgs e)
    {
        using (OpenFileDialog dialog = new OpenFileDialog { Filter = "星空图像 (*.tif;*.tiff;*.png)|*.tif;*.tiff;*.png", CheckFileExists = true })
            if (dialog.ShowDialog(this) == DialogResult.OK) SelectImage(dialog.FileName);
    }

    private void SelectImage(string path)
    {
        if (input.Text == path) QueuePreview();
        else input.Text = path;
    }

    private void ClearPreviewImage()
    {
        Image old = inputPreview.Image;
        inputPreview.Image = null;
        if (old != null) old.Dispose();
    }

    private void QueuePreview()
    {
        previewVersion++;
        previewTimer.Stop();
        StopPreviewProcess();
        ClearPreviewImage();
        previewSize.Text = "";
        previewMessage.Visible = true;
        string path = input.Text.Trim();
        bool valid = File.Exists(path) && IsImage(path);
        bool ready = BackendInstaller.IsUsable(backend.Text.Trim());
        previewMessage.Text = !ready ? "下载或选择官方后端后可预览"
            : valid ? "正在读取原图…" : "选择或拖入 TIFF／PNG 后预览";
        if (valid && ready) previewTimer.Start();
    }

    private void StopPreviewProcess()
    {
        if (previewProcess == null) return;
        try
        {
            if (!previewProcess.HasExited) previewProcess.Kill();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async Task LoadInputPreview()
    {
        int version = previewVersion;
        Process reader = null;
        try
        {
            string executable = Path.Combine(backend.Text.Trim(), "runtime", "python.exe");
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "process_inde.py");
            if (!File.Exists(executable) || !File.Exists(script))
                throw new FileNotFoundException();
            reader = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable, Arguments = "-B " + Quote(script) + " --preview-input " + Quote(input.Text.Trim()),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            } };
            reader.Start();
            previewProcess = reader;
            using (MemoryStream payload = new MemoryStream())
            {
                Task transfer = reader.StandardOutput.BaseStream.CopyToAsync(payload);
                Task<string> diagnostics = reader.StandardError.ReadToEndAsync();
                await Task.WhenAll(transfer, diagnostics);
                if (version != previewVersion || IsDisposed) return;
                reader.WaitForExit();
                if (reader.ExitCode != 0 || payload.Length <= 8)
                    throw new IOException("原图无法读取");
                payload.Position = 0;
                using (BinaryReader header = new BinaryReader(payload))
                {
                    int width = header.ReadInt32();
                    int height = header.ReadInt32();
                    using (MemoryStream png = new MemoryStream(payload.GetBuffer(), 8, (int)payload.Length - 8, false))
                    using (Image image = Image.FromStream(png, true))
                        inputPreview.Image = new Bitmap(image);
                    previewSize.Text = width + " × " + height + " 像素";
                    previewMessage.Visible = false;
                }
            }
        }
        catch (Exception)
        {
            if (version == previewVersion && !IsDisposed)
                previewMessage.Text = "无法预览原图，请检查文件及 InDe 后端";
        }
        finally
        {
            if (previewProcess == reader) previewProcess = null;
            if (reader != null) reader.Dispose();
        }
    }

    private void PickOutput(object sender, EventArgs e) { PickDirectory(output, "选择保存结果的父文件夹"); }
    private void PickBackend(object sender, EventArgs e) { PickDirectory(backend, "选择含有 runtime 和 engine 的 InDeBackend 文件夹"); }

    private async Task DownloadBackend()
    {
        string destination = BackendInstaller.ManagedPath;
        if (BackendInstaller.IsUsable(destination))
        {
            backend.Text = destination;
            status.Text = "已选用下载好的官方后端。";
            return;
        }
        if (MessageBox.Show(this, "从 Indigo2233 的 GitHub Release 下载约 405 MB 的官方压缩包，"
            + "校验后只解压 InDe 后端。是否继续？", "下载官方后端",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        using (CancellationTokenSource source = new CancellationTokenSource())
        {
            setupCancellation = source;
            SetBusy(true);
            progress.Value = 0;
            status.ForeColor = Muted;
            status.Text = "正在连接原作者 GitHub Release…";
            var report = new Progress<int>(percentage => {
                if (setupCancellation != source || source.IsCancellationRequested) return;
                progress.Value = percentage < 0 ? 100 : percentage;
                status.Text = percentage < 0 ? "正在校验并解压官方后端…" : "正在下载官方后端 · " + percentage + "%";
            });
            try
            {
                await BackendInstaller.InstallAsync(destination, report, source.Token);
                backend.Text = destination;
                QueuePreview();
                progress.Value = 100;
                status.ForeColor = Mint;
                status.Text = "官方后端已就绪，可以开始处理。";
            }
            catch (Exception error)
            {
                progress.Value = 0;
                if (source.IsCancellationRequested) status.Text = "已取消后端下载。";
                else
                {
                    status.ForeColor = Color.FromArgb(255, 170, 155);
                    status.Text = "后端下载失败。可重试，或手动下载后选择后端。";
                    log.AppendText(error.Message + Environment.NewLine);
                }
            }
            finally
            {
                setupCancellation = null;
                SetBusy(false);
                if (closeWhenFinished) Close();
            }
        }
    }

    private void PickDirectory(TextBox target, string description)
    {
        using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = description })
        {
            if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
        }
    }

    private static string Quote(string value)
    {
        return "\"" + Regex.Replace(value, "(\\\\*)(\"|$)", match =>
            new string('\\', match.Groups[1].Length * 2) +
            (match.Groups[2].Length == 0 ? "" : "\\\"")) + "\"";
    }

    private void StartProcessing()
    {
        try
        {
            string image = input.Text.Trim();
            string parent = output.Text.Trim();
            string backendPath = backend.Text.Trim();
            if (!File.Exists(image) || !IsImage(image)) throw new ArgumentException("请选择一张可读取的 TIFF 或 PNG。");
            if (parent.Length == 0) throw new ArgumentException("请选择输出文件夹。");
            if (backendPath.Length == 0) throw new ArgumentException("请选择 InDeBackend 文件夹。");
            string python = Path.Combine(Path.GetFullPath(backendPath), "runtime", "python.exe");
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "process_inde.py");
            if (!BackendInstaller.IsUsable(backendPath))
                throw new FileNotFoundException("找不到完整的 InDeBackend。请点击「下载官方后端」或选择已有后端。");
            if (!File.Exists(script)) throw new FileNotFoundException("请把 process_inde.py 和程序放在同一目录。");

            string name = Path.GetFileNameWithoutExtension(image);
            string suffix = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string destination = Path.Combine(Path.GetFullPath(parent), name + "_InDe_" + suffix);
            for (int i = 2; Directory.Exists(destination) || File.Exists(destination); i++)
                destination = Path.Combine(Path.GetFullPath(parent), name + "_InDe_" + suffix + "_" + i);
            string args = "-B -u " + Quote(script) + " " + Quote(Path.GetFullPath(image)) + " --desktop"
                + " --output " + Quote(destination) + " --backend " + Quote(Path.GetFullPath(backendPath))
                + " --ss " + Value(sharpening) + " --ash " + Value(halos)
                + " --dn " + Value(noise) + " --iterations " + Value(iterations);
            if (separateColor.Checked) args += " --color " + Value(color);

            Process candidate = new Process { StartInfo = new ProcessStartInfo {
                FileName = python, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            } };
            candidate.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            candidate.OutputDataReceived += Collect;
            candidate.ErrorDataReceived += Collect;
            candidate.Start();
            process = candidate;
            candidate.BeginOutputReadLine();
            candidate.BeginErrorReadLine();
            resultPath = destination;
            cancelled = false;
            log.Clear();
            progress.Value = 0;
            status.Text = "正在读取图像…";
            status.ForeColor = Muted;
            stageStart = 0;
            stageEnd = 0;
            EnableResults(false);
            SetBusy(true);
            timer.Start();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法开始", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string Value(NumericUpDown control) { return control.Value.ToString(CultureInfo.InvariantCulture); }

    private void Collect(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null) messages.Enqueue(e.Data);
    }

    private void ReadProgress()
    {
        DrainLog();
        if (!process.HasExited) return;
        process.WaitForExit();
        DrainLog();
        int exitCode = process.ExitCode;
        process.Dispose();
        process = null;
        timer.Stop();
        SetBusy(false);
        bool complete = exitCode == 0 && File.Exists(Path.Combine(resultPath, CompletionFile));
        EnableResults(complete);
        if (complete)
        {
            progress.Value = 100;
            status.ForeColor = Mint;
            status.Text = "处理完成。可以打开合成图或查看对比。";
        }
        else
        {
            status.ForeColor = cancelled ? Muted : Color.FromArgb(255, 170, 155);
            status.Text = cancelled ? "处理已取消。" : "处理失败，请查看下方日志。";
        }
        if (closeWhenFinished) Close();
    }

    private void DrainLog()
    {
        string line;
        while (messages.TryDequeue(out line))
        {
            switch (line)
            {
                case "@@stage conv": ShowStage("反卷积与星点锐化", 2, 32); break;
                case "@@stage stars": ShowStage("分离星点和背景", 34, 62); break;
                case "@@stage noise": ShowStage("无星层降噪", 64, 91); break;
                case "@@stage export": ShowStage("合成并保存 TIFF", 93, 99); break;
                default:
                    Match tiles = Regex.Match(line, @"^\s*(\d+)/(\d+)\s+块");
                    if (tiles.Success)
                    {
                        int done = Int32.Parse(tiles.Groups[1].Value);
                        int total = Int32.Parse(tiles.Groups[2].Value);
                        progress.Value = stageStart + (stageEnd - stageStart) * done / total;
                    }
                    log.AppendText(line + Environment.NewLine);
                    break;
            }
        }
    }

    private void ShowStage(string label, int begin, int end)
    {
        if (!cancelled) status.Text = "正在" + label + "…";
        stageStart = begin;
        stageEnd = end;
        progress.Value = begin;
    }

    private void SetBusy(bool busy)
    {
        start.Enabled = !busy;
        cancel.Enabled = busy;
        downloadBackend.Enabled = !busy;
        foreach (Control control in new Control[] {
            input.Parent, output.Parent, backend.Parent, sharpening.Parent.Parent, separateColor.Parent
        }) control.Enabled = !busy;
        color.Enabled = !busy && separateColor.Checked;
    }

    private void EnableResults(bool enabled)
    {
        result.Enabled = enabled;
        comparison.Enabled = enabled;
        folder.Enabled = enabled;
    }

    private void CancelProcessing()
    {
        if (setupCancellation != null)
        {
            setupCancellation.Cancel();
            cancel.Enabled = false;
            status.Text = "正在取消后端下载…";
            return;
        }
        if (process == null || process.HasExited || cancelled) return;
        try
        {
            process.StandardInput.WriteLine("cancel");
            process.StandardInput.Flush();
            cancelled = true;
            cancel.Enabled = false;
            status.Text = "将在当前阶段结束后取消…";
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private void OpenResult(string name)
    {
        try
        {
            string path = name.Length == 0 ? resultPath : Path.Combine(resultPath, name);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法打开结果", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
