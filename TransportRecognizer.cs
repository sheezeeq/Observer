using System.Drawing.Drawing2D;
using System.Text.Json;

namespace Observer;

internal sealed record Recognition(string Label, string Group, double Similarity);

internal sealed class TransportRecognizer
{
    private sealed class Model
    {
        public List<Template> Templates { get; set; } = [];
    }
    private sealed class Template
    {
        public string Class { get; set; } = "";
        public string Group { get; set; } = "";
        public double[] Features { get; set; } = [];
    }
    private readonly List<Template> templates;
    public int TemplateCount => templates.Count;

    public TransportRecognizer()
    {
        using var stream = typeof(TransportRecognizer).Assembly.GetManifestResourceStream("Observer.transport-model.json")
            ?? throw new InvalidOperationException("Модель транспорта не включена в сборку.");
        var model = JsonSerializer.Deserialize<Model>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        templates = model?.Templates ?? throw new InvalidOperationException("Модель транспорта повреждена.");
        if (templates.Count < 20 || templates.Any(t => t.Features.Length != 128))
            throw new InvalidOperationException("Модель транспорта имеет неверный формат.");
    }

    public Recognition? Classify(Bitmap frame, Rectangle detection)
    {
        if (detection.Width < 24 || detection.Height < 24) return null;
        var frameBounds = new Rectangle(0, 0, frame.Width, frame.Height);
        var bestByGroup = new Dictionary<string, (double Score, Template Template)>();
        foreach (double expansion in new[] { 0.0, 0.15, 0.35, 0.6 })
        {
            int dx = (int)(detection.Width * expansion);
            int dy = (int)(detection.Height * expansion);
            var box = Rectangle.Intersect(Rectangle.Inflate(detection, dx, dy), frameBounds);
            if (box.Width < 24 || box.Height < 24) continue;
            double[] vector = Features(frame, box);
            foreach (var template in templates)
            {
                double score = Dot(vector, template.Features);
                if (!bestByGroup.TryGetValue(template.Group, out var current) || score > current.Score)
                    bestByGroup[template.Group] = (score, template);
            }
        }
        if (bestByGroup.Count < 2) return null;
        var ordered = bestByGroup.Values.OrderByDescending(x => x.Score).ToArray();
        var best = ordered[0].Template;
        double bestScore = ordered[0].Score;
        double rival = ordered[1].Score;
        if (best.Group == "other" || bestScore < 0.86 || bestScore - rival < 0.045) return null;
        string label = best.Group switch { "tractor" => "Трактор", "galleon" => "Галеон", "frigate" => "Фрегат", _ => best.Class };
        return new Recognition(label, best.Group, bestScore);
    }

    public static double[] Features(Bitmap image, Rectangle area)
    {
        using var small = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.DrawImage(image, new Rectangle(0, 0, 32, 32), area, GraphicsUnit.Pixel);
        }
        double[,] gray = new double[32, 32];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            Color color = small.GetPixel(x, y);
            gray[y, x] = (color.R * 77 + color.G * 150 + color.B * 29) / 256.0;
        }
        double[] vector = new double[128];
        for (int y = 1; y < 31; y++)
        for (int x = 1; x < 31; x++)
        {
            double dx = gray[y, x + 1] - gray[y, x - 1];
            double dy = gray[y + 1, x] - gray[y - 1, x];
            double magnitude = Math.Sqrt(dx * dx + dy * dy);
            double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (angle < 0) angle += 180;
            int bin = Math.Min(7, (int)(angle / 22.5));
            vector[((y / 8) * 4 + x / 8) * 8 + bin] += magnitude;
        }
        for (int cell = 0; cell < 16; cell++)
        {
            double norm = 0;
            for (int bin = 0; bin < 8; bin++) norm += vector[cell * 8 + bin] * vector[cell * 8 + bin];
            norm = Math.Max(Math.Sqrt(norm), 0.00001);
            for (int bin = 0; bin < 8; bin++) vector[cell * 8 + bin] /= norm;
        }
        double total = Math.Max(Math.Sqrt(vector.Sum(x => x * x)), 0.00001);
        for (int i = 0; i < vector.Length; i++) vector[i] /= total;
        return vector;
    }

    private static double Dot(double[] a, double[] b)
    {
        double result = 0;
        for (int i = 0; i < a.Length; i++) result += a[i] * b[i];
        return result;
    }
}
