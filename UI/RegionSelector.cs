using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using FrameBurst.Capture;
using FrameBurst.Win32;

namespace FrameBurst.UI;

/// <summary>
/// Full-virtual-desktop overlay showing the frozen capture, with an editing toolbar at the top centre of
/// the monitor the cursor was on. Annotate / blur / pick colours on the frozen screen, then with the
/// Select tool drag a region or click a window to capture it (edits included). Enter = monitor under
/// the cursor, F = all monitors, Esc = cancel. All coordinates are physical pixels.
/// </summary>
internal sealed class RegionSelector : Form
{
    private const int MagCells = 15;
    private static readonly int[] StrokeSizes = { 1, 2, 3, 4, 6, 8, 10, 14, 18, 24 };
    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 59, 48), Color.FromArgb(255, 204, 0), Color.FromArgb(52, 199, 89),
        Color.FromArgb(10, 132, 255), Color.White, Color.Black,
    };

    private readonly Rectangle _virtual;
    private readonly List<Rectangle> _windows;   // client coordinates, front-most first
    private readonly List<Rectangle> _monitors;  // client coordinates
    private readonly byte[] _base;               // untouched frozen screen (BGRA), for the picker / readout
    private readonly Bitmap _canvas;             // frozen screen + applied edits, what is shown and captured

    // Edits
    private readonly List<AnnotationOp> _ops = new();
    private readonly Stack<AnnotationOp> _redo = new();
    private AnnotationOp? _current;
    private TextBox? _textBox;
    private Tool _tool = Tool.Select;
    private Color _color = Palette[0];
    private int _sizeIdx = 2;

    // Selection (Select tool)
    private Point _mouse, _start;
    private bool _dragging;
    private Rectangle _selection;
    private Rectangle? _hoverWindow;
    private readonly List<Rectangle> _lastDirty = new();

    // Toolbar
    private enum ItemKind { Tool, Color, Size, Undo, Redo, Cancel }
    private sealed record Item(Rectangle R, ItemKind Kind, Tool Tool, Color Color, string Tip);
    private readonly List<Item> _items = new();
    private readonly List<int> _separators = new();
    private Rectangle _bar, _hint;
    private Item? _hoverItem, _pressedItem;
    private string? _toast;
    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 2500 };

    // Look: tokens scaled to the toolbar's monitor, and cached frosted glass for the two static panels.
    private readonly OverlayTheme _theme;
    private readonly GlassPanel _barGlass = new(), _hintGlass = new();
    private readonly int _btn, _magZoom;
    private Rectangle _home;           // monitor the toolbar is on (client coordinates)
    private int _canvasVersion;        // bumped whenever _canvas pixels change, so glass re-blurs its backdrop

    public Rectangle? Result { get; private set; }

    /// <summary>The whole virtual desktop with the edits applied, or null if nothing was edited.</summary>
    public byte[]? AnnotatedBgra { get; private set; }

    /// <summary>Toolbar buttons and their client rectangles (used by the self-test).</summary>
    internal IReadOnlyList<(string Tip, Rectangle R)> ToolbarLayout => _items.Select(i => (i.Tip, i.R)).ToList();
    internal Rectangle ToolbarBounds => _bar;
    internal int EditCount => _ops.Count;

    public RegionSelector(CaptureSet set, byte[] virtualBgra, List<(IntPtr Hwnd, Rectangle Bounds)> windows)
    {
        _virtual = set.VirtualBounds;
        _base = virtualBgra;
        _canvas = ImageOutput.ToBitmap(virtualBgra, _virtual.Width, _virtual.Height, PixelFormat.Format32bppPArgb);

        _windows = windows.Select(w => ToClient(Rectangle.Intersect(w.Bounds, _virtual))).Where(r => r.Width > 0 && r.Height > 0).ToList();
        _monitors = set.Monitors.Select(m => ToClient(m.Bounds)).ToList();

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        Bounds = _virtual;
        Text = "FrameBurst region";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

        var cursor = Cursor.Position;
        var home = _monitors.FirstOrDefault(m => m.Contains(cursor.X - _virtual.X, cursor.Y - _virtual.Y));
        _home = home.IsEmpty ? _monitors[0] : home;
        _theme = new OverlayTheme(Native.ScaleAt(_home.X + _virtual.X + _home.Width / 2, _home.Y + _virtual.Y + _home.Height / 2));
        _btn = _theme.Px(36);
        _magZoom = _theme.Px(9);
        BuildToolbar(_home);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast = null; InvalidateBar(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Spanning monitors with different DPIs must not trigger a resize of the overlay.
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _virtual;
        Activate();
        Native.ForceForeground(Handle);
        Log.Write($"overlay shown; foreground is overlay: {Native.GetForegroundWindow() == Handle}");
        _mouse = PointToClient(Cursor.Position);
        UpdateHover();
        Invalidate();
    }

    private Rectangle ToClient(Rectangle r) => new(r.X - _virtual.X, r.Y - _virtual.Y, r.Width, r.Height);
    private Rectangle ToVirtual(Rectangle r) => new(r.X + _virtual.X, r.Y + _virtual.Y, r.Width, r.Height);
    private float StrokeWidth => StrokeSizes[_sizeIdx];
    private float TextPx => 12 + _sizeIdx * 4;
    private int BlurRadius => 4 + _sizeIdx * 2;
    private bool ShowMagnifier => _tool == Tool.Select && !_bar.Contains(_mouse) && _current == null;

    // ================================================================== toolbar

    private void BuildToolbar(Rectangle monitor)
    {
        var tools = new (Tool T, string Tip)[]
        {
            (Tool.Select, "Select / capture (S)"), (Tool.Pen, "Pen (P)"), (Tool.Highlighter, "Highlighter (H)"),
            (Tool.Arrow, "Arrow (A)"), (Tool.Rectangle, "Rectangle (R)"), (Tool.Text, "Text (T)"),
            (Tool.Blur, "Blur (B)"),
        };
        int gap = _theme.Px(2), sepW = _theme.Px(13), pad = _theme.Px(6);
        int x = 0;
        void Add(ItemKind k, Tool t, Color c, string tip) { _items.Add(new Item(new Rectangle(x, 0, _btn, _btn), k, t, c, tip)); x += _btn + gap; }
        void Sep() { x += sepW - gap; _separators.Add(x - sepW / 2 - gap / 2); x += gap; }

        foreach (var (t, tip) in tools) Add(ItemKind.Tool, t, Color.Empty, tip);
        Sep();
        foreach (var c in Palette) Add(ItemKind.Color, Tool.Select, c, "Colour");
        Sep();
        Add(ItemKind.Size, Tool.Select, Color.Empty, "Size — mouse wheel or [ ]");
        Add(ItemKind.Undo, Tool.Select, Color.Empty, "Undo (Ctrl+Z)");
        Add(ItemKind.Redo, Tool.Select, Color.Empty, "Redo (Ctrl+Y)");
        Sep();
        Add(ItemKind.Cancel, Tool.Select, Color.Empty, "Cancel (Esc)");

        int width = x - gap + pad * 2, height = _btn + pad * 2;
        _bar = new Rectangle(monitor.Left + (monitor.Width - width) / 2, monitor.Top + _theme.Px(16), width, height);
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            _items[i] = it with { R = new Rectangle(it.R.X + _bar.X + pad, _bar.Y + pad, _btn, _btn) };
        }
        for (int i = 0; i < _separators.Count; i++) _separators[i] += _bar.X + pad;
        _hint = new Rectangle(monitor.Left, _bar.Bottom + _theme.Px(8), monitor.Width, _theme.Px(30));
    }

    /// <summary>Everything the toolbar and hint pill (with their shadows) can cover.</summary>
    private Rectangle ChromeBounds
    {
        get
        {
            int m = _theme.Px(14);
            return Rectangle.FromLTRB(_home.Left, _bar.Top - m, _home.Right, _hint.Bottom + m);
        }
    }

    private void InvalidateBar() => Invalidate(ChromeBounds);

    private Item? ItemAt(Point p) => _bar.Contains(p) ? _items.FirstOrDefault(i => i.R.Contains(p)) : null;

    private void ClickItem(Item it)
    {
        switch (it.Kind)
        {
            case ItemKind.Tool: SetTool(it.Tool); break;
            case ItemKind.Color:
                if (!it.Color.IsEmpty) { _color = it.Color; InvalidateBar(); }
                break;
            case ItemKind.Size: ChangeSize(+1, wrap: true); break;
            case ItemKind.Undo: Undo(); break;
            case ItemKind.Redo: Redo(); break;
            case ItemKind.Cancel: Cancel(); break;
        }
    }

    private void SetTool(Tool t)
    {
        CommitText();
        _tool = t;
        _dragging = false;
        _hoverWindow = null;
        UpdateHover();
        Cursor = t == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        Invalidate(); // outlines / magnifier visibility change
    }

    private void ChangeSize(int delta, bool wrap = false)
    {
        int n = _sizeIdx + delta;
        _sizeIdx = wrap ? (n + StrokeSizes.Length) % StrokeSizes.Length : Math.Clamp(n, 0, StrokeSizes.Length - 1);
        ShowToast(_tool switch
        {
            Tool.Text => $"Text size {TextPx:0} px",
            Tool.Blur => $"Blur strength {BlurRadius}",
            Tool.Highlighter => $"Highlighter size {StrokeWidth * 4 + 8:0} px",
            _ => $"Line width {StrokeWidth:0} px",
        });
    }

    private void ShowToast(string text)
    {
        _toast = text;
        _toastTimer.Stop();
        _toastTimer.Start();
        InvalidateBar();
    }

    private string HintText()
    {
        if (_toast != null) return _toast;
        if (_hoverItem != null) return _hoverItem.Tip;
        string main = _tool switch
        {
            Tool.Select => "Drag to capture  ·  Click a window  ·  Enter = this monitor  ·  F = all monitors",
            Tool.Pen => "Draw freehand  ·  wheel = width",
            Tool.Highlighter => "Highlight  ·  wheel = width",
            Tool.Arrow => "Drag to draw an arrow  ·  wheel = width",
            Tool.Rectangle => "Drag to draw a box  ·  wheel = width",
            Tool.Text => "Click to type  ·  Enter = done  ·  Shift+Enter = new line  ·  wheel = size",
            Tool.Blur => "Drag over anything to blur it permanently  ·  wheel = strength",
            _ => "",
        };
        return _tool == Tool.Select ? main + "  ·  Esc = cancel" : main + "  ·  S = back to capture  ·  Ctrl+Z = undo";
    }

    // ================================================================== edits

    private void Commit(AnnotationOp op)
    {
        _ops.Add(op);
        _redo.Clear();
        op.Apply(_canvas);
        _canvasVersion++;
        Invalidate(op.Bounds);
        InvalidateBar();
    }

    private void Undo()
    {
        if (_textBox != null) { CancelText(); return; }
        if (_ops.Count == 0) return;
        var op = _ops[^1];
        _ops.RemoveAt(_ops.Count - 1);
        _redo.Push(op);
        RebuildCanvas();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var op = _redo.Pop();
        _ops.Add(op);
        op.Apply(_canvas);
        _canvasVersion++;
        Invalidate(op.Bounds);
        InvalidateBar();
    }

    /// <summary>Restores the untouched frozen screen and replays the remaining edits.</summary>
    private void RebuildCanvas()
    {
        var bd = _canvas.LockBits(new Rectangle(0, 0, _canvas.Width, _canvas.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int row = _canvas.Width * 4;
            for (int y = 0; y < _canvas.Height; y++) Marshal.Copy(_base, y * row, bd.Scan0 + y * bd.Stride, row);
        }
        finally { _canvas.UnlockBits(bd); }
        foreach (var op in _ops) op.Apply(_canvas);
        _canvasVersion++;
        Invalidate();
    }

    private void StartText(Point p)
    {
        _textBox = new TextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(255, OverlayTheme.GlassOpaque),
            ForeColor = _color == Color.Black ? Color.White : _color,
            Font = TextOp.MakeFont(TextPx),
            Location = p,
            Size = new Size((int)(TextPx * 6), (int)(TextPx * 1.6f)),
            WordWrap = false,
        };
        _textBox.TextChanged += (_, _) =>
        {
            var sz = TextRenderer.MeasureText(_textBox.Text + "W", _textBox.Font);
            _textBox.Size = new Size(Math.Max((int)(TextPx * 6), sz.Width + 8), Math.Max((int)(TextPx * 1.6f), sz.Height + 6));
        };
        _textBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; CommitText(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CancelText(); }
        };
        Controls.Add(_textBox);
        _textBox.Focus();
    }

    private void CommitText()
    {
        if (_textBox == null) return;
        var tb = _textBox;
        _textBox = null;
        string text = tb.Text.TrimEnd();
        var at = new Point(tb.Left + 2, tb.Top + 2);
        Controls.Remove(tb);
        tb.Dispose();
        Focus();
        if (text.Length > 0) Commit(new TextOp(text.Replace("\r\n", "\n"), at, _color, TextPx));
    }

    private void CancelText()
    {
        if (_textBox == null) return;
        Controls.Remove(_textBox);
        _textBox.Dispose();
        _textBox = null;
        Focus();
    }

    // ================================================================== selection helpers

    private void UpdateHover()
    {
        _hoverWindow = null;
        if (_dragging || _tool != Tool.Select || _bar.Contains(_mouse)) return;
        foreach (var w in _windows)
            if (w.Contains(_mouse)) { _hoverWindow = w; return; }
        foreach (var m in _monitors)
            if (m.Contains(_mouse)) { _hoverWindow = m; return; }
    }

    private Rectangle ActiveRect => _tool != Tool.Select ? Rectangle.Empty : _dragging ? _selection : _hoverWindow ?? Rectangle.Empty;

    // ================================================================== input

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            if (_current != null) { var b = _current.Bounds; _current = null; Invalidate(b); }
            else if (_dragging) { _dragging = false; UpdateHover(); Invalidate(); }
            else if (_textBox != null) CancelText();
            else Cancel();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (ItemAt(e.Location) is { } item) { _pressedItem = item; ClickItem(item); InvalidateBar(); return; }
        if (_bar.Contains(e.Location)) return;

        var p = e.Location;
        switch (_tool)
        {
            case Tool.Select:
                _start = p;
                _dragging = true;
                _selection = new Rectangle(p, new Size(1, 1));
                RefreshDirty();
                break;
            case Tool.Pen: _current = new StrokeOp(_color, StrokeWidth, false, p); break;
            case Tool.Highlighter: _current = new StrokeOp(_color, StrokeWidth, true, p); break;
            case Tool.Arrow: _current = new ArrowOp(_color, StrokeWidth, p); break;
            case Tool.Rectangle: _current = new RectOp(_color, StrokeWidth, p); break;
            case Tool.Blur: _current = new BlurOp(BlurRadius, p); break;
            case Tool.Text:
                if (_textBox != null) CommitText();
                else StartText(p);
                break;
        }
        if (_current != null) { Capture = true; Invalidate(_current.Bounds); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.Location;

        var hover = ItemAt(_mouse);
        if (hover != _hoverItem) { _hoverItem = hover; InvalidateBar(); }
        if (_current == null && !_dragging)
            Cursor = _bar.Contains(_mouse) ? Cursors.Hand : _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;

        if (_current != null)
        {
            var before = _current.Bounds;
            switch (_current)
            {
                case StrokeOp s: s.Add(_mouse); break;
                case ArrowOp a: a.To = _mouse; break;
                case RectOp r: r.To = _mouse; break;
                case BlurOp b: b.To = _mouse; break;
            }
            Invalidate(Rectangle.Union(before, _current.Bounds));
            return;
        }

        if (_dragging)
            _selection = Rectangle.FromLTRB(Math.Min(_start.X, _mouse.X), Math.Min(_start.Y, _mouse.Y),
                                            Math.Max(_start.X, _mouse.X) + 1, Math.Max(_start.Y, _mouse.Y) + 1);
        else
            UpdateHover();
        RefreshDirty();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_pressedItem != null) { _pressedItem = null; InvalidateBar(); }

        if (_current != null)
        {
            var op = _current;
            _current = null;
            Capture = false;
            bool meaningful = op switch
            {
                ArrowOp a => Distance(a.From, a.To) >= 4,
                RectOp r => r.Rect.Width >= 2 && r.Rect.Height >= 2,
                BlurOp b => b.Rect.Width >= 3 && b.Rect.Height >= 3,
                _ => true,
            };
            if (meaningful) Commit(op);
            else Invalidate(op.Bounds);
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        bool click = _selection.Width <= 3 && _selection.Height <= 3;
        if (click)
        {
            UpdateHover();
            if (_hoverWindow is { } w) Accept(w);
        }
        else Accept(_selection);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Y - b.Y) * (double)(a.Y - b.Y));

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_textBox != null) return;
        ChangeSize(e.Delta > 0 ? 1 : -1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_textBox != null) return; // typing goes to the text box

        if (e.Control)
        {
            if (e.KeyCode == Keys.Z && !e.Shift) Undo();
            else if (e.KeyCode == Keys.Y || (e.KeyCode == Keys.Z && e.Shift)) Redo();
            return;
        }
        switch (e.KeyCode)
        {
            case Keys.Escape: Cancel(); break;
            case Keys.Enter:
                if (_dragging && _selection.Width > 1) Accept(_selection);
                else
                {
                    var mon = _monitors.FirstOrDefault(m => m.Contains(_mouse));
                    if (!mon.IsEmpty) Accept(mon);
                }
                break;
            case Keys.F: Accept(new Rectangle(Point.Empty, _virtual.Size)); break;
            case Keys.S: case Keys.V: SetTool(Tool.Select); break;
            case Keys.P: SetTool(Tool.Pen); break;
            case Keys.H: SetTool(Tool.Highlighter); break;
            case Keys.A: SetTool(Tool.Arrow); break;
            case Keys.R: SetTool(Tool.Rectangle); break;
            case Keys.T: SetTool(Tool.Text); break;
            case Keys.B: SetTool(Tool.Blur); break;
            case Keys.OemOpenBrackets: ChangeSize(-1); break;
            case Keys.OemCloseBrackets: ChangeSize(+1); break;
            case Keys.Left: Nudge(-1, 0); break;
            case Keys.Right: Nudge(1, 0); break;
            case Keys.Up: Nudge(0, -1); break;
            case Keys.Down: Nudge(0, 1); break;
        }
    }

    private void Nudge(int dx, int dy)
    {
        var p = Cursor.Position;
        Cursor.Position = new Point(p.X + dx, p.Y + dy);
    }

    private void Accept(Rectangle client)
    {
        CommitText();
        Result = ToVirtual(client);
        if (_ops.Count > 0)
        {
            // Read the edited canvas back as straight BGRA (alpha is 255 on screen pixels, so this is exact).
            var bgra = new byte[_virtual.Width * _virtual.Height * 4];
            var bd = _canvas.LockBits(new Rectangle(0, 0, _canvas.Width, _canvas.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int row = _canvas.Width * 4;
                for (int y = 0; y < _canvas.Height; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, bgra, y * row, row);
            }
            finally { _canvas.UnlockBits(bd); }
            AnnotatedBgra = bgra;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        Result = null;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    // ================================================================== painting

    private void RefreshDirty()
    {
        foreach (var r in _lastDirty) Invalidate(r);
        _lastDirty.Clear();
        var active = ActiveRect;
        if (!active.IsEmpty)
        {
            // Only the outline strips and the label change as the selection moves.
            var o = Rectangle.Inflate(active, 3, 3);
            _lastDirty.Add(new Rectangle(o.Left, o.Top, o.Width, 6));
            _lastDirty.Add(new Rectangle(o.Left, o.Bottom - 6, o.Width, 6));
            _lastDirty.Add(new Rectangle(o.Left, o.Top, 6, o.Height));
            _lastDirty.Add(new Rectangle(o.Right - 6, o.Top, 6, o.Height));
            _lastDirty.Add(LabelRect(active));
        }
        _lastDirty.Add(Rectangle.Inflate(MagnifierRect(), 2, 2));
        foreach (var r in _lastDirty) Invalidate(r);
    }

    private Rectangle LabelRect(Rectangle sel)
    {
        var size = new Size(_theme.Px(240), _theme.Px(22));
        int y = sel.Top - size.Height - _theme.Px(6);
        if (y < 0) y = sel.Top + _theme.Px(6);
        return new Rectangle(sel.Left, y, size.Width, size.Height);
    }

    private Rectangle MagnifierRect()
    {
        int size = MagCells * _magZoom;
        int h = size + _theme.Px(40);
        int off = _theme.Px(24);
        int x = _mouse.X + off, y = _mouse.Y + off;
        var mon = _monitors.FirstOrDefault(m => m.Contains(_mouse));
        if (mon.IsEmpty) mon = ClientRectangle;
        if (x + size > mon.Right) x = _mouse.X - off - size;
        if (y + h > mon.Bottom) y = _mouse.Y - off - h;
        return new Rectangle(x, y, size, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_canvas, clip, clip, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        g.PixelOffsetMode = PixelOffsetMode.Default;

        if (_current != null && _current.Bounds.IntersectsWith(clip))
        {
            var state = g.Save();
            _current.Render(g);
            g.Restore(state);
        }

        var active = ActiveRect;
        if (!active.IsEmpty)
        {
            // Two-tone outline just outside the area, so it never covers a captured pixel.
            var outline = Rectangle.Inflate(active, 1, 1);
            if (!_monitors.Any(m => m.Contains(Rectangle.Inflate(outline, 1, 1)))) outline = Rectangle.Inflate(active, -2, -2);
            g.SmoothingMode = SmoothingMode.None;
            using (var dark = new Pen(OverlayTheme.Scrim, 3))
                g.DrawRectangle(dark, outline.X - 1, outline.Y - 1, outline.Width + 1, outline.Height + 1);
            using (var pen = new Pen(OverlayTheme.Accent, 1) { DashStyle = _dragging ? DashStyle.Solid : DashStyle.Dash })
                g.DrawRectangle(pen, outline.X, outline.Y, outline.Width - 1, outline.Height - 1);
            DrawLabel(g, active);
        }

        if (clip.IntersectsWith(ChromeBounds)) DrawToolbar(g);
        if (ShowMagnifier) DrawMagnifier(g);
    }

    private void DrawToolbar(Graphics g)
    {
        _barGlass.Paint(g, _theme, _canvas, _bar, _canvasVersion, OverlayTheme.GlassTint);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var sep = new Pen(OverlayTheme.Divider))
        {
            int inset = _theme.Px(10);
            foreach (int sx in _separators) g.DrawLine(sep, sx, _bar.Y + inset, sx, _bar.Bottom - inset);
        }

        foreach (var it in _items)
        {
            bool selected = it.Kind == ItemKind.Tool && it.Tool == _tool;
            bool disabled = (it.Kind == ItemKind.Undo && _ops.Count == 0 && _textBox == null) || (it.Kind == ItemKind.Redo && _redo.Count == 0);
            if (selected) _theme.FillRound(g, it.R, OverlayTheme.AccentStrong, OverlayTheme.RadiusControl);
            else if (!disabled && it == _pressedItem) _theme.FillRound(g, it.R, OverlayTheme.Pressed, OverlayTheme.RadiusControl);
            else if (!disabled && it == _hoverItem) _theme.FillRound(g, it.R, OverlayTheme.Hover, OverlayTheme.RadiusControl);
            var fg = disabled ? OverlayTheme.TextDisabled : OverlayTheme.Text;
            switch (it.Kind)
            {
                case ItemKind.Tool: _theme.DrawGlyph(g, ToolGlyph(it.Tool), it.R, fg); break;
                case ItemKind.Color: DrawSwatch(g, it); break;
                case ItemKind.Size:
                {
                    // Dot sized like the stroke, in the current colour, with a light edge so dark colours show.
                    float d = Math.Clamp(StrokeWidth + 2, 5, 22) * (float)_theme.Scale;
                    float x0 = it.R.X + (_btn - d) / 2f, y0 = it.R.Y + (_btn - d) / 2f;
                    using (var b = new SolidBrush(_color)) g.FillEllipse(b, x0, y0, d, d);
                    using (var edge = new Pen(OverlayTheme.SwatchEdge, _theme.PxF(1))) g.DrawEllipse(edge, x0, y0, d, d);
                    break;
                }
                case ItemKind.Undo: _theme.DrawGlyph(g, OverlayTheme.GlyphUndo, it.R, fg); break;
                case ItemKind.Redo: _theme.DrawGlyph(g, OverlayTheme.GlyphRedo, it.R, fg); break;
                case ItemKind.Cancel: _theme.DrawGlyph(g, OverlayTheme.GlyphClose, it.R, fg); break;
            }
        }

        // Hint / toast pill under the bar, sized to its text.
        string hint = HintText();
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var size = g.MeasureString(hint, _theme.Body);
        int w = Math.Min(Math.Max((int)size.Width + _theme.Px(32), _theme.Px(200)), _hint.Width);
        var box = new Rectangle(_hint.X + (_hint.Width - w) / 2, _hint.Y, w, _hint.Height);
        _hintGlass.Paint(g, _theme, _canvas, box, _canvasVersion, _toast != null ? OverlayTheme.ToastTint : OverlayTheme.GlassTint);
        using var text = new SolidBrush(OverlayTheme.Text);
        using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(hint, _theme.Body, text, box, fmt);
    }

    private static string ToolGlyph(Tool tool) => tool switch
    {
        Tool.Select => OverlayTheme.GlyphSelect,
        Tool.Pen => OverlayTheme.GlyphPen,
        Tool.Highlighter => OverlayTheme.GlyphHighlighter,
        Tool.Arrow => OverlayTheme.GlyphArrow,
        Tool.Rectangle => OverlayTheme.GlyphRectangle,
        Tool.Text => OverlayTheme.GlyphText,
        Tool.Blur => OverlayTheme.GlyphBlur,
        _ => "",
    };

    private void DrawSwatch(Graphics g, Item it)
    {
        var c = it.Color;
        bool active = c.ToArgb() == _color.ToArgb();
        var center = new PointF(it.R.X + _btn / 2f, it.R.Y + _btn / 2f);
        float d = _theme.PxF(16);
        using (var b = new SolidBrush(c)) g.FillEllipse(b, center.X - d / 2, center.Y - d / 2, d, d);
        using (var edge = new Pen(OverlayTheme.SwatchEdge, _theme.PxF(1))) g.DrawEllipse(edge, center.X - d / 2, center.Y - d / 2, d, d);
        if (active)
        {
            // A ring with a gap: the shape marks the choice, not just the colour.
            float rd = _theme.PxF(24);
            using var ring = new Pen(OverlayTheme.Text, _theme.PxF(1.5));
            g.DrawEllipse(ring, center.X - rd / 2, center.Y - rd / 2, rd, rd);
        }
    }

    private void DrawLabel(Graphics g, Rectangle sel)
    {
        var r = LabelRect(sel);
        var v = ToVirtual(sel);
        string text = $"{v.Width} × {v.Height}   @ {v.X}, {v.Y}";
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var sz = g.MeasureString(text, _theme.Mono);
        var box = new Rectangle(r.X, r.Y, Math.Min((int)sz.Width + _theme.Px(14), r.Width), r.Height);
        _theme.PaintElevated(g, box, OverlayTheme.RadiusControl);
        using var fg = new SolidBrush(OverlayTheme.Text);
        g.DrawString(text, _theme.Mono, fg, box.X + _theme.Px(7), box.Y + (box.Height - sz.Height) / 2);
    }

    private void DrawMagnifier(Graphics g)
    {
        var r = MagnifierRect();
        int size = MagCells * _magZoom;
        var src = new Rectangle(_mouse.X - MagCells / 2, _mouse.Y - MagCells / 2, MagCells, MagCells);
        var dst = new Rectangle(r.X, r.Y, size, size);

        _theme.PaintElevated(g, r);
        var state = g.Save();
        using (var clipPath = OverlayTheme.RoundRect(new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), _theme.PxF(OverlayTheme.RadiusSurface)))
            g.SetClip(clipPath);
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_canvas, dst, src, GraphicsUnit.Pixel);
        g.PixelOffsetMode = PixelOffsetMode.Default;

        using (var grid = new Pen(OverlayTheme.Divider))
            for (int i = 1; i < MagCells; i++)
            {
                g.DrawLine(grid, dst.X + i * _magZoom, dst.Y, dst.X + i * _magZoom, dst.Bottom);
                g.DrawLine(grid, dst.X, dst.Y + i * _magZoom, dst.Right, dst.Y + i * _magZoom);
            }
        int c = MagCells / 2 * _magZoom;
        using (var center = new Pen(OverlayTheme.Accent, 2)) g.DrawRectangle(center, dst.X + c, dst.Y + c, _magZoom, _magZoom);
        using (var divider = new Pen(OverlayTheme.Border)) g.DrawLine(divider, r.X, dst.Bottom, r.Right, dst.Bottom);
        g.Restore(state);

        // Readout shows the true screen colour (without edits), the same value the picker copies.
        string color = "";
        int pad = _theme.Px(8);
        if (_mouse.X >= 0 && _mouse.Y >= 0 && _mouse.X < _virtual.Width && _mouse.Y < _virtual.Height)
        {
            int o = (_mouse.Y * _virtual.Width + _mouse.X) * 4;
            color = $"#{_base[o + 2]:X2}{_base[o + 1]:X2}{_base[o]:X2}";
            int sws = _theme.Px(22);
            var swr = new Rectangle(r.Right - pad - sws, dst.Bottom + (r.Bottom - dst.Bottom - sws) / 2, sws, sws);
            using (var sw = new SolidBrush(Color.FromArgb(_base[o + 2], _base[o + 1], _base[o]))) g.FillRectangle(sw, swr);
            using (var edge = new Pen(OverlayTheme.SwatchEdge)) g.DrawRectangle(edge, swr);
        }
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var fg = new SolidBrush(OverlayTheme.Text);
        using var fg2 = new SolidBrush(OverlayTheme.TextSecondary);
        g.DrawString($"{_mouse.X + _virtual.X}, {_mouse.Y + _virtual.Y}", _theme.Mono, fg2, r.X + pad, dst.Bottom + _theme.Px(4));
        g.DrawString(color, _theme.Mono, fg, r.X + pad, dst.Bottom + _theme.Px(20));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toastTimer.Dispose();
            _textBox?.Dispose();
            _canvas.Dispose();
            _barGlass.Dispose(); _hintGlass.Dispose();
            _theme.Dispose();
        }
        base.Dispose(disposing);
    }
}
