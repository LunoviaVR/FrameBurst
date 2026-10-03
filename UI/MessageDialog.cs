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

/// <summary>A small WinUI message window: elevated acrylic surface, the shared title bar, status glyph and footer buttons.</summary>
internal sealed class MessageDialog : Microsoft.UI.Xaml.Window
{
    private readonly TaskCompletionSource<bool> _result = new();
    private const double Width = 480;

    private MessageDialog(string title, string heading, string message, DialogKind kind, string primary, string? secondary)
    {
        Title = "FrameBurst";
        // Level 2 material: dialogs float above everything, so they get desktop acrylic instead of Mica.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title bar
        var titleBar = new Grid { Padding = new Thickness(16, 0, 0, 0) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Res<double>("Space12"), VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new Image { Width = 16, Height = 16, Source = new BitmapImage(new Uri(WindowChrome.IconPath)) });
        titleRow.Children.Add(new TextBlock { Text = title, Style = Res<Style>("CaptionTextBlockStyle"), Foreground = Res<Brush>("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        titleBar.Children.Add(titleRow);
        root.Children.Add(titleBar);
        WindowChrome.Apply(this, root, titleBar, titleRow, tall: false);

        // Body: status glyph on a tinted plate + heading + message
        var body = new Grid { Padding = new Thickness(24, 12, 24, 24), ColumnSpacing = Res<double>("Space16") };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var (glyph, brush) = kind switch
        {
            DialogKind.Warning => ("", "WarningBrush"),
            DialogKind.Error => ("", "DangerBrush"),
            _ => ("", "AccentBrush"),
        };
        body.Children.Add(new Border
        {
            Style = Res<Style>("StatusPlateStyle"),
            Child = new FontIcon { Glyph = glyph, FontSize = 20, Foreground = Res<Brush>(brush) },
        });
        var text = new StackPanel { Spacing = Res<double>("Space8"), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = heading, Style = Res<Style>("SubtitleTextBlockStyle"), Foreground = Res<Brush>("TextPrimaryBrush"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(message))
        {
            var msg = new TextBlock { Text = message, Style = Res<Style>("BodySecondaryStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            text.Children.Add(new ScrollViewer { Content = msg, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        Grid.SetColumn(text, 1);
        body.Children.Add(text);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // Footer: primary action first, cancel/secondary on the right (Windows dialog order).
        var footer = new Grid { Style = Res<Style>("FooterBarStyle") };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        double minWidth = Res<double>("FooterButtonMinWidth");
        var ok = new Button { Content = primary, MinWidth = minWidth, Style = Res<Style>("PrimaryButtonStyle") };
        ok.Click += (_, _) => Finish(true);
        Grid.SetColumn(ok, secondary == null ? 2 : 1);
        footer.Children.Add(ok);
        if (secondary != null)
        {
            var cancel = new Button { Content = secondary, MinWidth = minWidth, Style = Res<Style>("SecondaryButtonStyle") };
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
        root.Loaded += (_, _) =>
        {
            // Templates (the message ScrollViewer) only exist once loaded, so re-fit the height to the real content.
            root.Measure(new Windows.Foundation.Size(Width, double.PositiveInfinity));
            WindowChrome.SizeAndCenter(this, Width, Math.Max(root.DesiredSize.Height, 180));
            ok.Focus(FocusState.Programmatic);
        };
        Content = root;
        Closed += (_, _) => _result.TrySetResult(false);

        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsMinimizable = false; p.IsMaximizable = false; p.IsResizable = false; }
        // Size the window to its content at a fixed width (refined once loaded, see above).
        root.Measure(new Windows.Foundation.Size(Width, double.PositiveInfinity));
        WindowChrome.SizeAndCenter(this, Width, Math.Max(root.DesiredSize.Height, 180));
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
