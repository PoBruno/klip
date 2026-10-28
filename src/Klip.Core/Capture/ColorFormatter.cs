using System.Globalization;

namespace Klip.Core.Capture;

/// <summary>Text forms of a picked screen color.</summary>
public static class ColorFormatter
{
    public const string Hex = "hex";
    public const string Rgb = "rgb";
    public const string Hsl = "hsl";

    /// <summary>Unknown or empty values fall back to hex.</summary>
    public static string Normalize(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        Rgb => Rgb,
        Hsl => Hsl,
        _ => Hex,
    };

    /// <summary>#RRGGBB, rgb(r, g, b) or hsl(h, s%, l%).</summary>
    public static string Format(byte r, byte g, byte b, string? format) => Normalize(format) switch
    {
        Rgb => string.Create(CultureInfo.InvariantCulture, $"rgb({r}, {g}, {b})"),
        Hsl => FormatHsl(r, g, b),
        _ => ToHex(r, g, b),
    };

    public static string ToHex(byte r, byte g, byte b) =>
        string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

    /// <summary>Hue in degrees, saturation and lightness in percent, all rounded.</summary>
    public static (int H, int S, int L) ToHsl(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var l = (max + min) / 2;
        var d = max - min;

        double h = 0, s = 0;
        if (d > 0)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == rf)
                h = (gf - bf) / d + (gf < bf ? 6 : 0);
            else if (max == gf)
                h = (bf - rf) / d + 2;
            else
                h = (rf - gf) / d + 4;
            h *= 60;
        }

        var hi = (int)Math.Round(h, MidpointRounding.AwayFromZero) % 360;
        return (hi, (int)Math.Round(s * 100, MidpointRounding.AwayFromZero),
            (int)Math.Round(l * 100, MidpointRounding.AwayFromZero));
    }

    private static string FormatHsl(byte r, byte g, byte b)
    {
        var (h, s, l) = ToHsl(r, g, b);
        return string.Create(CultureInfo.InvariantCulture, $"hsl({h}, {s}%, {l}%)");
    }
}
