using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FrameBurst.Win32;

namespace FrameBurst.Capture;

/// <summary>
/// Fallback capture path for systems where the GPU path is unavailable (no D3D11 feature level 11 GPU,
/// Microsoft Basic Display Adapter, remote sessions, Windows builds without Windows Graphics Capture).
/// Copies every monitor out of the screen DC with BitBlt. Always 8-bit SDR; no HDR copy.
/// </summary>
internal static class GdiCapture
{
    public const string Name = "GDI (BitBlt)";

    public static CaptureSet CaptureAll()
    {
        var total = Stopwatch.StartNew();
        var timings = new CaptureTimings();
        var frames = new List<MonitorFrame>();

        var monitors = new List<(string Device, Rectangle Bounds)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hmon, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(hmon, ref mi)) monitors.Add((mi.szDevice, mi.rcMonitor.ToRectangle()));
            return true;
        }, IntPtr.Zero);
        if (monitors.Count == 0) throw new InvalidOperationException("No monitors found.");

        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("Cannot open the screen device context.");
        try
        {
            foreach (var (device, b) in monitors)
            {
                if (b.Width <= 0 || b.Height <= 0) continue;
                var acquire = Stopwatch.StartNew();
                var bgra = Grab(screen, b, out double copyMs);
                timings.AcquireMs += acquire.Elapsed.TotalMilliseconds - copyMs;
                timings.ReadbackMs += copyMs;
                frames.Add(new MonitorFrame { DeviceName = device, Bounds = b, GpuName = Name, Vendor = GpuVendor.Unknown, Bgra = bgra });
                Log.Write($"gdi {device}: {b.Width}x{b.Height}");
            }
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        if (frames.Count == 0)
            throw new InvalidOperationException("No monitor could be captured. (The secure desktop, such as a UAC prompt or the lock screen, cannot be captured.)");

        timings.TotalMs = total.Elapsed.TotalMilliseconds;
        return new CaptureSet { Monitors = frames, Timings = timings, Backend = CaptureBackend.Gdi };
    }

    private static unsafe byte[] Grab(IntPtr screen, Rectangle b, out double copyMs)
    {
        int w = b.Width, h = b.Height;
        var mem = Native.CreateCompatibleDC(screen);
        if (mem == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed.");
        IntPtr dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, // top-down
                biPlanes = 1, biBitCount = 32,
            };
            dib = Native.CreateDIBSection(screen, in bmi, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException($"CreateDIBSection failed for {w}x{h}.");
            old = Native.SelectObject(mem, dib);

            // CAPTUREBLT includes layered (translucent / topmost overlay) windows.
            if (!Native.BitBlt(mem, 0, 0, w, h, screen, b.Left, b.Top, Native.SRCCOPY | Native.CAPTUREBLT))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "BitBlt failed.");
            Native.GdiFlush();

            var copy = Stopwatch.StartNew();
            var dst = new byte[w * h * 4];
            byte* src = (byte*)bits;
            fixed (byte* d = dst)
            {
                byte* dp = d;
                // A 32-bpp DIB row is already DWORD-aligned, so the stride is exactly w*4. GDI leaves alpha undefined.
                Parallel.For(0, h, y =>
                {
                    uint* s = (uint*)(src + (long)y * w * 4), o = (uint*)(dp + (long)y * w * 4);
                    for (int x = 0; x < w; x++) o[x] = s[x] | 0xFF000000u;
                });
            }
            copyMs = copy.Elapsed.TotalMilliseconds;
            return dst;
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(mem, old);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            Native.DeleteDC(mem);
        }
    }
}
