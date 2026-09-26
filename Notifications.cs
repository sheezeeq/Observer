using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Observer;

internal sealed class Settings
{
    public int X { get; set; } = 5;
    public int Y { get; set; } = 5;
    public int Width { get; set; } = 90;
    public int Height { get; set; } = 85;
    public int Sensitivity { get; set; } = 50;
    public string ProtectedWebhook { get; set; } = "";
    public string ProtectedTelegramToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    public string ProtectedVkToken { get; set; } = "";
    public string VkPeerId { get; set; } = "";
    public string PointName { get; set; } = "Фактория";
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Auto;
    public bool AlertUnknownMotion { get; set; } = false;
    public static string Folder => Environment.GetEnvironmentVariable("OBSERVER_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Observer");
    public static string EventsFolder => Path.Combine(Folder, "events");
    private static string PathName => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string temporary = PathName + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, PathName, true);
    }
}

internal enum ChannelKind { Discord, Telegram, Vk }

internal sealed record PendingAlert(string Id, string Text, byte[]? Image, ChannelKind Channel = ChannelKind.Discord)
{
    public static PendingAlert Create(string text, byte[]? image, ChannelKind channel = ChannelKind.Discord) =>
        new(DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8], text, image, channel);
}

internal static class Outbox
{
    private static string Folder => Path.Combine(Settings.Folder, "outbox");
    public static IEnumerable<PendingAlert> Load()
    {
        if (!Directory.Exists(Folder)) yield break;
        foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(x => x).Take(150))
        {
            PendingAlert? alert = null;
            try { alert = JsonSerializer.Deserialize<PendingAlert>(File.ReadAllText(file)); }
            catch { /* Keep unreadable entries for manual inspection. */ }
            if (alert is not null) yield return alert;
        }
    }
    public static void Save(PendingAlert alert)
    {
        Directory.CreateDirectory(Folder);
        string target = Path.Combine(Folder, alert.Id + ".json");
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(alert));
        File.Move(temporary, target, true);
    }
    public static void Delete(PendingAlert alert)
    {
        string path = Path.Combine(Folder, alert.Id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}

internal static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy,
        nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy,
        nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    public static string Protect(string value) => value.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value)
    {
        if (value.Length == 0) return "";
        try { return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false)); }
        catch { return ""; }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        nint pointer = Marshal.AllocHGlobal(input.Length);
        Marshal.Copy(input, 0, pointer, input.Length);
        var data = new Blob { Length = input.Length, Data = pointer };
        try
        {
            bool okay = protect
                ? CryptProtectData(ref data, "Observer notification credentials", 0, 0, 0, 0, out var output)
                : CryptUnprotectData(ref data, 0, 0, 0, 0, 0, out output);
            if (!okay) throw new InvalidOperationException("Windows не смогла защитить данные канала (ошибка " + Marshal.GetLastWin32Error() + ").");
            try
            {
                byte[] result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}

internal interface IChannelNotifier : IDisposable
{
    ChannelKind Kind { get; }
    Task SendAsync(PendingAlert alert, CancellationToken cancellation);
}

internal sealed class DiscordNotifier : IChannelNotifier
{
    private readonly HttpClient client;
    private readonly Uri webhook;
    public ChannelKind Kind => ChannelKind.Discord;
    public DiscordNotifier(string url, HttpClient? client = null)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var address) ||
            address.Scheme != "https" ||
            address.Host is not ("discord.com" or "discordapp.com") ||
            !address.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal))
            throw new ArgumentException("Нужна ссылка вебхука Discord вида https://discord.com/api/webhooks/...");
        webhook = address;
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task SendAsync(PendingAlert alert, CancellationToken cancellation)
    {
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(JsonSerializer.Serialize(new { content = alert.Text, allowed_mentions = new { parse = Array.Empty<string>() } })), "payload_json");
                if (alert.Image is not null)
                {
                    var image = new ByteArrayContent(alert.Image);
                    image.Headers.ContentType = new MediaTypeHeaderValue(PhotoEncoding.Mime(alert.Image));
                    form.Add(image, "files[0]", PhotoEncoding.FileName(alert.Image));
                }
                using var response = await client.PostAsync(webhook, form, cancellation);
                if (response.IsSuccessStatusCode) return;
                if (response.StatusCode == (HttpStatusCode)429 && attempt < 2)
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3), cancellation);
                    continue;
                }
                throw new HttpRequestException($"Discord вернул {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is not null) { throw; }
        catch (Exception ex) { throw new InvalidOperationException("Discord: соединение недоступно.", ex); }
    }

    public void Dispose() => client.Dispose();
}
