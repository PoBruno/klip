using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Klip.App.Controls;

/// <summary>Tamanho e cor das teclas de um <see cref="KeyChord"/>.</summary>
public enum KeyChordStyle
{
    /// <summary>Tecla neutra (referencia, sugestoes).</summary>
    Neutral,
    /// <summary>Cor de destaque: atalho configurado.</summary>
    Accent,
    /// <summary>Grande, na caixa de captura.</summary>
    Large,
}

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
        Focusable = false;
        IsTabStop = false;
    }

    public static readonly DependencyProperty ChordProperty =
        DependencyProperty.Register(
            nameof(Chord), typeof(string), typeof(KeyChord),
            new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty KeyStyleProperty =
        DependencyProperty.Register(
            nameof(KeyStyle), typeof(KeyChordStyle), typeof(KeyChord),
            new PropertyMetadata(KeyChordStyle.Neutral, OnChanged));

    /// <summary>The gesture text, e.g. "Ctrl+Shift+V" or "Win+V".</summary>
    public string Chord
    {
        get => (string)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    public KeyChordStyle KeyStyle
    {
        get => (KeyChordStyle)GetValue(KeyStyleProperty);
        set => SetValue(KeyStyleProperty, value);
    }

    /// <summary>Texto usado no lugar de "click" (a tecla do mouse nos atalhos do painel).</summary>
    public static string ClickLabel { get; set; } = "click";

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyChord)d).Rebuild();

    private void Rebuild()
    {
        Items.Clear();
        if (string.IsNullOrWhiteSpace(Chord))
            return;

        var (capKey, textKey) = KeyStyle switch
        {
            KeyChordStyle.Accent => ("KeyCapAccent", "KeyCapAccentText"),
            KeyChordStyle.Large => ("KeyCapLarge", "KeyCapLargeText"),
            _ => ("KeyCap", "KeyCapText"),
        };
        var keyCapStyle = TryFindResource(capKey) as Style;
        var keyTextStyle = TryFindResource(textKey) as Style;
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
        ["Delete"] = "Del",
    };

    private static string Prettify(string key)
    {
        if (string.Equals(key, "click", System.StringComparison.OrdinalIgnoreCase))
            return ClickLabel;
        return Labels.TryGetValue(key, out var label) ? label : key;
    }
}
