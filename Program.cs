using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Observer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }
        if (args.Length == 6 && args[0] == "--probe")
        {
            using var image = new Bitmap(args[1]);
            var bounds = new Rectangle(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
            var result = new TransportRecognizer().Classify(image, bounds);
            Console.WriteLine(result is null ? "unknown" : $"{result.Label}: {result.Similarity:F3}");
            return;
        }
        if (args.Length == 2 && args[0] == "--probe-detect")
        {
            using var image = new Bitmap(args[1]);
            using var detector = new TransportDetector();
            foreach (var vehicle in detector.Detect(image, new Rectangle(0, 0, image.Width, image.Height)))
                Console.WriteLine($"{vehicle.Label}: {vehicle.Confidence:F3} {vehicle.Bounds}");
            return;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--render-ui")
        {
            using var form = new MainForm();
            form.Show();
            Application.DoEvents();
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
            image.Save(args[1], ImageFormat.Png);
            form.Close();
            return;
        }
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly Settings settings = Settings.Load();
    private readonly ComboBox windows = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox webhook = new() { UseSystemPasswordChar = true };
    private readonly TextBox telegramToken = new() { UseSystemPasswordChar = true };
    private readonly TextBox telegramChat = new();
    private readonly TextBox vkToken = new() { UseSystemPasswordChar = true };
    private readonly TextBox vkPeer = new();
    private readonly TextBox pointName = new();
    private readonly ComboBox captureMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox unknownMotion = new() { Text = "Сообщать также о неопознанном движении", AutoSize = true };
    private readonly Button refresh = new() { Text = "Обновить окна" };
    private readonly Button previewButton = new() { Text = "Обновить снимок" };
    private readonly Button test = new() { Text = "Тестовое сообщение" };
    private readonly Button save = new() { Text = "Сохранить настройки" };
    private readonly Label channelSummary = new() { AutoSize = false };
    private readonly Button start = new() { Text = "Начать наблюдение" };
    private readonly Button stop = new() { Text = "Остановить", Enabled = false };
    private readonly PictureBox preview = new() { BackColor = Color.FromArgb(30, 35, 42), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly TrackBar sensitivity = new() { Minimum = 0, Maximum = 100, TickFrequency = 25, Value = 50 };
    private readonly Label status = new() { AutoSize = false, Text = "Готов к настройке" };
    private readonly Label regionText = new() { AutoSize = false };
    private readonly Label log = new() { AutoSize = false };
    private readonly MotionEngine motion = new();
    private readonly Tracker tracker = new();
    private readonly TransportRecognizer recognizer = new();
    private TransportDetector? transportDetector;
    private readonly Queue<PendingAlert> pending = new();
    private CancellationTokenSource? running;
    private ChannelHub? notifier;
    private Bitmap? latest;
    private Point? dragStart;
    private Rectangle? dragBox;
    private DateTime? faultSince;
    private string? faultReason;
    private bool faultSent;
    private DateTime? frozenSince;
    private readonly Dictionary<ChannelKind, DateTime> nextSend = [];
    private DateTime lastMovement = DateTime.MinValue;

    public MainForm()
    {
        Text = "Observer — наблюдение за ArcheAge";
        ClientSize = new Size(1200, 860);
        MinimumSize = new Size(1120, 830);
        Font = new Font("Segoe UI", 9);
        BackColor = Color.FromArgb(245, 247, 249);
        FormClosing += (_, _) => running?.Cancel();

        AddLabel("Окно ArcheAge", 20, 12, 180);
        windows.SetBounds(20, 36, 490, 28);
        refresh.SetBounds(520, 35, 140, 30);
        AddLabel("Захват экрана", 680, 12, 170);
        captureMode.SetBounds(680, 36, 180, 28);
        captureMode.Items.AddRange(["Автоматически", "Окно игры", "Весь дисплей"]);
        AddLabel("Название точки", 880, 12, 220);
        pointName.SetBounds(880, 36, 295, 27);

        AddLabel("Discord webhook", 20, 76, 180);
        webhook.SetBounds(20, 99, 700, 27);
        AddLabel("Telegram: токен бота", 20, 132, 240);
        telegramToken.SetBounds(20, 155, 430, 27);
        AddLabel("ID чата или @канал", 465, 132, 240);
        telegramChat.SetBounds(465, 155, 255, 27);
        AddLabel("ВК: ключ сообщества", 20, 188, 240);
        vkToken.SetBounds(20, 211, 430, 27);
        AddLabel("peer_id диалога", 465, 188, 240);
        vkPeer.SetBounds(465, 211, 255, 27);
        test.SetBounds(760, 210, 200, 30);
        save.SetBounds(970, 210, 205, 30);
        channelSummary.SetBounds(760, 98, 410, 82);

        preview.SetBounds(20, 270, 760, 510);
        preview.BorderStyle = BorderStyle.FixedSingle;
        preview.MouseDown += PreviewMouseDown;
        preview.MouseMove += PreviewMouseMove;
        preview.MouseUp += PreviewMouseUp;
        preview.Paint += PreviewPaint;
        previewButton.SetBounds(20, 790, 170, 30);

        AddLabel("Область наблюдения", 805, 275, 320);
        regionText.SetBounds(805, 302, 330, 50);
        AddLabel("Выделите область мышью на снимке.\nИсключите чат и интерфейс.", 805, 360, 330, 50);
        AddLabel("Чувствительность движения", 805, 425, 330);
        sensitivity.SetBounds(805, 450, 330, 45);
        unknownMotion.SetBounds(805, 510, 365, 28);
        AddLabel("Название транспорта показывается поверх\nснимка. Распознавание пока экспериментальное.", 805, 552, 365, 55);
        start.SetBounds(805, 660, 330, 40);
        stop.SetBounds(805, 710, 330, 40);
        status.SetBounds(205, 792, 950, 28);
        log.SetBounds(20, 830, 1150, 20);
        Controls.AddRange([windows, refresh, captureMode, pointName, webhook, telegramToken, telegramChat,
            vkToken, vkPeer, test, save, channelSummary, preview, previewButton, regionText, sensitivity, unknownMotion,
            start, stop, status, log]);

        webhook.Text = SecretStore.Unprotect(settings.ProtectedWebhook);
        telegramToken.Text = SecretStore.Unprotect(settings.ProtectedTelegramToken);
        telegramChat.Text = settings.TelegramChatId;
        vkToken.Text = SecretStore.Unprotect(settings.ProtectedVkToken);
        vkPeer.Text = settings.VkPeerId;
        pointName.Text = settings.PointName;
        captureMode.SelectedIndex = Math.Clamp((int)settings.CaptureMode, 0, 2);
        unknownMotion.Checked = settings.AlertUnknownMotion;
        foreach (var field in new[] { webhook, telegramToken, telegramChat, vkToken, vkPeer })
            field.TextChanged += (_, _) => UpdateChannelSummary();
        UpdateChannelSummary();
        sensitivity.Value = Math.Clamp(settings.Sensitivity, 0, 100);
        UpdateRegionText();
        refresh.Click += (_, _) => RefreshWindows();
        previewButton.Click += (_, _) => CapturePreview();
        test.Click += async (_, _) => await TestChannels();
        save.Click += (_, _) =>
        {
            try { SaveSettings(); MessageBox.Show("Настройки сохранены на этом компьютере."); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось сохранить настройки"); }
        };
        start.Click += (_, _) => Start();
        stop.Click += (_, _) => running?.Cancel();
        Shown += (_, _) => RefreshWindows();
    }

    private void AddLabel(string value, int x, int y, int width, int? height = null)
    {
        var label = new Label { Text = value, AutoSize = false, Width = width, Height = height ?? (value.Contains('\n') ? 66 : 22), Left = x, Top = y };
        Controls.Add(label);
    }

    private void RefreshWindows()
    {
        windows.Items.Clear();
        foreach (var item in Platform.Windows()) windows.Items.Add(item);
        if (windows.Items.Count > 0) windows.SelectedIndex = 0;
        status.Text = windows.Items.Count == 0 ? "Окно ArcheAge не найдено" : "Окно найдено. Обновите снимок и выберите область.";
    }

    private GameWindow? SelectedWindow => windows.SelectedItem as GameWindow;

    private void CapturePreview()
    {
        if (SelectedWindow is null) { MessageBox.Show("Сначала выберите окно ArcheAge."); return; }
        if (!Platform.TryCapture(SelectedWindow, (CaptureMode)captureMode.SelectedIndex, out var frame, out var error))
        {
            MessageBox.Show(error, "Снимок не получен");
            return;
        }
        latest?.Dispose();
        latest = frame;
        preview.Image = latest;
        preview.Invalidate();
    }

    private Rectangle ImageArea()
    {
        if (latest is null) return Rectangle.Empty;
        double scale = Math.Min(preview.Width / (double)latest.Width, preview.Height / (double)latest.Height);
        int w = (int)(latest.Width * scale), h = (int)(latest.Height * scale);
        return new Rectangle((preview.Width - w) / 2, (preview.Height - h) / 2, w, h);
    }

    private void PreviewMouseDown(object? sender, MouseEventArgs e)
    {
        if (latest is null || e.Button != MouseButtons.Left) return;
        dragStart = e.Location;
        dragBox = null;
    }

    private void PreviewMouseMove(object? sender, MouseEventArgs e)
    {
        if (dragStart is null) return;
        int left = Math.Min(dragStart.Value.X, e.X), top = Math.Min(dragStart.Value.Y, e.Y);
        dragBox = new Rectangle(left, top, Math.Abs(e.X - dragStart.Value.X), Math.Abs(e.Y - dragStart.Value.Y));
        preview.Invalidate();
    }

    private void PreviewMouseUp(object? sender, MouseEventArgs e)
    {
        if (dragStart is null || latest is null) return;
        dragStart = null;
        var box = Rectangle.Intersect(dragBox ?? Rectangle.Empty, ImageArea());
        dragBox = null;
        if (box.Width < 20 || box.Height < 20) { preview.Invalidate(); return; }
        var image = ImageArea();
        settings.X = Math.Clamp((int)Math.Round((box.Left - image.Left) * 100.0 / image.Width), 0, 99);
        settings.Y = Math.Clamp((int)Math.Round((box.Top - image.Top) * 100.0 / image.Height), 0, 99);
        settings.Width = Math.Clamp((int)Math.Round(box.Width * 100.0 / image.Width), 1, 100 - settings.X);
        settings.Height = Math.Clamp((int)Math.Round(box.Height * 100.0 / image.Height), 1, 100 - settings.Y);
        UpdateRegionText();
        preview.Invalidate();
    }

    private void PreviewPaint(object? sender, PaintEventArgs e)
    {
        var image = ImageArea();
        if (image.IsEmpty) return;
        var region = new Rectangle(image.Left + image.Width * settings.X / 100,
            image.Top + image.Height * settings.Y / 100,
            image.Width * settings.Width / 100, image.Height * settings.Height / 100);
        using var pen = new Pen(Color.Lime, 2);
        e.Graphics.DrawRectangle(pen, dragBox ?? region);
    }

    private void UpdateRegionText() => regionText.Text = $"X {settings.X}%, Y {settings.Y}%\nШирина {settings.Width}%, высота {settings.Height}%";

    private async Task TestChannels()
    {
        test.Enabled = false;
        try
        {
            using var connection = CreateChannels();
            if (connection.Connected.Count == 0) throw new InvalidOperationException("Настройте хотя бы один канал: Discord, Telegram или ВК.");
            SaveSettings();
            var results = new List<string>();
            byte[]? screenshot = latest is null ? null : PhotoEncoding.Encode(latest);
            foreach (var channel in connection.Connected)
            {
                try
                {
                    await connection.SendAsync(PendingAlert.Create(
                        $"Observer: тестовое сообщение с точки «{PointName()}». {DateTime.Now:dd.MM.yyyy HH:mm:ss}", screenshot, channel), CancellationToken.None);
                    results.Add($"{ChannelName(channel)}: отправлено");
                }
                catch (Exception ex) { results.Add($"{ChannelName(channel)}: ошибка — {ex.Message}"); }
            }
            MessageBox.Show(string.Join(Environment.NewLine, results), "Проверка каналов");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Проверка не выполнена"); }
        finally { test.Enabled = true; }
    }

    private ChannelHub CreateChannels() => new(webhook.Text, telegramToken.Text, telegramChat.Text, vkToken.Text, vkPeer.Text);
    private void UpdateChannelSummary()
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(webhook.Text)) names.Add("Discord");
        if (!string.IsNullOrWhiteSpace(telegramToken.Text) && !string.IsNullOrWhiteSpace(telegramChat.Text)) names.Add("Telegram");
        if (!string.IsNullOrWhiteSpace(vkToken.Text) && !string.IsNullOrWhiteSpace(vkPeer.Text)) names.Add("ВК");
        channelSummary.Text = names.Count == 0
            ? "Заполните данные хотя бы одного канала.\nДля проверки нажмите «Тестовое сообщение»."
            : "Заполнены: " + string.Join(", ", names) + ".\nТест отправит текст и открытый снимок, если он есть.";
    }
    private string PointName() => string.IsNullOrWhiteSpace(pointName.Text) ? "Фактория" : pointName.Text.Trim()[..Math.Min(40, pointName.Text.Trim().Length)];
    private static string ChannelName(ChannelKind kind) => kind switch { ChannelKind.Discord => "Discord", ChannelKind.Telegram => "Telegram", _ => "ВК" };

    private void SaveSettings()
    {
        settings.Sensitivity = sensitivity.Value;
        settings.CaptureMode = (CaptureMode)captureMode.SelectedIndex;
        settings.AlertUnknownMotion = unknownMotion.Checked;
        settings.PointName = PointName();
        settings.ProtectedWebhook = SecretStore.Protect(webhook.Text.Trim());
        settings.ProtectedTelegramToken = SecretStore.Protect(telegramToken.Text.Trim());
        settings.TelegramChatId = telegramChat.Text.Trim();
        settings.ProtectedVkToken = SecretStore.Protect(vkToken.Text.Trim());
        settings.VkPeerId = vkPeer.Text.Trim();
        settings.Save();
    }

    private void Start()
    {
        if (SelectedWindow is null) { MessageBox.Show("Окно ArcheAge не найдено."); return; }
        try
        {
            transportDetector = new TransportDetector();
            notifier = CreateChannels();
            if (notifier.Connected.Count == 0) throw new InvalidOperationException("Настройте хотя бы один канал оповещений.");
            SaveSettings();
        }
        catch (Exception ex)
        {
            transportDetector?.Dispose(); transportDetector = null;
            notifier?.Dispose(); notifier = null;
            MessageBox.Show(ex.Message, "Не удалось запустить Observer");
            return;
        }
        running = new CancellationTokenSource();
        motion.Reset(); tracker.Reset(); pending.Clear();
        try { foreach (var item in Outbox.Load().Where(x => notifier.Connected.Contains(x.Channel))) pending.Enqueue(item); }
        catch (Exception ex) { WriteLog("Очередь сообщений не прочитана: " + ex.Message); }
        faultSince = null; faultReason = null; faultSent = false; frozenSince = null;
        nextSend.Clear(); lastMovement = DateTime.MinValue;
        windows.Enabled = false; refresh.Enabled = false; start.Enabled = false; stop.Enabled = true;
        previewButton.Enabled = false; sensitivity.Enabled = false; captureMode.Enabled = false;
        status.Text = "Наблюдение запущено";
        _ = MonitorLoop(SelectedWindow, settings.CaptureMode, running.Token);
        Platform.Activate(SelectedWindow);
        WindowState = FormWindowState.Minimized;
    }

    private async Task MonitorLoop(GameWindow game, CaptureMode mode, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                DateTime now = DateTime.UtcNow;
                using Bitmap? frame = Platform.TryCapture(game, mode, out var capture, out var captureError) ? capture : null;
                string? fault = captureError;
                MotionResult? analysis = null;
                IReadOnlyList<Detection> detections = [];
                if (frame is not null)
                {
                    if (Platform.IsViewBlocked(game))
                    {
                        fault = "окно ArcheAge перекрыто другим окном";
                        motion.Reset();
                        frozenSince = null;
                    }
                    else
                    {
                        var area = FocusRect(frame);
                        analysis = motion.Analyze(frame, area, sensitivity.Value);
                        var vehicles = analysis.Detections.Count > 0 && transportDetector is not null
                            ? transportDetector.Detect(frame, area) : [];
                        detections = analysis.Detections.Select(item =>
                        {
                            var matching = vehicles.Select(box => (Box: box, Overlap: Rectangle.Intersect(box.Bounds, item.Bounds)))
                                .Where(x => x.Overlap.Width * (double)x.Overlap.Height >= item.Bounds.Width * (double)item.Bounds.Height * 0.08)
                                .OrderByDescending(x => x.Overlap.Width * (double)x.Overlap.Height).FirstOrDefault();
                            if (matching.Box is not null)
                                return new Detection(matching.Box.Bounds, item.Pixels, matching.Box.Label, matching.Box.Confidence);
                            Recognition? identified = recognizer.Classify(frame, item.Bounds);
                            return item with { Label = identified?.Label, Similarity = identified?.Similarity ?? 0 };
                        }).GroupBy(x => x.Bounds).Select(x => x.First()).ToArray();
                        if (analysis.Dark) fault = "изображение почти полностью чёрное";
                        frozenSince = analysis.Frozen ? frozenSince ?? now : null;
                        if (frozenSince is not null && now - frozenSince > TimeSpan.FromSeconds(90)) fault = "изображение не меняется более 90 секунд";
                    }
                }
                CheckHealth(fault, frame, now);
                if (fault is null && frame is not null && analysis is not null)
                {
                    ShowLivePreview(frame, detections);
                    var considered = unknownMotion.Checked ? detections : detections.Where(x => x.Label is not null).ToArray();
                    var ready = tracker.Update(considered, frame, now);
                    if (ready.Count > 0 && now - lastMovement > TimeSpan.FromSeconds(4))
                    {
                        tracker.MarkAttempt(ready, now);
                        byte[] photo = Annotate(frame, ready.Select(x => (x.Bounds, x.Label)));
                        try { SaveEvent(photo, "movement"); }
                        catch (Exception ex) { WriteLog("Снимок не сохранён на диск: " + ex.Message); }
                        var names = ready.Select(x => x.Label ?? "неопознанное движение").Distinct();
                        if (QueueAlert($"Observer · {PointName()}: {string.Join(", ", names)}. Время: {DateTime.Now:dd.MM.yyyy HH:mm:ss}", photo))
                        {
                            tracker.MarkAlerted(ready, frame);
                            lastMovement = now;
                            WriteLog($"Найдено: {string.Join(", ", names)}. Снимок сохранён.");
                        }
                    }
                }
                if (pending.Count > 0 && notifier is not null)
                {
                    int count = pending.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var item = pending.Dequeue();
                        if (nextSend.GetValueOrDefault(item.Channel) > now) { pending.Enqueue(item); continue; }
                        try
                        {
                            await notifier.SendAsync(item, cancellation);
                            try { Outbox.Delete(item); } catch (Exception ex) { WriteLog("Не удалено доставленное сообщение из очереди: " + ex.Message); }
                            WriteLog($"Отправлено в {ChannelName(item.Channel)}.");
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { pending.Enqueue(item); break; }
                        catch (Exception ex)
                        {
                            pending.Enqueue(item);
                            nextSend[item.Channel] = now.AddSeconds(30);
                            WriteLog($"Ошибка {ChannelName(item.Channel)}, повтор через 30 секунд: {ex.Message}");
                        }
                    }
                }
                status.Text = fault is null ? $"Наблюдение работает · очередь: {pending.Count}" : $"Обзор: {fault}";
                await Task.Delay(1000, cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WriteLog("Наблюдение остановлено из-за ошибки: " + ex.Message); }
        finally
        {
            transportDetector?.Dispose(); transportDetector = null;
            notifier?.Dispose(); notifier = null;
            running?.Dispose(); running = null;
            if (!IsDisposed)
            {
                windows.Enabled = true; refresh.Enabled = true; start.Enabled = true; stop.Enabled = false;
                previewButton.Enabled = true; sensitivity.Enabled = true; captureMode.Enabled = true;
                status.Text = "Наблюдение остановлено";
            }
        }
    }

    private void CheckHealth(string? reason, Bitmap? frame, DateTime now)
    {
        if (reason is null)
        {
            if (faultSent) QueueAlert($"Observer · {PointName()}: обзор восстановлен. Время: {DateTime.Now:dd.MM.yyyy HH:mm:ss}", null);
            faultSince = null; faultReason = null; faultSent = false;
            return;
        }
        if (reason != faultReason) { faultReason = reason; faultSince = now; faultSent = false; }
        if (!faultSent && now - faultSince > TimeSpan.FromSeconds(15))
        {
            byte[]? photo = frame is null ? null : PhotoEncoding.Encode(frame);
            faultSent = QueueAlert($"Observer · {PointName()}: обзор нарушен — {reason}. Время: {DateTime.Now:dd.MM.yyyy HH:mm:ss}", photo);
            WriteLog("Предупреждение о нарушенном обзоре добавлено в очередь.");
        }
    }

    private Rectangle FocusRect(Bitmap frame) => new(
        frame.Width * settings.X / 100, frame.Height * settings.Y / 100,
        Math.Max(1, frame.Width * settings.Width / 100), Math.Max(1, frame.Height * settings.Height / 100));

    private bool QueueAlert(string text, byte[]? image)
    {
        if (notifier is null || notifier.Connected.Count == 0) return false;
        if (pending.Count + notifier.Connected.Count > 150) { WriteLog("Очередь заполнена: требуется проверить соединение."); return false; }
        foreach (var channel in notifier.Connected)
        {
            var item = PendingAlert.Create(text, image, channel);
            pending.Enqueue(item);
            try { Outbox.Save(item); }
            catch (Exception ex) { WriteLog("Очередь не сохранена на диск: " + ex.Message); }
        }
        return true;
    }

    private void ShowLivePreview(Bitmap frame, IReadOnlyList<Detection> detections)
    {
        using var copy = DrawOverlay(frame, detections.Select(x => (x.Bounds, x.Label)));
        preview.Image = null;
        latest?.Dispose();
        latest = new Bitmap(copy);
        preview.Image = latest;
        preview.Invalidate();
    }

    private static Bitmap DrawOverlay(Bitmap frame, IEnumerable<(Rectangle Bounds, string? Label)> areas)
    {
        var copy = new Bitmap(frame);
        using (var graphics = Graphics.FromImage(copy))
        using (var border = new Pen(Color.Red, Math.Max(3, frame.Width / 350f)))
        using (var font = new Font("Segoe UI", Math.Max(12, frame.Width / 105f), FontStyle.Bold))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var (area, label) in areas)
            {
                var box = Rectangle.Inflate(area, 12, 12);
                graphics.DrawEllipse(border, box);
                if (label is not null)
                {
                    var size = graphics.MeasureString(label, font);
                    var caption = new RectangleF(box.Left, Math.Max(0, box.Top - size.Height), size.Width + 8, size.Height);
                    graphics.FillRectangle(Brushes.DarkRed, caption);
                    graphics.DrawString(label, font, Brushes.White, caption.Left + 4, caption.Top);
                }
            }
        }
        return copy;
    }

    private static byte[] Annotate(Bitmap frame, IEnumerable<(Rectangle Bounds, string? Label)> areas)
    {
        using var copy = DrawOverlay(frame, areas);
        return PhotoEncoding.Encode(copy);
    }

    private static void SaveEvent(byte[] photo, string kind)
    {
        Directory.CreateDirectory(Settings.EventsFolder);
        string name = kind + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".jpg";
        File.WriteAllBytes(Path.Combine(Settings.EventsFolder, name), photo);
        var old = Directory.GetFiles(Settings.EventsFolder)
            .Where(path => Path.GetExtension(path) is ".jpg" or ".png")
            .Select(path => new FileInfo(path)).OrderByDescending(x => x.CreationTimeUtc).Skip(100);
        foreach (var file in old) file.Delete();
    }

    private void WriteLog(string message) { if (!IsDisposed) log.Text = DateTime.Now.ToString("HH:mm:ss") + " — " + message; }
}
