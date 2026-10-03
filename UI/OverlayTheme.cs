using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace FrameBurst.UI;

/// <summary>
/// Design tokens for the GDI+ capture overlays (region selector, colour picker): the same palette, radii and
/// spacing as UI/Theme/Tokens.xaml, scaled to the DPI of the monitor the overlay chrome is drawn on. The overlay
/// always sits on arbitrary screen content, so it uses the dark material in either Windows theme.
/// </summary>
internal sealed class OverlayTheme : IDisposable
{
    // ---- palette (dark material; mirrors Tokens.xaml "Default") ----
    public static readonly Color Accent = Color.FromArgb(0x00, 0xA8, 0xFF);          // SystemAccentColor
    public static readonly Color AccentStrong = Color.FromArgb(0x00, 0x76, 0xBC);    // SystemAccentColorDark1: white text 4.5:1
    public static readonly Color Text = Color.FromArgb(0xF0, 0xF1, 0xF3);
    public static readonly Color TextSecondary = Color.FromArgb(0xB4, 0xB8, 0xBE);
    public static readonly Color TextDisabled = Color.FromArgb(0x5E, 0x62, 0x68);
    public static readonly Color GlassTint = Color.FromArgb(184, 0x1E, 0x1E, 0x22);      // over the blurred backdrop
    public static readonly Color GlassOpaque = Color.FromArgb(250, 0x26, 0x26, 0x2A);    // no transparency / high contrast
    public static readonly Color Elevated = Color.FromArgb(242, 0x26, 0x26, 0x2A);       // moving surfaces (no blur)
    public static readonly Color Border = Color.FromArgb(46, 255, 255, 255);
    public static readonly Color EdgeHighlight = Color.FromArgb(26, 255, 255, 255);
    public static readonly Color Divider = Color.FromArgb(34, 255, 255, 255);
    public static readonly Color Hover = Color.FromArgb(26, 255, 255, 255);
    public static readonly Color Pressed = Color.FromArgb(14, 255, 255, 255);
    public static readonly Color ToastTint = Color.FromArgb(232, 0x00, 0x5C, 0x93);      // SystemAccentColorDark2
    public static readonly Color SwatchEdge = Color.FromArgb(150, 255, 255, 255);
    public static readonly Color Scrim = Color.FromArgb(200, 0, 0, 0);                   // dark half of two-tone outlines

    // ---- metrics in DIPs (Tokens.xaml radius / spacing scale) ----
    public const int RadiusControl = 4, RadiusSurface = 8;
    public const int Space4 = 4, Space8 = 8, Space12 = 12, Space16 = 16;

    private const string IconFamily = "Segoe Fluent Icons", IconFallback = "Segoe MDL2 Assets";
    private const string UiFamily = "Segoe UI Variable Text", UiFallback = "Segoe UI";
    private const string MonoFamily = "Cascadia Mono", MonoFallback = "Consolas";

    // ---- Segoe Fluent Icons glyphs (same family as the WinUI windows) ----
    public const string GlyphSelect = "", GlyphPen = "", GlyphHighlighter = "", GlyphArrow = "",
        GlyphRectangle = "", GlyphText = "", GlyphBlur = "", GlyphUndo = "", GlyphRedo = "",
        GlyphClose = "";

    /// <summary>Display scale of the monitor the chrome lives on (1.0 = 100 %).</summary>
    public double Scale { get; }
    /// <summary>True when Windows transparency effects are on and high contrast is off: glass may be translucent.</summary>
    public bool Translucent { get; }

    public Font Body { get; }     // hints, toasts
    public Font Caption { get; }  // size label
    public Font Mono { get; }     // readouts
    public Font MonoStrong { get; }
    public Font Icon { get; }

    /// <summary>Self-test hooks: render as if at this display scale / with transparency effects off.</summary>
    internal static double? ScaleOverride { get; set; }
    internal static bool ForceOpaque { get; set; }

    public OverlayTheme(double scale)
    {
        Scale = Math.Clamp(ScaleOverride ?? scale, 1.0, 4.0);
        Translucent = !ForceOpaque && !SystemInformation.HighContrast && TransparencyEnabled();
        Body = MakeFont(UiFamily, UiFallback, 12, FontStyle.Regular);
        Caption = MakeFont(UiFamily, UiFallback, 11, FontStyle.Regular);
        Mono = MakeFont(MonoFamily, MonoFallback, 11, FontStyle.Regular);
        MonoStrong = MakeFont(MonoFamily, MonoFallback, 15, FontStyle.Bold);
        Icon = MakeFont(IconFamily, IconFallback, 16, FontStyle.Regular);
    }

    /// <summary>DIPs to device pixels on this overlay's monitor.</summary>
    public int Px(double dips) => (int)Math.Round(dips * Scale);
    public float PxF(double dips) => (float)(dips * Scale);

    private Font MakeFont(string family, string fallback, double dips, FontStyle style)
    {
        float px = PxF(dips);
        using var probe = new Font(family, px, style, GraphicsUnit.Pixel);
        // GDI+ silently substitutes a missing family; check the name to fall back deliberately.
        return probe.Name == family ? new Font(family, px, style, GraphicsUnit.Pixel) : new Font(fallback, px, style, GraphicsUnit.Pixel);
    }

    private static bool TransparencyEnabled()
    {
        try { return new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled; }
        catch { return true; }
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Fills a rounded rectangle (e.g. hover / selected state of a toolbar item).</summary>
    public void FillRound(Graphics g, Rectangle r, Color c, int radiusDips)
    {
        using var b = new SolidBrush(c);
        using var p = RoundRect(r, PxF(radiusDips));
        g.FillPath(b, p);
    }

    /// <summary>An opaque, non-blurred elevated surface for chrome that moves with the mouse (label, magnifier).</summary>
    public void PaintElevated(Graphics g, Rectangle r, int radiusDips = RadiusSurface)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
        using (var path = RoundRect(rf, PxF(radiusDips)))
        using (var fill = new SolidBrush(Translucent ? Elevated : GlassOpaque))
        using (var edge = new Pen(Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }
        g.SmoothingMode = mode;
    }

    public void DrawGlyph(Graphics g, string glyph, Rectangle r, Color fg)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var b = new SolidBrush(fg);
        using var fmt = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, Icon, b, new RectangleF(r.X, r.Y + PxF(0.5), r.Width, r.Height), fmt);
    }

    public void Dispose()
    {
        Body.Dispose(); Caption.Dispose(); Mono.Dispose(); MonoStrong.Dispose(); Icon.Dispose();
    }
}

/// <summary>
/// A frosted-glass panel over a frozen screen image: the backdrop under the panel is blurred once (down- and
/// up-sampled), tinted, edged and given a soft shadow, then cached until the panel moves or the image changes.
/// Painting the cache is a single bitmap blit, so hover and repaint cost stays the same as a flat fill.
/// </summary>
internal sealed class GlassPanel : IDisposable
{
    private Bitmap? _cache;
    private Rectangle _rect;
    private int _version = -1;
    private Color _tint;

    /// <summary>How far the shadow extends outside the panel; invalidate the panel inflated by this much.</summary>
    public int Margin { get; private set; }

    /// <param name="version">Bump whenever <paramref name="backdrop"/> pixels change.</param>
    public void Paint(Graphics g, OverlayTheme t, Bitmap backdrop, Rectangle r, int version, Color tint)
    {
        Margin = t.Px(12);
        if (_cache == null || r != _rect || version != _version || tint != _tint)
        {
            _cache?.Dispose();
            _cache = Build(t, backdrop, r, tint);
            _rect = r;
            _version = version;
            _tint = tint;
        }
        var state = g.Save();
        g.CompositingMode = CompositingMode.SourceOver;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_cache, new Rectangle(r.X - Margin, r.Y - Margin, _cache.Width, _cache.Height));
        g.Restore(state);
    }

    private Bitmap Build(OverlayTheme t, Bitmap backdrop, Rectangle r, Color tint)
    {
        int m = Margin;
        var bmp = new Bitmap(r.Width + m * 2, r.Height + m * 2, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = t.PxF(OverlayTheme.RadiusSurface);
        var panel = new RectangleF(m, m, r.Width, r.Height);

        // Soft shadow: a few widening, fading rings offset slightly downwards.
        const int rings = 6;
        for (int i = rings; i >= 1; i--)
        {
            float grow = i * m / (float)rings * 0.8f;
            var sr = new RectangleF(panel.X - grow, panel.Y - grow + t.PxF(2), panel.Width + grow * 2, panel.Height + grow * 2);
            using var sb = new SolidBrush(Color.FromArgb(9 - i, 0, 0, 0));
            using var sp = OverlayTheme.RoundRect(sr, radius + grow);
            g.FillPath(sb, sp);
        }

        using var path = OverlayTheme.RoundRect(panel, radius);
        if (t.Translucent)
        {
            // Blur: shrink the backdrop region ~10x with a filtering resample, then scale it back up.
            var src = Rectangle.Intersect(Rectangle.Inflate(r, m, m), new Rectangle(Point.Empty, backdrop.Size));
            int sw = Math.Max(1, src.Width / 10), sh = Math.Max(1, src.Height / 10);
            using var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
            using (var sg = Graphics.FromImage(small))
            {
                sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                sg.PixelOffsetMode = PixelOffsetMode.Half;
                sg.DrawImage(backdrop, new Rectangle(0, 0, sw, sh), src, GraphicsUnit.Pixel);
            }
            var state = g.Save();
            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new Rectangle(src.X - r.X + m, src.Y - r.Y + m, src.Width, src.Height), new Rectangle(0, 0, sw, sh), GraphicsUnit.Pixel);
            g.Restore(state);
            using var tb = new SolidBrush(tint);
            g.FillPath(tb, path);
        }
        else
        {
            using var ob = new SolidBrush(Color.FromArgb(255, tint));
            g.FillPath(ob, path);
        }

        // Hairline border, then a faint highlight along the top (lit) edge.
        var inner = new RectangleF(panel.X + 0.5f, panel.Y + 0.5f, panel.Width - 1, panel.Height - 1);
        using (var edge = new Pen(OverlayTheme.Border))
        using (var ep = OverlayTheme.RoundRect(inner, radius))
            g.DrawPath(edge, ep);
        using (var hl = new Pen(OverlayTheme.EdgeHighlight))
            g.DrawLine(hl, panel.X + radius, panel.Y + 1.5f, panel.Right - radius, panel.Y + 1.5f);
        return bmp;
    }

    public void Dispose() => _cache?.Dispose();
}
