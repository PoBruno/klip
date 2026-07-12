namespace Klip.Core.Recording;

/// <summary>Categoria de falha irrecuperavel do motor de captura (RF-F2.08).</summary>
public enum FrameCaptureErrorKind
{
    /// <summary>GPU removida/resetada e a recuperacao (1 tentativa) falhou.</summary>
    DeviceLost,

    /// <summary>Monitor capturado foi desconectado; encerrar a gravacao preservando o arquivo.</summary>
    MonitorLost,

    /// <summary>Falha inesperada no pipeline de frames.</summary>
    Unknown,
}

/// <summary>
/// Erro fatal do motor de captura. Apos receber este evento o consumidor deve
/// finalizar o arquivo em andamento e chamar StopAsync/DisposeAsync no engine.
/// </summary>
/// <param name="Kind">Categoria da falha.</param>
/// <param name="Message">Mensagem descritiva (pt-BR).</param>
/// <param name="Exception">Excecao original, quando houver.</param>
public sealed record FrameCaptureError(
    FrameCaptureErrorKind Kind,
    string Message,
    Exception? Exception = null);
