using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Klip.App.Controls;

/// <summary>
/// Cartao que abre e mostra linhas relacionadas (o SettingsExpander das Configuracoes do
/// Windows). <see cref="Header"/> e o controle opcional a direita do cabecalho (um toggle
/// que liga a opcao principal, um botao); o <c>Content</c> sao as linhas (<see cref="SettingsRow"/>).
/// Clicar no cabecalho abre e fecha, menos quando o clique e no proprio controle do cabecalho.
/// </summary>
public sealed class SettingsExpander : SettingsCard
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(object), typeof(SettingsExpander),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(SettingsExpander),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private FrameworkElement? _headerArea;
    private FrameworkElement? _headerContent;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_headerArea is not null)
            _headerArea.MouseLeftButtonUp -= OnHeaderClick;
        _headerArea = GetTemplateChild("PART_HeaderArea") as FrameworkElement;
        _headerContent = GetTemplateChild("PART_HeaderContent") as FrameworkElement;
        if (_headerArea is not null)
            _headerArea.MouseLeftButtonUp += OnHeaderClick;
    }

    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        // clique dentro do controle do cabecalho (toggle, botao) e dele, nao do expansor
        if (_headerContent is not null && e.OriginalSource is DependencyObject source && IsInside(source, _headerContent))
            return;
        if (e.OriginalSource is DependencyObject src && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) is { } b && b.Name != "PART_Chevron")
            return;
        IsExpanded = !IsExpanded;
    }

    private static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var current = node; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, container))
                return true;
        }
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = ParentOf(current))
        {
            if (current is T match)
                return match;
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
