using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenTail.Modules.OpenApi;

/// <summary>
/// An integration profile: which operations of an OpenAPI document become tools, under which names, with which risk, which arguments
/// the model may set and within which limits, what a reviewer sees, and how errors are reported. It describes; it never computes. A
/// profile cannot widen anything: hosts, credentials, policy and taint are still the operator's and the Host's. See MCP-HOST-V4-PLAN.md.
/// </summary>
public sealed partial class OpenApiProfile
{
    public const int CurrentFormat = 1;

    /// <summary>Format version of this document.</summary>
    public int Profile { get; init; }
    public string Name { get; init; } = "";
    public string? Version { get; init; }
    public string Spec { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string? Server { get; init; }

    /// <summary>Hosts the profile talks to; the operator's Network.Hosts must fall within them.</summary>
    public List<string> Hosts { get; init; } = [];

    public ProfileCredential? Credential { get; init; }

    /// <summary>Responses are outside content (taint the caller) unless a tool says otherwise. Default true.</summary>
    public bool Untrusted { get; init; } = true;

    public ProfileErrors Errors { get; init; } = new();
    public Dictionary<string, ProfileTool> Tools { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Settings the operator gives in modules.json next to <c>Profile</c> (allow-lists that <c>AllowedFrom</c> refers to).</summary>
    public Dictionary<string, ProfileSetting> Config { get; init; } = new(StringComparer.Ordinal);

    public List<string> Notes { get; init; } = [];

    /// <summary>Where the profile was read from (relative spec paths resolve against it).</summary>
    [JsonIgnore] public string Directory { get; private set; } = "";

    /// <summary>Where the profile came from, for operators: a file path or <c>package:Assembly/name</c>.</summary>
    [JsonIgnore] public string Source { get; private set; } = "";

    /// <summary>Reads a file next to the profile (the spec), wherever the profile came from.</summary>
    [JsonIgnore] internal Func<string, string> ReadRelative { get; private set; } = _ => throw new InvalidOperationException("No source.");

    public const string PackagePrefix = "package:";

    /// <summary>
    /// Opens <c>Config.Profile</c>: a file path (relative to <paramref name="baseDirectory"/>), or <c>package:Assembly.Name/profile</c> for a
    /// profile embedded in an assembly the deployment references (resources named <c>profiles/&lt;profile&gt;/&lt;file&gt;</c>). Like
    /// modules, packaged profiles come only from what the deployment was built with; nothing is loaded from a folder of packages.
    /// </summary>
    public static OpenApiProfile Open(string reference, string baseDirectory)
    {
        if (!reference.StartsWith(PackagePrefix, StringComparison.Ordinal))
            return Load(Path.IsPathRooted(reference) ? reference : Path.GetFullPath(Path.Combine(baseDirectory, reference)));

        var rest = reference[PackagePrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) throw new ArgumentException($"'{reference}' must look like package:Assembly.Name/profile.");
        var (assemblyName, name) = (rest[..slash], rest[(slash + 1)..]);
        System.Reflection.Assembly assembly;
        try { assembly = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(assemblyName)); }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            throw new ArgumentException($"Profile package {assemblyName} is not referenced by this deployment: add the package to the project.");
        }
        string Read(string relative)
        {
            var resource = $"profiles/{name}/{relative.Replace('\\', '/')}";
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                var available = assembly.GetManifestResourceNames().Where(r => r.StartsWith("profiles/", StringComparison.Ordinal))
                    .Select(r => r.Split('/')[1]).Distinct().Order();
                throw new ArgumentException($"{assemblyName} has no {resource} (profiles in it: {string.Join(", ", available)}).");
            }
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        var p = Parse(Read(name + ".json"), reference);
        p.Source = reference;
        p.ReadRelative = Read;
        return p;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static OpenApiProfile Load(string path)
    {
        if (!File.Exists(path)) throw new ArgumentException($"Profile {path} does not exist.");
        var p = Parse(File.ReadAllText(path), path);
        p.Directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        p.Source = Path.GetFullPath(path);
        var dir = p.Directory;
        p.ReadRelative = relative => File.ReadAllText(Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Combine(dir, relative)));
        return p;
    }

    public static OpenApiProfile Parse(string json, string where = "profile")
    {
        OpenApiProfile p;
        try { p = JsonSerializer.Deserialize<OpenApiProfile>(json, Options) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { throw new ArgumentException($"{where}: not a valid profile: {ex.Message}"); }
        p.Validate(where);
        return p;
    }

    private void Validate(string where)
    {
        var errors = new List<string>();
        if (Profile != CurrentFormat) errors.Add($"\"Profile\" must be {CurrentFormat} (this Host reads format {CurrentFormat}).");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Name is required.");
        if (string.IsNullOrWhiteSpace(Spec)) errors.Add("Spec is required.");
        if (Hosts.Count == 0) errors.Add("Hosts must list the hosts the profile talks to.");
        if (Tools.Count == 0) errors.Add("Tools must name at least one tool.");
        foreach (var code in Errors.Kinds.Keys.Where(k => !int.TryParse(k, out var n) || n is < 400 or > 599))
            errors.Add($"Errors.Kinds: '{code}' is not an HTTP error status (400 to 599).");
        if (Server is not null && !Uri.TryCreate(Server, UriKind.Absolute, out _)) errors.Add("Server must be an absolute URL.");
        if (Credential is { } c && string.IsNullOrWhiteSpace(c.Name)) errors.Add("Credential.Name is required.");
        if (Credential is { Kind: CredentialKind.Header, HeaderName: null or "" }) errors.Add("Credential Kind Header needs HeaderName.");

        foreach (var (name, t) in Tools)
        {
            var at = $"Tools.{name}";
            if (!ToolName().IsMatch(name)) errors.Add($"{at}: tool names are lower case letters, digits and _.");
            if (string.IsNullOrWhiteSpace(t.Operation)) errors.Add($"{at}: Operation is required.");
            if (t.Risk is null) errors.Add($"{at}: Risk is required (Read, Write, ExternalEffect or Destructive): a profile states it, it is never guessed.");
            if (t.Risk is not null and not ToolRisk.Read && string.IsNullOrWhiteSpace(t.Preview))
                errors.Add($"{at}: a {t.Risk} tool needs a Preview template so a reviewer can see what it will do.");
            foreach (var (pn, r) in t.Parameters.Concat(t.Body)) r.Validate($"{at}.{pn}", Config, errors);
            t.Response?.Validate($"{at}.Response", errors);
            t.Query?.Validate($"{at}.Query", Config, errors);
            if (t.Query is not null && t.Risk is not null and not ToolRisk.Read) errors.Add($"{at}: Query is for Read tools (searches).");
            if (t.Preview is not null)
                foreach (Match m in PreviewTemplate.Placeholder().Matches(t.Preview))
                    if (m.Groups["cut"].Success && !int.TryParse(m.Groups["cut"].Value, out _)) errors.Add($"{at}.Preview: '{m.Value}' has a bad cut length.");
        }
        foreach (var (k, s) in Config)
            if (s.Type is not ("string" or "string[]")) errors.Add($"Config.{k}: Type must be string or string[].");
        if (errors.Count > 0) throw new ArgumentException($"{where}: " + string.Join(" ", errors));
    }

    /// <summary>True when some tool replaces authors by pseudonyms (the module then asks for the optional <c>pseudonym-key</c>).</summary>
    [JsonIgnore] public bool Pseudonymises => Tools.Values.Any(t => t.Response is { } r && (r.Pseudonymise.Count > 0 || r.PseudonymiseMentions.Count > 0));

    public const string PseudonymKeyCredential = "pseudonym-key";

    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$")]
    private static partial Regex ToolName();
}

public sealed class ProfileCredential
{
    public string Name { get; init; } = "";
    public CredentialKind Kind { get; init; } = CredentialKind.Bearer;
    public string? HeaderName { get; init; }
    public string? Description { get; init; }
    public bool Optional { get; init; }
}

public sealed class ProfileErrors
{
    /// <summary>HTTP status codes that mean "try again later" (the call fails as retryable, honouring Retry-After).</summary>
    public List<int> Retryable { get; init; } = [429, 500, 502, 503, 504];

    /// <summary>A short message per status code. The remote body is never shown.</summary>
    public Dictionary<string, string> Messages { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The failure kind per status code, where this API uses a status differently from the default (for example
    /// <c>"404": "Auth"</c> for an API that answers 404 for something the caller may not see). Overrides <see cref="Retryable"/>.</summary>
    public Dictionary<string, ToolFailureKind> Kinds { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The kind of an HTTP failure: the profile's own entry first, then <see cref="Retryable"/> (transient), then the defaults:
    /// 401 and 403 are credentials, 507 the environment, other 4xx the request, other 5xx a bug.</summary>
    public ToolFailureKind KindOf(int status) =>
        Kinds.TryGetValue(status.ToString(System.Globalization.CultureInfo.InvariantCulture), out var k) ? k
        : Retryable.Contains(status) ? ToolFailureKind.Transient
        : DefaultKind(status);

    public static ToolFailureKind DefaultKind(int status) => status switch
    {
        408 or 429 or 502 or 503 or 504 => ToolFailureKind.Transient,
        401 or 403 or 407 => ToolFailureKind.Auth,
        507 => ToolFailureKind.Environment,
        >= 400 and < 500 => ToolFailureKind.Contract,
        _ => ToolFailureKind.Bug,
    };
}

public sealed class ProfileSetting
{
    public string Type { get; init; } = "string[]";
    public bool Required { get; init; }
    public string? Description { get; init; }
}

public sealed class ProfileTool
{
    /// <summary>The spec's operationId (or the generated name for an operation without one).</summary>
    public string Operation { get; init; } = "";
    public string? Description { get; init; }
    public ToolRisk? Risk { get; init; }

    /// <summary>Overrides the profile's <see cref="OpenApiProfile.Untrusted"/> for this tool.</summary>
    public bool? Untrusted { get; init; }

    /// <summary>Rules per path or query parameter; <c>"*"</c> applies to every parameter not named.</summary>
    public Dictionary<string, ParameterRule> Parameters { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Rules per top-level property of the JSON body; <c>"*"</c> as above.</summary>
    public Dictionary<string, ParameterRule> Body { get; init; } = new(StringComparer.Ordinal);

    /// <summary>What a reviewer reads: <c>"Comment on {id}: {body.text|cut:200}"</c>. Required for every tool that is not Read.</summary>
    public string? Preview { get; init; }

    /// <summary>What of the answer reaches the model. Null: the answer as the API sends it (capped by MaxResponseBytes).</summary>
    public ResponseRule? Response { get; init; }

    /// <summary>Typed filters the builder turns into one filter expression (JQL, OData, SOQL, Zendesk, Freshdesk). See <see cref="ProfileQuery"/>.</summary>
    public ProfileQuery? Query { get; init; }
}

/// <summary>What the model may send for one parameter or body property.</summary>
public sealed class ParameterRule
{
    /// <summary>Always sent with this value; not shown to the model, and the model may not set it.</summary>
    public JsonElement? Fixed { get; init; }

    /// <summary>Sent when the model leaves it out.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>Overrides the spec's required flag.</summary>
    public bool? Required { get; init; }

    public string? Pattern { get; init; }
    public int? MaxLength { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public List<string>? Enum { get; init; }

    /// <summary>The value must be one of the operator's list <c>Config.&lt;name&gt;</c> (allowed repositories, tables, channels).</summary>
    public string? AllowedFrom { get; init; }

    /// <summary>The model may not send it, and it is not shown.</summary>
    public bool Forbidden { get; init; }

    public string? Description { get; init; }

    [JsonIgnore] internal Regex? Compiled { get; private set; }

    internal bool Hidden => Forbidden || Fixed is not null;

    internal void Validate(string at, Dictionary<string, ProfileSetting> config, List<string> errors)
    {
        if (Forbidden && (Fixed is not null || Default is not null || Required == true)) errors.Add($"{at}: Forbidden cannot be combined with Fixed, Default or Required.");
        if (Fixed is not null && Default is not null) errors.Add($"{at}: Fixed and Default cannot both be set.");
        if (Pattern is not null)
        {
            try { Compiled = new Regex(Pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException ex) { errors.Add($"{at}: Pattern is not a valid regular expression ({ex.Message})."); }
        }
        if (AllowedFrom is not null)
        {
            var key = AllowedFrom.StartsWith("Config.", StringComparison.Ordinal) ? AllowedFrom[7..] : AllowedFrom;
            if (!config.TryGetValue(key, out var s) || s.Type != "string[]") errors.Add($"{at}: AllowedFrom '{AllowedFrom}' must name a string[] setting under Config.");
        }
        if (Min is not null && Max is not null && Min > Max) errors.Add($"{at}: Min is larger than Max.");
    }

    /// <summary>Checks one value the model sent. Throws <see cref="ArgumentException"/> (the call is refused, before review when previewed).</summary>
    internal void Check(string name, JsonElement value, IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
        if (MaxLength is { } max && text is not null && text.Length > max) throw new ArgumentException($"'{name}' is limited to {max} characters.");
        if (Enum is { Count: > 0 } allowed && (text is null || !allowed.Contains(text, StringComparer.Ordinal)))
            throw new ArgumentException($"'{name}' must be one of {string.Join(", ", allowed)}.");
        if (Compiled is { } re && (text is null || !re.IsMatch(text))) throw new ArgumentException($"'{name}' does not have the expected form.");
        if ((Min is not null || Max is not null) && value.ValueKind == JsonValueKind.Number)
        {
            var n = value.GetDouble();
            if (Min is { } lo && n < lo) throw new ArgumentException($"'{name}' must be at least {lo.ToString(CultureInfo.InvariantCulture)}.");
            if (Max is { } hi && n > hi) throw new ArgumentException($"'{name}' must be at most {hi.ToString(CultureInfo.InvariantCulture)}.");
        }
        else if (Min is not null || Max is not null)
            throw new ArgumentException($"'{name}' must be a number.");
        if (AllowedFrom is { } from)
        {
            var key = from.StartsWith("Config.", StringComparison.Ordinal) ? from[7..] : from;
            // Fail closed: with no list configured, nothing is allowed (an operator who forgot the list must not get "anything").
            var list = settings.GetValueOrDefault(key) ?? [];
            if (list.Count == 0) throw new ArgumentException($"'{name}' must be one of Config.{key}, and the operator has not configured any.");
            if (text is null || !list.Contains(text, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"'{name}' must be one of the configured {key} ({string.Join(", ", list)}).");
        }
    }

    /// <summary>The model-facing schema for this parameter: the spec's, narrowed by the rule.</summary>
    internal void Narrow(System.Text.Json.Nodes.JsonObject schema, IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        if (Description is not null) schema["description"] = Description;
        if (MaxLength is { } ml) schema["maxLength"] = ml;
        if (Pattern is not null) schema["pattern"] = Pattern;
        if (Min is { } lo) schema["minimum"] = lo;
        if (Max is { } hi) schema["maximum"] = hi;
        if (Default is { } d) schema["default"] = System.Text.Json.Nodes.JsonNode.Parse(d.GetRawText());
        var values = Enum is { Count: > 0 } e ? e
            : AllowedFrom is { } from && settings.GetValueOrDefault(from.StartsWith("Config.", StringComparison.Ordinal) ? from[7..] : from) is { Count: > 0 } l ? l.ToList()
            : null;
        if (values is not null) schema["enum"] = new System.Text.Json.Nodes.JsonArray(values.Select(v => (System.Text.Json.Nodes.JsonNode?)v).ToArray());
    }
}

/// <summary>Renders a profile's preview: <c>{name}</c>, <c>{body.field}</c>, <c>{name|cut:N}</c>. Missing values render as nothing.</summary>
internal static partial class PreviewTemplate
{
    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m =>
        {
            var v = values.GetValueOrDefault(m.Groups["name"].Value) ?? "";
            if (m.Groups["cut"].Success && int.TryParse(m.Groups["cut"].Value, out var n) && v.Length > n) v = v[..n] + "…";
            return v.Replace('\r', ' ').Replace('\n', ' ');
        });

    [GeneratedRegex(@"\{(?<name>[A-Za-z0-9_.\-]+)(\|cut:(?<cut>[^}]*))?\}")]
    internal static partial Regex Placeholder();
}
