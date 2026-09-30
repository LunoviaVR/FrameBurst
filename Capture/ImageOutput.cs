using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using FrameBurst.Imaging;
using FrameBurst.Win32;

namespace FrameBurst.Capture;

public sealed class SavedCapture
{
    public required Rectangle Area { get; init; }
    public required byte[] Bgra { get; init; }
    public string? FilePath { get; set; }
    public double EncodeMs { get; set; }
}

public static class ImageOutput
{
    /// <summary>Crops, optionally draws the cursor, and encodes. Safe to call off the UI thread.</summary>
    /// <param name="annotatedVirtual">The whole virtual desktop with overlay edits applied, or null for an unedited capture.</param>
    public static SavedCapture Produce(CaptureSet set, Rectangle area, Settings s, byte[]? annotatedVirtual = null)
    {
        area = Rectangle.Intersect(area, set.VirtualBounds);
        if (area.Width <= 0 || area.Height <= 0) throw new InvalidOperationException("Selected area is empty.");

        var sw = Stopwatch.StartNew();
        var bgra = annotatedVirtual != null ? Crop(annotatedVirtual, set.VirtualBounds, area) : set.ComposeBgra(area);
        if (s.CaptureCursor && set.Cursor is { } cur && area.Contains(cur.Position))
            DrawCursor(bgra, area, cur.Handle, cur.Position, cur.Hotspot);

        var result = new SavedCapture { Area = area, Bgra = bgra };
        if (s.SaveToFile)
        {
            Directory.CreateDirectory(s.OutputFolder);
            string baseName = Path.Combine(s.OutputFolder, FormatName(s.FileNamePattern));
            string file = Unique(baseName, ".png");
            var level = s.PngCompression switch
            {
                PngCompression.Fast => CompressionLevel.Fastest,
                PngCompression.Smallest => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal,
            };

            using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                PngEncoder.WriteBgra8(fs, bgra, area.Width, area.Height, level);
            result.FilePath = file;
        }
        result.EncodeMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    private static byte[] Crop(byte[] src, Rectangle srcBounds, Rectangle area)
    {
        var dst = new byte[area.Width * area.Height * 4];
        int row = area.Width * 4;
        for (int y = 0; y < area.Height; y++)
            Array.Copy(src, ((area.Y - srcBounds.Y + y) * srcBounds.Width + (area.X - srcBounds.X)) * 4, dst, y * row, row);
        return dst;
    }

    /// <summary>Puts the image on the clipboard as both a DIB and a lossless PNG stream. Must run on an STA thread.</summary>
    public static void CopyToClipboard(SavedCapture cap)
    {
        using var bmp = ToBitmap(cap.Bgra, cap.Area.Width, cap.Area.Height);
        var data = new DataObject();
        data.SetImage(bmp);
        var png = new MemoryStream();
        PngEncoder.WriteBgra8(png, cap.Bgra, cap.Area.Width, cap.Area.Height, CompressionLevel.Fastest);
        png.Position = 0;
        data.SetData("PNG", false, png);
        if (cap.FilePath != null)
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { cap.FilePath });
        for (int attempt = 0; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, true, 5, 50); break; }
            catch (ExternalException) when (attempt < 3) { Thread.Sleep(100); }
        }
    }

    public static Bitmap ToBitmap(byte[] bgra, int w, int h, PixelFormat format = PixelFormat.Format32bppArgb)
    {
        var bmp = new Bitmap(w, h, format);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, format);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(bgra, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    private static void DrawCursor(byte[] bgra, Rectangle area, IntPtr hCursor, Point pos, Point hotspot)
    {
        try
        {
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                using var bmp = new Bitmap(area.Width, area.Height, area.Width * 4, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
                using var g = Graphics.FromImage(bmp);
                using var icon = Icon.FromHandle(hCursor);
                using var cursorBmp = icon.ToBitmap();
                g.DrawImageUnscaled(cursorBmp, pos.X - area.X - hotspot.X, pos.Y - area.Y - hotspot.Y);
            }
            finally { handle.Free(); }
        }
        catch { /* cursor overlay is cosmetic */ }
    }

    public static (IntPtr Handle, Point Hotspot, Point Position)? GrabCursor()
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return null;
        var copy = Native.CopyIcon(ci.hCursor);
        if (copy == IntPtr.Zero) return null;
        var hot = Point.Empty;
        if (Native.GetIconInfo(copy, out var ii))
        {
            hot = new Point(ii.xHotspot, ii.yHotspot);
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
        }
        return (copy, hot, new Point(ci.ptScreenPos.X, ci.ptScreenPos.Y));
    }

    private static string FormatName(string pattern)
    {
        var now = DateTime.Now;
        // Replace {format} tokens with DateTime formatting; everything else is literal.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '{')
            {
                int end = pattern.IndexOf('}', i);
                if (end > i)
                {
                    try { sb.Append(now.ToString(pattern.Substring(i + 1, end - i - 1))); } catch { sb.Append(pattern, i, end - i + 1); }
                    i = end;
                    continue;
                }
            }
            sb.Append(pattern[i]);
        }
        var name = sb.ToString();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "FrameBurst" : name;
    }

    private static string Unique(string basePath, string ext)
    {
        string p = basePath + ext;
        for (int i = 2; File.Exists(p); i++) p = $"{basePath} ({i}){ext}";
        return p;
    }
}
