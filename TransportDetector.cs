using System.Drawing.Drawing2D;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Observer;

internal sealed record VehicleBox(Rectangle Bounds, string Label, double Confidence);

// YOLOX-Nano detects broad COCO vehicle classes. Game-specific templates refine the label
// only when their match is sufficiently clear; uncertain vehicles retain a broad label.
internal sealed class TransportDetector : IDisposable
{
    private readonly InferenceSession session;
    private readonly TransportRecognizer recognizer = new();
    private const int Size = 416;
    private readonly (int X, int Y, int Stride)[] grid = BuildGrid();

    public TransportDetector()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "yolox_nano.onnx");
        if (!File.Exists(path)) throw new FileNotFoundException("Модель транспорта не найдена рядом с Observer.exe.", path);
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        session = new InferenceSession(path, options);
    }

    public IReadOnlyList<VehicleBox> Detect(Bitmap frame, Rectangle focus)
    {
        double scale = Math.Min(Size / (double)frame.Width, Size / (double)frame.Height);
        int width = Math.Max(1, (int)(frame.Width * scale));
        int height = Math.Max(1, (int)(frame.Height * scale));
        using var small = new Bitmap(Size, Size);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.Clear(Color.FromArgb(114, 114, 114));
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.DrawImage(frame, 0, 0, width, height);
        }
        var data = new float[3 * Size * Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            var pixel = small.GetPixel(x, y);
            int index = y * Size + x;
            data[index] = pixel.B;
            data[Size * Size + index] = pixel.G;
            data[2 * Size * Size + index] = pixel.R;
        }
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", new DenseTensor<float>(data, [1, 3, Size, Size])) };
        using var outputs = session.Run(inputs);
        var predictions = outputs.First().AsTensor<float>();
        var candidates = new List<VehicleBox>();
        for (int i = 0; i < grid.Length; i++)
        {
            float objectness = predictions[0, i, 4];
            if (objectness < 0.18f) continue;
            int category = -1;
            float score = 0.18f;
            foreach (int c in new[] { 2, 3, 5, 7, 8 })
            {
                float candidate = objectness * predictions[0, i, 5 + c];
                if (candidate > score) { score = candidate; category = c; }
            }
            if (category < 0) continue;
            var (gx, gy, stride) = grid[i];
            double cx = (predictions[0, i, 0] + gx) * stride / scale;
            double cy = (predictions[0, i, 1] + gy) * stride / scale;
            double w = Math.Exp(predictions[0, i, 2]) * stride / scale;
            double h = Math.Exp(predictions[0, i, 3]) * stride / scale;
            if (w < 24 || h < 24 || w > frame.Width * 0.9 || h > frame.Height * 0.9) continue;
            var box = Rectangle.Intersect(Rectangle.FromLTRB((int)(cx - w / 2), (int)(cy - h / 2),
                (int)(cx + w / 2), (int)(cy + h / 2)), new Rectangle(0, 0, frame.Width, frame.Height));
            if (box.IsEmpty || !focus.IntersectsWith(box)) continue;
            string label = category == 8 ? "Судно" : "Наземный транспорт";
            var specific = recognizer.Classify(frame, box);
            if (specific is not null && ((category == 8 && specific.Group is "galleon" or "frigate") ||
                                         (category != 8 && specific.Group == "tractor"))) label = specific.Label;
            candidates.Add(new VehicleBox(box, label, score));
        }
        var selected = new List<VehicleBox>();
        foreach (var candidate in candidates.OrderByDescending(x => x.Confidence))
        {
            if (selected.Any(x => IoU(x.Bounds, candidate.Bounds) > 0.45)) continue;
            selected.Add(candidate);
            if (selected.Count == 12) break;
        }
        return selected;
    }

    private static double IoU(Rectangle a, Rectangle b)
    {
        var both = Rectangle.Intersect(a, b);
        if (both.IsEmpty) return 0;
        double overlap = (double)both.Width * both.Height;
        return overlap / (a.Width * (double)a.Height + b.Width * (double)b.Height - overlap);
    }

    private static (int, int, int)[] BuildGrid()
    {
        var cells = new List<(int, int, int)>();
        foreach (int stride in new[] { 8, 16, 32 })
        for (int y = 0; y < Size / stride; y++)
        for (int x = 0; x < Size / stride; x++) cells.Add((x, y, stride));
        return cells.ToArray();
    }

    public void Dispose() => session.Dispose();
}
