using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CellPort.App.Views;
using CellPort.Core.At;
using CellPort.Core.Services;

namespace CellPort.App.Services;

/// <summary>把实时收到的短信发送到用户配置的 webhook。</summary>
public sealed class SmsForwarder
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public async Task ForwardAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var settings = AppSettings.Current;
        var notification = $"新短信\n发件人：{message.Number}\n{message.Body}";
        var tasks = new List<Task>(3);

        if (!string.IsNullOrWhiteSpace(settings.BarkUrl))
        {
            tasks.Add(SendAsync(
                "Bark",
                settings.BarkUrl,
                new Dictionary<string, object?>
                {
                    ["title"] = $"新短信：{message.Number}",
                    ["body"] = message.Body,
                },
                responseCodeField: null,
                cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(settings.FeishuWebhook))
        {
            tasks.Add(SendAsync(
                "飞书",
                settings.FeishuWebhook,
                new Dictionary<string, object?>
                {
                    ["msg_type"] = "text",
                    ["content"] = new Dictionary<string, string> { ["text"] = notification },
                },
                responseCodeField: "code",
                cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(settings.DingTalkWebhook))
        {
            tasks.Add(SendAsync(
                "钉钉",
                settings.DingTalkWebhook,
                new Dictionary<string, object?>
                {
                    ["msgtype"] = "text",
                    ["text"] = new Dictionary<string, string> { ["content"] = notification },
                },
                responseCodeField: "errcode",
                cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task SendAsync(
        string provider,
        string webhook,
        object payload,
        string? responseCodeField,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(webhook, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            AppLog.Warn($"短信转发到{provider}失败：Webhook 地址无效。");
            return;
        }

        try
        {
            using var response = await HttpClient.PostAsJsonAsync(uri, payload, cancellationToken)
                .ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"短信转发到{provider}失败：HTTP {(int)response.StatusCode}。");
                return;
            }

            if (responseCodeField is not null && ProviderRejected(responseBody, responseCodeField))
            {
                AppLog.Warn($"短信转发到{provider}失败：Webhook 拒绝了请求。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"短信转发到{provider}失败：{ex.GetType().Name}。");
        }
    }

    private static bool ProviderRejected(string responseBody, string responseCodeField)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty(responseCodeField, out var code))
            {
                return false;
            }

            return code.ValueKind == JsonValueKind.Number &&
                   code.TryGetInt32(out var value) &&
                   value != 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
