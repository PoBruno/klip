using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// Lists of process names edited in place (ignored apps, apps that keep formatting).
/// The view is an ObservableCollection bound once and changed item by item, so adding
/// or removing an app never rebuilds every row.
/// </summary>
internal static class AppListEditor
{
    public static void OnEnter(KeyEventArgs e, Action action)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        action();
    }

    public static void Add(SettingsService settings, Wpf.Ui.Controls.TextBox box,
        ObservableCollection<string> view, Func<AppSettings, List<string>> list)
    {
        var name = box.Text.Trim();
        if (name.Length == 0)
            return;

        // process names: no dot gets ".exe", and dedupe ignores case like Windows does
        if (!name.Contains('.'))
            name += ".exe";

        var added = false;
        settings.Update(s =>
        {
            var apps = list(s);
            if (apps.Contains(name, StringComparer.OrdinalIgnoreCase))
                return;
            apps.Add(name);
            added = true;
        });

        box.Text = "";
        if (added)
            view.Add(name);
    }

    public static void Remove(SettingsService settings, RoutedEventArgs e,
        ObservableCollection<string> view, Func<AppSettings, List<string>> list)
    {
        // the button DataContext already is the process name
        if (e.OriginalSource is not FrameworkElement { DataContext: string app })
            return;

        e.Handled = true;
        settings.Update(s => list(s).RemoveAll(
            a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase)));

        for (var i = view.Count - 1; i >= 0; i--)
        {
            if (string.Equals(view[i], app, StringComparison.OrdinalIgnoreCase))
                view.RemoveAt(i);
        }
    }

    /// <summary>Only rebuilds the collection when the saved list really changed.</summary>
    public static void Sync(ObservableCollection<string> view, List<string> saved)
    {
        if (view.Count == saved.Count && view.SequenceEqual(saved, StringComparer.Ordinal))
            return;
        view.Clear();
        foreach (var app in saved)
            view.Add(app);
    }
}
