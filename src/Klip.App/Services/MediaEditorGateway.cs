namespace Klip.App.Services;

/// <summary>
/// Ponto de integracao desacoplado para abrir o editor de midia (spec F5).
/// Quem implementa o editor registra <see cref="Opener"/>; quem quer abrir
/// (historico, toasts de gravacao) chama <see cref="Open"/>. Mantem os dois
/// lados compilaveis de forma independente.
/// </summary>
public static class MediaEditorGateway
{
    /// <summary>Registrado pelo editor de midia na inicializacao do App.</summary>
    public static Action<string>? Opener { get; set; }

    /// <summary>True se o editor de midia esta disponivel nesta build.</summary>
    public static bool IsAvailable => Opener is not null;

    /// <summary>Abre o arquivo (gif/mp4) no editor de midia, se disponivel.</summary>
    public static void Open(string filePath) => Opener?.Invoke(filePath);
}
