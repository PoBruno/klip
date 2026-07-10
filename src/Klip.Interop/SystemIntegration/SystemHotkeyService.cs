using System.Diagnostics;
using System.Text.Json;
using Klip.Core.Common;
using Klip.Core.Settings;
using Microsoft.Win32;

namespace Klip.Interop.SystemIntegration;

/// <summary>Current state of the system with respect to the native hotkeys.</summary>
public sealed record SystemHotkeyState(
    string? DisabledHotkeys,
    bool WinVFreed,
    bool WinSFreed,
    bool HklmClipboardFeatureOff,
    bool PrintScreenFreed,
    bool HasManagedPolicies);

/// <summary>
/// The one and only place that writes to the registry for taking over native
/// hotkeys. Every op: backup first (once), write-ahead journal, merge without
/// clobbering, and it never touches Policies keys.
/// </summary>
public sealed class SystemHotkeyService(SettingsService settings)
{
    private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string DisabledHotkeysValue = "DisabledHotkeys";
    private const string KeyboardKey = @"Control Panel\Keyboard";
    private const string PrintScreenValue = "PrintScreenKeyForSnippingEnabled";
    private const string HklmClipboardKey = @"SOFTWARE\Microsoft\Clipboard";
    private const string HklmClipboardValue = "IsCloudAndHistoryFeatureAvailable";

    // ----- State -----

    public SystemHotkeyState GetState()
    {
        string? disabled;
        using (var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvancedKey))
            disabled = key?.GetValue(DisabledHotkeysValue) as string;

        bool hklmOff;
        using (var key = Registry.LocalMachine.OpenSubKey(HklmClipboardKey))
            hklmOff = key?.GetValue(HklmClipboardValue) is 0;

        bool prtScFreed;
        using (var key = Registry.CurrentUser.OpenSubKey(KeyboardKey))
        {
            var value = key?.GetValue(PrintScreenValue);
            // missing or 0 = key is free; 1 = opens the native snipping
            prtScFreed = value is null or 0 or "0";
        }

        bool managed;
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System"))
            managed = key?.GetValueNames().Length > 0;

        var upper = disabled?.ToUpperInvariant() ?? "";
        return new SystemHotkeyState(
            disabled,
            upper.Contains('V'),
            upper.Contains('S'),
            hklmOff,
            prtScFreed,
            managed);
    }

    // ----- DisabledHotkeys -----

    /// <summary>Adds letters to DisabledHotkeys, merging without clobber. Needs an Explorer restart.</summary>
    public void AddDisabledHotkeyLetters(string letters)
    {
        EnsureBackupTaken();
        WriteJournal($"AddDisabledHotkeyLetters:{letters}", started: true);

        using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedKey);
        var current = (key.GetValue(DisabledHotkeysValue) as string ?? "").ToUpperInvariant();
        foreach (var letter in letters.ToUpperInvariant())
        {
            if (!current.Contains(letter))
                current += letter;
        }
        key.SetValue(DisabledHotkeysValue, current, RegistryValueKind.String);

        WriteJournal($"AddDisabledHotkeyLetters:{letters}", started: false);
    }

    /// <summary>
    /// Removes ONLY the letters we added, restoring from the backup
    /// (letters from other apps are kept).
    /// </summary>
    public void RestoreDisabledHotkeys()
    {
        if (!settings.Current.RegistryBackupTaken)
            return;
        WriteJournal("RestoreDisabledHotkeys", started: true);

        using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedKey);
        var backup = settings.Current.RegistryBackupDisabledHotkeys;
        var current = (key.GetValue(DisabledHotkeysValue) as string ?? "").ToUpperInvariant();
        var backupUpper = backup?.ToUpperInvariant() ?? "";

        // letras que nos adicionamos = estao agora e nao estavam no backup
        var restored = new string(current.Where(c => backupUpper.Contains(c)).ToArray());
        // letras que outros apps colocaram depois do nosso backup tambem ficam
        foreach (var c in current)
        {
            if (!restored.Contains(c) && c is not ('V' or 'S'))
                restored += c;
        }

        if (restored.Length == 0 && backup is null)
            key.DeleteValue(DisabledHotkeysValue, throwOnMissingValue: false);
        else
            key.SetValue(DisabledHotkeysValue, restored, RegistryValueKind.String);

        WriteJournal("RestoreDisabledHotkeys", started: false);
    }

    private void EnsureBackupTaken()
    {
        if (settings.Current.RegistryBackupTaken)
            return; // backup is written once, never overwritten
        using var key = Registry.CurrentUser.OpenSubKey(ExplorerAdvancedKey);
        var current = key?.GetValue(DisabledHotkeysValue) as string;
        settings.Update(s =>
        {
            s.RegistryBackupDisabledHotkeys = current;
            s.RegistryBackupTaken = true;
        });
    }

    // ----- Print Screen -----

    public void SetPrintScreenFreed(bool freed)
    {
        WriteJournal($"SetPrintScreenFreed:{freed}", started: true);
        using var key = Registry.CurrentUser.CreateSubKey(KeyboardKey);
        if (!settings.Current.RegistryBackupPrintScreenTaken)
        {
            var original = key.GetValue(PrintScreenValue);
            settings.Update(s =>
            {
                s.RegistryBackupPrintScreen = original as int? ?? (original is string str && int.TryParse(str, out var v) ? v : null);
                s.RegistryBackupPrintScreenTaken = true;
            });
        }

        if (freed)
        {
            key.SetValue(PrintScreenValue, 0, RegistryValueKind.DWord);
        }
        else
        {
            var backup = settings.Current.RegistryBackupPrintScreen;
            if (backup is null)
                key.DeleteValue(PrintScreenValue, throwOnMissingValue: false);
            else
                key.SetValue(PrintScreenValue, backup.Value, RegistryValueKind.DWord);
        }
        WriteJournal($"SetPrintScreenFreed:{freed}", started: false);
    }

    // ----- HKLM 24H2 fallback, needs elevation -----

    /// <summary>Run this ONLY in an elevated process (via the --registry argument).</summary>
    public void SetHklmClipboardFeature(bool off)
    {
        WriteJournal($"SetHklmClipboardFeature:{off}", started: true);
        using var key = Registry.LocalMachine.CreateSubKey(HklmClipboardKey);
        if (off)
            key.SetValue(HklmClipboardValue, 0, RegistryValueKind.DWord);
        else
            key.DeleteValue(HklmClipboardValue, throwOnMissingValue: false);
        WriteJournal($"SetHklmClipboardFeature:{off}", started: false);
    }

    /// <summary>Relaunches our own exe elevated for the HKLM op. True if it finished.</summary>
    public static bool RunElevated(string operation)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--registry {operation}",
                Verb = "runas",
                UseShellExecute = true,
            });
            process?.WaitForExit(30_000);
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // user cancelled the UAC prompt
        }
    }

    // ----- Explorer restart -----

    /// <summary>Kills the shell and waits for it to come back (GetShellWindow, 15 s timeout).</summary>
    public static async Task<bool> RestartExplorerAsync()
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // process might already be gone
            }
        }

        // o Windows costuma subir o shell de novo sozinho, entao espera um pouco
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500).ConfigureAwait(true);
            if (NativeMethods.GetShellWindow() != nint.Zero)
                return true;
        }

        // didnt come back on its own, launch it by hand
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception)
        {
            return false;
        }
        await Task.Delay(2000).ConfigureAwait(true);
        return NativeMethods.GetShellWindow() != nint.Zero;
    }

    // ----- Anti-corruption journal -----

    private static readonly Lock JournalLock = new();

    private static void WriteJournal(string operation, bool started)
    {
        try
        {
            lock (JournalLock)
            {
                var entry = JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now,
                    operation,
                    phase = started ? "start" : "done",
                });
                File.AppendAllText(AppPaths.RegistryJournalFile, entry + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // journal is best-effort
        }
    }

    /// <summary>Detects an interrupted op (start with no done) on startup.</summary>
    public static string? DetectIncompleteOperation()
    {
        try
        {
            if (!File.Exists(AppPaths.RegistryJournalFile))
                return null;
            var lines = File.ReadAllLines(AppPaths.RegistryJournalFile);
            string? pending = null;
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                var op = doc.RootElement.GetProperty("operation").GetString();
                var phase = doc.RootElement.GetProperty("phase").GetString();
                pending = phase == "start" ? op : null;
            }
            return pending;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
