using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Klip.App.Localization;
using Klip.Core.Common;

namespace Klip.App.Diagnostics;

/// <summary>
/// What the process costs right now: memory, threads, hooks, loaded assemblies and
/// the database on disk. Read on demand from the maintenance page, never sampled.
/// </summary>
public static class ResourceSnapshot
{
    // the heavy ones that should NOT be loaded while idle
    private static readonly (string Name, string Label)[] HeavyAssemblies =
    [
        ("Microsoft.Windows.SDK.NET", "WinRT (OCR/WGC)"),
        ("NAudio.Wasapi", "NAudio"),
        ("Vortice.Direct3D11", "Direct3D 11"),
        ("Vortice.MediaFoundation", "Media Foundation"),
        ("System.Drawing.Common", "System.Drawing"),
    ];

    /// <summary>Builds the report. Safe off the UI thread.</summary>
    public static string Build()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var loaded = new HashSet<string>(
            assemblies.Select(a => a.GetName().Name ?? ""), StringComparer.OrdinalIgnoreCase);

        var heavy = HeavyAssemblies
            .Where(h => loaded.Contains(h.Name))
            .Select(h => h.Label)
            .ToList();

        var dbBytes = FileSize(AppPaths.DatabaseFile)
            + FileSize(AppPaths.DatabaseFile + "-wal")
            + FileSize(AppPaths.DatabaseFile + "-shm");

        var c = CultureInfo.CurrentCulture;
        var text = new StringBuilder();
        text.AppendLine(string.Format(c, "{0}: {1:0.0} MB", Loc.DiagWorkingSet, Mb(process.WorkingSet64)));
        text.AppendLine(string.Format(c, "{0}: {1:0.0} MB", Loc.DiagPrivateBytes, Mb(process.PrivateMemorySize64)));
        text.AppendLine(string.Format(c, "{0}: {1:0.0} MB", Loc.DiagManagedHeap, Mb(GC.GetTotalMemory(false))));
        text.AppendLine(string.Format(c, "{0}: {1}", Loc.DiagThreads, process.Threads.Count));
        text.AppendLine(string.Format(c, "{0}: {1}", Loc.DiagHooksActive,
            HookHealth.IsIdle ? Loc.DiagNone : Loc.DiagYes));
        text.AppendLine(string.Format(c, "{0}: {1}", Loc.DiagAssemblies, assemblies.Length));
        text.AppendLine(string.Format(c, "{0}: {1}", Loc.DiagHeavyLoaded,
            heavy.Count == 0 ? Loc.DiagNone : string.Join(", ", heavy)));
        text.Append(string.Format(c, "{0}: {1:0.0} MB", Loc.DiagDbOnDisk, Mb(dbBytes)));
        return text.ToString();
    }

    private static double Mb(long bytes) => bytes / 1024.0 / 1024.0;

    private static long FileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
