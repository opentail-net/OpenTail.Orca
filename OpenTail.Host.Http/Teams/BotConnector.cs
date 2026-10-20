using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace OpenTail.Host.Http.Teams;

/// <summary>
/// The two halves of the Bot Framework protocol the bot needs, without the SDK: checking that an incoming activity really comes from the
/// Bot Framework for this bot (signed token, issuer, audience, service URL), and sending or updating activities with the bot's own token.
/// </summary>
internal sealed class BotConnector
{
    private const string BotIssuer = "https://api.botframework.com";
    private readonly TeamsBotOptions _o;
    private readonly HttpClient _http;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _metadata;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private (string Token, DateTimeOffset Until)? _token;

    public BotConnector(TeamsBotOptions options)
    {
        _o = options;
        _http = new HttpClient(options.Handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        if (options.ConfigureValidation is null)
            _metadata = new ConfigurationManager<OpenIdConnectConfiguration>(options.OpenIdMetadataUrl, new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(_http) { RequireHttps = true });
    }

    /// <summary>The activity's sender is the Bot Framework, for this bot, and the service URL it names is the one the token was issued for.</summary>
    public async Task<bool> IsGenuineAsync(string? authorization, string serviceUrl, CancellationToken ct)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var p = new TokenValidationParameters
        {
            ValidIssuer = BotIssuer,
            ValidAudience = _o.AppId,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            RequireSignedTokens = true,
        };
        if (_metadata is not null) p.IssuerSigningKeys = (await _metadata.GetConfigurationAsync(ct)).SigningKeys;
        _o.ConfigureValidation?.Invoke(p);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(authorization[7..], p);
        if (!result.IsValid) return false;
        var claimed = result.Claims.TryGetValue("serviceurl", out var s) ? s as string : null;
        return claimed is not null && string.Equals(claimed.TrimEnd('/'), serviceUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> SendAsync(TeamsStore.Conversation conv, JsonObject activity, CancellationToken ct)
    {
        var r = await CallAsync(HttpMethod.Post, conv.ServiceUrl, $"v3/conversations/{Uri.EscapeDataString(conv.ConversationId)}/activities", activity, ct);
        return r?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Teams did not return an activity id.");
    }

    public Task UpdateAsync(TeamsStore.Conversation conv, string activityId, JsonObject activity, CancellationToken ct) =>
        CallAsync(HttpMethod.Put, conv.ServiceUrl, $"v3/conversations/{Uri.EscapeDataString(conv.ConversationId)}/activities/{Uri.EscapeDataString(activityId)}", activity, ct);

    private async Task<JsonNode?> CallAsync(HttpMethod method, string serviceUrl, string path, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(new Uri(serviceUrl.TrimEnd('/') + "/"), path))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        var text = await resp.Content.ReadAsStringAsync(ct);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is { } t && t.Until > DateTimeOffset.UtcNow.AddMinutes(1)) return t.Token;
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_token is { } again && again.Until > DateTimeOffset.UtcNow.AddMinutes(1)) return again.Token;
            var secret = _o.AppPasswordEnv is { } env ? _o.Env(env) : null;
            if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException($"The Teams bot's secret ({_o.AppPasswordEnv}) is not set.");
            Redaction.AddSecret(secret);
            var endpoint = _o.TokenEndpoint ?? $"https://login.microsoftonline.com/{Uri.EscapeDataString(_o.TenantId)}/oauth2/v2.0/token";
            using var resp = await _http.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = _o.AppId, ["client_secret"] = secret, ["scope"] = _o.ConnectorScope,
            }), ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"The bot's token request answered HTTP {(int)resp.StatusCode}.");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            var expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 3600;
            _token = (token, DateTimeOffset.UtcNow.AddSeconds(expires));
            return token;
        }
        finally { _tokenGate.Release(); }
    }
}
