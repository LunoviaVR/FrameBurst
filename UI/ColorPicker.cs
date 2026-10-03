using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using FrameBurst.Capture;
using FrameBurst.Win32;

namespace FrameBurst.UI;

/// <summary>
/// Stand-alone screen colour picker (opened from the tray menu). Shows the frozen screen with a magnifier;
/// click (or Enter) picks the pixel under the cursor, arrow keys move 1 px, Esc / right-click cancels.
/// Colours come from the same pixel-exact capture as screenshots.
/// </summary>
internal sealed class ColorPicker : Form
{
    private readonly int _zoom, _swatch, _pad, _infoHeight, _offset;  // device pixels, scaled from DIPs
    private readonly int _cells;      // magnifier cells per side (odd), sized so the readout text always fits
    private readonly int _textX;      // x offset of the hex / rgb text inside the panel
    private readonly OverlayTheme _theme;
    private readonly GlassPanel _hintGlass = new();

    private readonly Rectangle _virtual;
    private readonly byte[] _bgra;
    private readonly Bitmap _frozen;
    private readonly List<Rectangle> _monitors;
    private readonly Rectangle _hint;
    private Point _mouse;
    private Rectangle _shownPanel;    // where the panel is (or is about to be) painted, for exact erasing

    /// <summary>Where the magnifier panel and hint bar are drawn now (client coordinates; used by the self-test).</summary>
    internal Rectangle PanelBounds => PanelRect();
    internal Rectangle HintBounds => Rectangle.Inflate(_hint, _theme.Px(12), _theme.Px(12)); // pill plus its shadow

    /// <summary>The picked colour, or null if cancelled.</summary>
    public Color? Picked { get; private set; }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public ColorPicker(CaptureSet set)
    {
        _virtual = set.VirtualBounds;
        _bgra = set.ComposeBgra(_virtual);
        _frozen = ImageOutput.ToBitmap(_bgra, _virtual.Width, _virtual.Height, PixelFormat.Format32bppPArgb);
        _monitors = set.Monitors.Select(m => new Rectangle(m.Bounds.X - _virtual.X, m.Bounds.Y - _virtual.Y, m.Bounds.Width, m.Bounds.Height)).ToList();

        var cursor = Cursor.Position;
        var home = _monitors.FirstOrDefault(m => m.Contains(cursor.X - _virtual.X, cursor.Y - _virtual.Y));
        if (home.IsEmpty) home = _monitors[0];
        _theme = new OverlayTheme(Native.ScaleAt(home.X + _virtual.X + home.Width / 2, home.Y + _virtual.Y + home.Height / 2));
        _zoom = _theme.Px(12); _swatch = _theme.Px(40); _pad = _theme.Px(OverlayTheme.Space8);
        _infoHeight = _swatch + _pad * 2; _offset = _theme.Px(28);
        // Size everything from the widest possible text, so nothing is ever cut off or drawn outside.
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int hexW = (int)Math.Ceiling(g.MeasureString("#FFFFFF", _theme.MonoStrong).Width);
            int rgbW = (int)Math.Ceiling(g.MeasureString("rgb(255, 255, 255)", _theme.Mono).Width);
            _textX = _pad + _swatch + _pad;
            int needed = _textX + Math.Max(hexW, rgbW) + _pad;
            _cells = Math.Max(13, (needed + _zoom - 1) / _zoom) | 1;
            int hintW = (int)Math.Ceiling(g.MeasureString(HintText, _theme.Body).Width) + _theme.Px(32);
            _hint = new Rectangle(home.Left + (home.Width - hintW) / 2, home.Top + _theme.Px(16), hintW, _theme.Px(30));
        }

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Bounds = _virtual;
        Text = "FrameBurst colour picker";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _virtual;
        Activate();
        Native.ForceForeground(Handle);
        _mouse = PointToClient(Cursor.Position);
        Invalidate();
    }

    private bool TryColorAt(Point p, out Color c)
    {
        c = Color.Empty;
        if (p.X < 0 || p.Y < 0 || p.X >= _virtual.Width || p.Y >= _virtual.Height) return false;
        int o = (p.Y * _virtual.Width + p.X) * 4;
        if (_bgra[o + 3] == 0) return false; // gap between monitors
        c = Color.FromArgb(_bgra[o + 2], _bgra[o + 1], _bgra[o]);
        return true;
    }

    private void Pick(Point p)
    {
        if (!TryColorAt(p, out var c)) return;
        Picked = c;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.Location;
        // Erase exactly where the panel was and repaint where it goes. Tracked here rather than in OnPaint,
        // so several moves between two paints can never leave an old panel position behind.
        var next = PanelRect();
        if (next == _shownPanel) { Invalidate(next); return; }
        Invalidate(_shownPanel);
        Invalidate(next);
        _shownPanel = next;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Pick(e.Location);
        else if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape: DialogResult = DialogResult.Cancel; Close(); break;
            case Keys.Enter: case Keys.Space: Pick(_mouse); break;
            case Keys.Left: Nudge(-1, 0); break;
            case Keys.Right: Nudge(1, 0); break;
            case Keys.Up: Nudge(0, -1); break;
            case Keys.Down: Nudge(0, 1); break;
        }
    }

    private static void Nudge(int dx, int dy)
    {
        var p = Cursor.Position;
        Cursor.Position = new Point(p.X + dx, p.Y + dy);
    }

    private Rectangle PanelRect()
    {
        int size = _cells * _zoom, w = size, h = size + _infoHeight;
        int x = _mouse.X + _offset, y = _mouse.Y + _offset;
        var mon = _monitors.FirstOrDefault(m => m.Contains(_mouse));
        if (mon.IsEmpty) mon = ClientRectangle;
        if (x + w > mon.Right) x = _mouse.X - _offset - w;
        if (y + h > mon.Bottom) y = _mouse.Y - _offset - h;
        return new Rectangle(x, y, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_frozen, clip, clip, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // Hint pill (frosted glass over the frozen screen; static, so its blur is computed once).
        if (clip.IntersectsWith(HintBounds))
        {
            _hintGlass.Paint(g, _theme, _frozen, _hint, 0, OverlayTheme.GlassTint);
            using var fg = new SolidBrush(OverlayTheme.Text);
            using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(HintText, _theme.Body, fg, _hint, fmt);
        }

        // Magnifier panel, drawn at the position tracked in OnMouseMove and clipped to it.
        var r = _shownPanel.IsEmpty ? (_shownPanel = PanelRect()) : _shownPanel;
        if (!clip.IntersectsWith(r)) return;
        var state = g.Save();
        g.SetClip(r);
        int size = _cells * _zoom;
        var dst = new Rectangle(r.X, r.Y, size, size);
        _theme.PaintElevated(g, r);
        using (var round = OverlayTheme.RoundRect(new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), _theme.PxF(OverlayTheme.RadiusSurface)))
            g.SetClip(round, CombineMode.Intersect);
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_frozen, dst, new Rectangle(_mouse.X - _cells / 2, _mouse.Y - _cells / 2, _cells, _cells), GraphicsUnit.Pixel);
        g.PixelOffsetMode = PixelOffsetMode.Default;
        using (var grid = new Pen(OverlayTheme.Divider))
            for (int i = 1; i < _cells; i++)
            {
                g.DrawLine(grid, dst.X + i * _zoom, dst.Y, dst.X + i * _zoom, dst.Bottom);
                g.DrawLine(grid, dst.X, dst.Y + i * _zoom, dst.Right, dst.Y + i * _zoom);
            }
        int c0 = _cells / 2 * _zoom;
        using (var dark = new Pen(Color.Black, 3)) g.DrawRectangle(dark, dst.X + c0, dst.Y + c0, _zoom, _zoom);
        using (var center = new Pen(Color.White, 1)) g.DrawRectangle(center, dst.X + c0, dst.Y + c0, _zoom, _zoom);
        using (var divider = new Pen(OverlayTheme.Border)) g.DrawLine(divider, r.X, dst.Bottom, r.Right, dst.Bottom);

        if (TryColorAt(_mouse, out var col))
        {
            int top = dst.Bottom + _pad;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var swr = new Rectangle(r.X + _pad, top, _swatch, _swatch);
            _theme.FillRound(g, swr, col, OverlayTheme.RadiusControl);
            using (var swEdge = new Pen(OverlayTheme.SwatchEdge))
            using (var swPath = OverlayTheme.RoundRect(new RectangleF(swr.X + 0.5f, swr.Y + 0.5f, swr.Width - 1, swr.Height - 1), _theme.PxF(OverlayTheme.RadiusControl)))
                g.DrawPath(swEdge, swPath);
            using var fg = new SolidBrush(OverlayTheme.Text);
            using var fg2 = new SolidBrush(OverlayTheme.TextSecondary);
            g.DrawString(Hex(col), _theme.MonoStrong, fg, r.X + _textX, top);
            g.DrawString($"rgb({col.R}, {col.G}, {col.B})", _theme.Mono, fg2, r.X + _textX, top + _theme.Px(22));
        }
        else
        {
            using var fg = new SolidBrush(OverlayTheme.TextDisabled);
            g.DrawString("no screen here", _theme.Mono, fg, r.X + _pad, dst.Bottom + _theme.Px(20));
        }
        g.Restore(state);
    }

    private const string HintText = "Colour picker  ·  click to copy #RRGGBB  ·  arrows = 1 px  ·  Esc = cancel";

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _frozen.Dispose(); _hintGlass.Dispose(); _theme.Dispose(); }
        base.Dispose(disposing);
    }
}
