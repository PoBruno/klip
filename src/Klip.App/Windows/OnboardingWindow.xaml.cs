using Klip.Core.Settings;
using Wpf.Ui.Appearance;

namespace Klip.App.Windows;

/// <summary>
/// First-run screen: welcome, hotkeys and the invite to take over
/// Win+V. Shown only once (OnboardingCompleted flag).
/// </summary>
public partial class OnboardingWindow
{
    private readonly SettingsService _settings;
    private readonly Func<Task<string>> _takeWinV;

    public OnboardingWindow(SettingsService settings, Func<Task<string>> takeWinV)
    {
        _settings = settings;
        _takeWinV = takeWinV;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        TakeWinVButton.Click += async (_, _) =>
        {
            TakeWinVButton.IsEnabled = false;
            WinVResult.Text = Localization.Loc.BusyApplying;
            var result = await _takeWinV();
            WinVResult.Text = result == "ok" ? Localization.Loc.ResultWinVOk : Localization.Loc.ResultWinVFail;
            TakeWinVButton.IsEnabled = result != "ok";
        };

        FinishButton.Click += (_, _) => Complete();
    }

    private void Complete()
    {
        _settings.Update(s => s.OnboardingCompleted = true);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // fechar no X tambem conclui o onboarding pra nao repetir
        if (!_settings.Current.OnboardingCompleted)
            _settings.Update(s => s.OnboardingCompleted = true);
        base.OnClosed(e);
    }
}
