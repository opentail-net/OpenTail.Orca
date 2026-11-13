using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

// The Host carries one built-in module (access.request), so its assembly states the contract like any module's.
[assembly: HostContractVersion(HostContract.Version)]

namespace OpenTail.Orca;

/// <summary>A temporary grant: one tool for one identity, until a time or for a number of calls, whichever ends first.</summary>
public sealed record AccessGrant(string Id, string Subject, string Tool, DateTimeOffset Until, int CallsLeft, string GrantedBy, string Reason, DateTimeOffset At);

public interface IAccessGrantStore
{
    void Add(AccessGrant grant);
    IReadOnlyList<AccessGrant> Active(string subject, DateTimeOffset now);
    IReadOnlyList<AccessGrant> All(DateTimeOffset now);

    /// <summary>Uses one call of an active grant for this tool; returns it (with the calls left after) or null when there is none.</summary>
    AccessGrant? Use(string subject, string tool, DateTimeOffset now);

    bool Revoke(string id);
}

public sealed class InMemoryAccessGrantStore : IAccessGrantStore
{
    private readonly ConcurrentDictionary<string, AccessGrant> _grants = new(StringComparer.Ordinal);

    public void Add(AccessGrant grant) => _grants[grant.Id] = grant;

    public IReadOnlyList<AccessGrant> Active(string subject, DateTimeOffset now) =>
        _grants.Values.Where(g => g.Subject == subject && g.Until > now && g.CallsLeft > 0).ToList();

    public IReadOnlyList<AccessGrant> All(DateTimeOffset now) => _grants.Values.Where(g => g.Until > now && g.CallsLeft > 0).OrderBy(g => g.At).ToList();

    public AccessGrant? Use(string subject, string tool, DateTimeOffset now)
    {
        lock (_grants)
        {
            var g = Active(subject, now).Where(x => x.Tool == tool).OrderBy(x => x.Until).FirstOrDefault();
            if (g is null) return null;
            var used = g with { CallsLeft = g.CallsLeft - 1 };
            _grants[g.Id] = used;
            return used;
        }
    }

    public bool Revoke(string id) => _grants.TryRemove(id, out _);
}

/// <summary>
/// Temporary access for one task (smaller extensions design). A profile names the tools its agent may ask for (<c>Requestable</c>); the
/// agent calls <c>access.request</c>, which a person decides like any queued action; approval grants the tool to that identity for a
/// time or a number of calls, whichever ends first. Never administration tools. A grant only adds the tool to the profile: its policy
/// still decides (a granted send is still queued if policy says so).
/// </summary>
public sealed class AccessGrants(IAccessGrantStore store, TimeProvider clock)
{
    public const string RequestTool = "access.request";
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(8);
    public const int MaxCalls = 50;

    public IReadOnlyList<AccessGrant> Active(string subject) => store.Active(subject, clock.GetUtcNow());
    public IReadOnlyList<AccessGrant> All() => store.All(clock.GetUtcNow());
    public bool Has(string subject, string tool) => Active(subject).Any(g => g.Tool == tool);
    public AccessGrant? Use(string subject, string tool) => store.Use(subject, tool, clock.GetUtcNow());
    public bool Revoke(string id) => store.Revoke(id);

    internal AccessGrant Grant(string subject, string tool, TimeSpan duration, int calls, string by, string reason)
    {
        var now = clock.GetUtcNow();
        var g = new AccessGrant("grant_" + Guid.NewGuid().ToString("N")[..16], subject, tool, now + duration, calls, by, reason, now);
        store.Add(g);
        return g;
    }
}

/// <summary>The built-in module holding <c>access.request</c>. Registered when a profile has requestable tools.</summary>
internal sealed class AccessModule(AccessGrants grants) : IHostModule
{
    public string Id => "access";

    public void Register(IToolRegistry registry)
    {
        const string description = "Ask a person for temporary use of one tool your job does not normally need, with the reason. " +
                                   "If they agree, the tool is available to you for a while or a few calls.";
        registry.Add(new ToolDescriptor(AccessGrants.RequestTool, description, ToolRisk.Write, Id),
            AIFunctionFactory.Create(Request, AccessGrants.RequestTool, description), new Preview());
    }

    private object Request(
        [Description("The tool you need, by its exact name.")] string tool,
        [Description("Why, for the person who decides: the task and why your usual tools are not enough.")] string reason,
        [Description("Minutes of access to ask for (default 60, at most 480).")] int minutes = 60,
        [Description("Calls to ask for (default 3, at most 50).")] int calls = 3,
        AIFunctionArguments? arguments = null)
    {
        var subject = arguments?.Context?.TryGetValue(HostContract.RequesterContextKey, out var s) == true ? s as string : null;
        if (string.IsNullOrEmpty(subject)) throw new InvalidOperationException("No requester to grant to.");
        var (duration, n) = Limits(minutes, calls);
        // The approving reviewer is on the action; the grant records the approval as its source.
        var g = grants.Grant(subject, tool, duration, n, "approved request", reason);
        return new { granted = tool, until = g.Until, calls = g.CallsLeft, grantId = g.Id };
    }

    internal static (TimeSpan Duration, int Calls) Limits(int minutes, int calls) =>
        (TimeSpan.FromMinutes(Math.Clamp(minutes, 1, (int)AccessGrants.MaxDuration.TotalMinutes)), Math.Clamp(calls, 1, AccessGrants.MaxCalls));

    private sealed class Preview : IPreviewableTool
    {
        public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int I(string n, int d) => r.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : d;
            var tool = S("tool") ?? throw new ArgumentException("tool is required.");
            var reason = S("reason");
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required: the person deciding reads it first.");
            var (duration, calls) = Limits(I("minutes", 60), I("calls", 3));
            return Task.FromResult(new PreviewResult(
                $"Grant {tool} for {duration.TotalMinutes:0} minutes or {calls} call(s), whichever ends first. Reason: {reason}"));
        }
    }
}
