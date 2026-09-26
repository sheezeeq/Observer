using System.Net.Http.Headers;
using System.Text.Json;

namespace Observer;

internal sealed class ChannelHub : IDisposable
{
    private readonly Dictionary<ChannelKind, IChannelNotifier> channels = [];
    public IReadOnlyCollection<ChannelKind> Connected => channels.Keys;

    public ChannelHub(string discordWebhook, string telegramToken, string telegramChatId, string vkToken, string vkPeerId)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(discordWebhook)) channels.Add(ChannelKind.Discord, new DiscordNotifier(discordWebhook));
            if (!string.IsNullOrWhiteSpace(telegramToken) || !string.IsNullOrWhiteSpace(telegramChatId))
                channels.Add(ChannelKind.Telegram, new TelegramNotifier(telegramToken, telegramChatId));
            if (!string.IsNullOrWhiteSpace(vkToken) || !string.IsNullOrWhiteSpace(vkPeerId))
                channels.Add(ChannelKind.Vk, new VkNotifier(vkToken, vkPeerId));
        }
        catch { Dispose(); throw; }
    }

    public Task SendAsync(PendingAlert alert, CancellationToken cancellation) =>
        channels.TryGetValue(alert.Channel, out var channel)
            ? channel.SendAsync(alert, cancellation)
            : throw new InvalidOperationException($"Канал {alert.Channel} не настроен.");

    public void Dispose()
    {
        foreach (var channel in channels.Values) channel.Dispose();
        channels.Clear();
    }
}

internal sealed class TelegramNotifier : IChannelNotifier
{
    private readonly HttpClient client;
    private readonly string token;
    private readonly string chatId;
    public ChannelKind Kind => ChannelKind.Telegram;

    public TelegramNotifier(string token, string chatId, HttpClient? client = null)
    {
        this.token = token.Trim();
        this.chatId = chatId.Trim();
        if (this.token.Length < 10 || !this.token.Contains(':') || this.token.Any(c => !(char.IsLetterOrDigit(c) || c is ':' or '_' or '-')))
            throw new ArgumentException("Укажите токен Telegram-бота.");
        if (this.chatId.Length == 0 || this.chatId.Length > 80)
            throw new ArgumentException("Укажите ID чата Telegram или @имя канала.");
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task SendAsync(PendingAlert alert, CancellationToken cancellation)
    {
        string method = alert.Image is null ? "sendMessage" : "sendPhoto";
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId), "chat_id");
            form.Add(new StringContent(alert.Text), alert.Image is null ? "text" : "caption");
            if (alert.Image is not null)
            {
                var image = new ByteArrayContent(alert.Image);
                image.Headers.ContentType = new MediaTypeHeaderValue(PhotoEncoding.Mime(alert.Image));
                form.Add(image, "photo", PhotoEncoding.FileName(alert.Image));
            }
            using var response = await client.PostAsync($"https://api.telegram.org/bot{token}/{method}", form, cancellation);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            if (response.IsSuccessStatusCode && document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return;
            string description = document.RootElement.TryGetProperty("description", out var value) ? value.GetString() ?? "ошибка API" : "ошибка API";
            throw new InvalidOperationException("Telegram: " + description);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException("Telegram: соединение или ответ API недоступны.", ex); }
    }

    public void Dispose() => client.Dispose();
}

internal sealed class VkNotifier : IChannelNotifier
{
    private readonly HttpClient client;
    private readonly string token;
    private readonly long peerId;
    public ChannelKind Kind => ChannelKind.Vk;

    public VkNotifier(string token, string peerId, HttpClient? client = null)
    {
        this.token = token.Trim();
        if (this.token.Length < 10) throw new ArgumentException("Укажите ключ доступа сообщества ВК.");
        if (!long.TryParse(peerId.Trim(), out this.peerId) || this.peerId == 0)
            throw new ArgumentException("Укажите числовой peer_id диалога или беседы ВК.");
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task SendAsync(PendingAlert alert, CancellationToken cancellation)
    {
        try
        {
            string? attachment = alert.Image is null ? null : await UploadPhotoAsync(alert.Image, cancellation);
            var parameters = new Dictionary<string, string>
            {
                ["peer_id"] = peerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["random_id"] = StableRandomId(alert.Id).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["message"] = alert.Text,
            };
            if (attachment is not null) parameters["attachment"] = attachment;
            await CallApiAsync("messages.send", parameters, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException("ВК: соединение или ответ API недоступны.", ex); }
    }

    private async Task<string> UploadPhotoAsync(byte[] png, CancellationToken cancellation)
    {
        using var server = await CallApiAsync("photos.getMessagesUploadServer",
            new Dictionary<string, string> { ["peer_id"] = peerId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellation);
        string url = server.RootElement.GetProperty("response").GetProperty("upload_url").GetString() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "https")
            throw new InvalidOperationException("ВК вернул недопустимый адрес загрузки фото.");
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(png);
        image.Headers.ContentType = new MediaTypeHeaderValue(PhotoEncoding.Mime(png));
        form.Add(image, "photo", PhotoEncoding.FileName(png));
        using var response = await client.PostAsync(address, form, cancellation);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ВК: загрузка фото вернула {(int)response.StatusCode}.");
        using var upload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var payload = upload.RootElement;
        using var saved = await CallApiAsync("photos.saveMessagesPhoto", new Dictionary<string, string>
        {
            ["photo"] = payload.GetProperty("photo").GetString() ?? "",
            ["server"] = payload.GetProperty("server").ToString(),
            ["hash"] = payload.GetProperty("hash").GetString() ?? "",
        }, cancellation);
        var photo = saved.RootElement.GetProperty("response")[0];
        return "photo" + photo.GetProperty("owner_id") + "_" + photo.GetProperty("id");
    }

    private async Task<JsonDocument> CallApiAsync(string method, Dictionary<string, string> parameters, CancellationToken cancellation)
    {
        parameters["access_token"] = token;
        parameters["v"] = "5.199";
        using var response = await client.PostAsync("https://api.vk.com/method/" + method, new FormUrlEncodedContent(parameters), cancellation);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            string description = error.TryGetProperty("error_msg", out var message) ? message.GetString() ?? "ошибка API" : "ошибка API";
            throw new InvalidOperationException("ВК: " + description);
        }
        if (!response.IsSuccessStatusCode || !document.RootElement.TryGetProperty("response", out _))
            throw new InvalidOperationException($"ВК: сервер вернул {(int)response.StatusCode}.");
        return JsonDocument.Parse(document.RootElement.GetRawText());
    }

    private static int StableRandomId(string id)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id));
        return BitConverter.ToInt32(hash) & int.MaxValue;
    }

    public void Dispose() => client.Dispose();
}
