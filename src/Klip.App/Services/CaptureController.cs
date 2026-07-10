using System.Windows;
using System.Windows.Media.Imaging;
using Klip.App.Windows;
using Klip.Core.Clipboard;
using Klip.Core.Settings;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Drives a capture session: freeze frame, then one overlay per monitor,
/// then the result goes to clipboard + history + a tray toast. Also handles
/// the scrolling capture flow.
/// </summary>
public sealed class CaptureController(
    ScreenCaptureService screenCapture,
    PanoramicCaptureService panoramicCapture,
    ClipboardWriteGuard writeGuard,
    ClipboardIngestService ingest,
    SettingsService settings)
{
    private readonly List<CaptureOverlayWindow> _overlays = [];
    private bool _sessionActive;

    /// <summary>Post-capture tray toast; the App subscribes to this.</summary>
    public event Action<string>? CaptureCompleted;

    /// <summary>Asks the editor to open the scrolling capture result.</summary>
    public event Action<Core.Storage.ClipboardItem>? EditRequested;

    /// <summary>Last captured item so the toast can reopen it in the editor.</summary>
    public Core.Storage.ClipboardItem? LastCapturedItem { get; private set; }

    public void StartCapture()
    {
        if (_sessionActive)
            return;
        _sessionActive = true;

        try
        {
            var frames = screenCapture.FreezeAllMonitors();

            NativeMethods.GetCursorPos(out var cursor);
            var cursorMonitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);

            var ourWindows = new HashSet<nint>();
            foreach (Window window in Application.Current.Windows)
                ourWindows.Add(new System.Windows.Interop.WindowInteropHelper(window).Handle);

            // top-level windows in Z order, used by window mode
            var topWindows = NativeMethods.GetVisibleTopLevelWindows(ourWindows);

            foreach (var frame in frames)
            {
                var overlay = new CaptureOverlayWindow(
                    frame,
                    showToolbar: frame.Monitor.Handle == cursorMonitor,
                    topWindows,
                    OnRegionSelected,
                    CancelCapture);
                _overlays.Add(overlay);
            }

            foreach (var overlay in _overlays)
                overlay.ShowOverlay();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("StartCapture", ex);
            CancelCapture();
        }
    }

    /// <summary>Gets the selection in physical pixels from the source monitor.</summary>
    private void OnRegionSelected(FrozenMonitor source, Int32Rect physicalRect, CaptureMode mode, Point[]? mask)
    {
        CloseOverlays();

        if (physicalRect.Width < 1 || physicalRect.Height < 1)
        {
            _sessionActive = false;
            return;
        }

        if (mode == CaptureMode.Scrolling)
        {
            _ = RunScrollingCaptureAsync(source, physicalRect);
            return;
        }

        try
        {
            var cropped = ScreenCaptureService.Crop(source.Frame, physicalRect);
            // freeform: keep only what's inside the lasso, rest goes transparent
            if (mode == CaptureMode.Freeform && mask is { Length: >= 3 })
                cropped = ApplyPolygonMask(cropped, mask);
            PublishResult(cropped, Localization.Loc.CaptureTitleScreen, openEditor: false);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OnRegionSelected", ex);
        }
        finally
        {
            _sessionActive = false;
        }
    }

    /// <summary>Clips the image to the lasso polygon: outside the shape becomes transparent.</summary>
    private static BitmapSource ApplyPolygonMask(BitmapSource cropped, Point[] polygon)
    {
        var w = cropped.PixelWidth;
        var h = cropped.PixelHeight;

        var geometry = new System.Windows.Media.StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(polygon[0], true, true);
            ctx.PolyLineTo(polygon.Skip(1).ToArray(), true, false);
        }
        geometry.Freeze();

        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // clip to the polygon, then paint the cropped image inside it
            dc.PushClip(geometry);
            dc.DrawImage(cropped, new Rect(0, 0, w, h));
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Scrolling capture flow: fixed frame on the region plus a side panel;
    /// the user scrolls, and Done closes and publishes the result.
    /// </summary>
    private async Task RunScrollingCaptureAsync(FrozenMonitor source, Int32Rect rect)
    {
        var x = source.Monitor.Bounds.left + rect.X;
        var y = source.Monitor.Bounds.top + rect.Y;

        var frame = new RegionFrameWindow();
        var panel = new ScrollCapturePanel();
        var completion = new TaskCompletionSource<bool>();
        var limitHit = false;
        panel.DoneRequested += () => completion.TrySetResult(true);
        panel.CancelRequested += () => completion.TrySetResult(false);

        void OnProgress(PanoramicCaptureService.Progress p) =>
            panel.UpdateProgress(p.HeightPx, p.LastDiscarded);
        void OnPreview(System.Windows.Media.Imaging.BitmapSource preview) =>
            panel.UpdatePreview(preview);
        void OnLimit()
        {
            // memory near the cap, just finish with whatever we got
            limitHit = true;
            completion.TrySetResult(true);
        }

        panoramicCapture.ProgressChanged += OnProgress;
        panoramicCapture.PreviewUpdated += OnPreview;
        panoramicCapture.LimitReached += OnLimit;

        try
        {
            frame.ShowAround(x, y, rect.Width, rect.Height);
            panel.ShowBeside(source.Monitor.Bounds, x, y, rect.Width, rect.Height);

            // target UI comes back after the overlay closes, let it settle
            await Task.Delay(300);
            panoramicCapture.Start(x, y, rect.Width, rect.Height,
                cadenceMs: settings.Current.ScrollCaptureDelayMs);
            var done = await completion.Task;

            if (done)
            {
                var result = panoramicCapture.Stop();
                if (result is not null)
                {
                    var (image, discarded) = result.Value;
                    PublishResult(image, Localization.Loc.CaptureTitleScrolling, openEditor: true);
                    if (limitHit)
                        CaptureCompleted?.Invoke(Localization.Loc.NotifyMemoryLimit);
                    else if (discarded > 3)
                        CaptureCompleted?.Invoke(Localization.Loc.NotifyFramesDiscarded);
                }
            }
            else
            {
                panoramicCapture.Cancel();
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PanoramicCapture", ex);
            panoramicCapture.Cancel();
        }
        finally
        {
            panoramicCapture.ProgressChanged -= OnProgress;
            panoramicCapture.PreviewUpdated -= OnPreview;
            panoramicCapture.LimitReached -= OnLimit;
            frame.Close();
            panel.Close();
            _sessionActive = false;
        }
    }

    /// <summary>clipboard (PNG + DIB, anti-loop) + history + auto-save + toast.</summary>
    private Core.Storage.ClipboardItem? PublishResult(BitmapSource image, string title, bool openEditor)
    {
        var png = ScreenCaptureService.EncodePng(image);
        writeGuard.WriteImageFromPng(png, image);

        var item = ingest.Ingest(new ClipboardSnapshot
        {
            PngBytes = png,
            ImageWidth = image.PixelWidth,
            ImageHeight = image.PixelHeight,
            SourceApp = "Klip",
            SourceTitle = title,
            Origin = Core.Storage.ClipboardItemOrigin.Capture,
        });
        LastCapturedItem = item;

        // optional auto-save
        if (settings.Current.AutoSaveScreenshots)
            AutoSave(png);

        CaptureCompleted?.Invoke(
            string.Format(Localization.Loc.NotifyCaptureCopied, image.PixelWidth, image.PixelHeight) +
            ". " + Localization.Loc.NotifyClickToEdit);
        StartupLog.Write($"Captura: {image.PixelWidth}x{image.PixelHeight}, item {item?.Id}");

        if (openEditor && item is not null)
            EditRequested?.Invoke(item); // scroll result opens straight in the editor

        return item;
    }

    private void AutoSave(byte[] png)
    {
        try
        {
            var folder = settings.Current.ScreenshotsFolder
                ?? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            System.IO.Directory.CreateDirectory(folder);
            // same naming pattern the native tool uses
            var file = System.IO.Path.Combine(folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            System.IO.File.WriteAllBytes(file, png);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("AutoSave", ex);
        }
    }

    public void CancelCapture()
    {
        CloseOverlays();
        _sessionActive = false;
    }

    private void CloseOverlays()
    {
        foreach (var overlay in _overlays)
            overlay.CloseOverlay();
        _overlays.Clear();
    }
}
