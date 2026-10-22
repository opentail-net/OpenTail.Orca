using System.Runtime.CompilerServices;

namespace OpenTail.Modules.Email;

/// <summary>
/// An inbox built when a tool runs, not when the module is created, so a factory never reads credentials at startup (describing and
/// explaining work without secrets) and a rotated secret is picked up on the next call. Provider packages use it.
/// </summary>
public sealed class DeferredInbox(Func<CancellationToken, ValueTask<IInboxAdapter>> create) : IInboxAdapter
{
    public async IAsyncEnumerable<InboxEmailRecord> FetchRecentAsync(InboxFetchOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inbox = await create(cancellationToken);
        await foreach (var m in inbox.FetchRecentAsync(options, cancellationToken)) yield return m;
    }
}

/// <summary>A sender built when a tool runs (see <see cref="DeferredInbox"/>).</summary>
public sealed class DeferredSender(Func<CancellationToken, ValueTask<IEmailSender>> create) : IEmailSender
{
    public async Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default) => await (await create(ct)).SendAsync(email, ct);
}

/// <summary>A sender for a read-only instance: every call fails. The send tool is not registered then, so it is never reached.</summary>
public sealed class NoSender : IEmailSender
{
    public Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default) => throw new NotSupportedException("Sending is off for this module.");
}

