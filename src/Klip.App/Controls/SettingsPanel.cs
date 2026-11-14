using System.Windows;
using System.Windows.Controls;

namespace Klip.App.Controls;

/// <summary>
/// Vertical stack that always takes the full available width (up to MaxWidth) while
/// staying left aligned. A plain StackPanel with HorizontalAlignment=Left shrinks to its
/// widest child, so short pages ended up with narrow cards.
/// </summary>
public sealed class SettingsPanel : StackPanel
{
    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        if (!double.IsInfinity(constraint.Width))
            size.Width = constraint.Width;
        return size;
    }
}
