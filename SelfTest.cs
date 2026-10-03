using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using FrameBurst.Capture;
using FrameBurst.Imaging;

namespace FrameBurst;

/// <summary>
/// `FrameBurst.exe --selftest [outputDir]` â€” headless end-to-end check: enumerate GPUs, capture every monitor,
/// verify the PNG encoder round-trips bit-exactly, write all output formats and report timings.
/// </summary>
internal static class SelfTest
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Drives the real overlay with window messages (no real mouse/keyboard input): pen, blur, text, picker,
    /// toolbar undo/redo, then a region selection. Checks the result and saves a screenshot of the toolbar.
    /// </summary>
    private static int OverlayUiTest(DesktopCapturer cap, Action<string> W, string dir)
    {
        const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_KEYDOWN = 0x100, WM_CHAR = 0x102;
        var s0 = new Settings();
        var set = cap.CaptureAll();
        var vb = set.VirtualBounds;

        using var ov = new UI.RegionSelector(set, set.ComposeBgra(vb), new());
        int rc = 0;
        int editsAfterUndo = -1, editsAfterRedo = -1;

        IntPtr L(Point p) => (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
        void Move(Point p) => SendMessage(ov.Handle, WM_MOUSEMOVE, IntPtr.Zero, L(p));
        void Down(Point p) { Move(p); SendMessage(ov.Handle, WM_LBUTTONDOWN, (IntPtr)1, L(p)); }
        void Up(Point p) => SendMessage(ov.Handle, WM_LBUTTONUP, IntPtr.Zero, L(p));
        void Key(Keys k) => SendMessage(ov.Handle, WM_KEYDOWN, (IntPtr)(int)k, IntPtr.Zero);
        void Drag(Point a, Point b) { Down(a); for (int i = 1; i <= 10; i++) Move(new Point(a.X + (b.X - a.X) * i / 10, a.Y + (b.Y - a.Y) * i / 10)); Up(b); }
        Point Btn(string tipStart) { var r = ov.ToolbarLayout.First(t => t.Tip.StartsWith(tipStart)).R; return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }

        ov.Shown += async (_, _) =>
        {
            await Task.Delay(200);
            var bar = ov.ToolbarBounds;
            var o = new Point(bar.X + 40, bar.Bottom + 120); // work area just under the toolbar

            Key(Keys.P); Drag(o, new Point(o.X + 260, o.Y + 60));                                     // pen (shortcut)
            Down(Btn("Arrow")); Up(Btn("Arrow")); Drag(new Point(o.X + 300, o.Y), new Point(o.X + 460, o.Y + 90)); // arrow (toolbar click)
            Key(Keys.B); Drag(new Point(o.X, o.Y + 120), new Point(o.X + 300, o.Y + 200));             // blur
            Key(Keys.T); Down(new Point(o.X, o.Y + 240)); Up(new Point(o.X, o.Y + 240));                // text
            var tb = ov.Controls.OfType<TextBox>().FirstOrDefault();
            if (tb != null)
            {
                foreach (char ch in "FrameBurst annotation") SendMessage(tb.Handle, WM_CHAR, (IntPtr)ch, IntPtr.Zero);
                SendMessage(tb.Handle, WM_KEYDOWN, (IntPtr)(int)Keys.Enter, IntPtr.Zero);
            }

            Down(Btn("Undo")); Up(Btn("Undo")); editsAfterUndo = ov.EditCount;
            Down(Btn("Redo")); Up(Btn("Redo")); editsAfterRedo = ov.EditCount;

            Move(new Point(o.X + 200, o.Y + 150));
            await Task.Delay(250);
            var shot = cap.CaptureAll();
            var area = Rectangle.Intersect(new Rectangle(vb.X + bar.X - 40, vb.Y + bar.Y - 10, bar.Width + 80, 560), shot.VirtualBounds);
            using (var bmp = ImageOutput.ToBitmap(shot.ComposeBgra(area), area.Width, area.Height))
                bmp.Save(Path.Combine(dir, "overlay_ui.png"), ImageFormat.Png);

            Key(Keys.S); Drag(new Point(o.X - 20, o.Y - 20), new Point(o.X + 480, o.Y + 300));           // capture region
        };
        ov.ShowDialog();

        W($"uitest: result {ov.Result?.ToString() ?? "none"}, edited image {(ov.AnnotatedBgra != null ? "yes" : "no")}, edits {ov.EditCount}");
        W($"   text box created: {ov.EditCount >= 4}");
        W($"   toolbar undo -> {editsAfterUndo} edits, redo -> {editsAfterRedo} edits");
        if (ov.Result == null || ov.AnnotatedBgra == null) rc = 9;
        if (ov.EditCount != 4) rc = 10;                                    // pen, arrow, blur, text
        if (editsAfterUndo != 3 || editsAfterRedo != 4) rc = 12;

        if (ov.Result is { } res && ov.AnnotatedBgra != null)
        {
            var saved = ImageOutput.Produce(set, res, new Settings { SaveToFile = true, OutputFolder = dir, FileNamePattern = "uitest_capture" }, ov.AnnotatedBgra);
            W($"   saved {Path.GetFileName(saved.FilePath)} {saved.Area.Width}x{saved.Area.Height}");
        }
        W(rc == 0 ? "uitest PASSED" : $"uitest FAILED ({rc})");
        return rc;
    }

    /// <summary>Edits must change only the pixels they cover; blur must actually destroy detail.</summary>
    private static int AnnotationTest(DesktopCapturer cap, Action<string> W)
    {
        var set = cap.CaptureAll();
        var mon = set.Monitors[0];
        int w = mon.Bounds.Width, h = mon.Bounds.Height;
        byte[] basePx = mon.Bgra;
        int rc = 0;

        // Strong synthetic detail inside the blur area so the "detail destroyed" check is meaningful.
        var blurRect = new Rectangle(w / 2, h / 2, 300, 120);
        for (int y = blurRect.Top; y < blurRect.Bottom; y++)
            for (int x = blurRect.Left; x < blurRect.Right; x++)
            {
                int o = (y * w + x) * 4; byte v = ((x / 2 + y / 2) & 1) == 0 ? (byte)0 : (byte)255;
                basePx[o] = basePx[o + 1] = basePx[o + 2] = v; basePx[o + 3] = 255;
            }

        var pen = new UI.StrokeOp(Color.Red, 4, false, new Point(100, 100));
        for (int i = 0; i < 50; i++) pen.Add(new Point(100 + i * 6, 100 + (int)(30 * Math.Sin(i / 5.0))));
        var hl = new UI.StrokeOp(Color.Yellow, 6, true, new Point(100, 300)); hl.Add(new Point(500, 300));
        var arrow = new UI.ArrowOp(Color.Lime, 4, new Point(700, 200)) { To = new Point(900, 350) };
        var box = new UI.RectOp(Color.DeepSkyBlue, 3, new Point(200, 450)) { To = new Point(520, 620) };
        var text = new UI.TextOp("Pixel-perfect test", new Point(600, 500), Color.White, 32);
        var blur = new UI.BlurOp(14, blurRect.Location) { To = new Point(blurRect.Right - 1, blurRect.Bottom - 1) };
        var ops = new UI.AnnotationOp[] { pen, hl, arrow, box, text, blur };

        byte[] Render(IEnumerable<UI.AnnotationOp> list)
        {
            using var canvas = ImageOutput.ToBitmap(basePx, w, h, PixelFormat.Format32bppPArgb);
            foreach (var op in list) op.Apply(canvas);
            var bd = canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var outPx = new byte[w * h * 4];
            for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, outPx, y * w * 4, w * 4);
            canvas.UnlockBits(bd);
            return outPx;
        }

        var edited = Render(ops);
        var mask = new bool[w * h];
        foreach (var op in ops)
        {
            var b = Rectangle.Intersect(op.Bounds, new Rectangle(0, 0, w, h));
            for (int y = b.Top; y < b.Bottom; y++) for (int x = b.Left; x < b.Right; x++) mask[y * w + x] = true;
        }
        long outside = 0, outsideChanged = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (mask[i]) continue;
            outside++;
            int o = i * 4;
            if (edited[o] != basePx[o] || edited[o + 1] != basePx[o + 1] || edited[o + 2] != basePx[o + 2] || edited[o + 3] != basePx[o + 3]) outsideChanged++;
        }
        W($"annotest: {outside:N0} pixels outside the edits, changed: {outsideChanged:N0}");
        if (outsideChanged != 0) rc = 5;

        foreach (var op in ops)
        {
            var b = Rectangle.Intersect(op.Bounds, new Rectangle(0, 0, w, h));
            long changed = 0;
            for (int y = b.Top; y < b.Bottom; y++)
                for (int x = b.Left; x < b.Right; x++)
                {
                    int o = (y * w + x) * 4;
                    if (edited[o] != basePx[o] || edited[o + 1] != basePx[o + 1] || edited[o + 2] != basePx[o + 2]) changed++;
                }
            W($"   {op.GetType().Name,-9} changed {changed:N0} px inside its {b.Width}x{b.Height} bounds");
            if (changed == 0) rc = 6;
        }

        double Sharp(byte[] px, Rectangle r)
        {
            double s = 0, s2 = 0; long n = 0;
            for (int y = r.Top + 1; y < r.Bottom - 1; y++)
                for (int x = r.Left + 1; x < r.Right - 1; x++)
                {
                    double L(int xx, int yy) { int o = (yy * w + xx) * 4; return 0.114 * px[o] + 0.587 * px[o + 1] + 0.299 * px[o + 2]; }
                    double lap = L(x - 1, y) + L(x + 1, y) + L(x, y - 1) + L(x, y + 1) - 4 * L(x, y);
                    s += lap; s2 += lap * lap; n++;
                }
            return s2 / n - (s / n) * (s / n);
        }
        var inner = Rectangle.Inflate(blurRect, -20, -20);
        double before = Sharp(basePx, inner), after = Sharp(edited, inner);
        W($"   blur detail (Laplacian variance) inside area: {before:0} -> {after:0.00} ({(after / before - 1) * 100:0.0}%)");
        if (after > before * 0.01) rc = 7;

        // Undo = rebuild from the untouched frame + replay; must equal applying the same ops directly.
        bool replayOk = Render(ops.Take(4)).AsSpan().SequenceEqual(Render(ops.Take(4)));
        W($"   undo/replay deterministic: {replayOk}");
        if (!replayOk) rc = 8;

        W(rc == 0 ? "annotest PASSED" : $"annotest FAILED ({rc})");
        return rc;
    }

    /// <summary>
    /// Shows every one of the 16,777,216 24-bit colours on every monitor (16 frames of 1024Ã—1024), captures
    /// each frame through the real pipeline and compares every pixel with what was drawn.
    /// </summary>
    private static int PixelTest(DesktopCapturer cap, Action<string> W)
    {
        const int Size = 1024; // 1024Â² per frame, 16 frames = 2^24 colours
        var s0 = new Settings();
        var monitors = cap.CaptureAll().Monitors.Select(m => (m.DeviceName, m.Bounds)).ToList();
        int rc = 0;

        foreach (var (device, mb) in monitors)
        {
            var rect = new Rectangle(mb.X, mb.Y, Math.Min(Size, mb.Width), Math.Min(Size, mb.Height));
            int perFrame = rect.Width * rect.Height;
            int frames = (int)Math.Ceiling((double)(1 << 24) / perFrame);
            long checkedPx = 0, wrong = 0; int maxErr = 0;
            string? coveredBy = null;
            var errByChannelValue = new long[3, 256];

            using var pattern = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using var f = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, AutoScaleMode = AutoScaleMode.None,
                Bounds = rect, TopMost = true, ShowInTaskbar = false,
            };
            f.Paint += (_, e) =>
            {
                e.Graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                e.Graphics.DrawImage(pattern, new Rectangle(0, 0, rect.Width, rect.Height), new Rectangle(0, 0, rect.Width, rect.Height), GraphicsUnit.Pixel);
            };
            var expected = new byte[perFrame * 4];
            f.Shown += async (_, _) =>
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    // Colour index â†’ RGB; the last frame wraps around, which is harmless.
                    for (int i = 0; i < perFrame; i++)
                    {
                        int idx = (int)(((long)frame * perFrame + i) & 0xFFFFFF);
                        expected[i * 4] = (byte)idx; expected[i * 4 + 1] = (byte)(idx >> 8); expected[i * 4 + 2] = (byte)(idx >> 16); expected[i * 4 + 3] = 255;
                    }
                    var bd = pattern.LockBits(new Rectangle(0, 0, rect.Width, rect.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    for (int y = 0; y < rect.Height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(expected, y * rect.Width * 4, bd.Scan0 + y * bd.Stride, rect.Width * 4);
                    pattern.UnlockBits(bd);
                    f.Invalidate();
                    f.Update();
                    await Task.Delay(120); // let DWM compose the new frame

                    var shot = cap.CaptureAll();
                    var m = shot.Monitors.First(x => x.DeviceName == device);
                    if (frame == 0)
                    {
                        W($"   {device}: requested window {rect}, actual {f.Bounds}, DPI {f.DeviceDpi}");
                        var probe = new Point(rect.X + 17, rect.Y + 29);
                        var top = Win32.Native.WindowFromPoint(new Win32.Native.POINT { X = probe.X, Y = probe.Y });
                        using var gdi = new Bitmap(1, 1);
                        using (var g = Graphics.FromImage(gdi)) g.CopyFromScreen(probe.X, probe.Y, 0, 0, new Size(1, 1));
                        int ei = (29 * rect.Width + 17) * 4, ai = ((probe.Y - m.Bounds.Y) * m.Bounds.Width + (probe.X - m.Bounds.X)) * 4;
                        var gp = gdi.GetPixel(0, 0);
                        uint topPid = 0; Win32.Native.GetWindowThreadProcessId(top, out topPid);
                        string topName = "?"; try { topName = System.Diagnostics.Process.GetProcessById((int)topPid).ProcessName; } catch { }
                        W($"   probe: expected ({expected[ei + 2]},{expected[ei + 1]},{expected[ei]}) DD ({m.Bgra[ai + 2]},{m.Bgra[ai + 1]},{m.Bgra[ai]}) GDI ({gp.R},{gp.G},{gp.B}); window at point is ours: {top == f.Handle} (owner: {topName})");
                        if (top != f.Handle) { coveredBy = topName; break; }
                    }
                    for (int y = 0; y < rect.Height; y++)
                        for (int x = 0; x < rect.Width; x++)
                        {
                            int e = (y * rect.Width + x) * 4;
                            int a = ((rect.Y - m.Bounds.Y + y) * m.Bounds.Width + (rect.X - m.Bounds.X + x)) * 4;
                            bool bad = false;
                            for (int c = 0; c < 3; c++)
                            {
                                int d = Math.Abs(m.Bgra[a + c] - expected[e + c]);
                                if (d != 0) { bad = true; errByChannelValue[c, expected[e + c]]++; if (d > maxErr) maxErr = d; }
                            }
                            if (bad) wrong++;
                            checkedPx++;
                        }
                }
                f.Close();
            };
            f.ShowDialog();

            if (coveredBy != null)
            {
                W($"pixeltest {device}: SKIPPED — the test area is covered by a window from \"{coveredBy}\" (the capture correctly shows that window). Move it and re-run.");
                continue;
            }
            W($"pixeltest {device}: {checkedPx:N0} pixels checked (all 16,777,216 colours), mismatches {wrong:N0}, max channel error {maxErr}");
            if (wrong > 0)
            {
                rc = 4;
                string[] names = { "B", "G", "R" };
                for (int c = 0; c < 3; c++)
                {
                    var worst = Enumerable.Range(0, 256).Where(v => errByChannelValue[c, v] > 0).Take(24).Select(v => $"{v}:{errByChannelValue[c, v]}");
                    W($"   {names[c]} values with errors (value:count): {string.Join(" ", worst)}");
                }
            }
        }
        return rc;
    }

    /// <summary>
    /// `FrameBurst.exe --selftest [outputDir] --settingsui` — opens the real Settings window (every page, dark and
    /// light, wide and narrow) and a message dialog, and saves screen captures of them. Nothing is saved to settings.
    /// </summary>
    internal static async void SettingsShots(string dir)
    {
        Directory.CreateDirectory(dir);
        using var cap = new DesktopCapturer();
        int pid = Environment.ProcessId;
        var host = new UI.TrayMenu(); // like the tray: a hidden window outlives the ones we open and close
        void Shoot(IntPtr hwnd, string name)
        {
            var b = Win32.Native.GetWindowBounds(hwnd);
            var shot = cap.CaptureAll();
            var area = Rectangle.Intersect(b, shot.VirtualBounds);
            using var bmp = ImageOutput.ToBitmap(shot.ComposeBgra(area), area.Width, area.Height);
            string path = Path.Combine(dir, name + ".png");
            bmp.Save(path, ImageFormat.Png);
            Console.WriteLine($"settingsui: {path} ({area.Width}x{area.Height})");
        }
        try
        {
            foreach (var theme in new[] { Microsoft.UI.Xaml.ElementTheme.Dark, Microsoft.UI.Xaml.ElementTheme.Light })
            {
                string t = theme.ToString().ToLowerInvariant();
                var w = new UI.SettingsWindow(Settings.Load(), () => { });
                ((Microsoft.UI.Xaml.FrameworkElement)w.Content).RequestedTheme = theme;
                w.BringToFront();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                await Task.Delay(1200);
                foreach (var page in new[] { "Capture", "Output", "About" })
                {
                    w.ShowPage(page);
                    await Task.Delay(700);
                    Shoot(hwnd, $"settings_{t}_{page.ToLowerInvariant()}");
                }
                // Narrow: rows stack their action under the text.
                w.ShowPage("Output");
                var pos = w.AppWindow.Position;
                w.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(pos.X, pos.Y,
                    (int)(580 * UI.WindowChrome.Scale(w)), w.AppWindow.Size.Height));
                await Task.Delay(700);
                Shoot(hwnd, $"settings_{t}_narrow");
                w.Close();

                _ = UI.MessageDialog.Ask("FrameBurst 9.9.9 is available", $"You have {Updater.Current}.\n\nRelease notes go here.",
                    "Download and install", title: "FrameBurst update");
                await Task.Delay(1000);
                var dlg = Win32.Native.EnumerateVisibleWindows(IntPtr.Zero)
                    .FirstOrDefault(x => Win32.Native.GetWindowThreadProcessId(x.Hwnd, out uint p) != 0 && p == pid).Hwnd;
                if (dlg != IntPtr.Zero) { Shoot(dlg, $"dialog_{t}"); SendMessage(dlg, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); }
                await Task.Delay(300);
            }
            // README screenshots: dark, each page at the height that shows all of it.
            var docs = new UI.SettingsWindow(Settings.Load(), () => { });
            ((Microsoft.UI.Xaml.FrameworkElement)docs.Content).RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark;
            docs.BringToFront();
            var docsHwnd = WinRT.Interop.WindowNative.GetWindowHandle(docs);
            await Task.Delay(1200);
            foreach (var page in new[] { "Capture", "Output", "About" })
            {
                docs.ShowPage(page);
                await Task.Delay(500);
                UI.WindowChrome.SizeAndCenter(docs, 900, Math.Ceiling(docs.HeightForWholePage));
                await Task.Delay(700);
                Shoot(docsHwnd, $"docs_settings-{page.ToLowerInvariant()}");
            }
            docs.Close();
            await Task.Delay(300);

            // Tray menu (opens at the mouse pointer).
            var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
            menu.Items.Add(UI.TrayMenu.Item("Capture region", "", () => { }, "Insert"));
            menu.Items.Add(UI.TrayMenu.Item("Capture all monitors", "", () => { }, "Ctrl+PrintScreen"));
            menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            menu.Items.Add(UI.TrayMenu.Item("Settings", "", () => { }));
            menu.Items.Add(UI.TrayMenu.Item("Exit", "", () => { }));
            host.Show(menu);
            await Task.Delay(800);
            var c = Cursor.Position;
            var shot2 = cap.CaptureAll();
            var area2 = Rectangle.Intersect(new Rectangle(c.X - 360, c.Y - 360, 720, 720), shot2.VirtualBounds);
            using (var bmp = ImageOutput.ToBitmap(shot2.ComposeBgra(area2), area2.Width, area2.Height))
                bmp.Save(Path.Combine(dir, "traymenu.png"), ImageFormat.Png);
            menu.Hide();
            await Task.Delay(300); // let the flyout finish closing before its host window goes away
        }
        catch (Exception ex) { Console.WriteLine("settingsui FAILED: " + ex); }
        host.Close();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    public static int Run(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "FrameBurstSelfTest");
        Directory.CreateDirectory(dir);
        // `--uiscale 1.5` / `--opaque`: draw the overlays as on a 150 % monitor / with transparency effects off.
        int si = Array.IndexOf(args, "--uiscale");
        if (si >= 0 && si + 1 < args.Length && double.TryParse(args[si + 1], System.Globalization.CultureInfo.InvariantCulture, out double uiScale))
            UI.OverlayTheme.ScaleOverride = uiScale;
        UI.OverlayTheme.ForceOpaque = args.Contains("--opaque");
        var log = new StringBuilder();
        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }
        int rc = 0;
        try
        {
            foreach (var a in DesktopCapturer.EnumerateAdapters())
            {
                W($"GPU [{GpuVendors.Label(a.Vendor)}] {a.Name}  outputs={a.Outputs.Count}");
                foreach (var o in a.Outputs)
                    W($"   {o.DeviceName} {o.Bounds} bpc={o.BitsPerColor}");
            }

            using var cap = new DesktopCapturer { ForceGdi = args.Contains("--gdi") };
            if (cap.ForceGdi) W("capture backend forced to GDI BitBlt");

            if (args.Contains("--pixeltest"))
            {
                rc = PixelTest(cap, W);
                File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString());
                return rc;
            }

            if (args.Contains("--ghosttest"))
            {
                // Picker over a synthetic bright image (longest rgb() text). Move the cursor along a path, then any
                // on-screen pixel outside the current panel / hint bar that differs from the frozen image is a ghost.
                var real = cap.CaptureAll();
                var frames = real.Monitors.Select(m =>
                {
                    int w = m.Bounds.Width, h = m.Bounds.Height;
                    var px = new byte[w * h * 4];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int o = (y * w + x) * 4;
                            px[o] = 255; px[o + 1] = (byte)(200 + (x + y) % 56); px[o + 2] = 255; px[o + 3] = 255;
                            if ((x / 40 + y / 40) % 2 == 0) { px[o] = 250; px[o + 2] = 245; }
                        }
                    return new MonitorFrame { DeviceName = m.DeviceName, Bounds = m.Bounds, GpuName = m.GpuName, Vendor = m.Vendor, Bgra = px };
                }).ToList();
                var syn = new CaptureSet { Monitors = frames, Timings = new CaptureTimings() };
                var vb = syn.VirtualBounds;
                var frozen = syn.ComposeBgra(vb);
                using var picker = new UI.ColorPicker(syn);
                picker.Shown += async (_, _) =>
                {
                    // Path across the monitor that holds the hint bar.
                    var hb = picker.HintBounds;
                    var mon = frames.Select(f => new Rectangle(f.Bounds.X - vb.X, f.Bounds.Y - vb.Y, f.Bounds.Width, f.Bounds.Height)).First(r => r.IntersectsWith(hb));
                    for (int i = 0; i <= 60; i++)
                    {
                        var p = new Point(mon.X + 200 + i * (mon.Width - 400) / 60, mon.Y + 300 + (int)(200 * Math.Sin(i / 6.0)));
                        SendMessage(picker.Handle, 0x200, IntPtr.Zero, (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)));
                        if (i % 3 == 0) { picker.Update(); await Task.Delay(15); }
                    }
                    picker.Update();
                    await Task.Delay(250);
                    var live = cap.CaptureAll();
                    var liveFull = live.ComposeBgra(vb);
                    var panel = Rectangle.Inflate(picker.PanelBounds, 3, 3);
                    var hint = Rectangle.Inflate(hb, 3, 3);
                    var rcur = Cursor.Position; // the real mouse pointer can be drawn into the capture; it is not a ghost
                    var pointer = new Rectangle(rcur.X - vb.X - 20, rcur.Y - vb.Y - 20, 40, 40);
                    long ghost = 0; var ghostBox = Rectangle.Empty;
                    for (int y = mon.Top; y < mon.Bottom; y++)
                        for (int x = mon.Left; x < mon.Right; x++)
                        {
                            if (panel.Contains(x, y) || hint.Contains(x, y) || pointer.Contains(x, y)) continue;
                            int o = (y * vb.Width + x) * 4;
                            if (liveFull[o] != frozen[o] || liveFull[o + 1] != frozen[o + 1] || liveFull[o + 2] != frozen[o + 2])
                            {
                                ghost++;
                                ghostBox = ghostBox.IsEmpty ? new Rectangle(x, y, 1, 1) : Rectangle.Union(ghostBox, new Rectangle(x, y, 1, 1));
                            }
                        }
                    W($"ghosttest: stray pixels outside the panel and hint bar: {ghost:N0}{(ghost > 0 ? $" within {ghostBox}" : "")}");
                    var rc0 = Cursor.Position;
                    W($"   real cursor at capture time: overlay client ({rc0.X - vb.X},{rc0.Y - vb.Y})");
                    if (ghost > 0)
                    {
                        var gb = Rectangle.Intersect(Rectangle.Inflate(ghostBox, 8, 8), mon);
                        var gbytes = new byte[gb.Width * gb.Height * 4];
                        for (int y = 0; y < gb.Height; y++) Array.Copy(liveFull, ((gb.Y + y) * vb.Width + gb.X) * 4, gbytes, y * gb.Width * 4, gb.Width * 4);
                        using var gbmp = ImageOutput.ToBitmap(gbytes, gb.Width, gb.Height);
                        using var big = new Bitmap(gb.Width * 6, gb.Height * 6);
                        using (var gg = Graphics.FromImage(big)) { gg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; gg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half; gg.DrawImage(gbmp, 0, 0, big.Width, big.Height); }
                        big.Save(Path.Combine(dir, "ghost_stray.png"), ImageFormat.Png);
                    }
                    // Snapshot of the final panel + some surroundings for a visual check.
                    var snap = Rectangle.Intersect(Rectangle.Inflate(panel, 80, 30), mon);
                    var bytes = new byte[snap.Width * snap.Height * 4];
                    for (int y = 0; y < snap.Height; y++) Array.Copy(liveFull, ((snap.Y + y) * vb.Width + snap.X) * 4, bytes, y * snap.Width * 4, snap.Width * 4);
                    using (var bmp = ImageOutput.ToBitmap(bytes, snap.Width, snap.Height)) bmp.Save(Path.Combine(dir, "ghost_panel.png"), ImageFormat.Png);
                    var hs = Rectangle.Intersect(hint, mon);
                    var hbytes = new byte[hs.Width * hs.Height * 4];
                    for (int y = 0; y < hs.Height; y++) Array.Copy(liveFull, ((hs.Y + y) * vb.Width + hs.X) * 4, hbytes, y * hs.Width * 4, hs.Width * 4);
                    using (var bmp = ImageOutput.ToBitmap(hbytes, hs.Width, hs.Height)) bmp.Save(Path.Combine(dir, "ghost_hint.png"), ImageFormat.Png);
                    rc = ghost == 0 ? 0 : 14;
                    picker.Close();
                };
                picker.ShowDialog();
                W(rc == 0 ? "ghosttest PASSED" : "ghosttest FAILED");
                File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString());
                return rc;
            }

            if (args.Contains("--pickertest"))
            {
                // Open the tray colour picker, click known pixels with window messages, compare with the capture.
                var set = cap.CaptureAll();
                var vb = set.VirtualBounds;
                var full = set.ComposeBgra(vb);
                var pickRnd = new Random(5);
                int ok = 0, tries = 0;
                for (int n = 0; n < 5; n++)
                {
                    var mon = set.Monitors[n % set.Monitors.Count].Bounds;
                    var p = new Point(mon.X - vb.X + pickRnd.Next(50, mon.Width - 50), mon.Y - vb.Y + pickRnd.Next(50, mon.Height - 50));
                    int o = (p.Y * vb.Width + p.X) * 4;
                    var expected = Color.FromArgb(full[o + 2], full[o + 1], full[o]);
                    using var picker = new UI.ColorPicker(set);
                    bool snapshot = n == 0;
                    picker.Shown += async (_, _) =>
                    {
                        IntPtr lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
                        SendMessage(picker.Handle, 0x200, IntPtr.Zero, lp);
                        if (snapshot)
                        {
                            // The magnifier follows the real cursor, so snapshot around it.
                            await Task.Delay(200);
                            var shot = cap.CaptureAll();
                            var cp = Cursor.Position;
                            var area = Rectangle.Intersect(new Rectangle(cp.X - 60, cp.Y - 60, 360, 300), shot.VirtualBounds);
                            using var bmp = ImageOutput.ToBitmap(shot.ComposeBgra(area), area.Width, area.Height);
                            bmp.Save(Path.Combine(dir, "picker_ui.png"), ImageFormat.Png);
                        }
                        SendMessage(picker.Handle, 0x201, (IntPtr)1, lp);
                    };
                    picker.ShowDialog();
                    tries++;
                    bool match = picker.Picked is { } c && c.ToArgb() == expected.ToArgb();
                    if (match) ok++;
                    W($"pickertest at ({p.X + vb.X},{p.Y + vb.Y}): expected {UI.ColorPicker.Hex(expected)}, picked {(picker.Picked is { } pc ? UI.ColorPicker.Hex(pc) : "nothing")} {(match ? "OK" : "MISMATCH")}");
                }
                // Esc must cancel without picking.
                using (var picker = new UI.ColorPicker(set))
                {
                    picker.Shown += (_, _) => SendMessage(picker.Handle, 0x100, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                    picker.ShowDialog();
                    W($"pickertest Esc: result {picker.DialogResult}, picked {(picker.Picked == null ? "nothing (correct)" : "SOMETHING (wrong)")}");
                    if (picker.Picked != null) ok = -1;
                }
                rc = ok == tries ? 0 : 13;
                W(rc == 0 ? "pickertest PASSED" : "pickertest FAILED");
                File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString());
                return rc;
            }

            if (args.Contains("--uitest"))
            {
                rc = OverlayUiTest(cap, W, dir);
                File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString());
                return rc;
            }

            if (args.Contains("--annotest"))
            {
                rc = AnnotationTest(cap, W);
                File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString());
                return rc;
            }

            if (args.Contains("--graytest"))
            {
                // Show a flat RGB(128,128,128) window at different sizes and read back what DWM composites.
                var s0 = new Settings();
                var probe = cap.CaptureAll();
                var mon = probe.Monitors.First(m => m.Bounds.Location == Point.Empty) ?? probe.Monitors[0];
                var sizes = new (string Name, Rectangle Rect, bool Topmost)[]
                {
                    ("small window", new Rectangle(mon.Bounds.X + 200, mon.Bounds.Y + 200, 400, 400), true),
                    ("one full monitor", mon.Bounds, true),
                    ("all monitors", probe.VirtualBounds, true),
                    ("all monitors, not topmost", probe.VirtualBounds, false),
                };
                foreach (var (name, rect, topmost) in sizes)
                {
                    using var f = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, AutoScaleMode = AutoScaleMode.None,
                        Bounds = rect, BackColor = Color.FromArgb(128, 128, 128), TopMost = topmost, ShowInTaskbar = false,
                    };
                    var t = new System.Windows.Forms.Timer { Interval = 300 };
                    t.Tick += (_, _) =>
                    {
                        t.Stop();
                        var shot = cap.CaptureAll();
                        var m = shot.Monitors.First(x => x.DeviceName == mon.DeviceName);
                        int cx = rect.X + 100 - m.Bounds.X, cy = rect.Y + 100 - m.Bounds.Y, o = (cy * m.Bounds.Width + cx) * 4;
                        W($"graytest {name}: composited value = ({m.Bgra[o + 2]},{m.Bgra[o + 1]},{m.Bgra[o]}) expected (128,128,128)");
                        f.Close();
                    };
                    f.Shown += (_, _) => t.Start();
                    f.ShowDialog();
                }
                File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString());
                return 0;
            }

            if (args.Contains("--hdr"))
            {
                // Capture with the HDR copy on; saves an _HDR.png only if an HDR monitor is present.
                var hs = new Settings { OutputFolder = dir, FileNamePattern = "hdr", SaveHdr = true };
                GpuDevice.WarmUpHdr(); W("hdr shader compiled");
                var set = cap.CaptureAll(hdr: true);
                foreach (var m in set.Monitors) W($"  {m.DeviceName}: hdr={m.IsHdr} hdrCopy={(m.Hdr != null)}");
                var saved = ImageOutput.Produce(set, set.VirtualBounds, hs);
                W($"hdr: sdr={saved.FilePath} hdr={saved.HdrFilePath ?? "(no HDR monitor)"} encode {saved.EncodeMs:0} ms");
                File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString());
                return 0;
            }

            if (args.Contains("--overlay"))
            {
                // Show the region overlay for ~1 s, grab the screen while it is up, then close it.
                var s0 = new Settings { OutputFolder = dir, FileNamePattern = "overlay" };
                var set0 = cap.CaptureAll();
                using var ov = new UI.RegionSelector(set0, set0.ComposeBgra(set0.VirtualBounds), new());
                var timer = new System.Windows.Forms.Timer { Interval = 350 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var shot = cap.CaptureAll();
                    var saved = ImageOutput.Produce(shot, shot.VirtualBounds, s0);
                    W($"overlay snapshot: {saved.FilePath}");
                    // Compare what Windows composites with the overlay up vs. just before it appeared.
                    foreach (var (before, during) in set0.Monitors.Zip(shot.Monitors))
                    {
                        double sb = 0, sd = 0; long n = 0, same = 0;
                        for (int i = 0; i < before.Bgra.Length; i += 4 * 13)
                        {
                            sb += before.Bgra[i] + before.Bgra[i + 1] + before.Bgra[i + 2];
                            sd += during.Bgra[i] + during.Bgra[i + 1] + during.Bgra[i + 2];
                            if (before.Bgra[i] == during.Bgra[i] && before.Bgra[i + 1] == during.Bgra[i + 1] && before.Bgra[i + 2] == during.Bgra[i + 2]) same++;
                            n++;
                        }
                        W($"  {before.DeviceName}: mean before {sb / n / 3:0.00}, during overlay {sd / n / 3:0.00}, identical pixels {100.0 * same / n:0.0}%");
                        var sum = new double[256]; var cnt = new long[256];
                        for (int i = 0; i < before.Bgra.Length; i += 4) { int v = before.Bgra[i + 1]; sum[v] += during.Bgra[i + 1]; cnt[v]++; }
                        W("    green before->during: " + string.Join("  ", new[] { 8, 16, 32, 48, 64, 96, 128, 160, 192, 224, 255 }
                            .Where(v => cnt[v] > 50).Select(v => $"{v}->{sum[v] / cnt[v]:0.0}")));
                    }
                    ov.Close();
                };
                ov.Shown += (_, _) => timer.Start();
                ov.ShowDialog();
                W($"overlay closed: {ov.DialogResult} result={ov.Result}");
                File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString());
                return 0;
            }

            for (int run = 1; run <= 2; run++)
            {
                var s = new Settings { OutputFolder = dir, FileNamePattern = "selftest_" + run };
                var set = cap.CaptureAll();
                set = cap.CaptureAll(); // second capture = warm timings
                W($"Capture run {run} [{set.Backend}]: {set.Monitors.Count} monitor(s), virtual {set.VirtualBounds}, {set.Timings}");
                foreach (var m in set.Monitors)
                {
                    long nonBlack = 0;
                    for (int i = 0; i < m.Bgra.Length; i += 4) if ((m.Bgra[i] | m.Bgra[i + 1] | m.Bgra[i + 2]) != 0) nonBlack++;
                    W($"   {m.DeviceName} via {m.GpuName}: nonBlack={100.0 * nonBlack / (m.Bgra.Length / 4):0.0}%");
                }

                var saved = ImageOutput.Produce(set, set.VirtualBounds, s);
                W($"   saved {saved.FilePath} ({new FileInfo(saved.FilePath!).Length / 1024} KB) encode {saved.EncodeMs:0} ms");

                // Verify our PNG decodes to exactly the captured pixels.
                using (var bmp = new Bitmap(saved.FilePath!))
                {
                    var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var back = new byte[bmp.Width * bmp.Height * 4];
                    for (int y = 0; y < bmp.Height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, back, y * bmp.Width * 4, bmp.Width * 4);
                    bmp.UnlockBits(bd);
                    bool same = back.AsSpan().SequenceEqual(saved.Bgra);
                    W($"   PNG round-trip bit-exact: {same}");
                    if (!same) rc = 2;
                }

                // Encoder speed on the full image.
                var t = System.Diagnostics.Stopwatch.StartNew();
                using (var ms = new MemoryStream()) PngEncoder.WriteBgra8(ms, saved.Bgra, saved.Area.Width, saved.Area.Height, CompressionLevel.Optimal);
                W($"   PNG encode (parallel, Optimal): {t.ElapsedMilliseconds} ms");
            }

            // Synthetic round-trip with alpha + gradients.
            var rnd = new Random(1);
            int sw = 333, sh = 77;
            var px = new byte[sw * sh * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)(i / 4 % 256); px[i + 1] = (byte)rnd.Next(256); px[i + 2] = (byte)(i / 1000); px[i + 3] = (byte)(i % 3 == 0 ? 128 : 255); }
            var p2 = Path.Combine(dir, "synthetic_alpha.png");
            using (var fs = File.Create(p2)) PngEncoder.WriteBgra8(fs, px, sw, sh, CompressionLevel.SmallestSize);
            using (var bmp = new Bitmap(p2))
            {
                bool ok = true;
                for (int y = 0; y < sh && ok; y++)
                    for (int x = 0; x < sw && ok; x++)
                    {
                        var c = bmp.GetPixel(x, y); int o = (y * sw + x) * 4;
                        ok = c.B == px[o] && c.G == px[o + 1] && c.R == px[o + 2] && c.A == px[o + 3];
                    }
                W($"Synthetic RGBA PNG round-trip exact: {ok}");
                if (!ok) rc = 3;
            }
        }
        catch (Exception ex)
        {
            W("FAILED: " + ex);
            rc = 1;
        }
        File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString());
        return rc;
    }
}
