using System.Drawing;

namespace FrameBurst.Capture;

/// <summary>One monitor's processed frame, already in desktop orientation and in CPU memory.</summary>
public sealed class MonitorFrame
{
    public required string DeviceName { get; init; }
    public required Rectangle Bounds { get; init; }     // virtual-desktop coordinates, physical pixels
    public required string GpuName { get; init; }
    public required GpuVendor Vendor { get; init; }

    /// <summary>8-bit sRGB, BGRA, tightly packed (Width*4 stride).</summary>
    public required byte[] Bgra { get; init; }

    /// <summary>True when Windows HDR is on for this monitor.</summary>
    public bool IsHdr { get; init; }

    /// <summary>16-bit RGBA in BT.2100 PQ (BT.2020 primaries), or null when no HDR copy was requested.</summary>
    public ushort[]? Hdr { get; init; }
}

public sealed class CaptureTimings
{
    public double AcquireMs, GpuMs, ReadbackMs, TotalMs;
    public double GpuShaderMs = double.NaN; // measured with D3D11 timestamp queries
    public override string ToString() =>
        $"acquire {AcquireMs:0.0} ms · GPU {(double.IsNaN(GpuShaderMs) ? "" : $"shader {GpuShaderMs:0.00} ms, ")}pass {GpuMs:0.0} ms · readback {ReadbackMs:0.0} ms · total {TotalMs:0.0} ms";
}

/// <summary>A frozen snapshot of every monitor, taken at the moment the hotkey was pressed.</summary>
public sealed class CaptureSet
{
    public required List<MonitorFrame> Monitors { get; init; }
    public required CaptureTimings Timings { get; init; }
    public (IntPtr Handle, Point Hotspot, Point Position)? Cursor { get; set; }

    public Rectangle VirtualBounds
    {
        get
        {
            var r = Rectangle.Empty;
            foreach (var m in Monitors) r = r.IsEmpty ? m.Bounds : Rectangle.Union(r, m.Bounds);
            return r;
        }
    }

    /// <summary>Crops (and stitches across monitors) the 8-bit image. Areas outside every monitor are transparent.</summary>
    public byte[] ComposeBgra(Rectangle area) => Compose(area, 4, m => m.Bgra);

    /// <summary>True when an HDR copy was captured and the area touches a monitor that is in HDR mode.</summary>
    public bool HasHdr(Rectangle area) => Monitors.Any(m => m.IsHdr && m.Hdr != null && m.Bounds.IntersectsWith(area));

    /// <summary>Crops (and stitches) the 16-bit PQ image. Areas outside every HDR copy are black and transparent.</summary>
    public ushort[] ComposeHdr(Rectangle area) => Compose(area, 4, m => m.Hdr ?? new ushort[m.Bounds.Width * m.Bounds.Height * 4]);

    private T[] Compose<T>(Rectangle area, int channels, Func<MonitorFrame, T[]> pick)
    {
        var dst = new T[area.Width * area.Height * channels];
        foreach (var m in Monitors)
        {
            var isect = Rectangle.Intersect(area, m.Bounds);
            if (isect.IsEmpty) continue;
            var src = pick(m);
            int rowLen = isect.Width * channels;
            Parallel.For(0, isect.Height, y =>
            {
                int sy = isect.Top - m.Bounds.Top + y, sx = isect.Left - m.Bounds.Left;
                int dy = isect.Top - area.Top + y, dx = isect.Left - area.Left;
                Array.Copy(src, (sy * m.Bounds.Width + sx) * channels, dst, (dy * area.Width + dx) * channels, rowLen);
            });
        }
        return dst;
    }
}
