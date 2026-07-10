namespace Klip.Core.Imaging;

/// <summary>
/// Background removal by flood fill from the edges: pixels connected to the
/// edges with a color close to the seed become transparent. Works well for solid
/// backgrounds/screenshots; it's not AI segmentation. Runs on a BGRA32 buffer -
/// pure and testable without UI.
/// </summary>
public static class BackgroundRemover
{
    /// <summary>
    /// Runs the removal in-place on the BGRA buffer (4 bytes/pixel, row by row).
    /// Returns how many pixels were removed.
    /// </summary>
    public static int RemoveFromEdges(byte[] bgra, int width, int height, int tolerance = 32)
    {
        if (bgra.Length < width * height * 4)
            throw new ArgumentException("Buffer menor que width*height*4", nameof(bgra));

        var visited = new bool[width * height];
        var queue = new Queue<(int x, int y, byte r, byte g, byte b)>();

        // seeds: every pixel on the 4 edges (each one keeps its own color)
        for (var x = 0; x < width; x++)
        {
            EnqueueSeed(x, 0);
            EnqueueSeed(x, height - 1);
        }
        for (var y = 0; y < height; y++)
        {
            EnqueueSeed(0, y);
            EnqueueSeed(width - 1, y);
        }

        var removed = 0;
        while (queue.Count > 0)
        {
            var (x, y, r, g, b) = queue.Dequeue();
            var offset = (y * width + x) * 4;

            bgra[offset + 3] = 0; // wipe the alpha
            removed++;

            // 4-connected neighbors close to the region seed color
            TryVisit(x - 1, y, r, g, b);
            TryVisit(x + 1, y, r, g, b);
            TryVisit(x, y - 1, r, g, b);
            TryVisit(x, y + 1, r, g, b);
        }
        return removed;

        void EnqueueSeed(int x, int y)
        {
            var index = y * width + x;
            if (visited[index])
                return;
            visited[index] = true;
            var offset = index * 4;
            queue.Enqueue((x, y, bgra[offset + 2], bgra[offset + 1], bgra[offset]));
        }

        void TryVisit(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return;
            var index = y * width + x;
            if (visited[index])
                return;
            var offset = index * 4;
            if (Distance(bgra[offset + 2], bgra[offset + 1], bgra[offset], r, g, b) > tolerance)
                return;
            visited[index] = true;
            queue.Enqueue((x, y, r, g, b));
        }

        static int Distance(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2) =>
            Math.Max(Math.Abs(r1 - r2), Math.Max(Math.Abs(g1 - g2), Math.Abs(b1 - b2)));
    }
}
