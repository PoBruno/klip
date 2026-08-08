using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Klip.App.Controls;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.Core.Settings;
using Klip.Interop.SystemIntegration;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace Klip.App.Views.Pages;

/// <summary>
/// RF-S.05 - secao "Atalhos" (absorve <c>SectionAppHotkeys</c>,
/// <c>SectionNativeHotkeys</c> e <c>SectionFlyoutShortcuts</c>): 3 capturadores de
/// atalho, 4 botoes de takeover de registro, 2 linhas de status de registro e a
/// tabela de atalhos do painel.
/// <para>
/// RF-S.06: o resultado do takeover e da captura de atalho vai na linha de status
/// DESTA pagina (<c>ActionStatus</c>), nunca mais no <c>StatusText</c>
/// compartilhado - la a mensagem era sobrescrita pela contagem de itens antes de o
/// usuario conseguir ler.
/// </para>
/// <para>
/// ADR-S.07: o guard de reentrancia fica em <c>try/finally</c>; os eventos sao
/// assinados no CONSTRUTOR (a pagina e singleton por ADR-S.03, e o <c>Loaded</c>
/// dispara a cada re-entrada na arvore visual, o que duplicaria as assinaturas).
/// </para>
/// </summary>
public partial class HotkeysPage : ISettingsPage
{
    // gestos com significado especial para o estado do registro
    private const string WinVGesture = "Win+V";
    private const string WinShiftSGesture = "Win+Shift+S";
    private const string PrintScreenGesture = "PrintScreen";

    // codigos de retorno dos fluxos de takeover do App
    private const string ResultOk = "ok";
    private const string ResultNeedsHklm = "precisa-hklm";
    private const string ResultUacCancelled = "uac-cancelado";

    /// <summary>
    /// RF-S.08: placeholder da terceira linha enquanto a leitura do registro nao
    /// volta. Reticencia (U+2026) e neutra de idioma - nao existe chave em
    /// <c>Loc</c> para "verificando..." e esta pagina nao mexe no <c>Loc.cs</c>.
    /// </summary>
    private const string LoadingPlaceholder = "\u2026";

    private readonly SettingsService _settings;
    private readonly SystemHotkeyService _systemHotkeys;
    private readonly ITakeoverGateway _takeover;

    /// <summary>ADR-S.07: guard de reentrancia do <see cref="RefreshState"/>.</summary>
    private bool _loading;

    /// <summary>Um fluxo de takeover esta em andamento (registro + restart do Explorer).</summary>
    private bool _busy;

    /// <summary>
    /// Ultimo estado lido do registro, ou <c>null</c> se a leitura ainda nao voltou
    /// (ou falhou). Guardado para remontar as duas linhas de status depois de uma
    /// troca de idioma sem reler o registro.
    /// </summary>
    private SystemHotkeyState? _registryState;

    /// <summary>Descarta o resultado de uma leitura de registro que ficou obsoleta.</summary>
    private int _registryReadToken;

    /// <summary>
    /// Mensagem atual da linha de status, guardada como fabrica (e nao como string)
    /// para poder ser remontada no novo idioma sem repetir a acao.
    /// </summary>
    private Func<string>? _statusMessage;

    /// <summary>Construtor usado pelo container (RF-S.04: <c>AddSingleton</c>).</summary>
    public HotkeysPage(SettingsService settings, SystemHotkeyService systemHotkeys)
        : this(settings, systemHotkeys, new ApplicationTakeoverGateway())
    {
    }

    /// <summary>
    /// Costura de teste: so o container enxerga o construtor publico (o
    /// <c>CallSiteFactory</c> do Microsoft.Extensions.DependencyInjection so
    /// considera construtores publicos), entao este nao interfere na resolucao.
    /// </summary>
    internal HotkeysPage(
        SettingsService settings,
        SystemHotkeyService systemHotkeys,
        ITakeoverGateway takeover)
    {
        _settings = settings;
        _systemHotkeys = systemHotkeys;
        _takeover = takeover;

        PageDiagnostics.TrackConstruction(this);
        InitializeComponent();

        WireControls();
        RefreshState();
    }

    /// <summary>
    /// RF-S.05: assinaturas no CONSTRUTOR, nunca no <c>Loaded</c> - com pagina
    /// singleton o <c>Loaded</c> dispara a cada re-entrada na arvore visual.
    /// </summary>
    private void WireControls()
    {
        TakeWinVButton.Click += async (_, _) => await OnTakeWinVAsync();
        TakePrtScButton.Click += (_, _) => OnTakePrintScreen();
        TakeWinShiftSButton.Click += async (_, _) => await OnTakeWinShiftSAsync();
        RevertButton.Click += async (_, _) => await OnRevertAsync();

        foreach (var slot in AllSlots)
        {
            // editor de atalho: clique no keycap entra em captura, as teclas
            // pressionadas aparecem ao vivo, sair do foco cancela
            var button = ButtonOf(slot);
            button.Click += (_, _) => BeginCapture(slot);
            button.PreviewKeyDown += (_, e) => CaptureHotkey(e, slot);
            button.LostKeyboardFocus += (_, _) => EndCapture(slot);
        }

        // as duas linhas de status do registro e a linha de status da pagina sao
        // montadas em codigo: DynamicResource nao alcanca nenhuma delas. A pagina e
        // singleton e vive o processo inteiro, entao nao ha o que desassinar.
        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// RF-S.05: recarrega tudo que vem de <c>AppSettings</c> e dispara a releitura
    /// do registro. NAO toca na linha de status da pagina (RF-S.06).
    /// </summary>
    public void RefreshState()
    {
        // ADR-S.07: em try/finally. No original o _loading da MainWindow ficava
        // solto: uma excecao no meio do refresh deixava a tela somente-leitura ate
        // reiniciar o app.
        if (_loading)
            return;

        _loading = true;
        try
        {
            var s = _settings.Current;
            HistoryHotkeyChord.Chord = s.HotkeyHistory;
            CaptureHotkeyChord.Chord = s.HotkeyCapture;
            StopRecHotkeyChord.Chord = s.StopRecordingHotkey;
        }
        finally
        {
            _loading = false;
        }

        BeginRefreshTakeoverState();
    }

    private void OnLanguageChanged()
    {
        // sem reler o registro: o estado nao mudou, so o idioma dos rotulos
        if (_registryState is not null)
            ApplyTakeoverState(_registryState);
        if (_statusMessage is { } message)
            ActionStatus.Message = message();
    }

    // ===================== Estado do registro (RF-S.08) =====================

    /// <summary>
    /// RF-S.08: o <c>GetState()</c> abre 4 subchaves do registro. No original isso
    /// rodava sincronamente na UI thread antes de a janela aparecer; aqui vai para
    /// um <c>Task.Run</c> e volta pelo dispatcher (o <c>await</c> retoma no
    /// <c>SynchronizationContext</c> da UI), com placeholder nas duas linhas
    /// enquanto nao volta.
    /// </summary>
    private async void BeginRefreshTakeoverState()
    {
        var token = ++_registryReadToken;
        _registryState = null;
        WinVCard.Status = LoadingPlaceholder;
        CaptureKeyCard.Status = LoadingPlaceholder;
        // o atalho salvo pode ter mudado desde a ultima passagem
        UpdateTakeoverButtons();

        SystemHotkeyState? state = null;
        try
        {
            state = await Task.Run(_systemHotkeys.GetState);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RefreshTakeoverState", ex);
        }

        if (token != _registryReadToken)
            return; // uma leitura mais nova ja saiu na frente

        _registryState = state;
        ApplyTakeoverState(state);
    }

    /// <summary>
    /// Monta as duas linhas de status de uma vez so. No original o aviso de
    /// politica corporativa entrava com <c>WinVStatus.Text +=</c>, que so nao
    /// duplicava porque a atribuicao anterior estava tres linhas acima - qualquer
    /// reordenacao passaria a concatenar sobre o texto anterior.
    /// </summary>
    private void ApplyTakeoverState(SystemHotkeyState? state)
    {
        if (state is null)
        {
            // leitura falhou: melhor linha vazia do que estado inventado. Os botoes
            // continuam habilitados - os proprios fluxos reportam o que der errado.
            WinVCard.Status = null;
            CaptureKeyCard.Status = null;
            return;
        }

        var s = _settings.Current;
        var policyWarning = state.HasManagedPolicies ? Loc.ManagedPolicyWarning : string.Empty;

        var winV = s.HotkeyHistory == WinVGesture
            ? Loc.WinVActive
            : state.WinVFreed
                ? Loc.WinVFreedNotBound
                : Loc.WinVNative;
        WinVCard.Status = winV + policyWarning;

        CaptureKeyCard.Status = s.HotkeyCapture switch
        {
            PrintScreenGesture => Loc.PrtScActive,
            WinShiftSGesture => Loc.WinShiftSActive,
            _ => state.PrintScreenFreed
                ? string.Format(Loc.PrtScFreeInfo, s.HotkeyCapture)
                : string.Format(Loc.PrtScNativeInfo, s.HotkeyCapture),
        };
    }

    /// <summary>
    /// Dono UNICO do <c>IsEnabled</c> dos 4 botoes de takeover. No original o
    /// <c>SetBusy</c> e o <c>RefreshTakeoverState</c> escreviam os dois no
    /// <c>TakeWinVButton</c>, e quem ganhava dependia da ordem das chamadas.
    /// Nenhum dos 4 depende do estado lido do registro - so do fluxo em andamento
    /// e (no caso do Win+V) do atalho ja salvo -, entao a leitura assincrona da
    /// RF-S.08 nao pisca os botoes.
    /// </summary>
    private void UpdateTakeoverButtons()
    {
        // tomar o Win+V so faz sentido enquanto o historico nao estiver nele
        TakeWinVButton.IsEnabled = !_busy && _settings.Current.HotkeyHistory != WinVGesture;
        TakePrtScButton.IsEnabled = !_busy;
        TakeWinShiftSButton.IsEnabled = !_busy;
        RevertButton.IsEnabled = !_busy;
    }

    // ===================== Fluxos de takeover =====================

    private async Task OnTakeWinVAsync()
    {
        if (_busy || !Confirm(Loc.ConfirmWinV, Loc.ConfirmWinVTitle, MessageBoxImage.Question))
            return;

        SetBusy(true, () => Loc.BusyApplying, InfoBarSeverity.Informational);
        try
        {
            var result = await _takeover.TakeWinVAsync();

            if (result == ResultNeedsHklm)
            {
                // fallback do 24H2: desliga o recurso nativo via HKLM (pede elevacao).
                // Recusar o UAC deixa result em "precisa-hklm" e cai no ResultWinVFail,
                // igual ao original.
                if (Confirm(Loc.ConfirmHklm, Loc.ConfirmHklmTitle, MessageBoxImage.Warning))
                    result = await _takeover.TakeWinVWithHklmFallbackAsync();
            }

            SetBusy(
                false,
                result switch
                {
                    ResultOk => () => Loc.ResultWinVOk,
                    ResultUacCancelled => () => Loc.ResultUacCancelled,
                    _ => () => Loc.ResultWinVFail,
                },
                result switch
                {
                    ResultOk => InfoBarSeverity.Success,
                    ResultUacCancelled => InfoBarSeverity.Warning,
                    _ => InfoBarSeverity.Error,
                });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("TakeoverWinV", ex);
            SetBusy(false, () => ex.Message, InfoBarSeverity.Error);
        }

        RefreshState();
    }

    private void OnTakePrintScreen()
    {
        if (_busy)
            return;

        try
        {
            var ok = _takeover.TakePrintScreen() == ResultOk;
            ShowStatus(
                ok ? () => Loc.ResultPrtScOk : () => Loc.ResultPrtScConflict,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("TakeoverPrintScreen", ex);
            ShowStatus(() => ex.Message, InfoBarSeverity.Error);
        }

        RefreshState();
    }

    private async Task OnTakeWinShiftSAsync()
    {
        if (_busy || !Confirm(Loc.ConfirmWinShiftS, Loc.ConfirmWinShiftSTitle, MessageBoxImage.Warning))
            return;

        SetBusy(true, () => Loc.BusyApplying, InfoBarSeverity.Informational);
        try
        {
            var ok = await _takeover.TakeWinShiftSAsync() == ResultOk;
            SetBusy(
                false,
                ok ? () => Loc.ResultWinShiftSOk : () => Loc.ResultWinShiftSFail,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("TakeoverWinShiftS", ex);
            SetBusy(false, () => ex.Message, InfoBarSeverity.Error);
        }

        RefreshState();
    }

    private async Task OnRevertAsync()
    {
        if (_busy || !Confirm(Loc.ConfirmRevert, Loc.ConfirmRevertTitle, MessageBoxImage.Question))
            return;

        SetBusy(true, () => Loc.BusyReverting, InfoBarSeverity.Informational);
        try
        {
            await _takeover.RevertAsync();
            SetBusy(false, () => Loc.ResultReverted, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RevertTakeovers", ex);
            SetBusy(false, () => ex.Message, InfoBarSeverity.Error);
        }

        RefreshState();
    }

    /// <summary>
    /// Marca o fluxo em andamento e reporta na linha de status. Quem decide o
    /// <c>IsEnabled</c> continua sendo o <see cref="UpdateTakeoverButtons"/>.
    /// </summary>
    private void SetBusy(bool busy, Func<string> message, InfoBarSeverity severity)
    {
        _busy = busy;
        UpdateTakeoverButtons();
        ShowStatus(message, severity);
    }

    /// <summary>
    /// Confirmacao ancorada na janela dona da pagina. O original passava
    /// <c>this</c> (a MainWindow); aqui a pagina vive dentro do
    /// <c>SettingsShell</c>, entao o dono vem do <see cref="Window.GetWindow"/>.
    /// </summary>
    private bool Confirm(string text, string title, MessageBoxImage icon)
    {
        var owner = Window.GetWindow(this);
        var answer = owner is null
            ? MessageBox.Show(text, title, MessageBoxButton.YesNo, icon)
            : MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, icon);
        return answer == MessageBoxResult.Yes;
    }

    // ===================== Linha de status (RF-S.06) =====================

    /// <summary>
    /// RF-S.06: unico ponto que escreve na linha de status. O texto fica na tela
    /// ate a proxima acao (ou ate o usuario fechar o <c>InfoBar</c>); nenhum
    /// caminho de refresh de estado passa por aqui.
    /// </summary>
    private void ShowStatus(Func<string> message, InfoBarSeverity severity)
    {
        _statusMessage = message;
        ActionStatus.Severity = severity;
        ActionStatus.Message = message();
        ActionStatus.IsOpen = true;
    }

    // ===================== Editor de atalhos =====================

    /// <summary>
    /// Slots do editor de atalhos (o de parar gravacao so e registrado durante a
    /// sessao de gravacao, RF-F3.05).
    /// </summary>
    private enum HotkeySlot
    {
        History,
        Capture,
        StopRecording,
    }

    private static readonly HotkeySlot[] AllSlots =
        [HotkeySlot.History, HotkeySlot.Capture, HotkeySlot.StopRecording];

    /// <summary>
    /// Slot em captura, ou <c>null</c>. No original isto era um unico <c>bool</c>
    /// para os 3 slots e o <c>CaptureHotkey</c> nao testava QUAL slot estava
    /// capturando: com um slot ativo, tecla pressionada em qualquer um dos outros
    /// dois botoes era aceita e gravada no botao errado.
    /// </summary>
    private HotkeySlot? _capturingSlot;

    // Os 4 mapeamentos abaixo sao switch EXAUSTIVO de proposito. No original o
    // ramo default era "_ =>", o que fazia um slot novo cair silenciosamente no
    // "parar gravacao" - o unico slot que nao registra atalho global.
    private KeyChord ChordOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.History => HistoryHotkeyChord,
        HotkeySlot.Capture => CaptureHotkeyChord,
        HotkeySlot.StopRecording => StopRecHotkeyChord,
        _ => throw UnknownSlot(slot),
    };

    private TextBlock PromptOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.History => HistoryHotkeyPrompt,
        HotkeySlot.Capture => CaptureHotkeyPrompt,
        HotkeySlot.StopRecording => StopRecHotkeyPrompt,
        _ => throw UnknownSlot(slot),
    };

    private Button ButtonOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.History => HistoryHotkeyButton,
        HotkeySlot.Capture => CaptureHotkeyButton,
        HotkeySlot.StopRecording => StopRecHotkeyButton,
        _ => throw UnknownSlot(slot),
    };

    private string SavedGestureOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.History => _settings.Current.HotkeyHistory,
        HotkeySlot.Capture => _settings.Current.HotkeyCapture,
        HotkeySlot.StopRecording => _settings.Current.StopRecordingHotkey,
        _ => throw UnknownSlot(slot),
    };

    private static ArgumentOutOfRangeException UnknownSlot(HotkeySlot slot) =>
        new(nameof(slot), slot, "Slot de atalho nao previsto.");

    /// <summary>Entra em modo de captura no slot: troca os keycaps pelo convite.</summary>
    private void BeginCapture(HotkeySlot slot)
    {
        // clicar num capturador com outro ativo: fecha o anterior antes
        if (_capturingSlot is { } previous && previous != slot)
            EndCapture(previous);

        _capturingSlot = slot;
        ChordOf(slot).Visibility = Visibility.Collapsed;
        PromptOf(slot).Visibility = Visibility.Visible;
        ButtonOf(slot).Focus();
    }

    /// <summary>Sai do modo de captura, voltando a mostrar o atalho salvo.</summary>
    private void EndCapture(HotkeySlot slot)
    {
        if (_capturingSlot == slot)
            _capturingSlot = null;

        var chord = ChordOf(slot);
        chord.Chord = SavedGestureOf(slot);
        chord.Visibility = Visibility.Visible;
        PromptOf(slot).Visibility = Visibility.Collapsed;
    }

    private void CaptureHotkey(KeyEventArgs e, HotkeySlot slot)
    {
        // so o slot REALMENTE em captura responde
        if (_capturingSlot != slot)
            return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc desiste da captura sem mudar nada
        if (key == Key.Escape)
        {
            EndCapture(slot);
            return;
        }

        var parts = new List<string>(4);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        // so modificadores ate agora: pre-visualiza ao vivo e continua esperando
        if (IsModifierKey(key))
        {
            var live = ChordOf(slot);
            live.Visibility = Visibility.Visible;
            PromptOf(slot).Visibility = Visibility.Collapsed;
            live.Chord = string.Join("+", parts);
            return;
        }

        // RF-S.06: os dois casos abaixo eram "return" mudo no original - a tecla nao
        // entrava no atalho e absolutamente nada mudava na tela.
        if (!TryGetKeyName(key, out var keyName))
        {
            RejectCapture(slot); // tecla nao suportada
            return;
        }

        if (parts.Count == 0 && keyName != PrintScreenGesture)
        {
            RejectCapture(slot); // falta modificador (PrtSc e a unica que vale sozinha)
            return;
        }

        parts.Add(keyName);
        var gesture = string.Join("+", parts);

        // switch exaustivo montado ANTES do Update: slot nao previsto estoura aqui
        // em vez de gravar no lugar errado
        Action<AppSettings> assign = slot switch
        {
            HotkeySlot.History => s => s.HotkeyHistory = gesture,
            HotkeySlot.Capture => s => s.HotkeyCapture = gesture,
            HotkeySlot.StopRecording => s => s.StopRecordingHotkey = gesture,
            _ => throw UnknownSlot(slot),
        };
        _settings.Update(assign);

        // RF-F3.05: o atalho de parar gravacao so e registrado DURANTE a gravacao -
        // conflito aparece la, nao aqui. Por isso, de proposito, ele nao passa pelo
        // ApplyHotkeys (comportamento preservado do original).
        var ok = slot == HotkeySlot.StopRecording || _takeover.ApplyHotkeys(_settings);

        EndCapture(slot); // volta a mostrar o atalho recem-salvo como keycaps
        ShowStatus(
            ok
                ? () => string.Format(Loc.HotkeyUpdated, gesture)
                : () => string.Format(Loc.HotkeyConflict, gesture),
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);

        // trocar o atalho de historico/captura muda o que os cartoes de takeover
        // dizem (e se o botao Win+V faz sentido). O original nao reavaliava.
        if (slot != HotkeySlot.StopRecording)
            BeginRefreshTakeoverState();
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>
    /// RF-S.06: recusa a combinacao e deixa a recusa VISIVEL. O slot continua em
    /// captura: a pre-visualizacao ao vivo volta para o convite "pressione as
    /// teclas" (reacao imediata no proprio botao) e a linha de status da pagina diz
    /// o que se espera. Antes disso os dois casos eram um <c>return</c> mudo.
    /// </summary>
    private void RejectCapture(HotkeySlot slot)
    {
        ChordOf(slot).Visibility = Visibility.Collapsed;
        PromptOf(slot).Visibility = Visibility.Visible;
        ShowStatus(() => Loc.HotkeyHint, InfoBarSeverity.Warning);
    }

    /// <summary>Nome da tecla no formato que o <c>HotkeyGesture.TryParse</c> entende.</summary>
    private static bool TryGetKeyName(Key key, out string keyName)
    {
        keyName = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.Snapshot => PrintScreenGesture,
            _ => string.Empty,
        };
        return keyName.Length > 0;
    }

    /// <summary>
    /// Encaminha para o <see cref="App"/> corrente. Os fluxos moram la porque
    /// precisam do <c>HotkeyService</c> e do host de DI.
    /// </summary>
    private sealed class ApplicationTakeoverGateway : ITakeoverGateway
    {
        private static App Current => (App)Application.Current;

        public Task<string> TakeWinVAsync() => Current.TakeoverWinVAsync();

        public Task<string> TakeWinVWithHklmFallbackAsync() =>
            Current.TakeoverWinVWithHklmFallbackAsync();

        public string TakePrintScreen() => Current.TakeoverPrintScreen();

        public Task<string> TakeWinShiftSAsync() => Current.TakeoverWinShiftSAsync();

        public Task RevertAsync() => Current.RevertTakeoversAsync();

        public bool ApplyHotkeys(SettingsService settings) => Current.ApplyHotkeys(settings);
    }
}

/// <summary>
/// Ponto UNICO de contato desta pagina com os fluxos que escrevem no registro do
/// Windows e reiniciam o Explorer. No original as 4 chamadas
/// <c>((App)Application.Current).TakeoverXxx(...)</c> ficavam espalhadas pelos
/// handlers, o que amarrava a pagina ao <c>Application.Current</c> e tornava
/// qualquer teste impossivel sem mexer no registro da maquina.
/// </summary>
internal interface ITakeoverGateway
{
    Task<string> TakeWinVAsync();

    Task<string> TakeWinVWithHklmFallbackAsync();

    string TakePrintScreen();

    Task<string> TakeWinShiftSAsync();

    Task RevertAsync();

    bool ApplyHotkeys(SettingsService settings);
}
