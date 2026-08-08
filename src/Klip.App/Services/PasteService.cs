using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Klip.Core.Settings;
using Klip.Core.Storage;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Cola um item do historico no app que estava em foco.
/// O fluxo e: salvar o foco antes do flyout abrir, gravar o clipboard,
/// restaurar o foco (com fallback em AttachThreadInput), disparar Ctrl+V por
/// SendInput e, no fim, devolver o clipboard anterior se a opcao estiver ligada.
///
/// RF-P2.01 / ADR-P.05: nenhuma etapa de clipboard roda na UI thread. Antes o
/// snapshot saia sincrono na UI thread e a gravacao voltava para ela por
/// dispatcher.Invoke; agora tudo vai para a <see cref="ClipboardThread"/>. A UI
/// thread so e usada para o evento PasteFailed (que mexe no tray).
/// </summary>
public sealed class PasteService(
    ClipboardWriteGuard writeGuard,
    MediaStore mediaStore,
    SettingsService settings,
    ClipboardThread clipboardThread)
{
    /// <summary>
    /// Dispatcher da UI, resolvido uma vez na construcao (o host sobe na UI
    /// thread). Serve so para eventos de interface, nunca para clipboard.
    /// </summary>
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;

    /// <summary>HWND do app alvo, salvo antes de mostrar o flyout.</summary>
    public nint SavedTargetWindow { get; private set; }

    /// <summary>Dispara quando a colagem falha, para mostrarmos um toast de fallback.</summary>
    public event Action? PasteFailed;

    /// <summary>
    /// Salva o app que esta com o foco, para colarmos de volta nele. Se o
    /// foreground for uma janela nossa (reabertura rapida em que o foco ainda
    /// nao assentou), mantem o alvo anterior em vez de pegar a nos mesmos.
    /// </summary>
    public void CaptureForegroundTarget(nint ignoreHwnd = 0)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == nint.Zero || fg == ignoreHwnd)
            return; // nao troca um alvo bom pela nossa propria janela
        SavedTargetWindow = fg;
    }

    /// <summary>
    /// Traz o app alvo de volta para a frente. Usado quando o flyout tomou o
    /// foco (clique na busca) e fechou sem colar, para o cursor voltar para onde
    /// o usuario estava.
    /// </summary>
    public void RestoreTargetFocus()
    {
        if (SavedTargetWindow != nint.Zero)
            NativeMethods.ForceForeground(SavedTargetWindow);
    }

    /// <summary>
    /// Grava o item no clipboard e cola no alvo. Retorna na hora: todo o
    /// trabalho pesado (snapshot, decode da imagem, gravacao, esperas) roda fora
    /// do clique, para a UI e os hooks de input nunca travarem.
    /// </summary>
    public void PasteItem(ClipboardItem item, bool asPlainText = false)
    {
        var target = SavedTargetWindow;
        var restore = settings.Current.RestoreClipboardAfterPaste;

        // decodifica a imagem (disco + decode) FORA da UI thread; texto e barato
        var isImage = item.Type == ClipboardItemType.Image && item.FilePath is not null;
        var imagePath = isImage ? mediaStore.ToAbsolute(item.FilePath!) : null;

        _ = Task.Run(() =>
        {
            var ok = true;
            System.Windows.IDataObject? previous = null;
            try
            {
                // RF-P2.01: o snapshot saiu da UI thread. Ele so precisa
                // acontecer ANTES da gravacao, e continua acontecendo - as duas
                // operacoes sao sequenciais aqui e caem na mesma fila STA.
                if (restore)
                    previous = writeGuard.SnapshotCurrent();

                BitmapSource? bitmap = null;
                byte[]? pngBytes = null;
                if (imagePath is not null)
                    (pngBytes, bitmap) = ClipboardWriteGuard.DecodeImageFile(imagePath);

                // a gravacao em si acontece na thread STA do clipboard
                clipboardThread.Invoke(() =>
                {
                    if (bitmap is not null && pngBytes is not null)
                        writeGuard.WriteImageFromPng(pngBytes, bitmap);
                    else
                        writeGuard.WriteItem(item, plainTextOnly: asPlainText);
                });

                if (target != nint.Zero && !NativeMethods.ForceForeground(target))
                    ok = false;

                WaitForForeground(target);
                NativeMethods.ReleasePressedModifiers();
                Thread.Sleep(20);
                NativeMethods.SendCtrlV();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("Paste", ex);
                ok = false;
            }

            if (restore && previous is not null)
            {
                // espera o app alvo terminar de ler o clipboard antes de trocar
                // o conteudo de volta - faz parte da mecanica de colagem
                Thread.Sleep(150);
                clipboardThread.BeginInvoke(() => writeGuard.Restore(previous));
            }

            if (!ok)
                _uiDispatcher.BeginInvoke(() => PasteFailed?.Invoke());
        });
    }

    /// <summary>
    /// Espera (pouco) o alvo virar foreground, em vez de um sleep fixo.
    /// Desiste rapido para a colagem continuar responsiva.
    /// </summary>
    private static void WaitForForeground(nint target)
    {
        if (target == nint.Zero)
        {
            Thread.Sleep(40);
            return;
        }
        for (var i = 0; i < 12; i++) // ate ~120ms, normalmente resolve em uma ou duas voltas
        {
            if (NativeMethods.GetForegroundWindow() == target)
                return;
            Thread.Sleep(10);
        }
    }

    /// <summary>So copiar, sem colar (Ctrl+clique). Nada disso toca a UI thread.</summary>
    public void CopyItemToClipboard(ClipboardItem item, bool asPlainText = false)
    {
        var path = item.Type == ClipboardItemType.Image && item.FilePath is not null
            ? mediaStore.ToAbsolute(item.FilePath)
            : null;

        _ = Task.Run(() =>
        {
            try
            {
                if (path is not null)
                {
                    var (png, bmp) = ClipboardWriteGuard.DecodeImageFile(path);
                    writeGuard.WriteImageFromPng(png, bmp);
                }
                else
                {
                    writeGuard.WriteItem(item, plainTextOnly: asPlainText);
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CopyItem", ex);
            }
        });
    }

    /// <summary>Grava texto puro e cola no alvo (usado pelo painel de emoji).</summary>
    public void PasteText(string text)
    {
        var restore = settings.Current.RestoreClipboardAfterPaste;
        var target = SavedTargetWindow;

        _ = Task.Run(() =>
        {
            System.Windows.IDataObject? previous = null;
            try
            {
                // RF-P2.01: snapshot e gravacao sairam da UI thread; a ordem
                // (snapshot antes da gravacao) continua garantida por serem
                // sequenciais aqui
                if (restore)
                    previous = writeGuard.SnapshotCurrent();

                writeGuard.WriteText(text);

                if (target != nint.Zero)
                    NativeMethods.ForceForeground(target);
                WaitForForeground(target);
                NativeMethods.ReleasePressedModifiers();
                Thread.Sleep(20);
                NativeMethods.SendCtrlV();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PasteText", ex);
            }

            if (restore && previous is not null)
            {
                Thread.Sleep(150);
                clipboardThread.BeginInvoke(() => writeGuard.Restore(previous));
            }
        });
    }
}
