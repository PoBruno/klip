namespace Klip.Core.Capture;

/// <summary>What happened to a frame after we tried to stitch it.</summary>
public enum PanoramicFrameStatus
{
    /// <summary>New rows were appended.</summary>
    Appended,
    /// <summary>No scroll since the last frame.</summary>
    NoMovement,
    /// <summary>Couldn't align it with confidence, so we dropped the frame.</summary>
    LowConfidence,
    /// <summary>Hit the memory guard, wrap it up on its own.</summary>
    LimitReached,
}

public readonly record struct PanoramicFrameResult(PanoramicFrameStatus Status, int AppendedRows, int TotalRows);

/// <summary>
/// Snagit-style panoramic stitch: user scrolls at their own pace and every
/// frame gets aligned to the previous one by a tolerant translation (row
/// signatures + a SAD offset search). A frame that doesn't match with enough
/// confidence gets dropped so we never glue garbage in. Pure logic, no UI.
/// </summary>
public sealed class PanoramicStitcher
{
    private readonly int _width;
    private readonly int _frameHeight;
    private readonly int _maxTotalHeight;
    private readonly int _tolerance;
    private readonly int[] _sampleColumns;

    private readonly List<byte[]> _rows = [];     // result rows (stride = width*4)
    private byte[]? _lastFrame;
    private byte[][]? _lastSigs;
    private int _footerRows;

    private const int SampleCount = 32;
    private const int SigStride = SampleCount * 3; // B,G,R per sample

    public int TotalRows => _rows.Count;
    public int FramesAccepted { get; private set; }
    public int FramesDiscarded { get; private set; }

    public PanoramicStitcher(int width, int frameHeight,
        int tolerancePerChannel = 6, long maxMemoryBytes = 1_500_000_000)
    {
        _width = width;
        _frameHeight = frameHeight;
        _tolerance = tolerancePerChannel;
        // no fixed height cap: the real limit is the buffer memory
        _maxTotalHeight = (int)Math.Min(int.MaxValue, Math.Max(frameHeight, maxMemoryBytes / (width * 4L)));

        // sample columns away from the edges to dodge scrollbars and animated stuff
        var margin = Math.Max(8, width / 20);
        var usable = Math.Max(1, width - 2 * margin);
        _sampleColumns = new int[SampleCount];
        for (var i = 0; i < SampleCount; i++)
            _sampleColumns[i] = margin + (int)((long)i * usable / SampleCount);
    }

    private int Stride => _width * 4;

    public PanoramicFrameResult AddFrame(byte[] frameBgra)
    {
        if (frameBgra.Length < _frameHeight * Stride)
            throw new ArgumentException("Frame menor que o esperado", nameof(frameBgra));
        if (TotalRows >= _maxTotalHeight)
            return new(PanoramicFrameStatus.LimitReached, 0, TotalRows);
        var sigs = ComputeSignatures(frameBgra);

        if (_lastFrame is null)
        {
            AppendRows(frameBgra, 0, _frameHeight);
            Commit(frameBgra, sigs);
            return new(PanoramicFrameStatus.Appended, _frameHeight, TotalRows);
        }

        // Didn't scroll at all? compare the whole frame before looking at the
        // footer, otherwise two equal frames turned into one giant "footer"
        if (RangeDiff(_lastSigs!, 0, sigs, 0, _frameHeight) <= _tolerance)
        {
            Commit(frameBgra, sigs);
            return new(PanoramicFrameStatus.NoMovement, 0, TotalRows);
        }

        // sticky footer: bottom rows that stay put from one frame to the next
        var footer = CountStaticBottomRows(_lastSigs!, sigs);
        if (footer > _footerRows)
        {
            var trim = Math.Min(footer - _footerRows, _rows.Count);
            _rows.RemoveRange(_rows.Count - trim, trim);
            _footerRows = footer;
        }
        var effectiveH = _frameHeight - _footerRows;

        // grab a strip near the bottom (footer is gone, header sits up top).
        // the lower the strip, the bigger the scroll jump we can still catch
        var stripH = Math.Clamp(effectiveH / 3, 8, 64);
        var stripStart = Math.Max(0, effectiveH - stripH - 4);

        var (bestPos, bestDiff) = FindStrip(_lastSigs!, stripStart, stripH, sigs, searchMax: stripStart);
        if (bestDiff > _tolerance)
        {
            FramesDiscarded++;
            Commit(frameBgra, sigs); // becomes the new reference so we can settle and keep going
            return new(PanoramicFrameStatus.LowConfidence, 0, TotalRows);
        }

        var delta = stripStart - bestPos;
        if (delta <= 0)
        {
            Commit(frameBgra, sigs);
            return new(PanoramicFrameStatus.NoMovement, 0, TotalRows);
        }

        AppendRows(frameBgra, effectiveH - delta, delta);
        FramesAccepted++;
        Commit(frameBgra, sigs);
        return new(PanoramicFrameStatus.Appended, delta, TotalRows);
    }

    /// <summary>Final result: everything we stacked up plus the sticky footer (once) from the last frame.</summary>
    public (byte[] Bgra, int Height) GetResult()
    {
        var footer = _footerRows > 0 && _lastFrame is not null ? _footerRows : 0;
        var height = Math.Max(1, TotalRows + footer);
        var result = new byte[height * Stride];
        for (var i = 0; i < TotalRows; i++)
            _rows[i].CopyTo(result, i * Stride);
        if (footer > 0)
            Buffer.BlockCopy(_lastFrame!, (_frameHeight - footer) * Stride,
                result, TotalRows * Stride, footer * Stride);
        return (result, height);
    }

    /// <summary>
    /// Tail end of what we have so far (constant cost) for a live preview on
    /// really long captures. Never copies the whole image.
    /// </summary>
    public (byte[] Bgra, int Height) GetTail(int maxRows)
    {
        var count = Math.Min(maxRows, TotalRows);
        if (count == 0)
            return (new byte[Stride], 1);
        var result = new byte[count * Stride];
        var start = TotalRows - count;
        for (var i = 0; i < count; i++)
            _rows[start + i].CopyTo(result, i * Stride);
        return (result, count);
    }

    // ----- Internals -----

    private void Commit(byte[] frame, byte[][] sigs)
    {
        _lastFrame = (byte[])frame.Clone();
        _lastSigs = sigs;
    }

    private void AppendRows(byte[] frame, int startRow, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var row = new byte[Stride];
            Buffer.BlockCopy(frame, (startRow + i) * Stride, row, 0, Stride);
            _rows.Add(row);
        }
    }

    /// <summary>Per-row signature: B,G,R from the sampled columns.</summary>
    private byte[][] ComputeSignatures(byte[] frame)
    {
        var sigs = new byte[_frameHeight][];
        for (var y = 0; y < _frameHeight; y++)
        {
            var sig = new byte[SigStride];
            var rowOffset = y * Stride;
            for (var i = 0; i < SampleCount; i++)
            {
                var px = rowOffset + _sampleColumns[i] * 4;
                sig[i * 3] = frame[px];
                sig[i * 3 + 1] = frame[px + 1];
                sig[i * 3 + 2] = frame[px + 2];
            }
            sigs[y] = sig;
        }
        return sigs;
    }

    /// <summary>Finds where the strip landed in the new frame by minimizing the mean SAD.</summary>
    private static (int Pos, double Diff) FindStrip(byte[][] prevSigs, int stripStart, int stripH,
        byte[][] newSigs, int searchMax)
    {
        var bestPos = -1;
        var bestDiff = double.MaxValue;
        for (var p = searchMax; p >= 0; p--)
        {
            var diff = RangeDiff(prevSigs, stripStart, newSigs, p, stripH, earlyExit: bestDiff);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestPos = p;
            }
        }
        return (bestPos, bestDiff);
    }

    /// <summary>Mean SAD per byte between signature strips (tolerant to noise).</summary>
    private static double RangeDiff(byte[][] a, int aStart, byte[][] b, int bStart, int rows,
        double earlyExit = double.MaxValue)
    {
        long sum = 0;
        long count = 0;
        var exitThreshold = earlyExit == double.MaxValue ? long.MaxValue : (long)(earlyExit * rows * SigStride);
        for (var i = 0; i < rows; i++)
        {
            var ra = a[aStart + i];
            var rb = b[bStart + i];
            for (var j = 0; j < SigStride; j++)
                sum += Math.Abs(ra[j] - rb[j]);
            count += SigStride;
            if (sum > exitThreshold)
                return double.MaxValue; // already worse than the best so far
        }
        return (double)sum / count;
    }

    /// <summary>conta as linhas de baixo que ficaram paradas (com tolerancia), no maximo 1/3 do frame.</summary>
    private int CountStaticBottomRows(byte[][] prevSigs, byte[][] newSigs)
    {
        var count = 0;
        var cap = _frameHeight / 3;
        for (var y = _frameHeight - 1; y >= 0 && count < cap; y--)
        {
            long sum = 0;
            var ra = prevSigs[y];
            var rb = newSigs[y];
            for (var j = 0; j < SigStride; j++)
                sum += Math.Abs(ra[j] - rb[j]);
            if ((double)sum / SigStride > _tolerance)
                break;
            count++;
        }
        return count;
    }
}
