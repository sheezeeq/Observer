using System.Drawing.Imaging;

namespace Observer;

internal sealed class TrackedObject
{
    public Rectangle Bounds;
    public int Seen;
    public DateTime LastSeen;
    public DateTime LastTry;
    public bool Alerted;
    public double[]? Signature;
    public int ChangedFrames;
    public string? Label;
    public double Similarity;
}

internal sealed class Tracker
{
    private readonly List<TrackedObject> tracks = [];
    public void Reset() => tracks.Clear();

    public IReadOnlyList<TrackedObject> Update(IReadOnlyList<Detection> detections, Bitmap frame, DateTime now)
    {
        foreach (var track in tracks.Where(t => t.Alerted && t.Signature is not null))
        {
            double delta = SignatureDistance(track.Signature!, Sample(frame, track.Bounds));
            track.ChangedFrames = delta > 26 ? track.ChangedFrames + 1 : 0;
        }
        tracks.RemoveAll(t => t.Alerted ? t.ChangedFrames >= 15 : now - t.LastSeen > TimeSpan.FromSeconds(8));

        foreach (var item in detections)
        {
            var match = tracks
                .Where(t => Near(t.Bounds, item.Bounds))
                .OrderBy(t => Distance(t.Bounds, item.Bounds))
                .FirstOrDefault();
            if (match is null)
            {
                tracks.Add(new TrackedObject { Bounds = item.Bounds, Seen = 1, LastSeen = now, Label = item.Label, Similarity = item.Similarity });
            }
            else
            {
                if (match.Alerted)
                {
                    match.Bounds = item.Bounds;
                    match.Signature = Sample(frame, item.Bounds);
                    match.ChangedFrames = 0;
                }
                else match.Bounds = UnionCompact(match.Bounds, item.Bounds);
                match.Seen++;
                match.LastSeen = now;
                if (item.Label is not null) { match.Label = item.Label; match.Similarity = item.Similarity; }
            }
        }
        return tracks.Where(t => !t.Alerted && t.Seen >= 2 && now - t.LastSeen < TimeSpan.FromSeconds(3)
            && now - t.LastTry > TimeSpan.FromSeconds(15)).ToList();
    }

    public void MarkAttempt(IReadOnlyList<TrackedObject> objects, DateTime now)
    {
        foreach (var item in objects) item.LastTry = now;
    }

    public void MarkAlerted(IReadOnlyList<TrackedObject> objects, Bitmap frame)
    {
        foreach (var item in objects)
        {
            item.Alerted = true;
            item.Signature = Sample(frame, item.Bounds);
            item.ChangedFrames = 0;
        }
    }

    private static bool Near(Rectangle a, Rectangle b)
    {
        double distance = Distance(a, b);
        return distance < Math.Max(42, Math.Max(a.Width, a.Height) * 0.8);
    }

    private static double Distance(Rectangle a, Rectangle b)
    {
        double dx = a.Left + a.Width / 2.0 - b.Left - b.Width / 2.0;
        double dy = a.Top + a.Height / 2.0 - b.Top - b.Height / 2.0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Rectangle UnionCompact(Rectangle a, Rectangle b)
    {
        var union = Rectangle.Union(a, b);
        return union.Width < Math.Max(a.Width, b.Width) * 1.8 && union.Height < Math.Max(a.Height, b.Height) * 1.8 ? union : b;
    }

    private static double[] Sample(Bitmap image, Rectangle region)
    {
        Rectangle area = Rectangle.Intersect(region, new Rectangle(0, 0, image.Width, image.Height));
        if (area.Width <= 0 || area.Height <= 0) return new double[64];
        using var thumb = new Bitmap(8, 8, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(thumb)) g.DrawImage(image, new Rectangle(0, 0, 8, 8), area, GraphicsUnit.Pixel);
        var result = new double[64];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            var c = thumb.GetPixel(x, y);
            result[y * 8 + x] = (c.R * 77 + c.G * 150 + c.B * 29) / 256.0;
        }
        return result;
    }

    private static double SignatureDistance(double[] a, double[] b)
    {
        double total = 0;
        for (int i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
        return total / a.Length;
    }
}
