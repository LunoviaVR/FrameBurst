using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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
    private SettingsWindow? _settingsWindow;
    private TrayMenu? _menu;
    // A left click on the tray icon captures a region after this delay.
    private readonly System.Windows.Forms.Timer _clickTimer = new() { Interval = 1000 };
    // Automatic update checks: shortly after start-up, then every few hours (at most once a day hits GitHub).
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 15_000 };
    private Action? _balloonAction;
    private bool _updating;

    public TrayApp()
    {
        _settings = Settings.Load();

        _tray = new NotifyIcon { Icon = AppIcon, Text = "FrameBurst", Visible = true };
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(); };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { _clickTimer.Stop(); _clickTimer.Start(); } };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); Trigger(CaptureMode.Region, fromMenu: true); };
        _tray.BalloonTipClicked += (_, _) => _balloonAction?.Invoke();
        _tray.BalloonTipClosed += (_, _) => _balloonAction = null;
        _updateTimer.Tick += (_, _) => { _updateTimer.Interval = 4 * 60 * 60 * 1000; CheckForUpdates(manual: false); };
        _updateTimer.Start();

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
            var set = await Task.Run(() => _capturer.CaptureAll(settings.SaveHdr && settings.SaveToFile));
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
                bool gdi = set.Backend == CaptureBackend.Gdi;
                var gpus = gdi ? "GDI" : string.Join(", ", set.Monitors.Where(m => m.Bounds.IntersectsWith(r)).Select(m => GpuVendors.Label(m.Vendor)).Distinct()) + " GPU";
                _balloonAction = OpenLast;
                _tray.ShowBalloonTip(3000, $"Captured {r.Width} × {r.Height}  ·  {gpus}",
                    $"{where}\n{(gdi ? "GDI" : "GPU")} capture {set.Timings.TotalMs:0} ms · encode {saved.EncodeMs:0} ms", ToolTipIcon.None);
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

    private void ShowMenu()
    {
        static string? Key(Hotkey hk) => hk.IsEmpty ? null : hk.ToString();
        var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        flyout.Items.Add(TrayMenu.Item("Capture region", "", () => Trigger(CaptureMode.Region, fromMenu: true), Key(_settings.RegionHotkey)));
        flyout.Items.Add(TrayMenu.Item("Capture all monitors", "", () => Trigger(CaptureMode.AllMonitors, fromMenu: true), Key(_settings.FullscreenHotkey)));
        flyout.Items.Add(TrayMenu.Item("Capture monitor under cursor", "", () => Trigger(CaptureMode.Monitor, fromMenu: true), Key(_settings.MonitorHotkey)));
        flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        flyout.Items.Add(TrayMenu.Item("Pick screen colour", "", PickScreenColor));
        flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        flyout.Items.Add(TrayMenu.Item("Open screenshot folder", "", OpenFolder));
        flyout.Items.Add(TrayMenu.Item("Settings", "", ShowSettings));
        flyout.Items.Add(TrayMenu.Item("Check for updates", "", () => { ShowSettings(); _settingsWindow?.ShowAbout(checkNow: true); }));
        flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        flyout.Items.Add(TrayMenu.Item("Exit", "", ExitThread));
        (_menu ??= new TrayMenu()).Show(flyout);
    }

    private void ShowSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.BringToFront(); return; }
        _hotkeys.UnregisterAll(); // so the hotkey boxes can receive the keys
        _settingsWindow = new SettingsWindow(_settings, ExitThread);
        _settingsWindow.Saved += s => _settings = s;
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            RegisterHotkeys(showErrors: true);
        };
        _settingsWindow.BringToFront();
    }

    private async void CheckForUpdates(bool manual)
    {
        if (_updating) return;
        if (!manual && (!_settings.CheckForUpdates || DateTime.UtcNow - _settings.LastUpdateCheckUtc < TimeSpan.FromHours(24))) return;
        _updating = true;
        UpdateInfo? found = null;
        try
        {
            UpdateInfo? update;
            try { update = await Updater.CheckAsync(); }
            catch (Exception ex)
            {
                Log.Write("update check failed: " + ex.Message);
                if (manual) _ = MessageDialog.Show("Couldn't check for updates", ex.Message, DialogKind.Warning);
                return;
            }
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            try { _settings.Save(); } catch { /* not important */ }

            if (update == null)
            {
                if (manual) _ = MessageDialog.Show("You're up to date", $"FrameBurst {Updater.Current} is the latest version.");
                return;
            }
            if (manual) { found = update; return; }
            _balloonAction = () => PromptUpdate(update);
            _tray.ShowBalloonTip(10000, $"FrameBurst {update.Version} is available", "Click here to update.", ToolTipIcon.Info);
        }
        finally { _updating = false; }
        if (found != null) PromptUpdate(found);
    }

    private async void PromptUpdate(UpdateInfo update)
    {
        if (_updating) return;
        _updating = true; // also keeps a second prompt from opening while this one is shown
        string notes = update.Notes.Length > 1200 ? update.Notes[..1200] + "…" : update.Notes;
        string heading = $"FrameBurst {update.Version} is available";
        if (!Updater.IsInstalled || update.InstallerUrl == null)
        {
            // A copy run from a build folder isn't managed by the installer, so just show the release.
            bool open = await MessageDialog.Ask(heading, $"You have {Updater.Current}.\n\n{notes}", "Open download page", title: "FrameBurst update");
            _updating = false;
            if (open) Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
            return;
        }
        if (!await MessageDialog.Ask(heading, $"You have {Updater.Current}.\n\n{notes}\n\nFrameBurst will restart to finish installing.",
                "Install now", title: "FrameBurst update"))
        {
            _updating = false;
            return;
        }

        _tray.ShowBalloonTip(3000, "Downloading update…", $"FrameBurst {update.Version}", ToolTipIcon.None);
        try
        {
            await Updater.InstallAsync(update);
            ExitThread(); // the installer replaces the files and restarts FrameBurst
        }
        catch (Exception ex)
        {
            Log.Write("update failed: " + ex);
            _ = MessageDialog.Show("The update failed", ex.Message, DialogKind.Warning);
        }
        finally { _updating = false; }
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
        _clickTimer.Dispose();
        _updateTimer.Dispose();
        _tray.Dispose();
        _capturer.Dispose();
        _settingsWindow?.Close();
        _menu?.Close();
        base.ExitThreadCore();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private static Icon CreateIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("FrameBurst.FrameBurst.ico")!;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    /// <summary>Hidden message-only window that receives WM_HOTKEY.</summary>
    /// <summary>
    /// Hidden message-only window that receives WM_HOTKEY, plus a low-level keyboard hook. Full-screen games often
    /// read the keyboard through raw input with hotkeys disabled, or grab the key first, so WM_HOTKEY never arrives.
    /// The hook sees keys before any application does, so the capture hotkeys keep working on top of them.
    /// </summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        private const int WM_HOOK_HOTKEY = 0x8001; // WM_APP + 1

        private readonly Action<CaptureMode> _callback;
        private readonly List<int> _ids = new();
        private readonly List<(int Id, uint Vk, Keys Mods)> _keys = new();
        private readonly Native.LowLevelKeyboardProc _proc; // kept alive for as long as the hook is installed
        private IntPtr _hook;
        private uint _swallowedVk; // key whose press we consumed; its repeats and release are consumed too

        public HotkeyWindow(Action<CaptureMode> callback)
        {
            _callback = callback;
            CreateHandle(new CreateParams { Caption = "FrameBurstHotkeys", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
            _proc = HookProc;
            _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
            Log.Write(_hook != IntPtr.Zero ? "keyboard hook installed" : $"keyboard hook failed: {Marshal.GetLastWin32Error()}");
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
                _keys.Add((id, (uint)hk.Key, hk.Modifiers & (Keys.Control | Keys.Alt | Keys.Shift | Keys.LWin)));
                // The hook handles the key even when another app has registered the same combination.
                if (Native.RegisterHotKey(Handle, id, mods, (uint)hk.Key)) _ids.Add(id);
                else if (_hook == IntPtr.Zero) failed.Add(hk.ToString());
            }
            return failed;
        }

        public void UnregisterAll()
        {
            foreach (var id in _ids) Native.UnregisterHotKey(Handle, id);
            _ids.Clear();
            _keys.Clear();
            _swallowedVk = 0;
        }

        private static Keys CurrentModifiers()
        {
            static bool Down(int vk) => Native.GetAsyncKeyState(vk) < 0;
            var m = Keys.None;
            if (Down(0x11)) m |= Keys.Control;           // VK_CONTROL
            if (Down(0x12)) m |= Keys.Alt;               // VK_MENU
            if (Down(0x10)) m |= Keys.Shift;             // VK_SHIFT
            if (Down(0x5B) || Down(0x5C)) m |= Keys.LWin; // VK_LWIN / VK_RWIN
            return m;
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _keys.Count > 0)
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;
                if (msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
                {
                    if (_swallowedVk != 0 && k.vkCode == _swallowedVk) return 1; // auto-repeat
                    var mods = CurrentModifiers();
                    foreach (var (id, vk, want) in _keys)
                    {
                        if (vk != k.vkCode || want != mods) continue;
                        _swallowedVk = vk;
                        // Return immediately (Windows drops slow hooks); the capture starts from the message loop.
                        Native.PostMessageW(Handle, WM_HOOK_HOTKEY, id, IntPtr.Zero);
                        return 1;
                    }
                }
                else if ((msg is Native.WM_KEYUP or Native.WM_SYSKEYUP) && k.vkCode == _swallowedVk)
                {
                    _swallowedVk = 0;
                    return 1;
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public override void DestroyHandle()
        {
            if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            base.DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg is Native.WM_HOTKEY or WM_HOOK_HOTKEY)
            {
                int id = (int)m.WParam;
                Log.Write($"{(m.Msg == WM_HOOK_HOTKEY ? "hook" : "WM_HOTKEY")} id={id}");
                if (id is >= 1 and <= 4) _callback((CaptureMode)(id - 1));
            }
            base.WndProc(ref m);
        }
    }
}
