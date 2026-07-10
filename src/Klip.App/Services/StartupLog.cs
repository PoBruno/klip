using System.IO;
using Klip.Core.Common;

namespace Klip.App.Services;

/// <summary>
/// Tiny startup/error log for a tray app with no console.
/// Not a replacement for structured logging, just enough for field diagnosis.
/// </summary>
public static class StartupLog
{
    private static readonly Lock Sync = new();

    public static string LogFile => Path.Combine(AppPaths.Root, "startup.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.AppendAllText(LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // log e best effort, se falhar tudo bem
        }
    }

    public static void WriteException(string context, Exception ex) =>
        Write($"[ERRO] {context}: {ex}");
}
