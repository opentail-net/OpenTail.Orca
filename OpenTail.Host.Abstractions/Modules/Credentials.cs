namespace OpenTail.Host.Abstractions;

/// <summary>A secret handed to a module. <see cref="ToString"/> never prints it.</summary>
public sealed class Credential(CredentialKind kind, string secret)
{
    public CredentialKind Kind { get; } = kind;
    public string Secret { get; } = secret;

    /// <summary>For <see cref="CredentialKind.Basic"/>.</summary>
    public string? UserName { get; init; }

    /// <summary>For <see cref="CredentialKind.Header"/>.</summary>
    public string? HeaderName { get; init; }

    /// <summary>When a token expires (OAuth); null for static secrets.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public override string ToString() => $"Credential({Kind}, [redacted])";
}

/// <summary>Credentials for one module, by the names the operator mapped to it. Asking for any other name fails.</summary>
public interface ICredentialProvider
{
    /// <summary>Names this module may ask for.</summary>
    IReadOnlyCollection<string> Names { get; }

    ValueTask<Credential> GetAsync(string name, CancellationToken ct = default);

    /// <summary>The credential was refused (HTTP 401): drop any cached value so the next call re-reads or refreshes it.</summary>
    void Invalidate(string name);
}

/// <summary>
/// HTTP clients for a module. Requests may only go to the hosts the operator allowed for the module (intersected with its manifest),
/// over https unless the operator allowed a plain-http origin, to public addresses unless the operator allowed private ones. Redirects
/// are re-checked; a credential is attached only to requests for the host it is bound to.
/// This is a guard rail against mistakes and injected URLs, not containment: code running in the Host process can always open its own sockets.
/// </summary>
public interface IModuleHttpClientFactory
{
    /// <param name="credentialName">A credential mapped to this module, attached to requests for its bound host. Null: none.</param>
    HttpClient Create(string? credentialName = null);
}
