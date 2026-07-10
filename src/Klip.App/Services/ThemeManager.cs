using Wpf.Ui.Appearance;

namespace Klip.App.Services;

/// <summary>Applies the theme: System follows Windows, Light/Dark are fixed.</summary>
public static class ThemeManager
{
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
}
