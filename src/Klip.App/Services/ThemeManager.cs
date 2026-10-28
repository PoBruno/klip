using System.IO;
using System.Text.Json;
using System.Windows;
using Klip.Core.Common;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace Klip.App.Services;

/// <summary>Applies the theme: System follows Windows, Light/Dark are fixed.</summary>
public static class ThemeManager
{
    private static ApplicationTheme? _startupTheme;

    /// <summary>
    /// Theme the App.xaml dictionary is parsed with. Read straight from settings.json
    /// before the container exists, so the right theme is parsed once instead of
    /// Dark first and then Light on top of it.
    /// </summary>
    public static ApplicationTheme StartupTheme => _startupTheme ??= Resolve(PeekSavedTheme());

    /// <summary>
    /// Startup version of <see cref="Apply"/>. When the XAML already loaded the right
    /// dictionary, only the accent colors are pushed (no second theme parse).
    /// </summary>
    public static void ApplyAtStartup(string theme)
    {
        var wanted = theme is "light" or "dark" ? Resolve(theme) : FromSystem(ApplicationThemeManager.GetSystemTheme());
        if (wanted == StartupTheme && !SystemParameters.HighContrast && wanted != ApplicationTheme.Unknown)
        {
            ApplicationAccentColorManager.Apply(ApplicationAccentColorManager.GetColorizationColor(), wanted, false, false);
            return;
        }
        Apply(theme);
    }

    /// <param name="theme">"system" | "light" | "dark"</param>
    public static void Apply(string theme)
    {
        switch (theme)
        {
            case "light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case "dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                // follow Windows now and keep watching for system changes
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }

    private static ApplicationTheme Resolve(string theme) => theme switch
    {
        "light" => ApplicationTheme.Light,
        "dark" => ApplicationTheme.Dark,
        _ => SystemUsesLightApps() ? ApplicationTheme.Light : ApplicationTheme.Dark,
    };

    private static ApplicationTheme FromSystem(SystemTheme system) => system switch
    {
        SystemTheme.Light or SystemTheme.Flow or SystemTheme.Sunrise => ApplicationTheme.Light,
        SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
        _ => SystemUsesLightApps() ? ApplicationTheme.Light : ApplicationTheme.Dark,
    };

    private static bool SystemUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // only the "Theme" field; the real SettingsService comes later with the container
    private static string PeekSavedTheme()
    {
        try
        {
            var file = AppPaths.SettingsFile;
            if (!File.Exists(file))
                return "system";
            using var stream = File.OpenRead(file);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("Theme", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "system"
                : "system";
        }
        catch (Exception)
        {
            return "system";
        }
    }
}

/// <summary>ThemesDictionary that starts on the saved theme instead of a fixed one.</summary>
public sealed class StartupThemesDictionary : ThemesDictionary
{
    public StartupThemesDictionary()
    {
        Theme = ThemeManager.StartupTheme;
    }
}
