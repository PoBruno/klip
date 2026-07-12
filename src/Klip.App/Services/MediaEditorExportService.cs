using System.IO;
using Klip.Core.Media.Editing;
using Klip.Core.Media.Gif;
using Klip.Core.Settings;

namespace Klip.App.Services;

/// <summary>Ffmpeg.exe nao encontrado em nenhum dos locais (RF-F5.14).</summary>
public sealed class FfmpegNotFoundException : InvalidOperationException
{
    public FfmpegNotFoundException() : base("ffmpeg.exe nao encontrado.") { }
}

/// <summary>
/// Exportacoes do editor de midia (RF-F5.11..15).
/// - GIF de fonte GIF: encoder proprio do Core, sem FFmpeg (D-F5.4).
/// - MP4 re-encode e MP4-para-GIF: ffmpeg.exe como processo, saida em temp
///   na MESMA pasta do destino + move atomico (CA-F5.7).
/// Codec MP4: tenta h264_nvenc e recua automaticamente para libx264 e
/// libopenh264 re-invocando o ffmpeg quando a tentativa anterior falha -
/// mais simples que sondar encoders e cobre builds LGPL sem libx264 (RF-F5.12).
/// </summary>
public sealed class MediaEditorExportService(SettingsService settings)
{
    private static readonly string[] VideoCodecChain = ["h264_nvenc", "libx264", "libopenh264"];

    private readonly FfmpegRunner _runner = new();

    /// <summary>Caminho do ffmpeg.exe ou null (o chamador mostra o dialogo RF-F5.14).</summary>
    public string? LocateFfmpeg() => FfmpegLocator.Find(settings.Current.FfmpegPath);

    /// <summary>
    /// Exporta o projeto de video como MP4 re-encodado (RF-F5.12).
    /// <paramref name="template"/> carrega Width/Height/Fps REAIS do source
    /// para o filler preto dos gaps (RF-F5.20) - o codec e sobrescrito pela
    /// cadeia de fallback.
    /// </summary>
    public async Task ExportVideoAsync(
        MediaEditProject project, VideoExportSettings template, string outputPath,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        var ffmpeg = LocateFfmpeg() ?? throw new FfmpegNotFoundException();
        var temp = TempPathFor(outputPath);
        try
        {
            FfmpegRunResult? last = null;
            for (var i = 0; i < VideoCodecChain.Length; i++)
            {
                var codecSettings = new VideoExportSettings
                {
                    VideoCodec = VideoCodecChain[i],
                    BitrateKbps = template.BitrateKbps,
                    FastStart = template.FastStart,
                    Width = template.Width,
                    Height = template.Height,
                    Fps = template.Fps,
                };
                var args = FfmpegArgsBuilder.BuildVideoExportArgs(project, codecSettings, temp);
                // temp .partial registrado no runner: se o app fechar durante o
                // export, a arvore do ffmpeg morre e o temp e apagado
                last = await _runner.RunAsync(ffmpeg, args, project.EditedDuration, progress, cancellationToken, temp)
                    .ConfigureAwait(false);
                if (last.Success)
                {
                    File.Move(temp, outputPath, overwrite: true);
                    return;
                }
                StartupLog.Write($"Export MP4: codec {VideoCodecChain[i]} falhou (exit {last.ExitCode}), tentando o proximo");
            }
            throw new InvalidOperationException($"ffmpeg falhou em todos os codecs. Stderr: {last?.StderrTail}");
        }
        finally
        {
            DeleteQuiet(temp);
        }
    }

    /// <summary>Exporta o projeto de video como GIF via palettegen (RF-F5.13, D-F5.4).</summary>
    public async Task ExportGifFromVideoAsync(
        MediaEditProject project, GifFromVideoSettings gifSettings, string outputPath,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        var ffmpeg = LocateFfmpeg() ?? throw new FfmpegNotFoundException();
        var temp = TempPathFor(outputPath);
        try
        {
            var args = FfmpegArgsBuilder.BuildGifFromVideoArgs(project, gifSettings, temp);
            var result = await _runner.RunAsync(ffmpeg, args, project.EditedDuration, progress, cancellationToken, temp)
                .ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException($"ffmpeg falhou (exit {result.ExitCode}). Stderr: {result.StderrTail}");
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            DeleteQuiet(temp);
        }
    }

    /// <summary>
    /// Exporta uma sequencia GIF materializada com o encoder proprio (RF-F5.13,
    /// sem FFmpeg). O cancelamento e o PROGRESSO sao observados pelos loaders
    /// lazy dos frames (o encoder aborta na primeira leitura apos o cancel;
    /// o progresso e reportado por leitura - ver BuildGifExportFrames no
    /// editor); temp + move atomico como nas demais rotas (CA-F5.7).
    /// Roda sincrono - chame em Task.Run.
    /// </summary>
    public static void ExportGifFrames(
        IReadOnlyList<GifFrameSource> frames, string outputPath, CancellationToken cancellationToken)
    {
        var temp = TempPathFor(outputPath);
        try
        {
            using (var stream = File.Create(temp))
            {
                new GifEncoder().Encode(stream, frames, new GifEncodeOptions());
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            DeleteQuiet(temp);
        }
    }

    /// <summary>
    /// Temp na MESMA pasta do destino (o move fica atomico no mesmo volume);
    /// mantem a extensao para o ffmpeg inferir o muxer.
    /// </summary>
    private static string TempPathFor(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);
        return Path.Combine(dir, $"~{name}.{Guid.NewGuid():N}.partial{ext}");
    }

    private static void DeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
