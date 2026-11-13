using System.Text;
using System.Text.Json;

namespace OpenTail.Orca;

/// <summary>
/// Credentials for one module: only the names mapped under that module's entry in <c>modules.json</c>. Environment and file sources are
/// re-read on every use (rotation needs no restart); OAuth2 tokens are cached until shortly before they expire, refreshed by one caller at a
/// time, and dropped when a request comes back 401. Every value handed out is registered with <see cref="Redaction"/>.
/// </summary>
public sealed class ModuleCredentials : ICredentialProvider
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    private readonly string _moduleId;
    private readonly IReadOnlyDictionary<string, CredentialSource> _sources;
    private readonly string _baseDirectory;
    private readonly TimeProvider _clock;
    private readonly HttpClient _tokenHttp;
    private readonly Dictionary<string, Credential> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public ModuleCredentials(string moduleId, IReadOnlyDictionary<string, CredentialSource> sources, string baseDirectory,
        TimeProvider? clock = null, HttpClient? tokenHttp = null)
    {
        _moduleId = moduleId;
        _sources = sources;
        _baseDirectory = baseDirectory;
        _clock = clock ?? TimeProvider.System;
        _tokenHttp = tokenHttp ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        foreach (var name in sources.Keys) _gates[name] = new SemaphoreSlim(1, 1);
    }

    public IReadOnlyCollection<string> Names => _sources.Keys.ToList();

    /// <summary>Host pattern a credential is bound to (null for <see cref="CredentialKind.Secret"/>).</summary>
    public string? BoundHost(string name) => _sources.TryGetValue(name, out var s) ? s.Host : null;

    /// <summary>The header a <see cref="CredentialKind.Header"/> credential travels in (removed when a redirect leaves its host).</summary>
    public string? HeaderNameOf(string name) => _sources.TryGetValue(name, out var s) ? s.HeaderName : null;

    public async ValueTask<Credential> GetAsync(string name, CancellationToken ct = default)
    {
        if (!_sources.TryGetValue(name, out var source))
            throw new InvalidOperationException($"Module '{_moduleId}' asked for credential '{name}', which is not mapped to it in the modules file.");

        if (source.OAuth2 is null) return Register(ReadStatic(name, source));

        lock (_lock)
            if (_cache.TryGetValue(name, out var cached) && cached.ExpiresAt is { } exp && exp - Skew > _clock.GetUtcNow()) return cached;

        var gate = _gates[name];
        await gate.WaitAsync(ct);
        try
        {
            lock (_lock) // someone else may have refreshed while we waited
                if (_cache.TryGetValue(name, out var cached) && cached.ExpiresAt is { } exp && exp - Skew > _clock.GetUtcNow()) return cached;
            var fresh = Register(await FetchTokenAsync(name, source.OAuth2, ct));
            lock (_lock) _cache[name] = fresh;
            return fresh;
        }
        finally { gate.Release(); }
    }

    public void Invalidate(string name)
    {
        lock (_lock) _cache.Remove(name);
    }

    /// <summary>For run mode: every mapped source exists (variables set, files present). Describe mode never calls this.</summary>
    public IReadOnlyList<string> CheckPresent()
    {
        var missing = new List<string>();
        foreach (var (name, s) in _sources)
        {
            if (s.Env is { } env && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env)))
                missing.Add($"credential '{name}': environment variable {env} is not set");
            if (s.File is { } f && !System.IO.File.Exists(Resolve(f)))
                missing.Add($"credential '{name}': file {Resolve(f)} does not exist");
            if (s.UserNameEnv is { } u && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(u)))
                missing.Add($"credential '{name}': environment variable {u} is not set");
            if (s.OAuth2 is { } o)
                foreach (var v in new[] { o.ClientIdEnv, o.ClientSecretEnv })
                    if (v is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                        missing.Add($"credential '{name}': environment variable {v} is not set");
        }
        // Defence in depth: learn every mapped secret now, so it is scrubbed from errors and the audit trail even before its first use
        // (a module that echoes a value it read some other way is still caught). Values are re-read on use, so rotation still works.
        if (missing.Count == 0)
            foreach (var s in _sources.Values)
            {
                if (s.Env is { } env) Redaction.AddSecret(Environment.GetEnvironmentVariable(env)?.Trim());
                if (s.File is { } f) { try { Redaction.AddSecret(System.IO.File.ReadAllText(Resolve(f)).Trim()); } catch (IOException) { } }
                if (s.OAuth2?.ClientSecretEnv is { } cs) Redaction.AddSecret(Environment.GetEnvironmentVariable(cs)?.Trim());
            }
        return missing;
    }

    private Credential ReadStatic(string name, CredentialSource s)
    {
        string? secret = null;
        if (s.Env is { } env) secret = Environment.GetEnvironmentVariable(env);
        else if (s.File is { } f)
        {
            var path = Resolve(f);
            secret = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
        }
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException($"Credential '{name}' for module '{_moduleId}' is not available ({(s.Env is not null ? "environment variable " + s.Env : "file " + s.File)} is empty or missing).");
        var user = s.UserName ?? (s.UserNameEnv is { } ue ? Environment.GetEnvironmentVariable(ue) : null);
        return new Credential(s.Kind, secret.Trim()) { UserName = user, HeaderName = s.HeaderName };
    }

    private async Task<Credential> FetchTokenAsync(string name, OAuth2ClientCredentials o, CancellationToken ct)
    {
        var id = Environment.GetEnvironmentVariable(o.ClientIdEnv ?? "");
        var secret = Environment.GetEnvironmentVariable(o.ClientSecretEnv ?? "");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException($"Credential '{name}' for module '{_moduleId}': {o.ClientIdEnv} or {o.ClientSecretEnv} is not set.");
        Redaction.AddSecret(secret);

        var basic = o.ClientAuthentication == "Basic";
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (!basic) { form.Add(new("client_id", id)); form.Add(new("client_secret", secret)); }
        if (!string.IsNullOrWhiteSpace(o.Scope)) form.Add(new("scope", o.Scope));
        using var req = new HttpRequestMessage(HttpMethod.Post, o.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        req.Headers.TryAddWithoutValidation("User-Agent", "OpenTail-Host");
        if (basic)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Uri.EscapeDataString(id) + ":" + Uri.EscapeDataString(secret))));
        using var resp = await _tokenHttp.SendAsync(req, ct);
        // Never put the token endpoint's body in the error: it can echo the client secret.
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Credential '{name}' for module '{_moduleId}': the token endpoint answered HTTP {(int)resp.StatusCode}.");
        var text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var token = doc.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(token)) throw new JsonException();
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 3600;
            return new Credential(CredentialKind.Bearer, token) { ExpiresAt = _clock.GetUtcNow().AddSeconds(Math.Max(expiresIn, 1)) };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Credential '{name}' for module '{_moduleId}': the token endpoint did not return an access_token.");
        }
    }

    private static Credential Register(Credential c)
    {
        Redaction.AddSecret(c.Secret);
        if (c.Kind == CredentialKind.Basic && c.UserName is not null)
            Redaction.AddSecret(Convert.ToBase64String(Encoding.UTF8.GetBytes(c.UserName + ":" + c.Secret)));
        return c;
    }

    private string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_baseDirectory, path));
}
