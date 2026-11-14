using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Orca;

/// <summary>What the model answered, after the strict check of its output.</summary>
public enum ModelDecision { Approve, Reject, Escalate }

/// <summary>The one JSON shape the model may answer with: exactly <c>decision</c>, <c>checks</c> and <c>reason</c>.</summary>
public sealed record ModelVerdict(ModelDecision Decision, IReadOnlyList<string> Checks, string Reason)
{
    /// <summary>What the model's reason may hold, and what one check may hold. More is cut, not refused.</summary>
    public const int MaxReasonLength = 500;
    public const int MaxChecks = 10;
    public const int MaxCheckLength = 200;

    /// <summary>The verdict in the model's text, or null when the text is not exactly that shape: not JSON, a fenced block, extra or repeated
    /// properties, an unknown decision, checks that are not a list of non-empty strings, or no reason. Null is an abstention at the strict
    /// level; the reviewer records it as an escalation. The reason and checks are cut to their limits.</summary>
    public static ModelVerdict? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { return null; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var names = new HashSet<string>(StringComparer.Ordinal);
            string? decision = null, reason = null;
            List<string>? checks = null;
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name)) return null;
                switch (property.Name)
                {
                    case "decision" when property.Value.ValueKind == JsonValueKind.String:
                        decision = property.Value.GetString();
                        break;
                    case "reason" when property.Value.ValueKind == JsonValueKind.String:
                        reason = property.Value.GetString();
                        break;
                    case "checks" when property.Value.ValueKind == JsonValueKind.Array:
                        checks = [];
                        foreach (var check in property.Value.EnumerateArray())
                        {
                            var value = check.ValueKind == JsonValueKind.String ? check.GetString() : null;
                            if (string.IsNullOrWhiteSpace(value)) return null;
                            checks.Add(value);
                        }
                        break;
                    default:
                        return null;
                }
            }

            if (names.Count != 3 || decision is null || checks is null || string.IsNullOrWhiteSpace(reason)) return null;
            var parsed = decision switch
            {
                "approve" => ModelDecision.Approve,
                "reject" => ModelDecision.Reject,
                "escalate" => ModelDecision.Escalate,
                _ => (ModelDecision?)null,
            };
            if (parsed is not { } d) return null;
            return new ModelVerdict(d, checks.Take(MaxChecks).Select(c => Clip(c, MaxCheckLength)).ToList(), Clip(reason, MaxReasonLength));
        }
    }

    internal static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>The model and its limits, as configured (the <c>AiReviewer</c> section of <c>http.json</c>; the key is not in it).</summary>
public sealed record ModelReviewerOptions(string Model, TimeSpan Timeout);

/// <summary>
/// A reviewer backed by a chat model (section 5.1, R2). It is an <see cref="IReviewer"/>: the runner offers it actions, and what it returns goes
/// through the queue like any other decision, so the AI limits apply to it.
/// <list type="bullet">
/// <item>A pre-scan hit escalates at once, without a call to the model; the hit is recorded with its kind and the field it was in.</item>
/// <item>A timeout or a failed call returns null (the action waits for the next poll). Each such attempt is audited as a kind of outcome, and
/// counted from the audit, so the count survives a restart. The third one escalates: "the model could not be reached".</item>
/// <item>An answer that is not the strict shape escalates at once. An escalation or an approval or rejection with no check named is an
/// escalation too. The model's reason is kept on the stored review, cut to its limit; the audit never holds it.</item>
/// </list>
/// Escalations are returned as <see cref="ReviewDecision.Escalate"/>: the queue records them without changing the action. A model's reject goes
/// to the queue as a reject; with <c>AiMayRefuse</c> off, the queue refuses it and the action stays for a person.
/// </summary>
public sealed class ModelReviewer(OpenTailHost host, string name, IChatClient client, ModelReviewerOptions options) : IReviewer
{
    /// <summary>The version of the system prompt, which is what <see cref="ActionReview.AiPromptVersion"/> reports. It must equal the version line
    /// of the embedded prompt; the prompt's loader checks that.</summary>
    public const string PromptVersion = "model-reviewer.v2";

    /// <summary>The audit event of an attempt the model could not answer (a timeout or a failed call), and how many may fail before the escalation.</summary>
    public const string UnreachableEvent = "model_review_unreachable";
    public const int MaxUnreachable = 3;

    private const string PromptResource = "OpenTail.Orca.Review.Prompts.model-reviewer.v2.txt";

    private static readonly Lazy<string> Prompt = new(LoadPrompt);

    /// <summary>The system prompt the model is given. Not exposed through any tool.</summary>
    public static string SystemPrompt => Prompt.Value;

    public string Name => name;

    public async Task<ActionReview?> ReviewAsync(ActionRecord action, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var risk = host.Catalog.TryGet(action.ToolName, out var tool) ? (ToolRisk?)tool.Descriptor.Risk : null;
        var built = await ReviewPacket.BuildAsync(host, action, risk, now, ct);

        // The pre-scan comes first: its outcome is already fixed, so the model is not called, and the text is not sent to the provider.
        var hits = InjectionScan.ScanFields(built.RequesterText);
        if (hits.Count > 0)
            return Escalation(now, "the pre-scan found " + string.Join("; ", hits.Select(h => $"{h.Kind} in {h.Field}")) + "; the model was not asked", asked: false);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, built.Packet.ToJsonString()),
        };

        string? text;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(options.Timeout);
            var response = await client.GetResponseAsync(messages, new ChatOptions { ModelId = options.Model, Temperature = 0 }, deadline.Token);
            text = response.Text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return await Unreachable(action, "timeout", now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await Unreachable(action, $"call failed ({ex.GetType().Name})", now, ct);
        }

        var verdict = ModelVerdict.Parse(text);
        if (verdict is null) return Escalation(now, "the output was not the strict JSON shape", asked: true);
        if (verdict.Decision == ModelDecision.Escalate) return Escalation(now, verdict.Reason, true, verdict.Checks);
        if (verdict.Checks.Count == 0) return Escalation(now, "no check was named", asked: true);

        return new ActionReview(name, verdict.Decision == ModelDecision.Approve ? ReviewDecision.Approve : ReviewDecision.Reject,
            verdict.Reason, now)
        {
            AiModel = options.Model,
            AiPromptVersion = PromptVersion,
            AiChecks = verdict.Checks,
        };
    }

    /// <summary>A timeout or a failed call: audited as a kind of outcome (never the model's words), and counted from the audit. The attempt that
    /// reaches <see cref="MaxUnreachable"/> escalates; earlier ones return null, so the action waits for a later poll.</summary>
    private async Task<ActionReview?> Unreachable(ActionRecord action, string how, DateTimeOffset now, CancellationToken ct)
    {
        var attempt = Failures(action.Id) + 1;
        await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, UnreachableEvent, action.Id, action.ToolName, Subjects.Ai(name),
            $"attempt {attempt} of {MaxUnreachable}: {how}"), ct);
        return attempt >= MaxUnreachable ? Escalation(now, "the model could not be reached", asked: true) : null;
    }

    /// <summary>The failed attempts recorded for this action by this reviewer. Without an audit reader the count is unknown, so the reviewer
    /// escalates at once rather than call the model again without limit.</summary>
    private int Failures(string actionId) =>
        host.Audit is IAuditReader reader
            ? reader.Read(actionId).Count(e => e.Event == UnreachableEvent && e.Actor == Subjects.Ai(name))
            : MaxUnreachable;

    private ActionReview Escalation(DateTimeOffset now, string why, bool asked, IReadOnlyList<string>? checks = null) =>
        new(name, ReviewDecision.Escalate, ModelVerdict.Clip(why, ModelVerdict.MaxReasonLength), now)
        {
            AiModel = asked ? options.Model : null,
            AiPromptVersion = asked ? PromptVersion : null,
            AiChecks = checks,
        };

    private static string LoadPrompt()
    {
        using var stream = typeof(ModelReviewer).Assembly.GetManifestResourceStream(PromptResource)
            ?? throw new InvalidOperationException("The model reviewer's system prompt is missing from the package.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        if (!text.StartsWith($"version: {PromptVersion}", StringComparison.Ordinal))
            throw new InvalidOperationException($"The model reviewer's prompt is not version {PromptVersion}: its version line does not match.");
        return text;
    }
}
