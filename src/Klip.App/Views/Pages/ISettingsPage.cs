namespace Klip.App.Views.Pages;

/// <summary>
/// Pagina de configuracoes. RefreshState() e chamado ao entrar na pagina e quando o estado
/// externo muda (troca de idioma, takeover de registro concluido).
/// </summary>
public interface ISettingsPage
{
    void RefreshState();
}
