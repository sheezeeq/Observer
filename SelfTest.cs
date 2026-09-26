using System.Drawing;
using System.Net;
using System.Net.Http;

namespace Observer;

internal static class SelfTest
{
    public static int Run()
    {
        try { return RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }

    private static async Task<int> RunAsync()
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
            var recognizer = new TransportRecognizer();
            if (recognizer.TemplateCount != 28) throw new Exception("The transport model did not load all templates");
            using (var featureFrame = MakeFrame(70))
            {
                var vector = TransportRecognizer.Features(featureFrame, new Rectangle(50, 70, 100, 100));
                if (vector.Length != 128 || vector.Any(x => !double.IsFinite(x))) throw new Exception("Invalid image features");
                byte[] photo = PhotoEncoding.Encode(featureFrame);
                if (PhotoEncoding.Mime(photo) != "image/jpeg") throw new Exception("Photo MIME detection failed");
                using var decoded = new Bitmap(new MemoryStream(photo));
                if (decoded.Width != 480 || decoded.Height != 270) throw new Exception("Photo was not decodable");
                using var transport = new TransportDetector();
                _ = transport.Detect(featureFrame, new Rectangle(0, 0, 480, 270));
            }
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
                var alert = PendingAlert.Create("test alert", [1, 2, 3], ChannelKind.Telegram);
                Outbox.Save(alert);
                var loaded = Outbox.Load().Single();
                if (loaded.Id != alert.Id || loaded.Text != alert.Text || loaded.Channel != ChannelKind.Telegram || !loaded.Image!.SequenceEqual(alert.Image!))
                    throw new Exception("Outbox did not persist the alert");
                Outbox.Delete(alert);
                if (Outbox.Load().Any()) throw new Exception("Outbox did not delete the delivered alert");
            }
            finally { Environment.SetEnvironmentVariable("OBSERVER_DATA_DIR", oldFolder); }
            await TestChannelsAsync();
            Console.WriteLine($"PASS: {detections} motion candidates; one alert; no stationary duplicate; vehicle model and 28 image templates loaded; 3 channel protocols and outbox verified; DPAPI verified={secretVerified}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static async Task TestChannelsAsync()
    {
        var discordCalls = new List<string>();
        using (var client = new HttpClient(new FakeHandler(async request =>
        {
            discordCalls.Add(request.RequestUri!.AbsolutePath);
            string content = await request.Content!.ReadAsStringAsync();
            if (!content.Contains("test discord")) throw new Exception("Discord text missing");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        })))
        using (var discord = new DiscordNotifier("https://discord.com/api/webhooks/test/token", client))
        {
            await discord.SendAsync(PendingAlert.Create("test discord", [1, 2, 3], ChannelKind.Discord), CancellationToken.None);
        }
        if (discordCalls.Count != 1) throw new Exception("Discord request count was not one");

        var telegramCalls = new List<string>();
        using (var client = new HttpClient(new FakeHandler(async request =>
        {
            telegramCalls.Add(request.RequestUri!.AbsolutePath);
            string content = await request.Content!.ReadAsStringAsync();
            if (!content.Contains("test telegram")) throw new Exception("Telegram text missing");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{}}") };
        })))
        using (var telegram = new TelegramNotifier("123456789:TEST_TOKEN", "-100123456", client))
        {
            await telegram.SendAsync(PendingAlert.Create("test telegram", null, ChannelKind.Telegram), CancellationToken.None);
            await telegram.SendAsync(PendingAlert.Create("test telegram", [1, 2, 3], ChannelKind.Telegram), CancellationToken.None);
        }
        if (telegramCalls.Count != 2 || !telegramCalls[0].EndsWith("sendMessage") || !telegramCalls[1].EndsWith("sendPhoto"))
            throw new Exception("Telegram methods were incorrect");

        var vkCalls = new List<string>();
        using (var client = new HttpClient(new FakeHandler(async request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            vkCalls.Add(path);
            string body = await request.Content!.ReadAsStringAsync();
            if (path.EndsWith("photos.getMessagesUploadServer"))
                return Json("{\"response\":{\"upload_url\":\"https://upload.vk.com/test\"}}");
            if (path == "/test") return Json("{\"photo\":\"[]\",\"server\":1,\"hash\":\"abc\"}");
            if (path.EndsWith("photos.saveMessagesPhoto")) return Json("{\"response\":[{\"owner_id\":-1,\"id\":99}]}");
            if (path.EndsWith("messages.send") && body.Contains("test+vk")) return Json("{\"response\":123}");
            throw new Exception("Unexpected VK request: " + path);
        })))
        using (var vk = new VkNotifier("test-very-long-token", "2000000001", client))
        {
            await vk.SendAsync(PendingAlert.Create("test vk", null, ChannelKind.Vk), CancellationToken.None);
            await vk.SendAsync(PendingAlert.Create("test vk", [1, 2, 3], ChannelKind.Vk), CancellationToken.None);
        }
        if (vkCalls.Count != 5) throw new Exception("VK text/photo flow did not use five requests");
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
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
