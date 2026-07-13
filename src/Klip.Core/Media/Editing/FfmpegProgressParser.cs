using System.Globalization;
using System.Text.RegularExpressions;

namespace Klip.Core.Media.Editing;

/// <summary>
/// RF-F5.12: parse do progresso emitido pelo ffmpeg.exe no stderr
/// ("... time=00:01:23.45 bitrate=..."). Logica pura para o runner do App
/// calcular a fracao concluida (time / duracao editada) - testavel sem processo.
/// </summary>
public static partial class FfmpegProgressParser
{
    [GeneratedRegex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();

    /// <summary>
    /// Extrai o "time=" de uma linha de stderr do ffmpeg. Retorna false para
    /// linhas sem progresso (banner, "time=N/A", lixo).
    /// </summary>
    public static bool TryParseTime(string? line, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrEmpty(line))
            return false;

        var match = TimeRegex().Match(line);
        if (!match.Success)
            return false;

        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        time = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>Fracao 0..1 do progresso, dado o total da timeline editada.</summary>
    public static double Fraction(TimeSpan current, TimeSpan total) =>
        total <= TimeSpan.Zero ? 0 : Math.Clamp(current.TotalSeconds / total.TotalSeconds, 0, 1);
}
