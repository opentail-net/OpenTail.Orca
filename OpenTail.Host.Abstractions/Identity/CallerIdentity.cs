namespace OpenTail.Host.Abstractions;

public enum CallerKind
{
    /// <summary>The single local operator of a stdio deployment.</summary>
    Local = 0,
    User = 1,
    Agent = 2,
}

/// <summary>
/// Who is calling. <see cref="Subject"/> is a namespaced, stable id (see <see cref="Subjects"/>): different sign-in
/// mechanisms can never collide, so "alice" the reviewer token and a JWT whose <c>sub</c> is "alice" are different people
/// until the operator says otherwise.
/// </summary>
public sealed record CallerIdentity(string Subject, string DisplayName, CallerKind Kind = CallerKind.User)
{
    public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The agent profile bound to the credential this caller signed in with (an API key's <c>Profile</c>), or null for none.
    /// Set by the Host from configuration, never by the caller: it limits which tools the caller sees and may call.</summary>
    public string? Profile { get; init; }

    public static CallerIdentity Local { get; } = new(Subjects.Local, "local operator", CallerKind.Local);
}

/// <summary>Constructors for namespaced subject strings.</summary>
public static class Subjects
{
    public const string Local = "local";

    public static string Admin(string name) => "admin:" + name;
    public static string Jwt(string issuer, string subject) => $"jwt:{issuer}|{subject}";
    public static string Key(string name) => "key:" + name;
    public static string Ai(string name) => "ai:" + name;

    /// <summary>True for subjects that identify a non-human reviewer.</summary>
    public static bool IsAi(string subject) => subject.StartsWith("ai:", StringComparison.Ordinal);
}
