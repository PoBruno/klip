using System.Windows;
using System.Windows.Controls;
using Klip.App.Controls;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.Core.Settings;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace Klip.App.Views.Pages;

/// <summary>
/// Secao "Atalhos": os 6 atalhos do Klip, restaurar os padroes e a referencia dos
/// atalhos do painel.
/// <para>
/// Um controle so por atalho. Editar abre a <see cref="KeyCaptureDialog"/>; escolher uma
/// tecla do Windows (Win+V, Win+Shift+S, Print Screen) faz o Klip assumi-la no registro ao
/// salvar, e sair dela devolve so aquela tecla (<c>App.ChangeShortcutAsync</c>). Antes eram
/// o campo do atalho e quatro botoes de takeover, que podiam discordar entre si.
/// </para>
/// <para>
/// Eventos assinados no CONSTRUTOR (pagina singleton; o <c>Loaded</c> dispara a cada
/// re-entrada na arvore visual).
/// </para>
/// </summary>
public partial class HotkeysPage : ISettingsPage
{
    private const string ResultOk = "ok";
    private const string ResultNeedsHklm = "precisa-hklm";
    private const string ResultUacCancelled = "uac-cancelado";
    private const string ResultConflict = "conflito";

    private readonly SettingsService _settings;
    private readonly ITakeoverGateway _takeover;

    /// <summary>Uma troca de atalho esta em andamento (registro + restart do Explorer).</summary>
    private bool _busy;

    /// <summary>Mensagem da InfoBar como fabrica, para ser remontada no novo idioma.</summary>
    private Func<string>? _statusMessage;

    private sealed record Slot(string Id, Func<AppSettings, string> Get, bool Optional, Func<string> Name, string[] Suggestions);

    private readonly Slot[] _slots;

    /// <summary>Construtor usado pelo container (<c>AddSingleton</c>).</summary>
    public HotkeysPage(SettingsService settings)
        : this(settings, new ApplicationTakeoverGateway())
    {
    }

    /// <summary>Costura de teste: o container so enxerga o construtor publico.</summary>
    internal HotkeysPage(SettingsService settings, ITakeoverGateway takeover)
    {
        _settings = settings;
        _takeover = takeover;

        PageDiagnostics.TrackConstruction(this);
        InitializeComponent();

        _slots =
        [
            new("history", s => s.HotkeyHistory, false, () => Loc.OpenHistoryHotkey, ["Win+V", "Ctrl+Shift+V", "Ctrl+Alt+V"]),
            new("capture", s => s.HotkeyCapture, false, () => Loc.NewCaptureHotkey, ["PrintScreen", "Win+Shift+S", "Ctrl+Shift+S"]),
            new("stop", s => s.StopRecordingHotkey, false, () => Loc.StopRecordingHotkeyTitle, ["Ctrl+Shift+X", "Ctrl+Alt+X"]),
            new("text", s => s.HotkeyCaptureText, true, () => Loc.HotkeyCaptureTextTitle, ["Ctrl+Shift+T", "Win+Shift+T"]),
            new("repeat", s => s.HotkeyRepeatRegion, true, () => Loc.HotkeyRepeatRegionTitle, ["Ctrl+Shift+R", "Win+Shift+R"]),
            new("video", s => s.HotkeyRecordVideo, true, () => Loc.HotkeyRecordVideoTitle, ["Ctrl+Shift+G", "Win+Shift+G"]),
        ];

        RevertButton.Click += async (_, _) => await OnRestoreDefaultsAsync();
        Loc.LanguageChanged += OnLanguageChanged;

        BuildPanelShortcuts();
        RefreshState();
    }

    /// <summary>Remonta os 6 cartoes a partir de <c>AppSettings</c>.</summary>
    public void RefreshState()
    {
        foreach (var slot in _slots)
            BuildCard(slot);
        RevertButton.IsEnabled = !_busy;
    }

    private void OnLanguageChanged()
    {
        RefreshState();
        BuildPanelShortcuts();
        if (_statusMessage is { } message)
            ActionStatus.Message = message();
    }

    private SettingsCard CardOf(Slot slot) => slot.Id switch
    {
        "history" => HistoryCard,
        "capture" => CaptureCard,
        "stop" => StopRecCard,
        "text" => TextCard,
        "repeat" => RepeatCard,
        "video" => VideoCard,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot.Id, null),
    };

    /// <summary>
    /// Teclas em destaque + lapis quando ha atalho; "Definir atalho" quando nao ha. Tecla do
    /// Windows ganha a linha "Assumido do Windows · Devolver ao Windows".
    /// </summary>
    private void BuildCard(Slot slot)
    {
        var card = CardOf(slot);
        var gesture = slot.Get(_settings.Current);

        if (string.IsNullOrWhiteSpace(gesture))
        {
            var set = new Wpf.Ui.Controls.Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = "", FontFamily = (System.Windows.Media.FontFamily)FindResource("KlipIconFont"), FontSize = 12, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = Loc.HotkeySet, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                IsEnabled = !_busy,
            };
            System.Windows.Automation.AutomationProperties.SetName(set, Loc.HotkeySet + " " + slot.Name());
            set.Click += async (_, _) => await EditAsync(slot);
            card.Content = set;
        }
        else
        {
            var edit = new Button
            {
                Style = (Style)FindResource("EditKeyButton"),
                ToolTip = Loc.HotkeyEdit,
                IsEnabled = !_busy,
            };
            System.Windows.Automation.AutomationProperties.SetName(edit, Loc.HotkeyEdit + " " + slot.Name());
            edit.Click += async (_, _) => await EditAsync(slot);
            card.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new KeyChord { Chord = gesture, KeyStyle = KeyChordStyle.Accent }, edit },
            };
        }

        card.Status = KeyCaptureDialog.IsSystemKey(gesture) ? OwnedLine(slot) : null;
    }

    private StackPanel OwnedLine(Slot slot)
    {
        var giveBack = new Button
        {
            Style = (Style)FindResource("LinkButton"),
            Content = Loc.HotkeyGiveBack,
            FontSize = 12,
            IsEnabled = !_busy,
        };
        giveBack.Click += async (_, _) => await GiveBackAsync(slot);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock
                {
                    Text = "",
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("KlipIconFont"),
                    FontSize = 12,
                    Margin = new Thickness(0, 1, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (System.Windows.Media.Brush)FindResource("AccentTextFillColorPrimaryBrush"),
                },
                new TextBlock { Text = Loc.HotkeyOwned + " · ", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 },
                giveBack,
            },
        };
    }

    // ===================== Editar =====================

    private async Task EditAsync(Slot slot)
    {
        if (_busy)
            return;

        var current = slot.Get(_settings.Current);
        var others = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in _slots)
        {
            var g = other.Get(_settings.Current);
            if (other != slot && !string.IsNullOrWhiteSpace(g))
                others[g] = other.Name();
        }

        var dialog = new KeyCaptureDialog(slot.Name(), current, slot.Optional, slot.Suggestions, others)
        {
            Owner = Window.GetWindow(this),
        };

        // os atalhos do Klip ficam suspensos enquanto a caixa escuta: apertar o Win+V atual
        // tem que chegar na caixa, nao abrir o painel
        _takeover.SuspendHotkeys();
        bool? answer;
        try
        {
            answer = dialog.ShowDialog();
        }
        finally
        {
            _takeover.ApplyHotkeys(_settings);
        }

        if (answer != true || dialog.Result is not { } chosen)
            return;
        await ChangeAsync(slot, chosen);
    }

    private Task GiveBackAsync(Slot slot) =>
        // devolver ao Windows = voltar ao atalho padrao do Klip para aquela acao
        ChangeAsync(slot, slot.Id == "history" ? "Ctrl+Shift+V" : slot.Id == "capture" ? "Ctrl+Shift+S" : "");

    private async Task ChangeAsync(Slot slot, string gesture)
    {
        var touchesWindows = KeyCaptureDialog.IsSystemKey(gesture) || KeyCaptureDialog.IsSystemKey(slot.Get(_settings.Current));
        SetBusy(true);
        if (touchesWindows)
            ShowStatus(() => Loc.BusyApplying, InfoBarSeverity.Informational);
        try
        {
            var result = await _takeover.ChangeShortcutAsync(slot.Id, gesture);
            if (result == ResultNeedsHklm &&
                Confirm(Loc.ConfirmHklm, Loc.ConfirmHklmTitle, MessageBoxImage.Warning))
            {
                result = await _takeover.TakeWinVWithHklmFallbackAsync();
            }

            switch (result)
            {
                case ResultOk:
                    HideStatus(); // sucesso nao vira faixa: o cartao ja mostra a tecla nova
                    break;
                case ResultConflict:
                    ShowStatus(() => string.Format(Loc.HotkeyConflict, gesture), InfoBarSeverity.Warning);
                    break;
                case ResultUacCancelled:
                    ShowStatus(() => Loc.ResultUacCancelled, InfoBarSeverity.Warning);
                    break;
                default:
                    ShowStatus(() => string.Format(Loc.HotkeyTakeFailed, gesture), InfoBarSeverity.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ChangeShortcut", ex);
            ShowStatus(() => ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OnRestoreDefaultsAsync()
    {
        if (_busy || !Confirm(Loc.ConfirmRevert, Loc.ConfirmRevertTitle, MessageBoxImage.Question))
            return;

        SetBusy(true);
        ShowStatus(() => Loc.BusyReverting, InfoBarSeverity.Informational);
        try
        {
            await _takeover.RevertAsync();
            HideStatus();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RevertTakeovers", ex);
            ShowStatus(() => ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshState();
    }

    private bool Confirm(string text, string title, MessageBoxImage icon)
    {
        var owner = Window.GetWindow(this);
        var answer = owner is null
            ? MessageBox.Show(text, title, MessageBoxButton.YesNo, icon)
            : MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, icon);
        return answer == MessageBoxResult.Yes;
    }

    private void ShowStatus(Func<string> message, InfoBarSeverity severity)
    {
        _statusMessage = message;
        ActionStatus.Severity = severity;
        ActionStatus.Message = message();
        ActionStatus.IsOpen = true;
    }

    private void HideStatus()
    {
        _statusMessage = null;
        ActionStatus.IsOpen = false;
    }

    // ===================== Referencia do painel =====================

    /// <summary>Os atalhos reais do painel (HistoryFlyoutWindow.OnKeyDown + cliques).</summary>
    private void BuildPanelShortcuts()
    {
        KeyChord.ClickLabel = Loc.KeyMouse;
        (string Chord, string Text)[] rows =
        [
            ("click", Loc.ShortcutClickDesc),
            ("Ctrl+click", Loc.ShortcutCtrlClickDesc),
            ("Alt+click", Loc.ShortcutAltClickDesc),
            ("Shift+click", Loc.ShortcutShiftClickDesc),
            ("Enter", Loc.ShortcutClickDesc),
            ("Ctrl+Enter", Loc.PanelKeyCopyOnly),
            ("Alt+Enter", Loc.PanelKeyType),
            (Loc.KeySpace, Loc.PanelKeyPreview),
            ("Del", Loc.PanelKeyDelete),
            ("Ctrl+P", Loc.PanelKeyPin),
            ("Ctrl+D", Loc.PanelKeyFavorite),
            ("Ctrl+E", Loc.PanelKeyEditor),
            ("Ctrl+F", Loc.PanelKeySearch),
            ("Ctrl+Tab", Loc.PanelKeyTabs),
        ];

        var grid = PanelShortcutsGrid;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new KeyChord { Chord = rows[i].Chord, Margin = new Thickness(0, 6, 16, 6), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock
            {
                Text = rows[i].Text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
            };
            Grid.SetRow(keys, i);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            grid.Children.Add(keys);
            grid.Children.Add(text);
        }
    }

    /// <summary>
    /// Encaminha para o <see cref="App"/> corrente. Os fluxos moram la porque precisam do
    /// <c>HotkeyService</c> e do host de DI.
    /// </summary>
    private sealed class ApplicationTakeoverGateway : ITakeoverGateway
    {
        private static App Current => (App)Application.Current;

        public Task<string> ChangeShortcutAsync(string slot, string gesture) => Current.ChangeShortcutAsync(slot, gesture);

        public Task<string> TakeWinVWithHklmFallbackAsync() => Current.TakeoverWinVWithHklmFallbackAsync();

        public Task RevertAsync() => Current.RevertTakeoversAsync();

        public bool ApplyHotkeys(SettingsService settings) => Current.ApplyHotkeys(settings);

        public void SuspendHotkeys() => Current.SuspendHotkeys();
    }
}

/// <summary>
/// Ponto UNICO de contato desta pagina com os fluxos que escrevem no registro do Windows
/// e reiniciam o Explorer.
/// </summary>
internal interface ITakeoverGateway
{
    Task<string> ChangeShortcutAsync(string slot, string gesture);

    Task<string> TakeWinVWithHklmFallbackAsync();

    Task RevertAsync();

    bool ApplyHotkeys(SettingsService settings);

    void SuspendHotkeys();
}
