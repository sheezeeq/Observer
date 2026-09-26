using System.Drawing;

namespace Observer;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            var detector = new MotionEngine();
            var tracker = new Tracker();
            DateTime now = DateTime.UtcNow;
            bool alerted = false;
            int detections = 0;
            for (int i = 0; i < 45; i++)
            {
                using var frame = MakeFrame(i is >= 8 and <= 23 ? 70 + Math.Min(i - 8, 5) * 12 : -1);
                var result = detector.Analyze(frame, new Rectangle(0, 0, 480, 270), 50);
                detections += result.Detections.Count;
                var ready = tracker.Update(result.Detections, frame, now.AddSeconds(i));
                if (ready.Count > 0)
                {
                    if (alerted) throw new Exception("Duplicate alert for stationary object");
                    tracker.MarkAlerted(ready, frame);
                    alerted = true;
                }
            }
            if (!alerted || detections < 2) throw new Exception($"Moving object was missed: detections={detections}, alerted={alerted}");
            bool secretVerified = false;
            try
            {
                string secret = "https://discord.com/api/webhooks/test/test";
                secretVerified = SecretStore.Unprotect(SecretStore.Protect(secret)) == secret;
                if (!secretVerified) throw new Exception("Windows secret storage returned a different value");
            }
            catch (InvalidOperationException) { /* A restricted test profile may deny DPAPI. */ }
            string? oldFolder = Environment.GetEnvironmentVariable("OBSERVER_DATA_DIR");
            string testFolder = Path.Combine(Path.GetTempPath(), "ObserverTest-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("OBSERVER_DATA_DIR", testFolder);
            try
            {
                var alert = PendingAlert.Create("test alert", [1, 2, 3]);
                Outbox.Save(alert);
                var loaded = Outbox.Load().Single();
                if (loaded.Id != alert.Id || loaded.Text != alert.Text || !loaded.Image!.SequenceEqual(alert.Image!))
                    throw new Exception("Outbox did not persist the alert");
                Outbox.Delete(alert);
                if (Outbox.Load().Any()) throw new Exception("Outbox did not delete the delivered alert");
            }
            finally { Environment.SetEnvironmentVariable("OBSERVER_DATA_DIR", oldFolder); }
            Console.WriteLine($"PASS: {detections} candidate detections; one alert; no stationary duplicate; outbox persisted; DPAPI verified={secretVerified}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static Bitmap MakeFrame(int x)
    {
        var image = new Bitmap(480, 270);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.FromArgb(80, 95, 100));
        if (x >= 0)
        {
            using var brush = new SolidBrush(Color.FromArgb(220, 210, 180));
            g.FillRectangle(brush, x, 90, 60, 42);
        }
        return image;
    }
}
