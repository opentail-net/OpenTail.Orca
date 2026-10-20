namespace OpenTail.Host.Abstractions;

/// <summary>
/// Thrown by a tool when the failure is temporary (rate limited, service briefly unavailable) and trying again later can work.
/// The action fails as retryable, so a reviewer can retry it; the Host never sleeps or retries on its own inside the execution lease.
/// The message is shown to the model and reviewers (after redaction), so keep secrets and argument values out of it.
/// </summary>
public sealed class ToolTransientException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
