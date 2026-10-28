using System.Runtime.CompilerServices;
using Klip.Core.Recording;

namespace Klip.App;

/// <summary>
/// Own entry point (instead of the generated one) so the recorder child process never
/// touches wpf, the mutex or the container: it just runs the recorder and leaves.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (RecorderProtocol.IsRecorderInvocation(args))
            return RunRecorder(args);
        return RunApp();
    }

    // separate methods: the jit only loads the types of the path that actually runs
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunRecorder(string[] args) => Services.RecorderHost.Run(args);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
