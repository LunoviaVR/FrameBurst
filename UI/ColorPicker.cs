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
    private const int Zoom = 12, SwatchSize = 42, Pad = 8, InfoHeight = 58;
    private readonly int _cells;      // magnifier cells per side (odd), sized so the readout text always fits
    private readonly int _textX;      // x offset of the hex / rgb text inside the panel
    private static readonly Color Accent = Color.FromArgb(0, 168, 255);

    private readonly Rectangle _virtual;
    private readonly byte[] _bgra;
    private readonly Bitmap _frozen;
    private readonly List<Rectangle> _monitors;
    private readonly Rectangle _hint;
    private readonly Font _hexFont = new("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _smallFont = new("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _hintFont = new("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private Point _mouse;
    private Rectangle _shownPanel;    // where the panel is (or is about to be) painted, for exact erasing

    /// <summary>Where the magnifier panel and hint bar are drawn now (client coordinates; used by the self-test).</summary>
    internal Rectangle PanelBounds => PanelRect();
    internal Rectangle HintBounds => _hint;

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
        // Size everything from the widest possible text, so nothing is ever cut off or drawn outside.
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int hexW = (int)Math.Ceiling(g.MeasureString("#FFFFFF", _hexFont).Width);
            int rgbW = (int)Math.Ceiling(g.MeasureString("rgb(255, 255, 255)", _smallFont).Width);
            _textX = Pad + SwatchSize + Pad;
            int needed = _textX + Math.Max(hexW, rgbW) + Pad;
            _cells = Math.Max(13, (needed + Zoom - 1) / Zoom) | 1;
            int hintW = (int)Math.Ceiling(g.MeasureString(HintText, _hintFont).Width) + 32;
            _hint = new Rectangle(home.Left + (home.Width - hintW) / 2, home.Top + 16, hintW, 30);
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
        int size = _cells * Zoom, w = size, h = size + InfoHeight;
        int x = _mouse.X + 28, y = _mouse.Y + 28;
        var mon = _monitors.FirstOrDefault(m => m.Contains(_mouse));
        if (mon.IsEmpty) mon = ClientRectangle;
        if (x + w > mon.Right) x = _mouse.X - 28 - w;
        if (y + h > mon.Bottom) y = _mouse.Y - 28 - h;
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

        // Hint bar.
        if (clip.IntersectsWith(_hint))
        {
            using var bg = new SolidBrush(Color.FromArgb(225, 20, 20, 24));
            g.FillRectangle(bg, _hint);
            using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(HintText, _hintFont, Brushes.White, _hint, fmt);
        }

        // Magnifier panel, drawn at the position tracked in OnMouseMove and clipped to it.
        var r = _shownPanel.IsEmpty ? (_shownPanel = PanelRect()) : _shownPanel;
        if (!clip.IntersectsWith(r)) return;
        var state = g.Save();
        g.SetClip(r);
        int size = _cells * Zoom;
        var dst = new Rectangle(r.X, r.Y, size, size);
        using (var bg = new SolidBrush(Color.FromArgb(240, 20, 20, 24))) g.FillRectangle(bg, r);
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_frozen, dst, new Rectangle(_mouse.X - _cells / 2, _mouse.Y - _cells / 2, _cells, _cells), GraphicsUnit.Pixel);
        g.PixelOffsetMode = PixelOffsetMode.Default;
        using (var grid = new Pen(Color.FromArgb(35, 255, 255, 255)))
            for (int i = 1; i < _cells; i++)
            {
                g.DrawLine(grid, dst.X + i * Zoom, dst.Y, dst.X + i * Zoom, dst.Bottom);
                g.DrawLine(grid, dst.X, dst.Y + i * Zoom, dst.Right, dst.Y + i * Zoom);
            }
        int c0 = _cells / 2 * Zoom;
        using (var dark = new Pen(Color.Black, 3)) g.DrawRectangle(dark, dst.X + c0, dst.Y + c0, Zoom, Zoom);
        using (var center = new Pen(Color.White, 1)) g.DrawRectangle(center, dst.X + c0, dst.Y + c0, Zoom, Zoom);
        using (var border = new Pen(Color.FromArgb(120, 255, 255, 255))) g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);

        if (TryColorAt(_mouse, out var col))
        {
            int top = dst.Bottom + Pad;
            using (var sw = new SolidBrush(col)) g.FillRectangle(sw, r.X + Pad, top, SwatchSize, SwatchSize);
            using (var swEdge = new Pen(Color.FromArgb(150, 255, 255, 255))) g.DrawRectangle(swEdge, r.X + Pad, top, SwatchSize, SwatchSize);
            g.DrawString(Hex(col), _hexFont, Brushes.White, r.X + _textX, top);
            g.DrawString($"rgb({col.R}, {col.G}, {col.B})", _smallFont, Brushes.Gainsboro, r.X + _textX, top + 22);
        }
        else
        {
            g.DrawString("no screen here", _smallFont, Brushes.Gray, r.X + Pad, dst.Bottom + 20);
        }
        g.Restore(state);
    }

    private const string HintText = "Colour picker  ·  click to copy #RRGGBB  ·  arrows = 1 px  ·  Esc = cancel";

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _frozen.Dispose(); _hexFont.Dispose(); _smallFont.Dispose(); _hintFont.Dispose(); }
        base.Dispose(disposing);
    }
}
