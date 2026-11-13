namespace OpenTail.Orca.Abstractions;

/// <summary>What kind of failure a tool call ended in. The module says what went wrong; the Host decides what to do about it
/// (retry, pause the route, send it to a person). A failure nobody classified is <see cref="Bug"/>, which is never retried.</summary>
public enum ToolFailureKind
{
    /// <summary>Temporary: a timeout, rate limiting (429), the service briefly unavailable (502, 503, 504). Trying later can work.</summary>
    Transient = 0,

    /// <summary>The environment: disk full, quota used up, storage or a database unreachable. Needs someone to fix the cause.</summary>
    Environment = 1,

    /// <summary>The request or the contract: a bad request (400, 422), no such record (404), a conflict (409), a schema mismatch.
    /// Retrying the same call cannot work.</summary>
    Contract = 2,

    /// <summary>Credentials: not signed in (401), not allowed (403), an expired or revoked credential. A security question first.</summary>
    Auth = 3,

    /// <summary>Refused by a rule: the Host's policy, a guardrail, the remote system's own policy.</summary>
    Policy = 4,

    /// <summary>The call may or may not have taken effect (interrupted mid-run). A person checks before anything runs again.</summary>
    UnknownOutcome = 5,

    /// <summary>Unexpected: an exception the module did not classify. Never retried; the module's owner looks at it.</summary>
    Bug = 6,
}

/// <summary>
/// Thrown by a tool to say what kind of failure this is (<see cref="ToolFailureKind"/>). Use <see cref="ToolTransientException"/>
/// for temporary failures; it is the same as kind <see cref="ToolFailureKind.Transient"/>. The message is shown to the model and
/// reviewers (after redaction), so keep secrets, argument values and remote response bodies out of it.
/// </summary>
public sealed class ToolFailureException(ToolFailureKind kind, string message, string? code = null, TimeSpan? retryAfter = null) : Exception(message)
{
    public ToolFailureKind Kind { get; } = kind;

    /// <summary>A short, stable code for the failure (for example <c>http_403</c>); null lets the Host choose one from the kind.</summary>
    public string? Code { get; } = code;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
