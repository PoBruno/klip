using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Klip.Core.Capture;

namespace Klip.App.Services;

/// <summary>
/// Continuous scrolling capture: the user scrolls the area at their own pace,
/// a timer grabs frames (~150 ms) and PanoramicStitcher (Core) stitches the
/// ones that line up with enough confidence.
/// </summary>
public sealed class PanoramicCaptureService(ScreenCaptureService screenCapture)
{
    public sealed record Progress(int HeightPx, int FramesDiscarded, bool LastDiscarded);

    public event Action<Progress>? ProgressChanged;
    public event Action<BitmapSource>? PreviewUpdated;

    /// <summary>Memory guard rail hit, caller should finish automatically.</summary>
    public event Action? LimitReached;

    private PanoramicStitcher? _stitcher;
    private DispatcherTimer? _timer;
    private int _x, _y, _width, _height;
    private DateTime _lastPreview = DateTime.MinValue;

    public bool IsRunning => _timer?.IsEnabled == true;

    public void Start(int x, int y, int width, int height, int cadenceMs = 150)
    {
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _stitcher = new PanoramicStitcher(width, height);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(cadenceMs, 100, 2000)),
        };
        _timer.Tick += (_, _) => CaptureTick();
        _timer.Start();
        CaptureTick(); // grab the first frame right away
    }

    private void CaptureTick()
    {
        if (_stitcher is null)
            return;

        try
        {
            var capture = screenCapture.CaptureRegion(_x, _y, _width, _height);
            var stride = _width * 4;
            var pixels = new byte[_height * stride];
            var converted = capture.Format == PixelFormats.Pbgra32 || capture.Format == PixelFormats.Bgra32
                ? capture
                : new FormatConvertedBitmap(capture, PixelFormats.Pbgra32, null, 0);
            converted.CopyPixels(new Int32Rect(0, 0, _width, _height), pixels, stride, 0);

            var result = _stitcher.AddFrame(pixels);

            // memoria no teto: conclui com o que tem, nunca descarta
            if (result.Status == PanoramicFrameStatus.LimitReached)
            {
                _timer?.Stop();
                LimitReached?.Invoke();
                return;
            }

            ProgressChanged?.Invoke(new Progress(
                _stitcher.TotalRows,
                _stitcher.FramesDiscarded,
                result.Status == PanoramicFrameStatus.LowConfidence));

            // live preview of the tail only (cost stays flat, no matter the
            // total height), at most once per second
            if (result.Status == PanoramicFrameStatus.Appended &&
                (DateTime.UtcNow - _lastPreview).TotalMilliseconds > 1000)
            {
                _lastPreview = DateTime.UtcNow;
                var (tail, tailHeight) = _stitcher.GetTail(4000);
                var preview = BitmapSource.Create(_width, tailHeight, 96, 96,
                    PixelFormats.Pbgra32, null, tail, stride);
                preview.Freeze();
                PreviewUpdated?.Invoke(preview);
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PanoramicTick", ex);
        }
    }

    /// <summary>Stops and returns the final image.</summary>
    public (BitmapSource Image, int Discarded)? Stop()
    {
        _timer?.Stop();
        _timer = null;
        if (_stitcher is null || _stitcher.TotalRows == 0)
            return null;
        var image = BuildBitmap();
        var discarded = _stitcher.FramesDiscarded;
        _stitcher = null;
        return (image, discarded);
    }

    public void Cancel()
    {
        _timer?.Stop();
        _timer = null;
        _stitcher = null;
    }

    private BitmapSource BuildBitmap()
    {
        var (bgra, height) = _stitcher!.GetResult();
        var bitmap = BitmapSource.Create(_width, height, 96, 96,
            PixelFormats.Pbgra32, null, bgra, _width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
