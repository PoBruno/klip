namespace Klip.Core.Recording;

/// <summary>
/// Gravador MP4 (H.264 + AAC) - contrato consumido pelo App, implementado no
/// Interop (Media Foundation SinkWriter + WASAPI). Spec F3.
/// </summary>
public interface IMp4Recorder : IAsyncDisposable
{
    /// <summary>Inicia a gravacao. Lanca se ja estiver gravando.</summary>
    Task StartAsync(Mp4RecordingOptions options, CancellationToken ct = default);

    /// <summary>RF-F3.13: pausa (timestamps seguem contiguos ao retomar).</summary>
    Task PauseAsync();

    Task ResumeAsync();

    /// <summary>
    /// Para e finaliza o arquivo. Pode levar segundos (RF-F3.16) - o chamador
    /// deve mostrar estado "finalizando".
    /// </summary>
    Task<Mp4RecordingResult> StopAsync();

    bool IsRecording { get; }
    bool IsPaused { get; }

    /// <summary>Tempo gravado, excluindo pausas.</summary>
    TimeSpan Elapsed { get; }

    // ------ Toggles ao vivo (spec M2, RF-M2.09..11) ------
    // Seguras antes/depois da gravacao (viram estado inicial/no-op).

    /// <summary>RF-M2.09: muta/desmuta microfones (ganho no mixer com rampa; captura nunca para).</summary>
    void SetMicrophoneMuted(bool muted);
    bool IsMicrophoneMuted { get; }

    /// <summary>RF-M2.09: muta/desmuta o som do sistema (loopback).</summary>
    void SetSystemAudioMuted(bool muted);
    bool IsSystemAudioMuted { get; }

    /// <summary>RF-M2.10: mostra/oculta o cursor na gravacao, ao vivo.</summary>
    void SetCursorCaptureEnabled(bool enabled);
    bool IsCursorCaptureEnabled { get; }

    /// <summary>Falha fatal (gravacao parou sozinha). Handler deve chamar StopAsync/DisposeAsync.</summary>
    event Action<RecordingFailure>? Failed;

    /// <summary>
    /// A gravacao parou por conta propria com arquivo valido (ex.: guarda-corpo de
    /// disco, RF-F3.15). O consumidor deve encerrar a UI de gravacao como se o
    /// usuario tivesse parado. Nao e disparado em StopAsync explicito.
    /// </summary>
    event Action<Mp4RecordingResult>? AutoStopped;

    /// <summary>Aviso nao-fatal (RF-F3.14: mic removido -> silencio; RF-F3.15: disco baixo).</summary>
    event Action<string>? Warning;
}
