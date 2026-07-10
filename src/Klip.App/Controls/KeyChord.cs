using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Klip.App.Controls;

/// <summary>
/// Renders a hotkey string like "Ctrl+Shift+V" as Windows 11 style keycaps,
/// with a small "+" between each key. Set <see cref="Chord"/> to the gesture.
/// </summary>
public sealed class KeyChord : ItemsControl
{
    public KeyChord()
    {
        // lay the keys out in a row
        var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
        panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        ItemsPanel = new ItemsPanelTemplate(panelFactory);
    }

    public static readonly DependencyProperty ChordProperty =
        DependencyProperty.Register(
            nameof(Chord), typeof(string), typeof(KeyChord),
            new PropertyMetadata(string.Empty, OnChordChanged));

    /// <summary>The gesture text, e.g. "Ctrl+Shift+V" or "Win+V".</summary>
    public string Chord
    {
        get => (string)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    private static void OnChordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((KeyChord)d).Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();
        if (string.IsNullOrWhiteSpace(Chord))
            return;

        var keyCapStyle = TryFindResource("KeyCap") as Style;
        var keyTextStyle = TryFindResource("KeyCapText") as Style;
        var plusStyle = TryFindResource("KeyCapPlus") as Style;

        var parts = Chord.Split('+', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                Items.Add(new TextBlock { Style = plusStyle });

            var text = new TextBlock { Text = Prettify(parts[i]), Style = keyTextStyle };
            var cap = new Border { Style = keyCapStyle, Child = text };
            Items.Add(cap);
        }
    }

    // friendlier labels for a couple of keys. we keep "Win" as text: the windows
    // logo isn't a reliable glyph in segoe fluent icons and would risk a tofu box
    private static readonly Dictionary<string, string> Labels = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["Control"] = "Ctrl",
        ["Windows"] = "Win",
        ["PrintScreen"] = "PrtSc",
        ["Escape"] = "Esc",
    };

    private static string Prettify(string key) =>
        Labels.TryGetValue(key, out var label) ? label : key;
}
