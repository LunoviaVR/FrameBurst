using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FrameBurst.UI;

/// <summary>
/// One setting inside a <see cref="SettingsGroup"/>: icon, header, description, the action control (Content) on the
/// right and an optional full-width Footer under it. The template lives in UI/Theme/Controls.xaml. When the row is
/// narrower than the RowStackBreakpoint token the action moves under the text, so small windows stay usable.
/// </summary>
public sealed partial class SettingsRow : ContentControl
{
    private Grid? _layout;
    private FrameworkElement? _action, _divider, _footer, _description;
    private bool _stacked;

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingsRow), new PropertyMetadata(null, (d, _) => ((SettingsRow)d).UpdateIcon()));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconElement), typeof(SettingsRow), new PropertyMetadata(null, (d, _) => ((SettingsRow)d).UpdateIcon()));
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsRow), new PropertyMetadata(null, (d, _) => ((SettingsRow)d).UpdateAutomationName()));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(object), typeof(SettingsRow), new PropertyMetadata(null, (d, _) => ((SettingsRow)d).UpdateParts()));
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(object), typeof(SettingsRow), new PropertyMetadata(null, (d, _) => ((SettingsRow)d).UpdateParts()));
    public static readonly DependencyProperty ShowDividerProperty = DependencyProperty.Register(
        nameof(ShowDivider), typeof(bool), typeof(SettingsRow), new PropertyMetadata(false, (d, _) => ((SettingsRow)d).UpdateParts()));
    public static readonly DependencyProperty ResolvedIconProperty = DependencyProperty.Register(
        nameof(ResolvedIcon), typeof(IconElement), typeof(SettingsRow), new PropertyMetadata(null));

    /// <summary>Segoe Fluent Icons glyph for the row; ignored when <see cref="Icon"/> is set.</summary>
    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    /// <summary>A custom icon element (e.g. a named FontIcon whose glyph changes with state).</summary>
    public IconElement? Icon { get => (IconElement?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    /// <summary>Secondary text: a string, or any element (e.g. a TextBlock with a hyperlink).</summary>
    public object? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    /// <summary>Full-width content under the header and action (text boxes, progress bars).</summary>
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    /// <summary>Draws the hairline above the row; set by <see cref="SettingsGroup"/> on every row but the first.</summary>
    public bool ShowDivider { get => (bool)GetValue(ShowDividerProperty); set => SetValue(ShowDividerProperty, value); }
    /// <summary>The icon the template shows: <see cref="Icon"/>, or a FontIcon built from <see cref="Glyph"/>.</summary>
    public IconElement? ResolvedIcon { get => (IconElement?)GetValue(ResolvedIconProperty); set => SetValue(ResolvedIconProperty, value); }

    public SettingsRow()
    {
        IsTabStop = false;
        SizeChanged += (_, e) => UpdateLayoutMode(e.NewSize.Width);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _layout = GetTemplateChild("LayoutRoot") as Grid;
        _action = GetTemplateChild("ActionPresenter") as FrameworkElement;
        _divider = GetTemplateChild("Divider") as FrameworkElement;
        _footer = GetTemplateChild("FooterPresenter") as FrameworkElement;
        _description = GetTemplateChild("DescriptionPresenter") as FrameworkElement;
        _stacked = false;
        UpdateParts();
        UpdateLayoutMode(ActualWidth);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        UpdateAutomationName();
    }

    private void UpdateParts()
    {
        if (_divider != null) _divider.Visibility = ShowDivider ? Visibility.Visible : Visibility.Collapsed;
        if (_footer != null) _footer.Visibility = Footer != null ? Visibility.Visible : Visibility.Collapsed;
        // An empty presenter must not be measured (it throws) and would leave a gap under single-line rows.
        if (_description != null) _description.Visibility = Description is null or "" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateIcon() => ResolvedIcon = Icon ?? (string.IsNullOrEmpty(Glyph) ? null : new FontIcon { Glyph = Glyph });

    /// <summary>Screen readers announce the action control by the row's header unless it already has a name.</summary>
    private void UpdateAutomationName()
    {
        if (Content is DependencyObject d && !string.IsNullOrEmpty(Header) && string.IsNullOrEmpty(AutomationProperties.GetName(d)))
            AutomationProperties.SetName(d, Header);
    }

    private void UpdateLayoutMode(double width)
    {
        if (_layout == null || _action == null || width <= 0) return;
        double breakpoint = Microsoft.UI.Xaml.Application.Current.Resources["RowStackBreakpoint"] is double b ? b : 520;
        bool stacked = width < breakpoint;
        if (stacked == _stacked) return;
        _stacked = stacked;
        // Wide: [icon][text][action]. Narrow: the action drops to its own line under the text.
        Grid.SetRow(_action, stacked ? 1 : 0);
        Grid.SetColumn(_action, stacked ? 1 : 2);
        _action.HorizontalAlignment = stacked ? Microsoft.UI.Xaml.HorizontalAlignment.Left : Microsoft.UI.Xaml.HorizontalAlignment.Right;
        _action.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }
}
