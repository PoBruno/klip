using NAudio.CoreAudioApi;
using Klip.Core.Recording;

namespace Klip.Interop.Recording;

/// <summary>
/// Enumera microfones ativos via WASAPI (<see cref="MMDeviceEnumerator"/>,
/// eCapture + DEVICE_STATE_ACTIVE) para o seletor de fontes de audio do
/// painel pre-gravacao (RF-F3.02).
/// </summary>
public sealed class AudioDeviceEnumerator : IAudioDeviceEnumerator
{
    /// <inheritdoc />
    public IReadOnlyList<AudioSourceInfo> GetActiveMicrophones()
    {
        var result = new List<AudioSourceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var defaultDevice =
                    enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                defaultId = defaultDevice.ID;
            }
            catch (Exception)
            {
                // sem dispositivo default de captura (nenhum mic conectado)
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    result.Add(new AudioSourceInfo(
                        device.ID,
                        device.FriendlyName,
                        IsDefault: device.ID == defaultId));
                }
            }
        }
        catch (Exception)
        {
            // servico de audio indisponivel -> lista vazia (gravacao segue sem mic)
        }

        return result;
    }
}
