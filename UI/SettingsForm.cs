using System.Diagnostics;
using System.Text;
using FrameBurst.Capture;

namespace FrameBurst.UI;

internal sealed class SettingsForm : Form
{
    private readonly Settings _s;
    private readonly DesktopCapturer _capturer;

    private readonly HotkeyBox _hkRegion = new(), _hkFull = new(), _hkMonitor = new(), _hkWindow = new();
    private readonly NumericUpDown _delay = new() { Maximum = 10000, Increment = 250, Width = 90 };
    private readonly CheckBox _cursor = new() { Text = "Include mouse cursor", AutoSize = true };

    private readonly TextBox _folder = new() { Width = 340 };
    private readonly TextBox _pattern = new() { Width = 340 };
    private readonly CheckBox _save = new() { Text = "Save to file", AutoSize = true };
    private readonly CheckBox _clip = new() { Text = "Copy to clipboard", AutoSize = true };
    private readonly CheckBox _notify = new() { Text = "Show notification", AutoSize = true };
    private readonly ComboBox _png = Combo("Fast (largest files)", "Balanced", "Smallest (slowest)");


    private readonly TextBox _gpuInfo = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };

    public Settings Result => _s;

    public SettingsForm(Settings settings, DesktopCapturer capturer)
    {
        _s = settings.Clone();
        _capturer = capturer;

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "FrameBurst Settings";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 480);
        Icon = TrayApp.AppIcon;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Capture", Grid(
            ("Region", _hkRegion), ("All monitors", _hkFull), ("Monitor under cursor", _hkMonitor), ("Active window", _hkWindow),
            ("Delay (ms)", _delay), ("", _cursor),
            ("", Note("Click a box and press a key combination. Backspace clears it.\nTip: if PrintScreen won't register, turn off \"Use the Print screen key to open screen capture\" in Windows Settings › Accessibility › Keyboard.")))));

        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text, UseDescriptionForTitle = true, Description = "Screenshot folder" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
        };
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        folderRow.Controls.AddRange(new Control[] { _folder, browse });

        tabs.TabPages.Add(Page("Output", Grid(
            ("Folder", folderRow), ("File name", _pattern), ("", Note("Text in {braces} is a .NET date format, e.g. {yyyy-MM-dd_HH-mm-ss}.")),
            ("", _save), ("", _clip), ("", _notify),
            ("PNG compression", _png), ("", Note("PNG is lossless at every setting: this only trades file size for speed.")))));

        var gpuPage = new TabPage("GPU") { Padding = new Padding(8) };
        var gpuButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var bench = new Button { Text = "Benchmark capture (10×)", AutoSize = true };
        refresh.Click += (_, _) => LoadGpuInfo();
        bench.Click += async (_, _) => await Benchmark(bench);
        gpuButtons.Controls.AddRange(new Control[] { refresh, bench });
        gpuPage.Controls.Add(_gpuInfo);
        gpuPage.Controls.Add(gpuButtons);
        tabs.TabPages.Add(gpuPage);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        bottom.Controls.AddRange(new Control[] { cancel, ok });
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Commit();

        Controls.Add(tabs);
        Controls.Add(bottom);

        LoadValues();
        LoadGpuInfo();
    }

    private static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
        c.Items.AddRange(items);
        return c;
    }

    private static Label Note(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8) };

    private static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { Padding = new Padding(10), AutoScroll = true };
        p.Controls.Add(content);
        return p;
    }

    private static TableLayoutPanel Grid(params (string Label, Control Control)[] rows)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (label, control) in rows)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3) });
            if (control is HotkeyBox hk) hk.Width = 200;
            t.Controls.Add(control);
        }
        return t;
    }

    private void LoadValues()
    {
        _hkRegion.Value = _s.RegionHotkey; _hkFull.Value = _s.FullscreenHotkey; _hkMonitor.Value = _s.MonitorHotkey; _hkWindow.Value = _s.WindowHotkey;
        _delay.Value = Math.Clamp(_s.CaptureDelayMs, 0, 10000);
        _cursor.Checked = _s.CaptureCursor;
        _folder.Text = _s.OutputFolder; _pattern.Text = _s.FileNamePattern;
        _save.Checked = _s.SaveToFile; _clip.Checked = _s.CopyToClipboard; _notify.Checked = _s.ShowNotification;
        _png.SelectedIndex = (int)_s.PngCompression;
    }

    private void Commit()
    {
        _s.RegionHotkey = _hkRegion.Value; _s.FullscreenHotkey = _hkFull.Value; _s.MonitorHotkey = _hkMonitor.Value; _s.WindowHotkey = _hkWindow.Value;
        _s.CaptureDelayMs = (int)_delay.Value;
        _s.CaptureCursor = _cursor.Checked;
        _s.OutputFolder = _folder.Text.Trim(); _s.FileNamePattern = _pattern.Text.Trim();
        _s.SaveToFile = _save.Checked; _s.CopyToClipboard = _clip.Checked; _s.ShowNotification = _notify.Checked;
        _s.PngCompression = (PngCompression)_png.SelectedIndex;
    }

    private void LoadGpuInfo()
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var a in DesktopCapturer.EnumerateAdapters())
            {
                sb.AppendLine($"[{GpuVendors.Label(a.Vendor)}] {a.Name}");
                sb.AppendLine($"    PCI {a.VendorId:X4}:{a.DeviceId:X4}   VRAM {a.VramBytes / (1024.0 * 1024 * 1024):0.0} GB");
                if (a.Outputs.Count == 0) sb.AppendLine("    (no displays attached — not used for capture)");
                foreach (var o in a.Outputs)
                {
                    sb.AppendLine($"    {o.DeviceName}  {o.Bounds.Width}×{o.Bounds.Height} @ ({o.Bounds.X},{o.Bounds.Y})  rot {o.Rotation}");
                    sb.AppendLine($"        {o.BitsPerColor} bits per colour");
                }
                sb.AppendLine();
            }
            sb.AppendLine("Each display is captured by the GPU that drives it (DXGI Desktop Duplication), and");
            sb.AppendLine("converted there by D3D11 compute shaders — NVIDIA, AMD and Intel are all supported.");
        }
        catch (Exception ex) { sb.AppendLine("Failed to enumerate GPUs: " + ex.Message); }
        _gpuInfo.Text = sb.ToString();
    }

    private async Task Benchmark(Button b)
    {
        b.Enabled = false;
        try
        {
            var runs = await Task.Run(() =>
            {
                var list = new List<CaptureTimings>();
                _capturer.CaptureAll(); // warm-up
                for (int i = 0; i < 10; i++) list.Add(_capturer.CaptureAll().Timings);
                return list;
            });
            var set = await Task.Run(() => _capturer.CaptureAll());
            var sb = new StringBuilder(_gpuInfo.Text);
            sb.AppendLine();
            sb.AppendLine($"Benchmark — {set.Monitors.Count} display(s), {set.Monitors.Sum(m => (long)m.Bounds.Width * m.Bounds.Height) / 1e6:0.0} MP");
            foreach (var m in set.Monitors)
                sb.AppendLine($"    {m.DeviceName}: {GpuVendors.Label(m.Vendor)} {m.GpuName}");
            sb.AppendLine($"    avg total  {runs.Average(r => r.TotalMs):0.0} ms   (min {runs.Min(r => r.TotalMs):0.0})");
            sb.AppendLine($"    avg acquire {runs.Average(r => r.AcquireMs):0.0} ms,  GPU pass {runs.Average(r => r.GpuMs):0.0} ms,  readback {runs.Average(r => r.ReadbackMs):0.0} ms");
            var shader = runs.Where(r => !double.IsNaN(r.GpuShaderMs)).ToList();
            if (shader.Count > 0) sb.AppendLine($"    GPU shader execution (timestamp queries): {shader.Average(r => r.GpuShaderMs):0.000} ms");
            _gpuInfo.Text = sb.ToString();
            _gpuInfo.SelectionStart = _gpuInfo.TextLength;
            _gpuInfo.ScrollToCaret();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Benchmark failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            b.Enabled = true;
        }
    }
}
