using Klip.Core.Recording;
using Klip.Interop.Recording;

namespace Klip.App.Services;

/// <summary>
/// PONTO UNICO de instanciacao das classes concretas de gravacao do Interop
/// (Mp4Recorder e AudioDeviceEnumerator, spec F3). Todo o resto do App fala
/// apenas com as interfaces do Core (IMp4Recorder / IAudioDeviceEnumerator).
/// </summary>
public static class RecordingInteropFactory
{
    public static IMp4Recorder CreateMp4Recorder() => new Mp4Recorder();

    public static IAudioDeviceEnumerator CreateAudioDeviceEnumerator() => new AudioDeviceEnumerator();
}
