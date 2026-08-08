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
        // RF-P3.07: le antes de escrever. Este metodo roda em TODA inicializacao do app
        // (App.OnStartup) e antes gravava no registro sempre, mesmo sem nada a mudar.
        var desired = enabled
            ? $"\"{Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath indisponivel")}\" --minimized"
            : null;

        using (var read = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            var current = read?.GetValue(ValueName) as string;
            if (string.Equals(current, desired, StringComparison.Ordinal))
                return; // ja esta como queremos: nenhuma escrita no registro
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (desired is not null)
            key.SetValue(ValueName, desired);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
