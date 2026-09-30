using FrameBurst.Win32;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FrameBurst.UI;

/// <summary>
/// Shows a WinUI <see cref="MenuFlyout"/> for the tray icon. The shell has no XAML tray menu, so the flyout is
/// anchored to an invisible 1×1 tool window moved to the cursor; the flyout itself pops out of that window
/// (ShouldConstrainToRootBounds = false) and light-dismisses when the user clicks elsewhere.
/// </summary>
internal sealed class TrayMenu
{
    private readonly Microsoft.UI.Xaml.Window _host;
    private readonly Grid _root = new();
    private readonly IntPtr _hwnd;

    public TrayMenu()
    {
        _host = new Microsoft.UI.Xaml.Window { Content = _root, Title = "FrameBurst menu" };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_host);
        var app = _host.AppWindow;
        app.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        app.IsShownInSwitchers = false;

        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE_MASK = ~0x08000000;
        int ex = Native.GetWindowLongW(_hwnd, GWL_EXSTYLE);
        Native.SetWindowLongW(_hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW | WS_EX_LAYERED) & WS_EX_NOACTIVATE_MASK);
        Native.SetLayeredWindowAttributes(_hwnd, 0, 0, 0x2 /* LWA_ALPHA */);
    }

    public void Show(MenuFlyout flyout)
    {
        Native.GetCursorPos(out var pt);
        _host.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(pt.X, pt.Y, 1, 1));
        _host.Activate();
        // The tray click came to us, so we may take the foreground; without it the menu would not dismiss.
        Native.SetForegroundWindow(_hwnd);

        flyout.ShouldConstrainToRootBounds = false;
        flyout.Closed += (_, _) => _host.AppWindow.Hide();
        void Open() => flyout.ShowAt(_root, new FlyoutShowOptions
        {
            Position = new Windows.Foundation.Point(0, 0),
            Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Standard,
        });
        if (_root.XamlRoot != null && _root.IsLoaded) Open();
        else
        {
            void OnLoaded(object s, RoutedEventArgs e) { _root.Loaded -= OnLoaded; Open(); }
            _root.Loaded += OnLoaded;
        }
    }

    public void Close() => _host.Close();

    public static MenuFlyoutItem Item(string text, string glyph, Action action, string? shortcut = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (!string.IsNullOrEmpty(shortcut)) item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += (_, _) => action();
        return item;
    }
}
