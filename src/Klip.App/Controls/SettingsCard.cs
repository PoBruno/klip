using System.Windows;
using System.Windows.Controls;

namespace Klip.App.Controls;

/// <summary>
/// RF-S.02 / ADR-S.05: o cartao de configuracao da tela de Configuracoes. Substitui
/// os 23 <c>Grid</c> escritos a mao que repetiam "titulo + legenda a esquerda,
/// controle a direita" com margens inconsistentes entre eles.
/// <para>
/// Layout do template (em <c>Styles/Settings.xaml</c>): 3 colunas
/// <c>icone Auto | texto * | conteudo Auto</c> e uma segunda linha para o
/// <see cref="Footer"/>. Os elementos opcionais colapsam por <c>Trigger</c> sobre a
/// propria DP - sem converter, que alocaria uma instancia por cartao.
/// </para>
/// <para>
/// O <c>Content</c> herdado do <see cref="ContentControl"/> e o controle da direita
/// (toggle, combo, botao). O <see cref="Footer"/> e o conteudo empilhado abaixo
/// (caixa de texto + botao dos seletores de pasta, por exemplo).
/// </para>
/// </summary>
public sealed class SettingsCard : ContentControl
{
    /// <summary>Titulo do cartao. Primeira linha, 14 px SemiBold.</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(SettingsCard),
            new PropertyMetadata(string.Empty));

    /// <summary>Legenda opcional. Segunda linha, 12 px secundario; colapsa se nula.</summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(SettingsCard),
            new PropertyMetadata(null));

    /// <summary>Glifo Segoe Fluent Icons opcional a esquerda; colapsa se nulo.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon), typeof(string), typeof(SettingsCard),
            new PropertyMetadata(null));

    /// <summary>
    /// Terceira linha opcional, em SemiBold: resultado de uma acao do cartao
    /// (takeover de registro concluido, conflito de atalho). RF-S.06.
    /// </summary>
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(
            nameof(Status), typeof(string), typeof(SettingsCard),
            new PropertyMetadata(null));

    /// <summary>Conteudo empilhado abaixo das tres linhas; colapsa se nulo.</summary>
    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(
            nameof(Footer), typeof(object), typeof(SettingsCard),
            new PropertyMetadata(null));

    /// <inheritdoc cref="TitleProperty" />
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <inheritdoc cref="DescriptionProperty" />
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <inheritdoc cref="IconProperty" />
    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <inheritdoc cref="StatusProperty" />
    public string? Status
    {
        get => (string?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <inheritdoc cref="FooterProperty" />
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }
}
