using Microsoft.Win32;

namespace Klip.App.Services;

/// <summary>Start with Windows through the HKCU Run key (unpackaged).</summary>
public sealed class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Klip";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("ProcessPath indisponível");
            key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
