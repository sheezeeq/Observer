using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Observer;

internal sealed record Detection(Rectangle Bounds, int Pixels);

internal sealed class MotionEngine
{
    private const int AnalysisWidth = 480;
    private byte[]? previous;
    private float[]? background;
    private int width;
    private int height;
    private int warmup;

    public void Reset()
    {
        previous = null;
        background = null;
        warmup = 0;
    }

    public MotionResult Analyze(Bitmap source, Rectangle focus, int sensitivity)
    {
        int sampleWidth = Math.Min(AnalysisWidth, source.Width);
        int sampleHeight = Math.Max(1, (int)Math.Round(source.Height * sampleWidth / (double)source.Width));
        using var small = new Bitmap(sampleWidth, sampleHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            g.DrawImage(source, new Rectangle(0, 0, sampleWidth, sampleHeight));
        }
        byte[] gray = Grayscale(small);
        if (background is null || previous is null || width != sampleWidth || height != sampleHeight)
        {
            width = sampleWidth;
            height = sampleHeight;
            background = gray.Select(x => (float)x).ToArray();
            previous = gray;
            warmup = 0;
            return new MotionResult([], 0, false, IsDark(gray));
        }

        int count = gray.Length;
        bool dark = IsDark(gray);
        long frameDelta = 0;
        for (int i = 0; i < count; i++) frameDelta += Math.Abs(gray[i] - previous[i]);
        bool frozen = frameDelta / (double)count < 0.35;
        double globalChange = frameDelta / (double)count;
        if (globalChange > 42)
        {
            Reset();
            return new MotionResult([], globalChange, frozen, dark);
        }

        warmup++;
        int x0 = Math.Clamp((int)(focus.Left * sampleWidth / (double)source.Width), 0, sampleWidth - 1);
        int y0 = Math.Clamp((int)(focus.Top * sampleHeight / (double)source.Height), 0, sampleHeight - 1);
        int x1 = Math.Clamp((int)(focus.Right * sampleWidth / (double)source.Width), x0 + 1, sampleWidth);
        int y1 = Math.Clamp((int)(focus.Bottom * sampleHeight / (double)source.Height), y0 + 1, sampleHeight);
        int threshold = 46 - sensitivity / 4;
        int cell = 8;
        int columns = (sampleWidth + cell - 1) / cell;
        int rows = (sampleHeight + cell - 1) / cell;
        int[] active = new int[columns * rows];
        int[] changed = new int[columns * rows];
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int p = y * sampleWidth + x;
                int k = (y / cell) * columns + x / cell;
                if (Math.Abs(gray[p] - background[p]) > threshold) active[k]++;
                if (Math.Abs(gray[p] - previous[p]) > 6) changed[k]++;
            }
        }

        var selected = new bool[active.Length];
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            int k = y * columns + x;
            selected[k] = active[k] >= 14 && changed[k] >= 3;
        }

        var found = new List<Detection>();
        var visited = new bool[selected.Length];
        var queue = new Queue<int>();
        for (int start = 0; start < selected.Length; start++)
        {
            if (!selected[start] || visited[start]) continue;
            visited[start] = true;
            queue.Enqueue(start);
            int minX = columns, minY = rows, maxX = 0, maxY = 0, pixels = 0;
            while (queue.Count > 0)
            {
                int k = queue.Dequeue();
                int cx = k % columns, cy = k / columns;
                minX = Math.Min(minX, cx); minY = Math.Min(minY, cy);
                maxX = Math.Max(maxX, cx); maxY = Math.Max(maxY, cy);
                pixels += active[k];
                foreach (int n in new[] { k - 1, k + 1, k - columns, k + columns })
                {
                    if (n < 0 || n >= selected.Length || visited[n] || !selected[n]) continue;
                    if (Math.Abs(n % columns - cx) + Math.Abs(n / columns - cy) != 1) continue;
                    visited[n] = true;
                    queue.Enqueue(n);
                }
            }
            int w = (maxX - minX + 1) * cell;
            int h = (maxY - minY + 1) * cell;
            int focusArea = Math.Max(1, (x1 - x0) * (y1 - y0));
            if (pixels < 80 || w < 16 || h < 16 || w * h > focusArea * 0.35) continue;
            var scaled = new Rectangle(
                (int)(minX * cell * source.Width / (double)sampleWidth),
                (int)(minY * cell * source.Height / (double)sampleHeight),
                Math.Max(1, (int)(w * source.Width / (double)sampleWidth)),
                Math.Max(1, (int)(h * source.Height / (double)sampleHeight)));
            found.Add(new Detection(scaled, pixels));
        }

        for (int i = 0; i < count; i++) background[i] = background[i] * 0.985f + gray[i] * 0.015f;
        previous = gray;
        return new MotionResult(warmup >= 6 ? found : [], globalChange, frozen, dark);
    }

    private static byte[] Grayscale(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int length = Math.Abs(data.Stride) * bitmap.Height;
            byte[] bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);
            byte[] gray = new byte[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                int i = y * data.Stride + x * 3;
                gray[y * bitmap.Width + x] = (byte)((bytes[i] * 29 + bytes[i + 1] * 150 + bytes[i + 2] * 77) >> 8);
            }
            return gray;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static bool IsDark(byte[] gray)
    {
        long sum = 0;
        for (int i = 0; i < gray.Length; i += 8) sum += gray[i];
        return sum / (double)((gray.Length + 7) / 8) < 12;
    }
}

internal sealed record MotionResult(IReadOnlyList<Detection> Detections, double MeanFrameChange, bool Frozen, bool Dark);
