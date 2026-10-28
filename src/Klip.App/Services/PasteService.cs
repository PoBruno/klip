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
/// Nenhuma etapa de clipboard roda na UI thread: tudo vai para a
/// <see cref="ClipboardThread"/>. A UI thread so e usada para o evento
/// PasteFailed (que mexe no tray).
/// </summary>
public sealed class PasteService(
    ClipboardWriteGuard writeGuard,
    MediaStore mediaStore,
    SettingsService settings,
    ClipboardThread clipboardThread,
    ClipboardItemRepository repository,
    PasteQueueService pasteQueue)
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
        var forcePlain = settings.Current.AlwaysPastePlainText;
        var richApps = forcePlain ? settings.Current.RichPasteApps.ToArray() : [];

        // decodifica a imagem (disco + decode) FORA da UI thread; texto e barato
        var isImage = item.Type == ClipboardItemType.Image && item.FilePath is not null;
        var imagePath = isImage ? mediaStore.ToAbsolute(item.FilePath!) : null;

        _ = Task.Run(() =>
        {
            var ok = true;
            System.Windows.IDataObject? previous = null;
            try
            {
                // the list hands us a slim row; the full content (html, rtf, long text)
                // is read here, off the UI thread
                var full = repository.LoadFull(item);
                var plain = asPlainText || (forcePlain && !IsRichPasteTarget(target, richApps));

                // o snapshot so precisa acontecer ANTES da gravacao, e continua
                // acontecendo: as duas operacoes sao sequenciais e caem na mesma fila STA
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
                        writeGuard.WriteItem(full, plainTextOnly: plain);
                });

                if (!BringTargetBack(target))
                    ok = false;

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
    /// With "always paste as plain text" on, these apps still get the formatting.
    /// Matched by exe name (WINWORD.EXE), with or without the .exe.
    /// </summary>
    private static bool IsRichPasteTarget(nint target, string[] richApps)
    {
        if (target == nint.Zero || richApps.Length == 0)
            return false;
        var exe = ProcessExeName(target);
        if (exe is null)
            return false;
        var bare = System.IO.Path.GetFileNameWithoutExtension(exe);
        foreach (var app in richApps)
        {
            var name = app.Trim();
            if (string.Equals(name, exe, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, bare, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? ProcessExeName(nint hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return null;
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == nint.Zero)
            return null;
        try
        {
            var path = NativeMethods.QueryProcessImagePathSafe(handle);
            return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Time for the target app to hand focus back to its own field after being
    /// reactivated. Browsers and TeamViewer do that asynchronously; a Ctrl+V that
    /// arrives earlier lands on a window with no focused field and just vanishes.
    /// </summary>
    private const int FocusSettleMs = 120;

    /// <summary>
    /// Brings the target back to the front. Only waits for focus to settle when the
    /// target really lost activation (a click on the search box, a menu); in the normal
    /// path the panel opens without activating and the target never stopped being the
    /// foreground window.
    /// </summary>
    private static bool BringTargetBack(nint target)
    {
        if (target == nint.Zero)
        {
            Thread.Sleep(40);
            return true;
        }

        var wasForeground = NativeMethods.GetForegroundWindow() == target;
        var ok = wasForeground || NativeMethods.ForceForeground(target);
        WaitForForeground(target);
        if (!wasForeground)
            Thread.Sleep(FocusSettleMs);
        return ok;
    }

    /// <summary>
    /// Types the item's text as keystrokes instead of pasting it. For fields that refuse
    /// Ctrl+V (password boxes, TeamViewer ID and password, remote sessions). Leaves the
    /// clipboard alone.
    /// </summary>
    public void TypeItem(ClipboardItem item)
    {
        if (item.Type is not (ClipboardItemType.Text or ClipboardItemType.Html))
            return;
        var target = SavedTargetWindow;

        _ = Task.Run(() =>
        {
            var ok = true;
            try
            {
                // the list hands us a slim row, so the full text is read here, off the UI thread
                var text = repository.LoadFull(item).TextContent;
                if (string.IsNullOrEmpty(text))
                    return;

                if (!BringTargetBack(target))
                    ok = false;
                NativeMethods.ReleasePressedModifiers();
                Thread.Sleep(20);
                NativeMethods.SendUnicodeText(text);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TypeItem", ex);
                ok = false;
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
                    writeGuard.WriteItem(repository.LoadFull(item), plainTextOnly: asPlainText);
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CopyItem", ex);
            }
        });
    }

    /// <summary>Grava texto puro e cola no alvo (usado pelo painel de emoji).</summary>
    public void PasteText(string text) => PasteTextCore(() => text);

    /// <summary>Resolves the snippet placeholders right before pasting it.</summary>
    public void PasteSnippet(string content) => PasteTextCore(() => ExpandSnippet(content));

    /// <summary>Copies the expanded snippet without pasting.</summary>
    public void CopySnippet(string content) => CopyText(() => ExpandSnippet(content));

    /// <summary>Plain text to the clipboard, off the UI thread (OCR result, joined items).</summary>
    public void CopyText(string text) => CopyText(() => text);

    private void CopyText(Func<string> text) =>
        _ = Task.Run(() =>
        {
            try
            {
                writeGuard.WriteText(text());
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CopyText", ex);
            }
        });

    /// <summary>
    /// Several items at once (collect mode, "paste together"). All text: one paste with a
    /// line break between them. Anything else: the sequential queue, one per Ctrl+V.
    /// </summary>
    public void PasteTogether(IReadOnlyList<ClipboardItem> items)
    {
        if (items.Count == 0)
            return;
        if (Klip.Core.Clipboard.PasteJoin.CanJoinAsText(items))
        {
            PasteTextCore(() => Klip.Core.Clipboard.PasteJoin.Join(
                items.Select(i => repository.LoadFull(i).TextContent)));
            return;
        }
        // the queue writes the content itself, so it gets full rows (at most a hundred)
        var full = items.Select(repository.LoadFull).ToList();
        pasteQueue.Arm(full);
    }

    // {clipboard} reads the clipboard thread; called from a pool thread
    private string ExpandSnippet(string content)
    {
        Func<string?>? clip = null;
        if (Klip.Core.Snippets.SnippetExpander.UsesClipboard(content))
        {
            clip = () => clipboardThread.Invoke(() =>
                System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null);
        }
        return Klip.Core.Snippets.SnippetExpander.Expand(content, DateTime.Now, clip);
    }

    private void PasteTextCore(Func<string> buildText)
    {
        var restore = settings.Current.RestoreClipboardAfterPaste;
        var target = SavedTargetWindow;

        _ = Task.Run(() =>
        {
            System.Windows.IDataObject? previous = null;
            try
            {
                // built here, off the UI thread: may read the db or the clipboard
                var text = buildText();

                // snapshot antes da gravacao, garantido por serem sequenciais aqui
                if (restore)
                    previous = writeGuard.SnapshotCurrent();

                writeGuard.WriteText(text);

                BringTargetBack(target);
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
