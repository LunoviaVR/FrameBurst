using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace FrameBurst.UI;

internal enum Tool { Select, Pen, Highlighter, Arrow, Rectangle, Text, Blur }

/// <summary>
/// One edit on the frozen screen. Edits are applied in order onto the canvas; everything outside an
/// edit's bounds is left untouched, so unedited pixels stay bit-exact.
/// </summary>
internal abstract class AnnotationOp
{
    /// <summary>Every pixel the op can change (client coordinates).</summary>
    public abstract Rectangle Bounds { get; }

    /// <summary>Applies the op to the canvas bitmap.</summary>
    public virtual void Apply(Bitmap canvas)
    {
        using var g = Graphics.FromImage(canvas);
        Render(g);
    }

    /// <summary>Draws the op (also used for the live preview while dragging).</summary>
    public abstract void Render(Graphics g);

    protected static void Setup(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.Half;
    }

    protected static Rectangle BoundsOf(IEnumerable<Point> pts, float pad)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var p in pts) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y); }
        int pd = (int)Math.Ceiling(pad) + 2;
        return Rectangle.FromLTRB(l - pd, t - pd, r + pd + 1, b + pd + 1);
    }
}

internal sealed class StrokeOp : AnnotationOp
{
    public readonly List<Point> Points = new();
    private readonly Color _color;
    private readonly float _width;
    private readonly bool _highlighter;

    public StrokeOp(Color color, float width, bool highlighter, Point start)
    {
        _highlighter = highlighter;
        _color = highlighter ? Color.FromArgb(110, color) : color;
        _width = highlighter ? width * 4 + 8 : width;
        Points.Add(start);
    }

    public void Add(Point p)
    {
        if (Points[^1] != p) Points.Add(p);
    }

    public override Rectangle Bounds => BoundsOf(Points, _width / 2);

    public override void Render(Graphics g)
    {
        Setup(g);
        using var pen = new Pen(_color, _width)
        {
            StartCap = _highlighter ? LineCap.Square : LineCap.Round,
            EndCap = _highlighter ? LineCap.Square : LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        if (Points.Count == 1)
        {
            using var b = new SolidBrush(_color);
            g.FillEllipse(b, Points[0].X - _width / 2, Points[0].Y - _width / 2, _width, _width);
            return;
        }
        // One path, so a translucent highlighter doesn't darken where the stroke overlaps itself.
        using var path = new GraphicsPath();
        path.AddLines(Points.ToArray());
        g.DrawPath(pen, path);
    }
}

internal sealed class ArrowOp : AnnotationOp
{
    public Point From, To;
    private readonly Color _color;
    private readonly float _width;

    public ArrowOp(Color color, float width, Point from) { _color = color; _width = Math.Max(2, width); From = To = from; }

    public override Rectangle Bounds => BoundsOf(new[] { From, To }, _width * 5 + 6);

    public override void Render(Graphics g)
    {
        Setup(g);
        using var pen = new Pen(_color, _width) { StartCap = LineCap.Round, LineJoin = LineJoin.Round };
        pen.CustomEndCap = new AdjustableArrowCap(3.5f, 4.5f, true);
        if (From != To) g.DrawLine(pen, From, To);
    }
}

internal sealed class RectOp : AnnotationOp
{
    public Point From, To;
    private readonly Color _color;
    private readonly float _width;

    public RectOp(Color color, float width, Point from) { _color = color; _width = width; From = To = from; }

    public Rectangle Rect => Rectangle.FromLTRB(Math.Min(From.X, To.X), Math.Min(From.Y, To.Y), Math.Max(From.X, To.X), Math.Max(From.Y, To.Y));
    public override Rectangle Bounds => Rectangle.Inflate(Rect, (int)Math.Ceiling(_width) + 2, (int)Math.Ceiling(_width) + 2);

    public override void Render(Graphics g)
    {
        Setup(g);
        using var pen = new Pen(_color, _width) { LineJoin = LineJoin.Miter };
        var r = Rect;
        if (r.Width > 0 && r.Height > 0) g.DrawRectangle(pen, r);
    }
}

internal sealed class TextOp : AnnotationOp
{
    private readonly string _text;
    private readonly Point _at;
    private readonly Color _color;
    private readonly float _px;
    private readonly Rectangle _bounds;

    public TextOp(string text, Point at, Color color, float px)
    {
        _text = text; _at = at; _color = color; _px = px;
        using var font = MakeFont(px);
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        var size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        _bounds = new Rectangle(at.X - 4, at.Y - 4, (int)Math.Ceiling(size.Width) + (int)px + 8, (int)Math.Ceiling(size.Height) + (int)(px / 2) + 8);
    }

    public static Font MakeFont(float px) => new("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);

    public override Rectangle Bounds => _bounds;

    public override void Render(Graphics g)
    {
        Setup(g);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = MakeFont(_px);
        // A soft dark outline keeps text readable on any background.
        using var path = new GraphicsPath();
        path.AddString(_text, font.FontFamily, (int)font.Style, _px, _at, StringFormat.GenericTypographic);
        using var outline = new Pen(Color.FromArgb(160, 0, 0, 0), Math.Max(2f, _px / 8)) { LineJoin = LineJoin.Round };
        g.DrawPath(outline, path);
        using var fill = new SolidBrush(_color);
        g.FillPath(fill, path);
    }
}

/// <summary>Gaussian-like blur (three box-blur passes). Rewrites the pixels, so the content is gone.</summary>
internal sealed class BlurOp : AnnotationOp
{
    public Point From, To;
    private readonly int _radius;

    public BlurOp(int radius, Point from) { _radius = Math.Max(2, radius); From = To = from; }

    public Rectangle Rect => Rectangle.FromLTRB(Math.Min(From.X, To.X), Math.Min(From.Y, To.Y), Math.Max(From.X, To.X) + 1, Math.Max(From.Y, To.Y) + 1);
    public override Rectangle Bounds => Rect;

    public override void Render(Graphics g)
    {
        // Live preview while dragging: frosted, hatched rectangle.
        var r = Rect;
        using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(90, 255, 255, 255), Color.FromArgb(90, 40, 40, 48));
        g.FillRectangle(hatch, r);
        using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    public override void Apply(Bitmap canvas)
    {
        var r = Rectangle.Intersect(Rect, new Rectangle(0, 0, canvas.Width, canvas.Height));
        if (r.Width < 2 || r.Height < 2) return;
        var bd = canvas.LockBits(r, ImageLockMode.ReadWrite, canvas.PixelFormat);
        try
        {
            int w = r.Width, h = r.Height;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * w * 4, w * 4);
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(px, w, h, _radius, horizontal: true);
                BoxBlur(px, w, h, _radius, horizontal: false);
            }
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        }
        finally { canvas.UnlockBits(bd); }
    }

    /// <summary>Running-sum box blur of B, G, R (alpha untouched), edges clamped inside the rectangle.</summary>
    private static void BoxBlur(byte[] px, int w, int h, int radius, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int step = horizontal ? 4 : w * 4;
        int rad = Math.Min(radius, len - 1);
        int div = 2 * rad + 1;
        Parallel.For(0, lines, line =>
        {
            int start = horizontal ? line * w * 4 : line * 4;
            var tmp = new byte[len * 3];
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -rad; k <= rad; k++) sum += px[start + Math.Clamp(k, 0, len - 1) * step + c];
                for (int i = 0; i < len; i++)
                {
                    tmp[i * 3 + c] = (byte)((sum + div / 2) / div);
                    int add = Math.Min(i + rad + 1, len - 1), sub = Math.Max(i - rad, 0);
                    sum += px[start + add * step + c] - px[start + sub * step + c];
                }
            }
            for (int i = 0; i < len; i++)
                for (int c = 0; c < 3; c++) px[start + i * step + c] = tmp[i * 3 + c];
        });
    }
}
