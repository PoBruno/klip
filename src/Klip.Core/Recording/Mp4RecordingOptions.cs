namespace Klip.Core.Recording;

/// <summary>Opcoes de uma gravacao MP4 (spec F3).</summary>
public sealed class Mp4RecordingOptions
{
    /// <summary>Regiao em pixels fisicos (coordenadas de desktop virtual).</summary>
    public required RecordingRegion Region { get; init; }

    /// <summary>Caminho completo do arquivo .mp4 de saida.</summary>
    public required string OutputPath { get; init; }

    /// <summary>FPS alvo (CFR no motor de captura). Default 30.</summary>
    public int Fps { get; init; } = 30;

    /// <summary>Bitrate de video em kbps. 0 = preset automatico pela resolucao (RF-F3.09).</summary>
    public int BitrateKbps { get; init; }

    /// <summary>fMP4 ligado por padrao - crash preserva o gravado (RF-F3.10).</summary>
    public bool FragmentedMp4 { get; init; } = true;

    public bool CaptureCursor { get; init; } = true;

    /// <summary>Gravar o som do sistema (WASAPI loopback).</summary>
    public bool CaptureSystemAudio { get; init; } = true;

    /// <summary>Ids de microfones (de <see cref="AudioSourceInfo.Id"/>) a mixar. Vazio = nenhum.</summary>
    public IReadOnlyList<string> MicrophoneDeviceIds { get; init; } = [];
}

/// <summary>Resultado de uma gravacao finalizada com sucesso.</summary>
public sealed record Mp4RecordingResult(
    string OutputPath,
    TimeSpan Duration,
    int Width,
    int Height,
    long FileSizeBytes);

/// <summary>Falha fatal de gravacao (a gravacao parou; arquivo pode estar parcial/valido se fMP4).</summary>
public sealed record RecordingFailure(string Message, Exception? Exception = null);
