using FrameBurst.Win32;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace FrameBurst.UI;

/// <summary>
/// The custom title bar shared by every FrameBurst WinUI window: content extends into the title bar, caption
/// buttons are transparent and follow the theme, the title dims while the window is inactive, and the window is
/// sized in DIPs (DPI-correct) and centred on its display.
/// </summary>
internal static class WindowChrome
{
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "FrameBurst.ico");

    /// <param name="titleBar">The element that acts as the drag region.</param>
    /// <param name="title">Title text that dims while the window is inactive (optional).</param>
    /// <param name="tall">48 px caption area (main windows) instead of 32 px (dialogs).</param>
    public static void Apply(Window window, FrameworkElement root, FrameworkElement titleBar, UIElement? title, bool tall)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        window.AppWindow.SetIcon(IconPath);
        var bar = window.AppWindow.TitleBar;
        bar.PreferredHeightOption = tall ? TitleBarHeightOption.Tall : TitleBarHeightOption.Standard;

        void Recolor() => ApplyCaptionColors(bar, root.ActualTheme);
        root.ActualThemeChanged += (_, _) => Recolor();
        root.Loaded += (_, _) => Recolor();
        Recolor();

        if (title != null)
            window.Activated += (_, e) => title.Opacity = e.WindowActivationState == WindowActivationState.Deactivated ? 0.55 : 1;
    }

    private static void ApplyCaptionColors(AppWindowTitleBar bar, ElementTheme theme)
    {
        Color fg = ThemeColor("TextPrimaryColor", theme), muted = ThemeColor("TextMutedColor", theme);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = bar.ButtonHoverForegroundColor = bar.ButtonPressedForegroundColor = fg;
        bar.ButtonInactiveForegroundColor = muted;
        bar.ButtonHoverBackgroundColor = BrushColor("HoverFillBrush", theme);
        bar.ButtonPressedBackgroundColor = BrushColor("PressedFillBrush", theme);
    }

    /// <summary>Sizes the window's client area in DIPs on its current display and centres it in the work area.</summary>
    public static void SizeAndCenter(Window window, double width, double height)
    {
        double scale = Scale(window);
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        // Client size, so content gets exactly what was asked for regardless of (invisible) frame borders.
        window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
            Math.Min((int)Math.Round(width * scale), area.Width), Math.Min((int)Math.Round(height * scale), area.Height)));
        var outer = window.AppWindow.Size;
        window.AppWindow.Move(new Windows.Graphics.PointInt32(
            area.X + Math.Max(0, (area.Width - outer.Width) / 2), area.Y + Math.Max(0, (area.Height - outer.Height) / 2)));
    }

    /// <summary>Sets the smallest size (DIPs) the user can resize the window to.</summary>
    public static void SetMinimumSize(Window window, double width, double height)
    {
        if (window.AppWindow.Presenter is not OverlappedPresenter p) return;
        double scale = Scale(window);
        p.PreferredMinimumWidth = (int)Math.Round(width * scale);
        p.PreferredMinimumHeight = (int)Math.Round(height * scale);
    }

    public static double Scale(Window window) => Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;

    /// <summary>Reads a token for a specific theme (title-bar colours are not XAML, so ThemeResource can't reach them).</summary>
    private static object? Token(string key, ElementTheme theme)
    {
        string dict = theme == ElementTheme.Light ? "Light" : "Default";
        foreach (var md in Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries)
            if (md.ThemeDictionaries.TryGetValue(dict, out var td) && td is ResourceDictionary rd && rd.TryGetValue(key, out var v))
                return v;
        return null;
    }

    private static Color ThemeColor(string key, ElementTheme theme) => Token(key, theme) is Color c ? c : Colors.Gray;

    /// <summary>A translucent brush token flattened to a colour with the brush opacity in its alpha.</summary>
    private static Color BrushColor(string key, ElementTheme theme) => Token(key, theme) is SolidColorBrush b
        ? Color.FromArgb((byte)Math.Round(b.Color.A * b.Opacity), b.Color.R, b.Color.G, b.Color.B)
        : Colors.Transparent;
}
