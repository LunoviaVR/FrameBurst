using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FrameBurst.UI;

/// <summary>
/// A level-1 glass surface holding related <see cref="SettingsRow"/>s. Rows are separated by hairline dividers
/// rather than each sitting in its own card. Styled by the implicit style in UI/Theme/Controls.xaml.
/// </summary>
public sealed partial class SettingsGroup : StackPanel
{
    public SettingsGroup() => Loaded += (_, _) => UpdateDividers();

    /// <summary>Shows the divider on every visible row except the first.</summary>
    public void UpdateDividers()
    {
        bool first = true;
        foreach (var row in Children.OfType<SettingsRow>())
        {
            if (row.Visibility != Visibility.Visible) continue;
            row.ShowDivider = !first;
            first = false;
        }
    }
}
