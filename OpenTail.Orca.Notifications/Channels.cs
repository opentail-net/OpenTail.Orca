using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Orca.Notifications;

/// <summary>What a channel is asked to deliver. Facts about the action, never its arguments or result.</summary>
public sealed record NotificationMessage(
    string EventId,
    string Name,
    ActionEvent Event,
    string? ReviewUrl,
    string? PreviewSummary,
    int? ReminderNumber);

public interface INotificationChannel
{
    string Name { get; }

    /// <summary>Delivers or throws. The exception text must not contain secrets or response bodies.</summary>
    Task SendAsync(NotificationMessage message, CancellationToken ct = default);
}

internal static class ChannelHttp
{
    /// <summary>No redirects (a destination must not be able to bounce us elsewhere) and a short timeout.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler) =>
        new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task PostJsonAsync(HttpClient http, string url, string json, Action<HttpRequestMessage>? tweak, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        tweak?.Invoke(req);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        // Only the status code is kept: a response body could echo anything.
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
    }
}

/// <summary>
/// A generic HTTPS webhook with a signed body: header <c>X-OpenTail-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c> where v1 is
/// HMAC-SHA256(secret, "&lt;t&gt;.&lt;body&gt;"). Receivers should reject old timestamps and dedupe on <c>X-OpenTail-Event-Id</c>.
/// </summary>
public sealed class WebhookChannel : INotificationChannel
{
    private readonly string _url;
    private readonly byte[] _secret;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    public WebhookChannel(string name, string url, string secret, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        Name = name;
        _url = url;
        _secret = Encoding.UTF8.GetBytes(secret);
        _http = ChannelHttp.CreateClient(handler);
        _clock = clock ?? TimeProvider.System;
    }

    public string Name { get; }

    public static string Sign(byte[] secret, long unixSeconds, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{unixSeconds}.{body}"))).ToLowerInvariant();

    public static string Body(NotificationMessage m)
    {
        if (m.Name == NotificationEvents.AuditCheckpoint)
            return new JsonObject { ["event"] = m.Name, ["eventId"] = m.EventId, ["at"] = m.Event.At, ["checkpoint"] = m.Event.Detail }.ToJsonString();
        if (m.Name == NotificationEvents.WorkReady)
            return new JsonObject { ["event"] = m.Name, ["eventId"] = m.EventId, ["at"] = m.Event.At, ["work"] = JsonNode.Parse(m.Event.Detail!) }.ToJsonString();
        if (m.Name == NotificationEvents.AuditEntry)
            return new JsonObject { ["event"] = m.Name, ["eventId"] = m.EventId, ["entry"] = JsonNode.Parse(m.Event.Detail!) }.ToJsonString();
        var action = new JsonObject
        {
            ["id"] = m.Event.ActionId,
            ["tool"] = m.Event.ToolName,
            ["module"] = m.Event.ModuleId,
            ["risk"] = m.Event.Risk?.ToString(),
            ["requester"] = m.Event.RequesterId,
            ["status"] = m.Event.Status.ToString(),
            ["tainted"] = m.Event.Tainted,
        };
        var o = new JsonObject
        {
            ["event"] = m.Name,
            ["eventId"] = m.EventId,
            ["at"] = m.Event.At,
            ["action"] = action,
            ["reviewUrl"] = m.ReviewUrl,
        };
        if (m.ReminderNumber is { } n) o["reminder"] = n;
        if (m.Event.Detail is { } d) o["detail"] = d;
        if (m.PreviewSummary is { } p) o["previewSummary"] = p;
        return o.ToJsonString();
    }

    public Task SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        var body = Body(message);
        var t = _clock.GetUtcNow().ToUnixTimeSeconds();
        return ChannelHttp.PostJsonAsync(_http, _url, body, req =>
        {
            req.Headers.Add("X-OpenTail-Event-Id", message.EventId);
            req.Headers.Add("X-OpenTail-Signature", $"t={t},v1={Sign(_secret, t, body)}");
        }, ct);
    }
}

/// <summary>A Slack incoming webhook (Block Kit) with a link button to the review page. The webhook URL is the secret.</summary>
public sealed class SlackChannel : INotificationChannel
{
    private readonly string _url;
    private readonly HttpClient _http;

    public SlackChannel(string name, string url, HttpMessageHandler? handler = null)
    {
        Name = name;
        _url = url;
        _http = ChannelHttp.CreateClient(handler);
    }

    public string Name { get; }

    /// <summary>Slack treats &amp; &lt; &gt; as control characters, and backticks/asterisks/underscores/tildes as formatting. Values come from
    /// the model (tool names are module-declared, but requester ids and previews are not), so none of them may inject markup or links.</summary>
    public static string Escape(string? text, int max = 120)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        foreach (var ch in text)
        {
            if (sb.Length >= max) { sb.Append('…'); break; }
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '`' or '*' or '_' or '~' or '|': sb.Append('\''); break;
                case '\r' or '\n' or '\t': sb.Append(' '); break;
                default: if (!char.IsControl(ch)) sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    public static string Body(NotificationMessage m)
    {
        if (m.Name == NotificationEvents.AuditCheckpoint)
            return new JsonObject { ["text"] = $"Audit checkpoint {Escape(m.Event.Detail, 90)} (keep this message: --verify-audit --checkpoint checks the trail against it)" }.ToJsonString();
        var headline = m.Name switch
        {
            "queued" when m.Event.Detail?.StartsWith("runs at", StringComparison.Ordinal) == true => "Going out soon: " + Escape(m.Event.Detail, 60) + " (open to stop it)",
            "queued" => "Approval needed",
            "reminder" => $"Still waiting for approval (reminder {m.ReminderNumber})",
            "failed" => "Action failed",
            "expired" => "Approval request expired",
            _ => "Action update: " + Escape(m.Name.Replace('_', ' '), 40),
        };
        var detail = new StringBuilder();
        detail.Append($"*{headline}*: `{Escape(m.Event.ToolName, 80)}`");
        if (m.Event.Risk is { } risk) detail.Append($" ({risk})");
        detail.Append($"\nRequested by {Escape(m.Event.RequesterId, 80)}");
        if (m.Event.Tainted) detail.Append(" · the session read untrusted content");
        if (m.PreviewSummary is { } p) detail.Append($"\n{Escape(p, 200)}");

        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = detail.ToString() } },
        };
        if (m.ReviewUrl is { } url)
            blocks.Add(new JsonObject
            {
                ["type"] = "actions",
                ["elements"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "button",
                        ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = "Review" },
                        ["url"] = url,
                    },
                },
            });
        return new JsonObject { ["text"] = $"{headline}: {Escape(m.Event.ToolName, 80)}", ["blocks"] = blocks }.ToJsonString();
    }

    public Task SendAsync(NotificationMessage message, CancellationToken ct = default) =>
        ChannelHttp.PostJsonAsync(_http, _url, Body(message), null, ct);
}

/// <summary>
/// Microsoft Teams through a Workflows webhook: an Adaptive Card saying what waits, why and how risky it is, with an Open button to the
/// review page. Approving happens only on the page, after sign-in, so Teams never calls the Host and the Host can stay inside the network.
/// </summary>
public sealed class TeamsChannel : INotificationChannel
{
    private readonly string _url;
    private readonly HttpClient _http;

    public TeamsChannel(string name, string url, HttpMessageHandler? handler = null)
    {
        Name = name;
        _url = url;
        _http = ChannelHttp.CreateClient(handler);
    }

    public string Name { get; }

    /// <summary>Teams renders Markdown in card text and turns bare addresses into links. Values come partly from the model, so markup
    /// characters are replaced and "://" is broken with a zero-width space: no injected link, image, heading or emphasis.</summary>
    public static string Escape(string? text, int max = 120)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        foreach (var ch in text)
        {
            if (sb.Length >= max) { sb.Append('…'); break; }
            switch (ch)
            {
                case '[' or '(': sb.Append('‹'); break;
                case ']' or ')': sb.Append('›'); break;
                case '<': sb.Append('‹'); break;
                case '>': sb.Append('›'); break;
                case '*' or '_' or '`' or '~' or '#' or '|' or '!' or '\\': sb.Append('\''); break;
                case '\r' or '\n' or '\t': sb.Append(' '); break;
                default: if (!char.IsControl(ch)) sb.Append(ch); break;
            }
        }
        return sb.ToString().Replace("://", ":\u200B//", StringComparison.Ordinal).Replace("www.", "www\u200B.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <see cref="Escape"/> would cut this text at <paramref name="max"/> characters (batch 04, B3): a card that shows the
    /// cut text must say so, and must not offer to approve it.</summary>
    public static bool IsCut(string? text, int max) => !string.IsNullOrEmpty(text) && Escape(text, int.MaxValue - 1).Length > max;

    public static string Body(NotificationMessage m)
    {
        var headline = m.Name switch
        {
            "queued" when m.Event.Detail?.StartsWith("runs at", StringComparison.Ordinal) == true => "Going out soon: " + Escape(m.Event.Detail, 60) + " (open to stop it)",
            "queued" => "Approval needed",
            "reminder" => $"Still waiting for approval (reminder {m.ReminderNumber})",
            "failed" => "Action failed",
            "expired" => "Approval request expired",
            _ => "Action update: " + Escape(m.Name.Replace('_', ' '), 40),
        };
        var effect = m.Event.Risk switch
        {
            ToolRisk.ExternalEffect => "Reaches other people.",
            ToolRisk.Destructive => "Deletes or closes something; may not be undoable.",
            ToolRisk.Write => "Changes data this deployment owns.",
            _ => null,
        };
        JsonObject Text(string text, bool bold = false, bool subtle = false) => new()
        {
            ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true,
            ["weight"] = bold ? "Bolder" : "Default", ["isSubtle"] = subtle,
        };

        var body = m.Name == NotificationEvents.AuditCheckpoint
            ? new JsonArray
            {
                new JsonObject { ["type"] = "TextBlock", ["text"] = "Audit checkpoint", ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
                Text(Escape(m.Event.Detail, 90)),
                Text("Keep this message: --verify-audit --checkpoint checks the audit trail against it.", subtle: true),
            }
            : new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = headline, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            Text(Escape(m.Event.ToolName, 80) + (m.Event.Risk is { } risk ? $" · {risk}" : ""), subtle: true),
        };
        if (m.PreviewSummary is { } p)
        {
            body.Add(Text(Escape(p, 300)));
            if (IsCut(p, 300)) body.Add(Text("The text is cut here. Open the review page to see all of it before anyone decides.", bold: true));
        }
        if (effect is not null) body.Add(Text(effect, bold: true));
        if (m.Event.Tainted) body.Add(Text("Held because the caller read outside content first. Check that this is what you want.", subtle: true));
        body.Add(Text("Requested by " + Escape(m.Event.RequesterId, 80), subtle: true));

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };
        if (m.ReviewUrl is { } url)
            card["actions"] = new JsonArray { new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open", ["url"] = url } };

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray
            {
                new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["contentUrl"] = null, ["content"] = card },
            },
        }.ToJsonString();
    }

    public Task SendAsync(NotificationMessage message, CancellationToken ct = default) =>
        ChannelHttp.PostJsonAsync(_http, _url, Body(message), null, ct);
}
