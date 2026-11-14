using System.Windows;
using System.Windows.Controls;

namespace Klip.App.Controls;

/// <summary>
/// o cartao de configuracao da tela de Configuracoes.
/// <para>
/// Layout do template (em <c>Styles/Settings.xaml</c>): coluna do icone SEMPRE reservada
/// (20 px, mesmo sem icone, para os titulos alinharem), texto e o controle a direita. Abaixo
/// de <see cref="NarrowWidth"/> o controle desce para baixo do texto, como o SettingsCard
/// do Windows. O <see cref="Footer"/> ocupa a largura toda abaixo (caminho de pasta, lista).
/// </para>
/// </summary>
public class SettingsCard : ContentControl
{
    /// <summary>Abaixo disso (DIP de largura do cartao) o controle vai para baixo do texto.</summary>
    public const double NarrowWidth = 520;

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsCard),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard),
            new PropertyMetadata(null));

    /// <summary>Glifo Segoe Fluent Icons; sem icone a coluna continua reservada.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingsCard),
            new PropertyMetadata(null));

    /// <summary>Linha extra abaixo da descricao (estado: "Assumido do Windows", conflito).</summary>
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(object), typeof(SettingsCard),
            new PropertyMetadata(null));

    /// <summary>Conteudo em largura total abaixo do texto; colapsa se nulo.</summary>
    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(nameof(Footer), typeof(object), typeof(SettingsCard),
            new PropertyMetadata(null));

    private static readonly DependencyPropertyKey IsNarrowKey =
        DependencyProperty.RegisterReadOnly(nameof(IsNarrow), typeof(bool), typeof(SettingsCard),
            new PropertyMetadata(false));

    /// <summary>True quando o cartao esta estreito e o controle foi para baixo do texto.</summary>
    public static readonly DependencyProperty IsNarrowProperty = IsNarrowKey.DependencyProperty;

    public SettingsCard()
    {
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                SetValue(IsNarrowKey, e.NewSize.Width < NarrowWidth);
        };
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public object? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public bool IsNarrow => (bool)GetValue(IsNarrowProperty);
}

/// <summary>
/// Linha dentro de um <see cref="SettingsExpander"/>: mesmo layout do cartao, sem borda,
/// fundo secundario e divisoria em cima. Tipo proprio so para ter estilo implicito.
/// </summary>
public sealed class SettingsRow : SettingsCard;
