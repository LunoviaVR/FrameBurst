using FrameBurst.Win32;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Brush = Microsoft.UI.Xaml.Media.Brush;
using Button = Microsoft.UI.Xaml.Controls.Button;
using Image = Microsoft.UI.Xaml.Controls.Image;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;

namespace FrameBurst.UI;

internal enum DialogKind { Info, Warning, Error }

/// <summary>A small WinUI message window styled like the Settings window (Mica, custom title bar, footer buttons).</summary>
internal sealed class MessageDialog : Microsoft.UI.Xaml.Window
{
    private readonly TaskCompletionSource<bool> _result = new();

    private MessageDialog(string title, string heading, string message, DialogKind kind, string primary, string? secondary)
    {
        Title = "FrameBurst";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FrameBurst.ico");
        AppWindow.SetIcon(iconPath);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title bar
        var titleBar = new Grid { Padding = new Thickness(16, 0, 0, 0) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new Image { Width = 16, Height = 16, Source = new BitmapImage(new Uri(iconPath)) });
        titleRow.Children.Add(new TextBlock { Text = title, Style = Res<Style>("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
        titleBar.Children.Add(titleRow);
        root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        // Body: status glyph + heading + message
        var body = new Grid { Padding = new Thickness(24, 12, 24, 24), ColumnSpacing = 16 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var (glyph, brush) = kind switch
        {
            DialogKind.Warning => ("", "SystemFillColorCautionBrush"),
            DialogKind.Error => ("", "SystemFillColorCriticalBrush"),
            _ => ("", "AccentTextFillColorPrimaryBrush"),
        };
        body.Children.Add(new FontIcon { Glyph = glyph, FontSize = 28, Foreground = Res<Brush>(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var text = new StackPanel { Spacing = 8 };
        text.Children.Add(new TextBlock { Text = heading, Style = Res<Style>("SubtitleTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(message))
        {
            var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Res<Brush>("TextFillColorSecondaryBrush") };
            text.Children.Add(new ScrollViewer { Content = msg, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        Grid.SetColumn(text, 1);
        body.Children.Add(text);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // Footer
        var footer = new Grid
        {
            Padding = new Thickness(24, 12, 24, 12), ColumnSpacing = 8,
            Background = Res<Brush>("LayerFillColorDefaultBrush"),
            BorderBrush = Res<Brush>("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ok = new Button { Content = primary, MinWidth = 120, Style = Res<Style>("AccentButtonStyle") };
        ok.Click += (_, _) => Finish(true);
        Grid.SetColumn(ok, secondary == null ? 2 : 1);
        footer.Children.Add(ok);
        if (secondary != null)
        {
            var cancel = new Button { Content = secondary, MinWidth = 120 };
            cancel.Click += (_, _) => Finish(false);
            Grid.SetColumn(cancel, 2);
            footer.Children.Add(cancel);
        }
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) Finish(false);
            else if (e.Key == Windows.System.VirtualKey.Enter) Finish(true);
        };
        root.Loaded += (_, _) => ok.Focus(FocusState.Programmatic);
        Content = root;
        Closed += (_, _) => _result.TrySetResult(false);

        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsMinimizable = false; p.IsMaximizable = false; p.IsResizable = false; }
        // Size the window to its content at a fixed width.
        const double width = 460;
        root.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        double height = Math.Max(root.DesiredSize.Height, 180);
        double scale = Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
    }

    private static T Res<T>(string key) => (T)Microsoft.UI.Xaml.Application.Current.Resources[key];

    private void Finish(bool result)
    {
        _result.TrySetResult(result);
        Close();
    }

    private Task<bool> ShowAsync()
    {
        Activate();
        Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return _result.Task;
    }

    /// <summary>Shows a message with a single OK button.</summary>
    public static Task Show(string heading, string message = "", DialogKind kind = DialogKind.Info, string title = "FrameBurst") =>
        new MessageDialog(title, heading, message, kind, "OK", null).ShowAsync();

    /// <summary>Asks a question; true when the primary button (or Enter) was chosen.</summary>
    public static Task<bool> Ask(string heading, string message, string primary, string secondary = "Not now", string title = "FrameBurst") =>
        new MessageDialog(title, heading, message, DialogKind.Info, primary, secondary).ShowAsync();
}
