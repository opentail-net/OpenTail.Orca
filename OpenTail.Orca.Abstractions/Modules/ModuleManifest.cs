namespace OpenTail.Orca.Abstractions;

/// <summary>What a credential is used as. The Host attaches it to requests to its bound host only.</summary>
public enum CredentialKind
{
    /// <summary><c>Authorization: Bearer &lt;secret&gt;</c>.</summary>
    Bearer = 0,
    /// <summary><c>Authorization: Basic base64(user:secret)</c>.</summary>
    Basic = 1,
    /// <summary>A named header carrying the secret (an API-key header).</summary>
    Header = 2,
    /// <summary>A secret the module uses itself (not HTTP: a database password, an IMAP login). The Host still redacts it everywhere.</summary>
    Secret = 3,
}

/// <summary>A credential a module asks for by name. The operator maps the name to a source; the module never sees where it came from.</summary>
public sealed record CredentialRequirement(string Name, CredentialKind Kind, string Description)
{
    public bool Optional { get; init; }
}

/// <summary>
/// Data a module returns about itself. It is displayed to operators and used to check configuration; it never grants access.
/// Allowed network hosts are the operator's config intersected with <see cref="NetworkHosts"/>, never this list alone.
/// </summary>
public sealed record ModuleManifest
{
    /// <summary>Version of this manifest format.</summary>
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }

    /// <summary>Hosts the module talks to: <c>api.github.com</c>, <c>*.example.com</c>, or an origin such as <c>http://127.0.0.1:5123</c>.</summary>
    public IReadOnlyList<string> NetworkHosts { get; init; } = [];

    public IReadOnlyList<CredentialRequirement> Credentials { get; init; } = [];

    /// <summary>Plain statements an operator should read before enabling the module ("reads issue text written by anyone").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>A suggested policy fragment (JSON with only a <c>Tools</c> object). Never applied without the operator adopting it.</summary>
    public string? SuggestedPolicyJson { get; init; }

    /// <summary>One or two sentences the Host adds to its MCP server instructions while any of the module's tools is advertised
    /// ("Before handling an email, call skills.find."). Plain guidance for the model; it grants nothing.</summary>
    public string? ServerInstructions { get; init; }

    public static ModuleManifest Minimal(string id) => new() { Id = id };
}
