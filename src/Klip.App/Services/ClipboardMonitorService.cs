using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Klip.Core.Clipboard;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Watches the clipboard with AddClipboardFormatListener + WM_CLIPBOARDUPDATE.
/// Fast read on the UI thread (has to be STA), then process and save in the background.
/// </summary>
public sealed class ClipboardMonitorService : IDisposable
{
    private readonly ClipboardIngestService _ingest;
    private readonly ClipboardWriteGuard _writeGuard;
    private readonly HwndSource _source;
    private readonly System.Windows.Threading.DispatcherTimer _debounce;
    private uint _lastProcessedSequence;

    /// <summary>Pause capture from the tray or settings.</summary>
    public bool IsPaused { get; set; }

    public ClipboardMonitorService(ClipboardIngestService ingest, ClipboardWriteGuard writeGuard)
    {
        _ingest = ingest;
        _writeGuard = writeGuard;

        // apps fire many WM_CLIPBOARDUPDATE per copy (clear then write),
        // so a short debounce coalesces them and we don't read twice
        _debounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ProcessClipboard();
        };

        var parameters = new HwndSourceParameters("KlipClipboardListener")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new nint(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        if (!NativeMethods.AddClipboardFormatListener(_source.Handle))
            StartupLog.Write("[ERRO] AddClipboardFormatListener falhou");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            handled = true;
            if (!IsPaused)
            {
                _debounce.Stop();
                _debounce.Start(); // restart the coalescing window
            }
        }
        return nint.Zero;
    }

    private void ProcessClipboard()
    {
        var sequence = NativeMethods.GetClipboardSequenceNumber();
        if (sequence == _lastProcessedSequence)
            return; // same state, duplicate echo
        _lastProcessedSequence = sequence;

        // anti-loop: skip our own writes
        if (_writeGuard.IsOwnWrite(sequence))
            return;

        try
        {
            var snapshot = ReadSnapshotWithRetry();
            if (snapshot is null || snapshot.IsEmpty)
                return;

            // save off the UI thread
            _ = Task.Run(() =>
            {
                try
                {
                    var item = _ingest.Ingest(snapshot);
                    if (item is not null)
                        StartupLog.Write($"Ingest: {item.Type} {item.ByteSize}B de {item.SourceApp ?? "?"}");
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("Ingest", ex);
                }
            });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ClipboardUpdate", ex);
        }
    }

    /// <summary>Whole read runs under retry, any GetData can blow up with
    /// CLIPBRD_E_CANT_OPEN while the source app still holds the clipboard.
    /// Reading needs the UI thread (STA), so keep the waits tiny: the clipboard
    /// usually frees within a few ms, and long sleeps here freeze the UI.</summary>
    private ClipboardSnapshot? ReadSnapshotWithRetry()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                return ReadSnapshot();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(15); // short and flat, total worst case ~90ms
            }
        }
        StartupLog.Write("[AVISO] Clipboard ocupado após 6 tentativas; item perdido");
        return null;
    }

    /// <summary>Reads the clipboard; runs on the UI thread (STA).</summary>
    private ClipboardSnapshot? ReadSnapshot()
    {
        var data = System.Windows.Clipboard.GetDataObject();
        if (data is null)
            return null;

        // password managers use these formats to opt out of history
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing"))
            return null;
        if (data.GetDataPresent("CanIncludeInClipboardHistory") &&
            ReadDwordFormat(data, "CanIncludeInClipboardHistory") == 0)
            return null;

        var (sourceApp, sourceTitle) = ResolveSource();

        string? text = null;
        string? htmlFragment = null;
        string? rtf = null;
        byte[]? pngBytes = null;
        int? width = null, height = null;
        IReadOnlyList<string>? files = null;

        if (data.GetDataPresent(DataFormats.UnicodeText))
            text = data.GetData(DataFormats.UnicodeText) as string;

        if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string rawHtml)
        {
            // WPF hands the CF_HTML payload as a string; parser wants UTF-8 bytes
            if (CfHtmlParser.TryParse(System.Text.Encoding.UTF8.GetBytes(rawHtml), out var parsed))
                htmlFragment = parsed.Fragment;
        }

        // keep RTF so a formatação sobrevive when pasting back into Word/WordPad/Outlook
        if (data.GetDataPresent(DataFormats.Rtf) && data.GetData(DataFormats.Rtf) is string rawRtf)
            rtf = rawRtf;

        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream pngStream)
        {
            pngBytes = pngStream.ToArray();
            var (w, h) = TryReadPngSize(pngBytes);
            width = w;
            height = h;
        }
        else if (data.GetDataPresent(DataFormats.Bitmap) &&
                 data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource image)
        {
            image.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            pngBytes = ms.ToArray();
            width = image.PixelWidth;
            height = image.PixelHeight;
        }

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] drop)
            files = drop;

        return new ClipboardSnapshot
        {
            Text = text,
            HtmlFragment = htmlFragment,
            Rtf = rtf,
            PngBytes = pngBytes,
            ImageWidth = width,
            ImageHeight = height,
            Files = files,
            SourceApp = sourceApp,
            SourceTitle = sourceTitle,
        };
    }

    private static int? ReadDwordFormat(IDataObject data, string format)
    {
        try
        {
            if (data.GetData(format) is MemoryStream ms && ms.Length >= 4)
            {
                Span<byte> buf = stackalloc byte[4];
                ms.Position = 0;
                ms.ReadExactly(buf);
                return BitConverter.ToInt32(buf);
            }
        }
        catch (Exception)
        {
            // formato ilegível, trata como se não existisse
        }
        return null;
    }

    /// <summary>Source app/window from clipboard owner, falls back to foreground.</summary>
    private static (string? app, string? title) ResolveSource()
    {
        try
        {
            var owner = NativeMethods.GetClipboardOwner();
            var hwnd = owner != nint.Zero ? owner : NativeMethods.GetForegroundWindow();
            if (hwnd == nint.Zero)
                return (null, null);

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            string? app = null;
            if (pid != 0)
            {
                using var process = Process.GetProcessById((int)pid);
                app = process.ProcessName + ".exe";
            }

            // titulo: a janela em foreground [e mais fiel que o owner, que costuma vir oculto
            var title = NativeMethods.GetWindowTextSafe(NativeMethods.GetForegroundWindow());
            return (app, title.Length > 0 ? title : null);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static (int?, int?) TryReadPngSize(byte[] png)
    {
        // IHDR: width/height are big-endian at offsets 16..23
        if (png.Length < 24)
            return (null, null);
        var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (w > 0 ? w : null, h > 0 ? h : null);
    }

    public void Dispose()
    {
        _debounce.Stop();
        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}
