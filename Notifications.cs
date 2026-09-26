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
        File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed record PendingAlert(string Id, string Text, byte[]? Image)
{
    public static PendingAlert Create(string text, byte[]? image) =>
        new(DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8], text, image);
}

internal static class Outbox
{
    private static string Folder => Path.Combine(Settings.Folder, "outbox");
    public static IEnumerable<PendingAlert> Load()
    {
        if (!Directory.Exists(Folder)) yield break;
        foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(x => x).Take(50))
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
        File.WriteAllText(Path.Combine(Folder, alert.Id + ".json"), JsonSerializer.Serialize(alert));
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

    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value)
    {
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
                ? CryptProtectData(ref data, "Observer Discord webhook", 0, 0, 0, 0, out var output)
                : CryptUnprotectData(ref data, 0, 0, 0, 0, 0, out output);
            if (!okay) throw new InvalidOperationException("Windows could not protect the Discord address (error " + Marshal.GetLastWin32Error() + ").");
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

internal sealed class DiscordNotifier : IDisposable
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Uri webhook;
    public DiscordNotifier(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var address) ||
            address.Scheme != "https" ||
            address.Host is not ("discord.com" or "discordapp.com") ||
            !address.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal))
            throw new ArgumentException("Нужна ссылка вебхука Discord вида https://discord.com/api/webhooks/...");
        webhook = address;
    }

    public async Task SendAsync(string text, byte[]? png, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(new { content = text, allowed_mentions = new { parse = Array.Empty<string>() } })), "payload_json");
            if (png is not null)
            {
                var image = new ByteArrayContent(png);
                image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(image, "files[0]", "observer.png");
            }
            using var response = await client.PostAsync(webhook, form, cancellation);
            if (response.IsSuccessStatusCode) return;
            if (response.StatusCode == (HttpStatusCode)429 && attempt < 2)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3), cancellation);
                continue;
            }
            throw new HttpRequestException($"Discord вернул {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    public void Dispose() => client.Dispose();
}
