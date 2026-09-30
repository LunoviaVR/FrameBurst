using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using GpuShot.Capture;
using GpuShot.Win32;

namespace GpuShot.UI;

/// <summary>
/// Stand-alone screen colour picker (opened from the tray menu). Shows the frozen screen with a magnifier;
/// click (or Enter) picks the pixel under the cursor, arrow keys move 1 px, Esc / right-click cancels.
/// Colours come from the same pixel-exact capture as screenshots.
/// </summary>
internal sealed class ColorPicker : Form
{
    private const int Cells = 13, Zoom = 12;
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
    private Rectangle _lastPanel;

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
        _hint = new Rectangle(home.Left + (home.Width - 460) / 2, home.Top + 16, 460, 30);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Bounds = _virtual;
        Text = "GpuShot colour picker";
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
        Invalidate(_lastPanel);
        Invalidate(PanelRect());
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
        int size = Cells * Zoom, w = size, h = size + 58;
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
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Colour picker  ·  click to copy #RRGGBB  ·  arrows = 1 px  ·  Esc = cancel", _hintFont, Brushes.White, _hint, fmt);
        }

        // Magnifier panel.
        var r = PanelRect();
        _lastPanel = Rectangle.Inflate(r, 2, 2);
        int size = Cells * Zoom;
        var dst = new Rectangle(r.X, r.Y, size, size);
        using (var bg = new SolidBrush(Color.FromArgb(240, 20, 20, 24))) g.FillRectangle(bg, r);
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_frozen, dst, new Rectangle(_mouse.X - Cells / 2, _mouse.Y - Cells / 2, Cells, Cells), GraphicsUnit.Pixel);
        g.PixelOffsetMode = PixelOffsetMode.Default;
        using (var grid = new Pen(Color.FromArgb(35, 255, 255, 255)))
            for (int i = 1; i < Cells; i++)
            {
                g.DrawLine(grid, dst.X + i * Zoom, dst.Y, dst.X + i * Zoom, dst.Bottom);
                g.DrawLine(grid, dst.X, dst.Y + i * Zoom, dst.Right, dst.Y + i * Zoom);
            }
        int c0 = Cells / 2 * Zoom;
        using (var dark = new Pen(Color.Black, 3)) g.DrawRectangle(dark, dst.X + c0, dst.Y + c0, Zoom, Zoom);
        using (var center = new Pen(Color.White, 1)) g.DrawRectangle(center, dst.X + c0, dst.Y + c0, Zoom, Zoom);
        using (var border = new Pen(Color.FromArgb(120, 255, 255, 255))) g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);

        if (TryColorAt(_mouse, out var col))
        {
            using (var sw = new SolidBrush(col)) g.FillRectangle(sw, r.X + 8, dst.Bottom + 8, 42, 42);
            using (var swEdge = new Pen(Color.FromArgb(150, 255, 255, 255))) g.DrawRectangle(swEdge, r.X + 8, dst.Bottom + 8, 42, 42);
            g.DrawString(Hex(col), _hexFont, Brushes.White, r.X + 58, dst.Bottom + 8);
            g.DrawString($"rgb({col.R}, {col.G}, {col.B})", _smallFont, Brushes.Gainsboro, r.X + 58, dst.Bottom + 30);
        }
        else
        {
            g.DrawString("no screen here", _smallFont, Brushes.Gray, r.X + 8, dst.Bottom + 20);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _frozen.Dispose(); _hexFont.Dispose(); _smallFont.Dispose(); _hintFont.Dispose(); }
        base.Dispose(disposing);
    }
}
