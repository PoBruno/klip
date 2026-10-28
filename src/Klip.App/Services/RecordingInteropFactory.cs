using Klip.Core.Recording;
using Klip.Interop.Recording;

namespace Klip.App.Services;

/// <summary>
/// Single place that creates the concrete recording classes from Interop. The rest of
/// the app only talks to the Core interfaces (IMp4Recorder / IAudioDeviceEnumerator).
/// </summary>
public static class RecordingInteropFactory
{
    /// <summary>
    /// mp4 runs in a child process (Klip.exe --recorder) so the d3d/driver memory dies
    /// with it; if the child cannot start, the same object records in process.
    /// </summary>
    public static IMp4Recorder CreateMp4Recorder() =>
        new RemoteMp4Recorder(CreateInProcessMp4Recorder, StartupLog.Write);

    /// <summary>Fallback and tooling: the recorder inside this process.</summary>
    public static IMp4Recorder CreateInProcessMp4Recorder() => new Mp4Recorder();

    public static IAudioDeviceEnumerator CreateAudioDeviceEnumerator() => new AudioDeviceEnumerator();
}
