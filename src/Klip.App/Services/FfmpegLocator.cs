using System.IO;
using Klip.Core.Common;

namespace Klip.App.Services;

/// <summary>
/// Localiza o ffmpeg.exe para as exportacoes do editor de midia (RF-F5.12/14).
/// Ordem: caminho configurado (AppSettings.FfmpegPath) -> pasta de dados do
/// Klip (%LocalAppData%\Klip, raiz ou subpasta ffmpeg) -> PATH do sistema.
/// O download sob demanda do binario (RF-F5.14) fica para uma entrega futura;
/// sem ffmpeg o editor abre um dialogo para escolher o executavel manualmente.
/// </summary>
public static class FfmpegLocator
{
    public static string? Find(string? configuredPath)
    {
        // 1. caminho explicito das configuracoes
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // 2. pasta de dados do app
        foreach (var candidate in new[]
        {
            Path.Combine(AppPaths.Root, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppPaths.Root, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(AppPaths.Root, "ffmpeg.exe"),
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // 3. PATH (equivalente a "where ffmpeg", sem subprocesso)
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // entrada invalida no PATH: ignora e segue
            }
        }

        return null;
    }
}
