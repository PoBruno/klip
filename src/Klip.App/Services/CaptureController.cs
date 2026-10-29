using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Klip.App.Windows;
using Klip.Core.Capture;
using Klip.Core.Clipboard;
using Klip.Core.Settings;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Drives a capture session: freeze frame, then one overlay per monitor,
/// then the result goes to clipboard + history + a toast with a pin button.
/// Also handles the scrolling flow, text and color picking, and repeating the
/// last region without the overlay.
/// </summary>
public sealed class CaptureController(
    ScreenCaptureService screenCapture,
    PanoramicCaptureService panoramicCapture,
    ClipboardWriteGuard writeGuard,
    ClipboardIngestService ingest,
    SettingsService settings,
    RecordingController recording,
    MemorySettler memory,
    OcrService ocr,
    PinnedImageService pinned)
{
    private readonly List<CaptureOverlayWindow> _overlays = [];
    private bool _sessionActive;
    private CaptureSessionState? _session;

    /// <summary>Post-capture tray toast; the App subscribes to this.</summary>
    public event Action<string>? CaptureCompleted;

    /// <summary>Asks for the editor with the captured bitmap, straight from memory.</summary>
    public event Action<BitmapSource>? EditImageRequested;

    /// <summary>Last captured item so the toast can reopen it in the editor.</summary>
    public Core.Storage.ClipboardItem? LastCapturedItem { get; private set; }

    public void StartCapture(CaptureMode? forcedMode = null)
    {
        if (_sessionActive)
            return;
        _sessionActive = true;

        var sw = StartupLog.VerboseEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            var frames = screenCapture.FreezeAllMonitors();

            NativeMethods.GetCursorPos(out var cursor);
            var cursorMonitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);

            // one state for every monitor of this session, a fresh one next time
            var s = settings.Current;
            _session = new CaptureSessionState(
                CaptureModePolicy.ResolveInitial(s, recording.IsActive, forcedMode))
            {
                Forced = forcedMode is not null,
            };

            // top-level windows only matter for window mode: enumerate on first use,
            // once per session, shared by all overlays
            var topWindows = new Lazy<IReadOnlyList<NativeMethods.TopLevelWindow>>(EnumerateTopWindows);

            foreach (var frame in frames)
            {
                var overlay = new CaptureOverlayWindow(
                    frame,
                    showToolbar: frame.Monitor.Handle == cursorMonitor,
                    _session,
                    () => topWindows.Value,
                    s.EditorModifierKey,
                    s.AlwaysOpenEditorAfterCapture,
                    settings,
                    // concrete interop types only through the factory
                    RecordingInteropFactory.CreateAudioDeviceEnumerator,
                    OnRegionSelected,
                    CancelCapture);
                _overlays.Add(overlay);
            }

            foreach (var overlay in _overlays)
                overlay.ShowOverlay();

            if (sw is not null)
                StartupLog.WriteVerbose($"Captura: overlay visivel em {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("StartCapture", ex);
            CancelCapture();
        }
    }

    /// <summary>
    /// Captures the last rectangle again with no overlay. When the monitor layout or
    /// DPI changed since it was saved the pixels mean something else, so the normal
    /// overlay opens instead.
    /// </summary>
    public void RepeatLastRegion()
    {
        if (_sessionActive)
            return;

        if (!SavedCaptureRegion.TryParse(settings.Current.LastCaptureRegion, out var saved))
        {
            StartCapture();
            return;
        }

        var monitors = NativeMethods.GetMonitors();
        var snapshots = monitors.Select(m => new MonitorSnapshot(
            new ScreenRect(m.Bounds.left, m.Bounds.top, m.Bounds.right - m.Bounds.left, m.Bounds.bottom - m.Bounds.top),
            m.Dpi == 0 ? 96 : m.Dpi));
        if (!saved.IsStillValid(snapshots))
        {
            StartCapture();
            return;
        }

        _sessionActive = true;
        try
        {
            var r = saved.Region;
            var frame = screenCapture.CaptureRegion(r.X, r.Y, r.Width, r.Height);
            PublishResult(frame, Localization.Loc.CaptureTitleScreen, openEditor: false);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RepeatRegion", ex);
        }
        finally
        {
            _sessionActive = false;
        }
    }

    private static IReadOnlyList<NativeMethods.TopLevelWindow> EnumerateTopWindows()
    {
        var ours = new HashSet<nint>();
        foreach (Window window in Application.Current.Windows)
            ours.Add(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        return NativeMethods.GetVisibleTopLevelWindows(ours);
    }

    /// <summary>
    /// Pays the cold start of the first capture (GDI, WIC, JIT of the overlay) while the
    /// app is idle, so the first Win+Shift+S after boot doesn't take ~300 ms extra.
    /// </summary>
    public void WarmUp()
    {
        if (_sessionActive)
            return;
        try
        {
            var monitors = NativeMethods.GetMonitors();
            if (monitors.Count == 0)
                return;
            var m = monitors[0];
            var frame = screenCapture.CaptureRegion(m.Bounds.left, m.Bounds.top, 1, 1);
            var crop = ScreenCaptureService.Crop(frame, new Int32Rect(0, 0, 1, 1));
            _ = ScreenCaptureService.EncodePng(crop);

            // built and thrown away, never shown (no hwnd): just JIT and the static brushes
            var warm = new CaptureOverlayWindow(
                new FrozenMonitor(m, frame), showToolbar: true,
                new CaptureSessionState(CaptureMode.Rectangle), () => [],
                CaptureEditorModifier.None, false, settings,
                RecordingInteropFactory.CreateAudioDeviceEnumerator,
                (_, _, _, _, _) => { }, () => { });
            // a Window registers itself in Application.Windows even unshown: close to drop it
            warm.CloseOverlay();
            StartupLog.WriteVerbose("Captura: aquecida");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("CaptureWarmUp", ex);
        }
    }

    /// <summary>
    /// Gets the selection in physical pixels from the source monitor.
    /// <paramref name="openEditorRequested"/> is the physical modifier state reported by
    /// the overlay; the decision about it lives here.
    /// </summary>
    private void OnRegionSelected(FrozenMonitor source, Int32Rect physicalRect, CaptureMode mode, Point[]? mask, bool openEditorRequested)
    {
        CloseOverlays();
        EndSession();

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

        if (mode == CaptureMode.Color)
        {
            PickColor(source, physicalRect);
            return;
        }

        if (mode == CaptureMode.Text)
        {
            _ = ReadTextAsync(source, physicalRect);
            return;
        }

        // recordings leave the still capture flow, region in physical px of the virtual desktop
        if (mode is CaptureMode.Gif or CaptureMode.Mp4)
        {
            var region = new Core.Recording.RecordingRegion(
                source.Monitor.Bounds.left + physicalRect.X,
                source.Monitor.Bounds.top + physicalRect.Y,
                physicalRect.Width,
                physicalRect.Height);
            recording.Start(region,
                mode == CaptureMode.Gif ? RecordingKind.Gif : RecordingKind.Mp4,
                source.Monitor.Bounds);
            _sessionActive = false;
            return;
        }

        try
        {
            var cropped = ScreenCaptureService.Crop(source.Frame, physicalRect);
            // freeform: keep only what's inside the lasso, rest goes transparent
            if (mode == CaptureMode.Freeform && mask is { Length: >= 3 })
                cropped = ApplyPolygonMask(cropped, mask);
            // modifier OR "always editor"; no toast when the editor opens anyway
            var openEditor = EditorOpenDecision.ShouldOpenEditor(
                openEditorRequested, settings.Current.AlwaysOpenEditorAfterCapture);
            PublishResult(cropped, Localization.Loc.CaptureTitleScreen, openEditor, suppressToast: openEditor);
            if (mode == CaptureMode.Rectangle)
                RememberRegion(source, physicalRect);
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

    /// <summary>Saves the rectangle (physical px of the virtual desktop) for the repeat hotkey.</summary>
    private void RememberRegion(FrozenMonitor source, Int32Rect rect)
    {
        var b = source.Monitor.Bounds;
        var monitor = new ScreenRect(b.left, b.top, b.right - b.left, b.bottom - b.top);
        var region = new ScreenRect(b.left + rect.X, b.top + rect.Y, rect.Width, rect.Height);
        var text = new SavedCaptureRegion(region, monitor, source.Monitor.Dpi == 0 ? 96 : source.Monitor.Dpi).Serialize();
        if (text != settings.Current.LastCaptureRegion)
            settings.Update(x => x.LastCaptureRegion = text);
    }

    /// <summary>Copies the color under the click, read from the frozen frame.</summary>
    private void PickColor(FrozenMonitor source, Int32Rect rect)
    {
        _sessionActive = false;
        var pixel = new byte[4];
        if (!ScreenCaptureService.TryReadPixel(source.Frame, rect.X, rect.Y, pixel, out var r, out var g, out var b))
            return;

        var text = ColorFormatter.Format(r, g, b, settings.Current.ColorPickerFormat);
        _ = Task.Run(() =>
        {
            try
            {
                writeGuard.WriteText(text);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PickColor", ex);
            }
        });

        CaptureToastWindow.Present(new CaptureToast(
            string.Format(Localization.Loc.NotifyColorCopied, text), "")
        {
            Swatch = System.Windows.Media.Color.FromRgb(r, g, b),
        });
    }

    /// <summary>
    /// OCR on the selected area, straight to the clipboard. The engine loads on the
    /// first call here, never before.
    /// </summary>
    private async Task ReadTextAsync(FrozenMonitor source, Int32Rect rect)
    {
        _sessionActive = false;
        try
        {
            var crop = ScreenCaptureService.Crop(source.Frame, rect);
            var png = await Task.Run(() => ScreenCaptureService.EncodePng(crop));
            var text = (await ocr.ReadTextAsync(png).ConfigureAwait(true)).Trim();

            if (text.Length == 0)
            {
                var message = ocr.IsAvailable ? Localization.Loc.NotifyNoText : Localization.Loc.OcrUnavailable;
                CaptureToastWindow.Present(new CaptureToast(message, "") { Glyph = "\uE8D2" });
                return;
            }

            await Task.Run(() => writeGuard.WriteText(text));
            CaptureToastWindow.Present(new CaptureToast(
                string.Format(Localization.Loc.NotifyTextCopied, text.Length), Preview(text))
            {
                Glyph = "\uE8D2",
            });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("CaptureText", ex);
        }
        finally
        {
            memory.Schedule();
        }
    }

    // one line, short: the toast is not a text viewer
    private static string Preview(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= 80 ? line : line[..79] + "...";
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
        void OnPreview(BitmapSource preview) =>
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
                    // scrolling always opens in the editor, so no toast here either
                    PublishResult(image, Localization.Loc.CaptureTitleScrolling, openEditor: true, suppressToast: true);
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
            memory.Schedule();
        }
    }

    /// <summary>
    /// clipboard (PNG + DIB, anti-loop) + history + auto-save + toast.
    /// The editor opens right away with the bitmap in memory; PNG encode, ingest
    /// (hash, file, thumbnail, SQLite) and auto-save run on the pool, so the UI
    /// thread is free the moment the mouse is released.
    /// </summary>
    private void PublishResult(BitmapSource image, string title, bool openEditor, bool suppressToast = false)
    {
        if (!image.IsFrozen)
        {
            if (image.CanFreeze)
                image.Freeze();
            else
                image = BitmapFrame.Create(image); // frozen copy, the pool can't touch a live one
        }

        if (openEditor)
            EditImageRequested?.Invoke(image);

        var dispatcher = Dispatcher.CurrentDispatcher;
        var autoSave = settings.Current.AutoSaveScreenshots;
        var width = image.PixelWidth;
        var height = image.PixelHeight;

        _ = Task.Run(() =>
        {
            var png = ScreenCaptureService.EncodePng(image);
            writeGuard.WriteImageFromPng(png, image);

            var item = ingest.Ingest(new ClipboardSnapshot
            {
                PngBytes = png,
                ImageWidth = width,
                ImageHeight = height,
                SourceApp = "Klip",
                SourceTitle = title,
                Origin = Core.Storage.ClipboardItemOrigin.Capture,
            });

            if (autoSave)
                AutoSave(png);
            return item;
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                StartupLog.WriteException("PublishResult", t.Exception!.GetBaseException());
                return;
            }
            var item = t.Result;
            LastCapturedItem = item;
            if (!suppressToast)
            {
                CaptureToastWindow.Present(new CaptureToast(
                    string.Format(Localization.Loc.NotifyCaptureCopied, width, height),
                    Localization.Loc.NotifyClickToEdit)
                {
                    Thumbnail = image,
                    OnClick = () => EditImageRequested?.Invoke(image),
                    OnPin = () => pinned.Pin(image),
                });
            }
            StartupLog.Write($"Captura: {width}x{height}, item {item?.Id}");
        }, CancellationToken.None, TaskContinuationOptions.None,
            dispatcher.CheckAccess() ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default);
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
        EndSession();
        _sessionActive = false;
    }

    /// <summary>
    /// Saves the chosen mode once per session (captured or cancelled), not on every
    /// toolbar click: each Update wakes the settings listeners.
    /// </summary>
    private void EndSession()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            var remember = CaptureModePolicy.ModeToRemember(settings.Current, session.Mode, session.Forced);
            if (remember is { } mode)
                settings.Update(x => x.LastCaptureMode = mode);
        }
        // the frozen frames (8 MB per 1080p monitor, 33 MB per 4K) only go back with a GC
        memory.Schedule();
    }

    private void CloseOverlays()
    {
        foreach (var overlay in _overlays)
            overlay.CloseOverlay();
        _overlays.Clear();
    }
}
