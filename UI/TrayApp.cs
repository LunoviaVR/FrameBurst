using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using FrameBurst.Capture;
using FrameBurst.Win32;

namespace FrameBurst.UI;

internal enum CaptureMode { Region, AllMonitors, Monitor, ActiveWindow }

internal sealed class TrayApp : ApplicationContext
{
    private static Icon? _icon;
    public static Icon AppIcon => _icon ??= CreateIcon();

    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys;
    private readonly DesktopCapturer _capturer = new();
    private Settings _settings;
    private bool _busy;
    private string? _lastFile;
    private SettingsForm? _settingsForm;

    public TrayApp()
    {
        _settings = Settings.Load();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Capture region", null, (_, _) => Trigger(CaptureMode.Region, fromMenu: true));
        menu.Items.Add("Capture all monitors", null, (_, _) => Trigger(CaptureMode.AllMonitors, fromMenu: true));
        menu.Items.Add("Capture monitor under cursor", null, (_, _) => Trigger(CaptureMode.Monitor, fromMenu: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pick screen colour", null, (_, _) => PickScreenColor());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open screenshot folder", null, (_, _) => OpenFolder());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { Icon = AppIcon, Text = "FrameBurst", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => OpenLast();

        _hotkeys = new HotkeyWindow(OnHotkey);
        RegisterHotkeys(showErrors: true);

        // Compile shaders and create per-GPU devices in the background so the first capture is instant.
        Task.Run(() => { try { _capturer.WarmUp(); } catch { /* reported on first capture */ } });
    }

    private void RegisterHotkeys(bool showErrors)
    {
        var failed = _hotkeys.Register(new[]
        {
            (CaptureMode.Region, _settings.RegionHotkey),
            (CaptureMode.AllMonitors, _settings.FullscreenHotkey),
            (CaptureMode.Monitor, _settings.MonitorHotkey),
            (CaptureMode.ActiveWindow, _settings.WindowHotkey),
        });
        Log.Write("hotkeys registered; failed: " + (failed.Count == 0 ? "none" : string.Join(", ", failed)));
        if (showErrors && failed.Count > 0)
            _tray.ShowBalloonTip(6000, "Some hotkeys are in use",
                string.Join(", ", failed) + " could not be registered. Another app (or Windows' Snipping Tool PrintScreen setting) owns them. Change them in Settings.",
                ToolTipIcon.Warning);
        _tray.Text = "FrameBurst — " + (_settings.RegionHotkey.IsEmpty ? "right-click to capture" : $"{_settings.RegionHotkey} to capture");
    }

    private void OnHotkey(CaptureMode mode) => Trigger(mode, fromMenu: false);

    private async void Trigger(CaptureMode mode, bool fromMenu)
    {
        if (_busy) { Log.Write($"ignored {mode}: previous capture still running"); return; }
        _busy = true;
        Log.Write($"trigger {mode} fromMenu={fromMenu}");
        try
        {
            // Snapshot what's under the user's eyes *before* anything of ours appears.
            IntPtr foreground = Native.GetForegroundWindow();
            int delay = _settings.CaptureDelayMs + (fromMenu ? 250 : 0); // let the tray menu fade out
            if (delay > 0) await Task.Delay(delay);
            if (fromMenu) foreground = Native.GetForegroundWindow();

            var cursor = _settings.CaptureCursor ? ImageOutput.GrabCursor() : null;
            var windows = mode == CaptureMode.Region ? Native.EnumerateVisibleWindows(IntPtr.Zero) : new();
            Native.GetCursorPos(out var cursorPos);

            var settings = _settings;
            var set = await Task.Run(() => _capturer.CaptureAll());
            set.Cursor = cursor;

            byte[]? annotated = null;
            Rectangle? area = mode switch
            {
                CaptureMode.AllMonitors => set.VirtualBounds,
                CaptureMode.Monitor => set.Monitors.FirstOrDefault(m => m.Bounds.Contains(cursorPos.X, cursorPos.Y))?.Bounds ?? set.VirtualBounds,
                CaptureMode.ActiveWindow => foreground != IntPtr.Zero ? Native.GetWindowBounds(foreground) : set.VirtualBounds,
                _ => SelectRegion(set, windows, out annotated),
            };
            Log.Write($"area={area?.ToString() ?? "cancelled"} capture: {set.Timings}");
            if (area is not { } rect || rect.Width <= 0 || rect.Height <= 0) return;

            var sw = Stopwatch.StartNew();
            var saved = await Task.Run(() => ImageOutput.Produce(set, rect, settings, annotated));
            if (settings.CopyToClipboard) ImageOutput.CopyToClipboard(saved);
            _lastFile = saved.FilePath;

            if (settings.ShowNotification)
            {
                var r = saved.Area;
                string where = saved.FilePath != null ? Path.GetFileName(saved.FilePath) : "Copied to clipboard";
                var gpus = string.Join(", ", set.Monitors.Where(m => m.Bounds.IntersectsWith(r)).Select(m => GpuVendors.Label(m.Vendor)).Distinct());
                _tray.ShowBalloonTip(3000, $"Captured {r.Width} × {r.Height}  ·  {gpus} GPU",
                    $"{where}\nGPU capture {set.Timings.TotalMs:0} ms · encode {saved.EncodeMs:0} ms", ToolTipIcon.None);
            }
            if (set.Cursor is { } c) Native.DestroyIcon(c.Handle);
        }
        catch (Exception ex)
        {
            Log.Write("capture failed: " + ex);
            _tray.ShowBalloonTip(6000, "Capture failed", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private static Rectangle? SelectRegion(CaptureSet set, List<(IntPtr, Rectangle)> windows, out byte[]? annotated)
    {
        var full = set.ComposeBgra(set.VirtualBounds);
        using var overlay = new RegionSelector(set, full, windows);
        bool ok = overlay.ShowDialog() == DialogResult.OK;
        annotated = ok ? overlay.AnnotatedBgra : null;
        return ok ? overlay.Result : null;
    }

    private void ShowSettings()
    {
        if (_settingsForm != null) { _settingsForm.Activate(); return; }
        _hotkeys.UnregisterAll(); // so the hotkey boxes can receive the keys
        using (_settingsForm = new SettingsForm(_settings, _capturer))
        {
            if (_settingsForm.ShowDialog() == DialogResult.OK)
            {
                _settings = _settingsForm.Result;
                try { _settings.Save(); }
                catch (Exception ex) { MessageBox.Show("Could not save settings: " + ex.Message, "FrameBurst"); }
            }
        }
        _settingsForm = null;
        RegisterHotkeys(showErrors: true);
    }

    /// <summary>Tray tool: freeze the screen, let the user click a pixel, copy its #RRGGBB to the clipboard.</summary>
    private async void PickScreenColor()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await Task.Delay(250); // let the tray menu fade out so it is not part of the frozen screen
            var set = await Task.Run(() => _capturer.CaptureAll());
            using var picker = new ColorPicker(set);
            if (picker.ShowDialog() != DialogResult.OK || picker.Picked is not { } color) return;
            string hex = ColorPicker.Hex(color);
            Clipboard.SetText(hex);
            Log.Write($"colour picked {hex}");
            if (_settings.ShowNotification)
                _tray.ShowBalloonTip(2500, $"Copied {hex}", $"rgb({color.R}, {color.G}, {color.B}) is on the clipboard.", ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            Log.Write("colour pick failed: " + ex);
            _tray.ShowBalloonTip(5000, "Colour picker failed", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.OutputFolder}\"") { UseShellExecute = true });
    }

    private void OpenLast()
    {
        if (_lastFile != null && File.Exists(_lastFile))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFile}\"") { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _hotkeys.UnregisterAll();
        _hotkeys.DestroyHandle();
        _tray.Visible = false;
        _tray.Dispose();
        _capturer.Dispose();
        base.ExitThreadCore();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(0, 190, 255), Color.FromArgb(120, 60, 255), 45f);
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90); path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90); path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var pen = new Pen(Color.White, 2.5f);
            g.DrawLines(pen, new[] { new Point(7, 13), new Point(7, 7), new Point(13, 7) });
            g.DrawLines(pen, new[] { new Point(19, 25), new Point(25, 25), new Point(25, 19) });
            g.FillEllipse(Brushes.White, 12, 12, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Hidden message-only window that receives WM_HOTKEY.</summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action<CaptureMode> _callback;
        private readonly List<int> _ids = new();

        public HotkeyWindow(Action<CaptureMode> callback)
        {
            _callback = callback;
            CreateHandle(new CreateParams { Caption = "FrameBurstHotkeys", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
        }

        public List<string> Register(IEnumerable<(CaptureMode Mode, Hotkey Key)> keys)
        {
            UnregisterAll();
            var failed = new List<string>();
            foreach (var (mode, hk) in keys)
            {
                if (hk.IsEmpty) continue;
                uint mods = Native.MOD_NOREPEAT;
                if (hk.Modifiers.HasFlag(Keys.Control)) mods |= Native.MOD_CONTROL;
                if (hk.Modifiers.HasFlag(Keys.Alt)) mods |= Native.MOD_ALT;
                if (hk.Modifiers.HasFlag(Keys.Shift)) mods |= Native.MOD_SHIFT;
                if (hk.Modifiers.HasFlag(Keys.LWin)) mods |= Native.MOD_WIN;
                int id = (int)mode + 1;
                if (Native.RegisterHotKey(Handle, id, mods, (uint)hk.Key)) _ids.Add(id);
                else failed.Add(hk.ToString());
            }
            return failed;
        }

        public void UnregisterAll()
        {
            foreach (var id in _ids) Native.UnregisterHotKey(Handle, id);
            _ids.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = (int)m.WParam;
                Log.Write($"WM_HOTKEY id={id}");
                if (id is >= 1 and <= 4) _callback((CaptureMode)(id - 1));
            }
            base.WndProc(ref m);
        }
    }
}
