using System.IO;
using System.Text;
using System.Windows;
using Klip.App.Diagnostics;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.Core.Common;
using Klip.Core.Settings;
using Klip.Core.Storage;
using Klip.Interop.SystemIntegration;
using Microsoft.Win32;

namespace Klip.App.Views.Pages;

/// <summary>
/// Maintenance: export/import the history, compact the database, open the data
/// folder, the diagnostics dump and the resource panel.
/// <para>
/// Export, import, compact and diagnostics run on <c>Task.Run</c>: export reads the
/// whole database and writes a ZIP, import does ExistsByHash + Upsert per item, compact
/// runs VACUUM. Each one disables the buttons, shows an indeterminate bar and posts the
/// result back through the dispatcher.
/// </para>
/// </summary>
public partial class MaintenancePage : ISettingsPage
{
    private readonly SettingsService _settings;
    private readonly ClipboardItemRepository _repository;
    private readonly BackupService _backup;
    private readonly ClipboardWriteGuard _clipboard;
    private readonly SystemHotkeyService _systemHotkeys;

    /// <summary>Texto exato do ultimo diagnostico, para o botao de copiar.</summary>
    private string _lastReport = "";

    /// <summary>
    /// Reentrancy guards, always flipped inside try/finally: a failure halfway through
    /// an import can't leave the buttons disabled and the bar spinning until restart.
    /// </summary>
    private bool _maintenanceBusy;

    /// <inheritdoc cref="_maintenanceBusy" />
    private bool _diagnosticsBusy;

    public MaintenancePage(
        SettingsService settings,
        ClipboardItemRepository repository,
        BackupService backup,
        ClipboardWriteGuard clipboard,
        SystemHotkeyService systemHotkeys)
    {
        _settings = settings;
        _repository = repository;
        _backup = backup;
        _clipboard = clipboard;
        _systemHotkeys = systemHotkeys;

        PageDiagnostics.TrackConstruction(this);
        InitializeComponent();

        // handlers in the constructor, never in Loaded: singleton page, and Loaded
        // fires on every return to the visual tree.
        // Os lambdas async sao async void por serem RoutedEventHandler: cada metodo
        // abaixo trata TODAS as suas excecoes, entao nada escapa para o dispatcher.
        ExportButton.Click += async (_, _) => await OnExportAsync();
        ImportButton.Click += async (_, _) => await OnImportAsync();
        CompactButton.Click += async (_, _) => await OnCompactAsync();
        OpenDataFolderButton.Click += (_, _) => OnOpenDataFolder();
        DiagnosticsButton.Click += async (_, _) => await OnRunDiagnosticsAsync();
        CopyDiagnosticsButton.Click += async (_, _) => await OnCopyDiagnosticsAsync();
        ResourcesButton.Click += async (_, _) => await OnRefreshResourcesAsync();

        RefreshState();
    }

    /// <summary>
    /// Called at the end of the constructor and on every visit to the page
    /// (<c>SettingsShell.OnNavigated</c>).
    /// <para>
    /// A pagina nao expoe nenhum ajuste persistido - os dois blocos sao acoes -,
    /// entao o unico estado a reconciliar e o dos botoes e das barras de progresso,
    /// que precisa sobreviver a uma ida e volta na navegacao enquanto uma operacao
    /// longa ainda roda.
    /// </para>
    /// <para>
    /// DECISAO CONSCIENTE: <c>MaintenanceStatus</c> e <c>DiagnosticsText</c> NAO sao
    /// limpos aqui. Limpar faria o resultado de um import - ou um dump de
    /// diagnostico prestes a ser enviado ao suporte - sumir so porque o usuario foi
    /// conferir um atalho em outra secao e voltou. A MainWindow tambem nao limpava,
    /// mas por omissao; aqui e escolha.
    /// </para>
    /// </summary>
    public void RefreshState()
    {
        ApplyMaintenanceBusy(_maintenanceBusy);
        ApplyDiagnosticsBusy(_diagnosticsBusy);
    }

    // ----- Backup e manutencao -----

    private async Task OnExportAsync()
    {
        if (_maintenanceBusy)
            return;

        string path;
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = Loc.BackupFilter,
                FileName = string.Format(Loc.BackupFileName, DateTime.Now),
            };
            if (dialog.ShowDialog() != true)
                return;
            path = dialog.FileName;
        }
        catch (Exception ex)
        {
            ShowMaintenanceFailure("Export", ex);
            return;
        }

        // O dialogo tem que abrir na UI thread; so o trabalho pesado (ler o banco
        // inteiro e escrever o ZIP) vai para o pool.
        await RunMaintenanceAsync("Export", () =>
        {
            var result = _backup.Export(path);
            return string.Format(Loc.ExportDone, result.Items, result.MediaFiles);
        });
    }

    private async Task OnImportAsync()
    {
        if (_maintenanceBusy)
            return;

        string path;
        try
        {
            var dialog = new OpenFileDialog { Filter = Loc.BackupFilter };
            if (dialog.ShowDialog() != true)
                return;
            path = dialog.FileName;
        }
        catch (Exception ex)
        {
            ShowMaintenanceFailure("Import", ex);
            return;
        }

        // Import e o pior dos tres: descompacta e faz ExistsByHash + Upsert POR ITEM
        // (N+1 consultas). A contagem de itens do historico nao e reexibida aqui -
        // ela foi para a AboutPage; quem quiser o numero novo roda o
        // diagnostico logo abaixo.
        await RunMaintenanceAsync("Import", () =>
        {
            var result = _backup.Import(path);
            return string.Format(Loc.ImportDone, result.Imported, result.SkippedDuplicates);
        });
    }

    private async Task OnCompactAsync()
    {
        if (_maintenanceBusy)
            return;

        // VACUUM reescreve o arquivo inteiro e bloqueia os escritores enquanto roda.
        await RunMaintenanceAsync("Compact", () =>
        {
            _repository.Vacuum();
            return Loc.CompactDone;
        });
    }

    private void OnOpenDataFolder()
    {
        // Continua sincrono: e um ShellExecute que retorna assim que o Explorer
        // aceita o pedido, sem IO de banco. E o unico dos quatro que fica habilitado
        // durante uma operacao longa - abrir a pasta nao concorre com nada.
        try
        {
            AppPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowMaintenanceFailure("OpenDataFolder", ex);
        }
    }

    /// <summary>Runs the work off the UI thread and posts the result back to it.</summary>
    private async Task RunMaintenanceAsync(string context, Func<string> work)
    {
        ApplyMaintenanceBusy(true);
        MaintenanceStatus.Text = "";
        try
        {
            // O await captura o DispatcherSynchronizationContext desta thread, entao
            // a continuacao - a unica linha que toca a arvore visual - volta pelo
            // Dispatcher da UI. Sem ConfigureAwait(false) de proposito: quem desliga
            // o contexto e o Core, a UI precisa dele.
            MaintenanceStatus.Text = await Task.Run(work);
        }
        catch (Exception ex)
        {
            ShowMaintenanceFailure(context, ex);
        }
        finally
        {
            ApplyMaintenanceBusy(false);
        }
    }

    private void ShowMaintenanceFailure(string context, Exception ex)
    {
        MaintenanceStatus.Text = ex.Message;
        StartupLog.WriteException(context, ex);
    }

    private void ApplyMaintenanceBusy(bool busy)
    {
        _maintenanceBusy = busy;
        ExportButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        CompactButton.IsEnabled = !busy;
        // IsIndeterminate acompanha a Visibility: o storyboard do template nao pode
        // ficar animando um elemento colapsado.
        MaintenanceProgress.IsIndeterminate = busy;
        MaintenanceProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Diagnostico -----

    private async Task OnRunDiagnosticsAsync()
    {
        if (_diagnosticsBusy)
            return;

        ApplyDiagnosticsBusy(true);
        DiagnosticsStatus.Text = "";
        try
        {
            var report = await Task.Run(BuildDiagnosticsReport);
            _lastReport = string.Concat(
                report.HookLine, Environment.NewLine, Environment.NewLine, report.Body);
            HookHealthText.Text = report.HookLine;
            HookHealthBox.Visibility = Visibility.Visible;
            DiagnosticsText.Text = report.Body;
        }
        catch (Exception ex)
        {
            _lastReport = "";
            DiagnosticsText.Text = ex.Message;
            StartupLog.WriteException("Diagnostico", ex);
        }
        finally
        {
            ApplyDiagnosticsBusy(false);
        }
    }

    private async Task OnCopyDiagnosticsAsync()
    {
        if (_diagnosticsBusy || _lastReport.Length == 0)
            return;

        var report = _lastReport;
        ApplyDiagnosticsBusy(true);
        try
        {
            // NUNCA System.Windows.Clipboard direto. Abrir o clipboard e um
            // lock global do sistema e o caminho do WPF dorme ate ~1 s por retry
            // interno, entao todo acesso vive na thread STA dedicada
            // (Services/ClipboardThread). O Invoke do ClipboardWriteGuard e SINCRONO
            // na thread de quem chama - por isso a chamada sai da UI thread no
            // Task.Run, e nao apenas "porque escrever no clipboard e lento".
            // Efeito colateral desejado: o guard registra o sequence number
            // (anti-loop), logo o dump nao volta como item do proprio historico.
            await Task.Run(() => _clipboard.WriteText(report));
            DiagnosticsStatus.Text = Loc.DiagCopied;
        }
        catch (Exception ex)
        {
            DiagnosticsStatus.Text = ex.Message;
            StartupLog.WriteException("DiagnosticoCopiar", ex);
        }
        finally
        {
            ApplyDiagnosticsBusy(false);
        }
    }

    /// <summary>
    /// Monta o relatorio FORA da UI thread. Nada aqui toca a
    /// arvore visual, e <c>Loc.*</c> e leitura de dicionario em memoria (sem
    /// afinidade de thread). As leituras caras estao todas aqui: <c>Count()</c> e um
    /// <c>SELECT COUNT(*)</c>, <c>GetState()</c> sao 4 aberturas de chave de
    /// registro e o <c>FileInfo</c> bate no disco.
    /// </summary>
    private DiagnosticsReport BuildDiagnosticsReport()
    {
        var state = _systemHotkeys.GetState();
        var current = _settings.Current;
        var databaseFile = AppPaths.DatabaseFile;
        var databaseSize = File.Exists(databaseFile) ? new FileInfo(databaseFile).Length : 0;

        var text = new StringBuilder();
        text.AppendLine($"{Loc.DiagItems}: {_repository.Count()}");
        text.AppendLine($"{Loc.DiagDbSize}: {databaseSize / 1024.0 / 1024.0:F1} MB");
        text.AppendLine($"{Loc.DiagHotkeyHistory}: {current.HotkeyHistory}");
        text.AppendLine($"{Loc.DiagHotkeyCapture}: {current.HotkeyCapture}");
        text.AppendLine($"{Loc.DiagDisabledHotkeys}: {state.DisabledHotkeys ?? Loc.DiagEmpty}");
        text.AppendLine($"{Loc.DiagWinVFreed}: {state.WinVFreed}   {Loc.DiagWinSFreed}: {state.WinSFreed}");
        text.AppendLine($"{Loc.DiagPrtScFreed}: {state.PrintScreenFreed}");
        text.AppendLine($"{Loc.DiagHklmOff}: {state.HklmClipboardFeatureOff}");
        text.Append($"{Loc.DiagPolicies}: {state.HasManagedPolicies}");

        // a linha mais importante do diagnostico, por isso sai separada e destacada.
        // "teclado=nao, mouse=nao" em repouso: um hook LL instalado e chamado pela Raw
        // Input Thread a cada evento do sistema inteiro, e e isso que pesa em jogos
        return new DiagnosticsReport(HookHealth.FormatSummary(), text.ToString());
    }

    private void ApplyDiagnosticsBusy(bool busy)
    {
        _diagnosticsBusy = busy;
        DiagnosticsButton.IsEnabled = !busy;
        CopyDiagnosticsButton.IsEnabled = !busy && _lastReport.Length > 0;
        DiagnosticsProgress.IsIndeterminate = busy;
        DiagnosticsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Recursos -----

    private bool _resourcesBusy;

    private async Task OnRefreshResourcesAsync()
    {
        if (_resourcesBusy)
            return;
        _resourcesBusy = true;
        ResourcesButton.IsEnabled = false;
        try
        {
            ResourcesText.Text = await Task.Run(ResourceSnapshot.Build);
        }
        catch (Exception ex)
        {
            ResourcesText.Text = ex.Message;
            StartupLog.WriteException("Recursos", ex);
        }
        finally
        {
            ResourcesText.Visibility = Visibility.Visible;
            ResourcesButton.IsEnabled = true;
            _resourcesBusy = false;
        }
    }

    /// <summary>Relatorio montado fora da UI thread; a linha dos hooks vai separada.</summary>
    private readonly record struct DiagnosticsReport(string HookLine, string Body);
}
