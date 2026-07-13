namespace Klip.Core.Recording;

/// <summary>
/// Fonte de audio disponivel para gravacao (microfone/entrada WASAPI ativa).
/// O som do sistema (loopback) nao aparece aqui - e um toggle proprio em
/// <see cref="Mp4RecordingOptions.CaptureSystemAudio"/>.
/// </summary>
public sealed record AudioSourceInfo(string Id, string Name, bool IsDefault);

/// <summary>Enumera dispositivos de entrada de audio ativos (impl. no Interop via WASAPI).</summary>
public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioSourceInfo> GetActiveMicrophones();
}
