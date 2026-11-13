using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTail.Orca;

/// <summary>
/// <c>modules.json</c>: which modules a deployment enables, how each is configured, which hosts it may reach and where its credentials
/// come from. Written by the operator; secrets are never in it (only the names of environment variables or files that hold them).
/// <code>
/// {
///   "Modules": [
///     { "Type": "github-issues", "Id": "gh",
///       "Network": { "Hosts": [ "api.github.com" ] },
///       "Credentials": { "github": { "Env": "GITHUB_TOKEN", "Kind": "Bearer", "Host": "api.github.com" } },
///       "Config": { "Repositories": [ "acme/support" ] } }
///   ]
/// }
/// </code>
/// </summary>
public sealed class ModulesConfig
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(), new DurationConverter() },
    };

    public List<ModuleEntry> Modules { get; init; } = [];

    /// <summary>Assembly name prefixes searched for module factories besides <c>OpenTail.Modules.</c>, such as <c>"Contoso.Bricks."</c>.
    /// Only assemblies the deployment references (listed in its deps.json) are searched; nothing is loaded from a folder.</summary>
    public List<string> FactoryAssemblies { get; init; } = [];

    /// <summary>Directory relative paths in module config resolve against (the file's directory when loaded from a file).</summary>
    [JsonIgnore] public string BaseDirectory { get; private set; } = Directory.GetCurrentDirectory();

    public static ModulesConfig Parse(string json, string? baseDirectory = null)
    {
        var cfg = JsonSerializer.Deserialize<ModulesConfig>(json, JsonOptions) ?? throw new InvalidOperationException("The modules file is empty.");
        cfg.BaseDirectory = baseDirectory ?? Directory.GetCurrentDirectory();
        var errors = cfg.Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Invalid modules file: " + string.Join(" ", errors));
        return cfg;
    }

    public static ModulesConfig Load(string path) =>
        Parse(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in Modules)
        {
            var at = $"Module '{m.Id}'";
            if (string.IsNullOrWhiteSpace(m.Type)) errors.Add($"{at}: Type is required.");
            if (string.IsNullOrWhiteSpace(m.Id) || m.Id.Contains('.') || m.Id.Any(char.IsWhiteSpace) || m.Id != m.Id.ToLowerInvariant())
                errors.Add($"{at}: Id is required: lower case, no dots, no spaces (it prefixes every tool name).");
            else if (!ids.Add(m.Id)) errors.Add($"{at}: two modules share this id.");
            foreach (var l in m.Labels.Where(l => !DataLabels.IsValidName(l)))
                errors.Add($"{at}: label '{l}' must be lower-case letters, digits and '-' (up to 32), starting with a letter.");
            foreach (var h in m.Network.Hosts)
                if (!IsValidHostPattern(h)) errors.Add($"{at}: '{h}' is not a host (api.example.com), a wildcard (*.example.com) or an origin (https://host:port).");
            if (m.Isolation is { } iso)
            {
                if (iso.Mode is not ("InProcess" or "Process")) errors.Add($"{at}: Isolation.Mode must be InProcess or Process.");
                if (iso.CallTimeout <= TimeSpan.Zero || iso.StartTimeout <= TimeSpan.Zero) errors.Add($"{at}: Isolation timeouts must be positive.");
                if (iso.MaxMessageBytes < 64 * 1024) errors.Add($"{at}: Isolation.MaxMessageBytes must be at least 65536.");
                var secretVariables = Modules.SelectMany(x => x.Credentials.Values)
                    .SelectMany(c => new[] { c.Env, c.UserNameEnv, c.OAuth2?.ClientIdEnv, c.OAuth2?.ClientSecretEnv }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var v in iso.Environment.Where(secretVariables.Contains))
                    errors.Add($"{at}: Isolation.Environment may not pass '{v}': it holds a credential, which the worker gets only through the Host.");
            }
            foreach (var (name, c) in m.Credentials)
            {
                var cat = $"{at}, credential '{name}'";
                var sources = (c.Env is null ? 0 : 1) + (c.File is null ? 0 : 1) + (c.OAuth2 is null ? 0 : 1);
                if (sources != 1) errors.Add($"{cat}: give exactly one of Env, File or OAuth2.");
                if (c.OAuth2 is not null && c.Kind != CredentialKind.Bearer) errors.Add($"{cat}: an OAuth2 credential is a Bearer token.");
                if (c.Kind == CredentialKind.Header && string.IsNullOrWhiteSpace(c.HeaderName)) errors.Add($"{cat}: Kind Header needs HeaderName.");
                if (c.Kind == CredentialKind.Basic && c.UserName is null && c.UserNameEnv is null) errors.Add($"{cat}: Kind Basic needs UserName or UserNameEnv.");
                if (c.Kind != CredentialKind.Secret)
                {
                    if (string.IsNullOrWhiteSpace(c.Host)) errors.Add($"{cat}: Host is required: the credential is attached only to requests for that host.");
                    else if (!m.Network.Hosts.Any(h => NetworkRules.Covers(h, c.Host)))
                        errors.Add($"{cat}: Host '{c.Host}' is not one of the module's Network.Hosts.");
                }
                if (c.OAuth2 is { } o)
                {
                    if (!Uri.TryCreate(o.TokenUrl, UriKind.Absolute, out var tu) || tu.Scheme != Uri.UriSchemeHttps && !tu.IsLoopback)
                        errors.Add($"{cat}: OAuth2.TokenUrl must be an https URL.");
                    if (string.IsNullOrWhiteSpace(o.ClientIdEnv) || string.IsNullOrWhiteSpace(o.ClientSecretEnv))
                        errors.Add($"{cat}: OAuth2 needs ClientIdEnv and ClientSecretEnv (names of environment variables).");
                    if (o.ClientAuthentication is not ("Body" or "Basic"))
                        errors.Add($"{cat}: OAuth2.ClientAuthentication must be Body or Basic.");
                }
            }
        }
        return errors;
    }

    private static bool IsValidHostPattern(string h)
    {
        if (string.IsNullOrWhiteSpace(h)) return false;
        if (h.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(h, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
                   && u.AbsolutePath == "/" && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.UserInfo);
        var name = h.StartsWith("*.", StringComparison.Ordinal) ? h[2..] : h;
        return name.Length > 0 && Uri.CheckHostName(name) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6 && !name.Contains('*');
    }
}

public sealed class ModuleEntry
{
    /// <summary>The factory's <see cref="IHostModuleFactory.Type"/>.</summary>
    public string Type { get; init; } = "";

    /// <summary>Instance id: prefixes the tool names (<c>id.tool</c>) and names the policy fragment (<c>policy.d/id.json</c>).</summary>
    public string Id { get; init; } = "";

    /// <summary>False switches the module off. Its policy entries are then tolerated, its pending actions expire and its promotions are
    /// cleared (this is also how to uninstall: switch it off, then remove the package and later the entry).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Let this module's unlisted Read tools follow <c>DefaultReadMode</c>. Off by default: the module labels its own tools.</summary>
    public bool AllowDefaultRead { get; init; }

    /// <summary>Data labels for what this instance holds (<c>["finance"]</c> for the ERP database): added to every Read tool's labels.</summary>
    public List<string> Labels { get; init; } = [];

    public ModuleNetwork Network { get; init; } = new();

    public Dictionary<string, CredentialSource> Credentials { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The module's own settings, passed to its factory as is.</summary>
    public JsonElement Config { get; init; }

    /// <summary>Run the module in its own process (<c>"Mode": "Process"</c>). Null or <c>InProcess</c>: inside the Host.</summary>
    public ModuleIsolation? Isolation { get; init; }

    internal bool IsIsolated => Isolation is { Mode: "Process" };
}

/// <summary>
/// Process isolation for one module. The worker is the deployment's own program started with <c>--module-worker</c> (or
/// <see cref="WorkerCommand"/>), behind <see cref="Command"/> when given, so the operator can run it as another user, without network
/// (its HTTP goes through the Host), or in a container: for example <c>[ "systemd-run", "--uid=otail-mod", "-p", "PrivateNetwork=yes",
/// "--pipe", "--wait", "--quiet", "--" ]</c>.
/// </summary>
public sealed class ModuleIsolation
{
    /// <summary><c>InProcess</c> (default) or <c>Process</c>.</summary>
    public string Mode { get; init; } = "InProcess";

    /// <summary>A command the worker is started under (the worker's own command line is appended).</summary>
    public List<string> Command { get; init; } = [];

    /// <summary>The worker program and its arguments, when it is not this program (<c>--module-worker</c> is appended).</summary>
    public List<string>? WorkerCommand { get; init; }

    /// <summary>Environment variables passed to the worker besides the few a runtime needs. Never a credential's variable.</summary>
    public List<string> Environment { get; init; } = [];

    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxMessageBytes { get; init; } = 16 * 1024 * 1024;
}

public sealed class ModuleNetwork
{
    /// <summary>Hosts the module may reach. When its manifest declares hosts, each entry here must fall within one of them (you can
    /// narrow what the module asked for, never widen it).</summary>
    public List<string> Hosts { get; init; } = [];

    /// <summary>Allow names that resolve to private, loopback or link-local addresses (an on-premises server). Off by default.
    /// An IP-literal origin you list explicitly is always allowed.</summary>
    public bool AllowPrivateNetwork { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>Where one credential comes from. Exactly one of <see cref="Env"/>, <see cref="File"/>, <see cref="OAuth2"/>.</summary>
public sealed class CredentialSource
{
    public CredentialKind Kind { get; init; } = CredentialKind.Bearer;

    /// <summary>Name of an environment variable holding the secret (re-read on every use, so rotation needs no restart).</summary>
    public string? Env { get; init; }

    /// <summary>Path of a file holding the secret (trimmed; re-read on every use).</summary>
    public string? File { get; init; }

    public OAuth2ClientCredentials? OAuth2 { get; init; }

    /// <summary>Host pattern the credential is attached to. Requests to any other host never carry it.</summary>
    public string? Host { get; init; }

    public string? HeaderName { get; init; }
    public string? UserName { get; init; }
    public string? UserNameEnv { get; init; }
}

public sealed class OAuth2ClientCredentials
{
    public string TokenUrl { get; init; } = "";
    public string? ClientIdEnv { get; init; }
    public string? ClientSecretEnv { get; init; }
    public string? Scope { get; init; }

    /// <summary>How the client authenticates to the token endpoint: <c>Body</c> (client_id and client_secret in the form, the default) or
    /// <c>Basic</c> (an HTTP Basic header, which some providers such as Reddit require).</summary>
    public string ClientAuthentication { get; init; } = "Body";
}
