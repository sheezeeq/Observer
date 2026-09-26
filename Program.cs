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
    private readonly Button refresh = new() { Text = "Обновить окна" };
    private readonly Button previewButton = new() { Text = "Обновить снимок" };
    private readonly Button test = new() { Text = "Проверить Discord" };
    private readonly Button start = new() { Text = "Начать наблюдение" };
    private readonly Button stop = new() { Text = "Остановить", Enabled = false };
    private readonly PictureBox preview = new() { BackColor = Color.FromArgb(30, 35, 42), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly TrackBar sensitivity = new() { Minimum = 0, Maximum = 100, TickFrequency = 25, Value = 50 };
    private readonly Label status = new() { AutoSize = false, Text = "Готов к настройке" };
    private readonly Label regionText = new() { AutoSize = false };
    private readonly Label log = new() { AutoSize = false };
    private readonly MotionEngine motion = new();
    private readonly Tracker tracker = new();
    private readonly Queue<PendingAlert> pending = new();
    private CancellationTokenSource? running;
    private DiscordNotifier? notifier;
    private Bitmap? latest;
    private Point? dragStart;
    private Rectangle? dragBox;
    private DateTime? faultSince;
    private string? faultReason;
    private bool faultSent;
    private DateTime? frozenSince;
    private DateTime nextSend = DateTime.MinValue;
    private DateTime lastMovement = DateTime.MinValue;

    public MainForm()
    {
        Text = "Observer — наблюдение за ArcheAge";
        ClientSize = new Size(950, 685);
        MinimumSize = new Size(900, 675);
        Font = new Font("Segoe UI", 9);
        BackColor = Color.FromArgb(245, 247, 249);
        FormClosing += (_, _) => running?.Cancel();

        AddLabel("Окно ArcheAge", 20, 15, 150);
        windows.SetBounds(20, 38, 470, 28);
        refresh.SetBounds(505, 37, 135, 30);
        AddLabel("Discord webhook", 20, 78, 160);
        webhook.SetBounds(20, 100, 620, 27);
        test.SetBounds(660, 99, 265, 30);
        preview.SetBounds(20, 150, 640, 455);
        preview.BorderStyle = BorderStyle.FixedSingle;
        preview.MouseDown += PreviewMouseDown;
        preview.MouseMove += PreviewMouseMove;
        preview.MouseUp += PreviewMouseUp;
        preview.Paint += PreviewPaint;
        previewButton.SetBounds(20, 615, 150, 30);

        AddLabel("Область наблюдения", 680, 150, 240);
        regionText.SetBounds(680, 174, 245, 50);
        AddLabel("Выделите мышью область на снимке.\nИсключите чат, интерфейс и воду.", 680, 230, 250, 55);
        AddLabel("Чувствительность", 680, 315, 230);
        sensitivity.SetBounds(680, 340, 235, 50);
        AddLabel("Детектор ищет движение. Определение\nконкретного типа транспорта потребует\nигровых примеров для обучения.", 680, 415, 245, 70);
        start.SetBounds(680, 510, 245, 37);
        stop.SetBounds(680, 555, 245, 37);
        status.SetBounds(180, 617, 740, 28);
        log.SetBounds(20, 655, 905, 20);
        Controls.AddRange([windows, refresh, webhook, test, preview, previewButton, regionText,
            sensitivity, start, stop, status, log]);

        webhook.Text = SecretStore.Unprotect(settings.ProtectedWebhook);
        sensitivity.Value = Math.Clamp(settings.Sensitivity, 0, 100);
        UpdateRegionText();
        refresh.Click += (_, _) => RefreshWindows();
        previewButton.Click += (_, _) => CapturePreview();
        test.Click += async (_, _) => await TestDiscord();
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
        if (!Platform.TryCapture(SelectedWindow, out var frame, out var error))
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

    private async Task TestDiscord()
    {
        try
        {
            using var connection = new DiscordNotifier(webhook.Text);
            test.Enabled = false;
            await connection.SendAsync("Observer: проверка связи с Discord выполнена.", null, CancellationToken.None);
            SaveSettings();
            MessageBox.Show("Сообщение отправлено в Discord.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Discord недоступен"); }
        finally { test.Enabled = true; }
    }

    private void SaveSettings()
    {
        settings.Sensitivity = sensitivity.Value;
        try { settings.ProtectedWebhook = SecretStore.Protect(webhook.Text.Trim()); }
        catch
        {
            settings.ProtectedWebhook = "";
            WriteLog("Windows не разрешила сохранить адрес Discord. Введите его снова после перезапуска.");
        }
        try { settings.Save(); }
        catch (Exception ex) { WriteLog("Настройки не сохранены: " + ex.Message); }
    }

    private void Start()
    {
        if (SelectedWindow is null) { MessageBox.Show("Окно ArcheAge не найдено."); return; }
        try { notifier = new DiscordNotifier(webhook.Text); SaveSettings(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось запустить Observer"); return; }
        running = new CancellationTokenSource();
        motion.Reset(); tracker.Reset(); pending.Clear();
        try { foreach (var item in Outbox.Load()) pending.Enqueue(item); }
        catch (Exception ex) { WriteLog("Очередь сообщений не прочитана: " + ex.Message); }
        faultSince = null; faultReason = null; faultSent = false; frozenSince = null;
        nextSend = DateTime.MinValue; lastMovement = DateTime.MinValue;
        windows.Enabled = false; refresh.Enabled = false; start.Enabled = false; stop.Enabled = true;
        previewButton.Enabled = false; sensitivity.Enabled = false;
        status.Text = "Наблюдение запущено";
        _ = MonitorLoop(SelectedWindow, running.Token);
        Platform.Activate(SelectedWindow);
        WindowState = FormWindowState.Minimized;
    }

    private async Task MonitorLoop(GameWindow game, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                DateTime now = DateTime.UtcNow;
                using Bitmap? frame = Platform.TryCapture(game, out var capture, out var captureError) ? capture : null;
                string? fault = captureError;
                MotionResult? analysis = null;
                if (frame is not null)
                {
                    if (!Platform.IsForeground(game))
                    {
                        fault = "окно ArcheAge не на переднем плане: обзор может быть перекрыт";
                        motion.Reset();
                        frozenSince = null;
                    }
                    else
                    {
                        var area = FocusRect(frame);
                        analysis = motion.Analyze(frame, area, sensitivity.Value);
                        if (analysis.Dark) fault = "изображение почти полностью чёрное";
                        frozenSince = analysis.Frozen ? frozenSince ?? now : null;
                        if (frozenSince is not null && now - frozenSince > TimeSpan.FromSeconds(90)) fault = "изображение не меняется более 90 секунд";
                    }
                }
                CheckHealth(fault, frame, now);
                if (fault is null && frame is not null && analysis is not null)
                {
                    var ready = tracker.Update(analysis.Detections, frame, now);
                    if (ready.Count > 0 && now - lastMovement > TimeSpan.FromSeconds(4))
                    {
                        tracker.MarkAttempt(ready, now);
                        byte[] photo = Annotate(frame, ready.Select(x => x.Bounds));
                        try { SaveEvent(photo, "movement"); }
                        catch (Exception ex) { WriteLog("Снимок не сохранён на диск: " + ex.Message); }
                        if (QueueAlert("Observer: найдено движение у фактории. Время: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), photo))
                        {
                            tracker.MarkAlerted(ready, frame);
                            lastMovement = now;
                            WriteLog($"Найдено движение: {ready.Count} объект(ов). Снимок сохранён.");
                        }
                    }
                }
                if (pending.Count > 0 && now >= nextSend && notifier is not null)
                {
                    try
                    {
                        var item = pending.Peek();
                        await notifier.SendAsync(item.Text, item.Image, cancellation);
                        pending.Dequeue();
                        try { Outbox.Delete(item); } catch { }
                        nextSend = now.AddSeconds(1);
                        WriteLog("Уведомление отправлено в Discord.");
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        nextSend = now.AddSeconds(30);
                        WriteLog("Ошибка Discord, повтор через 30 секунд: " + ex.Message);
                    }
                }
                status.Text = fault is null ? $"Наблюдение работает · очередь Discord: {pending.Count}" : $"Обзор: {fault}";
                await Task.Delay(1000, cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WriteLog("Наблюдение остановлено из-за ошибки: " + ex.Message); }
        finally
        {
            notifier?.Dispose(); notifier = null;
            running?.Dispose(); running = null;
            if (!IsDisposed)
            {
                windows.Enabled = true; refresh.Enabled = true; start.Enabled = true; stop.Enabled = false;
                previewButton.Enabled = true; sensitivity.Enabled = true;
                status.Text = "Наблюдение остановлено";
            }
        }
    }

    private void CheckHealth(string? reason, Bitmap? frame, DateTime now)
    {
        if (reason is null)
        {
            if (faultSent) QueueAlert("Observer: обзор восстановлен. Время: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), null);
            faultSince = null; faultReason = null; faultSent = false;
            return;
        }
        if (reason != faultReason) { faultReason = reason; faultSince = now; faultSent = false; }
        if (!faultSent && now - faultSince > TimeSpan.FromSeconds(15))
        {
            byte[]? photo = frame is null ? null : PlainPng(frame);
            faultSent = QueueAlert("Observer: обзор нарушен — " + reason + ". Время: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), photo);
            WriteLog("Предупреждение о нарушенном обзоре добавлено в очередь.");
        }
    }

    private Rectangle FocusRect(Bitmap frame) => new(
        frame.Width * settings.X / 100, frame.Height * settings.Y / 100,
        Math.Max(1, frame.Width * settings.Width / 100), Math.Max(1, frame.Height * settings.Height / 100));

    private bool QueueAlert(string text, byte[]? image)
    {
        if (pending.Count >= 50) { WriteLog("Очередь Discord заполнена: требуется проверить соединение."); return false; }
        var item = PendingAlert.Create(text, image);
        pending.Enqueue(item);
        try { Outbox.Save(item); }
        catch (Exception ex) { WriteLog("Очередь не сохранена на диск: " + ex.Message); }
        return true;
    }

    private static byte[] PlainPng(Bitmap frame)
    {
        using var stream = new MemoryStream();
        frame.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] Annotate(Bitmap frame, IEnumerable<Rectangle> areas)
    {
        using var copy = new Bitmap(frame);
        using (var graphics = Graphics.FromImage(copy))
        using (var border = new Pen(Color.Red, Math.Max(3, frame.Width / 350f)))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var area in areas)
            {
                var box = Rectangle.Inflate(area, 12, 12);
                graphics.DrawEllipse(border, box);
            }
        }
        return PlainPng(copy);
    }

    private static void SaveEvent(byte[] png, string kind)
    {
        Directory.CreateDirectory(Settings.EventsFolder);
        string name = kind + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
        File.WriteAllBytes(Path.Combine(Settings.EventsFolder, name), png);
        var old = Directory.GetFiles(Settings.EventsFolder, "*.png")
            .Select(path => new FileInfo(path)).OrderByDescending(x => x.CreationTimeUtc).Skip(100);
        foreach (var file in old) file.Delete();
    }

    private void WriteLog(string message) { if (!IsDisposed) log.Text = DateTime.Now.ToString("HH:mm:ss") + " — " + message; }
}
