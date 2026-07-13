using System.Diagnostics;
using System.IO;
using System.Text;
using Klip.Core.Media.Editing;

namespace Klip.App.Services;

/// <summary>Resultado de uma execucao do ffmpeg.exe.</summary>
public sealed record FfmpegRunResult(int ExitCode, string StderrTail)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Executa o ffmpeg.exe como processo (RF-F5.12): stderr parseado para
/// progresso via <see cref="FfmpegProgressParser"/>, cancelamento mata o
/// processo. O chamador e responsavel pelo temp + move atomico (CA-F5.7).
/// Anti-orfao (bug do ffmpeg vivo apos fechar o app): todo processo ativo
/// entra num registro estatico; handlers de ProcessExit/Application.Exit
/// (assinados uma vez, lazily) matam a arvore dos processos registrados e
/// apagam os .partial correspondentes.
/// </summary>
public sealed class FfmpegRunner
{
    private const int StderrTailChars = 4000;

    // ----- registro estatico de processos ativos (anti-orfao) -----

    private static readonly Dictionary<int, (Process Process, string? PartialPath)> ActiveProcesses = new();
    private static readonly object ActiveSync = new();
    private static bool _exitHooked;

    /// <summary>Assina os handlers de encerramento do app uma unica vez.</summary>
    private static void EnsureExitHooks()
    {
        lock (ActiveSync)
        {
            if (_exitHooked)
                return;
            _exitHooked = true;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAllActive();
        var app = System.Windows.Application.Current;
        // Application.Exit exige a thread do Dispatcher; BeginInvoke evita
        // bloquear quando EnsureExitHooks roda numa thread de fundo
        app?.Dispatcher.BeginInvoke(new Action(() => app.Exit += (_, _) => KillAllActive()));
    }

    /// <summary>Mata a arvore de todos os processos registrados e apaga os .partial.</summary>
    private static void KillAllActive()
    {
        List<(Process Process, string? PartialPath)> snapshot;
        lock (ActiveSync)
        {
            snapshot = [.. ActiveProcesses.Values];
            ActiveProcesses.Clear();
        }

        foreach (var (process, partialPath) in snapshot)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch (Exception)
            {
                // processo ja terminou ou saiu do nosso controle: segue a limpeza
            }

            if (partialPath is null)
                continue;
            try
            {
                if (File.Exists(partialPath))
                    File.Delete(partialPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Register(Process process, string? partialPath)
    {
        lock (ActiveSync)
            ActiveProcesses[process.Id] = (process, partialPath);
    }

    private static void Unregister(Process process)
    {
        lock (ActiveSync)
            ActiveProcesses.Remove(process.Id);
    }

    /// <summary>
    /// Roda "ffmpeg -y -hide_banner {args}". <paramref name="editedDuration"/>
    /// calibra o progresso (nulo = sem progresso determinado).
    /// <paramref name="partialOutputPath"/> e o temp .partial a apagar se o
    /// app encerrar com o processo vivo.
    /// </summary>
    public async Task<FfmpegRunResult> RunAsync(
        string ffmpegPath, string args, TimeSpan? editedDuration,
        IProgress<double>? progress, CancellationToken cancellationToken,
        string? partialOutputPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
        ArgumentException.ThrowIfNullOrEmpty(args);

        EnsureExitHooks();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-y -hide_banner " + args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        var stderrTail = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { } line)
                return;

            // mantem so o fim do stderr para diagnostico de falha
            stderrTail.AppendLine(line);
            if (stderrTail.Length > StderrTailChars * 2)
                stderrTail.Remove(0, stderrTail.Length - StderrTailChars);

            if (editedDuration is { } total && FfmpegProgressParser.TryParseTime(line, out var time))
                progress?.Report(FfmpegProgressParser.Fraction(time, total));
        };

        process.Start();
        Register(process, partialOutputPath);
        try
        {
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // processo ja terminou entre o cancel e o kill
                }
                throw;
            }
        }
        finally
        {
            Unregister(process); // terminou (ou cancelou): sai do registro anti-orfao
        }

        var tail = stderrTail.ToString();
        if (tail.Length > StderrTailChars)
            tail = tail[^StderrTailChars..];
        return new FfmpegRunResult(process.ExitCode, tail);
    }
}
