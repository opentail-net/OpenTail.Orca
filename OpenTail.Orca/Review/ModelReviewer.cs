using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Orca;

/// <summary>What the model answered, after the strict check of its output.</summary>
public enum ModelDecision { Approve, Reject, Escalate }

/// <summary>The one JSON shape the model may answer with: exactly <c>decision</c>, <c>checks</c> and <c>reason</c>.</summary>
public sealed record ModelVerdict(ModelDecision Decision, IReadOnlyList<string> Checks, string Reason)
{
    /// <summary>The verdict in the model's text, or null when the text is not exactly that shape: not JSON, a fenced block, extra or repeated
    /// properties, an unknown decision, checks that are not a list of non-empty strings, or no reason. Null is an abstention.</summary>
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
            return parsed is { } d ? new ModelVerdict(d, checks, reason) : null;
        }
    }
}

/// <summary>The model and its limits, as configured (the <c>AiReviewer</c> section of <c>http.json</c>; the key is not in it).</summary>
public sealed record ModelReviewerOptions(string Model, TimeSpan Timeout);

/// <summary>
/// A reviewer backed by a chat model (section 5.1, R2). It is an <see cref="IReviewer"/>: the runner offers it actions, and its decision goes
/// through the queue like any other, so the AI limits apply to it. It abstains (leaves the action for a person) on a timeout, a failed call,
/// output that is not the strict shape, or a decision with no checks named; it escalates (the same, and audited) when the pre-scan found
/// reviewer-directed text, whatever the model answered. A model's reject goes to the queue as a reject; with <c>AiMayRefuse</c> off, the queue
/// refuses it and the action stays for a person.
/// </summary>
public sealed class ModelReviewer(OpenTailHost host, string name, IChatClient client, ModelReviewerOptions options) : IReviewer
{
    /// <summary>The version of the system prompt, which is what <see cref="ActionReview.AiPromptVersion"/> reports. It must equal the version line of the
    /// embedded prompt; the constructor checks that.</summary>
    public const string PromptVersion = "model-reviewer.v1";

    private const string PromptResource = "OpenTail.Orca.Review.Prompts.model-reviewer.v1.txt";

    private static readonly Lazy<string> Prompt = new(LoadPrompt);

    /// <summary>The system prompt the model is given. Not exposed through any tool.</summary>
    public static string SystemPrompt => Prompt.Value;

    public string Name => name;

    public async Task<ActionReview?> ReviewAsync(ActionRecord action, CancellationToken ct = default)
    {
        var risk = host.Catalog.TryGet(action.ToolName, out var tool) ? (ToolRisk?)tool.Descriptor.Risk : null;
        var packet = await ReviewPacket.BuildAsync(host, action, risk, ct);
        var hits = InjectionScan.Scan(packet);
        packet["preScan"] = new JsonArray(hits.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray());

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, packet.ToJsonString()),
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
            return await Abstain(action, "the model did not answer in time", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await Abstain(action, $"the model call failed ({ex.GetType().Name})", ct);
        }

        var verdict = ModelVerdict.Parse(text);
        if (verdict is null) return await Abstain(action, "the output is not the strict JSON shape", ct);
        if (hits.Count > 0) return await Escalate(action, $"the pre-scan found {string.Join(", ", hits)}", ct);
        if (verdict.Decision == ModelDecision.Escalate || verdict.Checks.Count == 0)
            return await Escalate(action, verdict.Decision == ModelDecision.Escalate ? verdict.Reason : "no check was named", ct);

        return new ActionReview(name, verdict.Decision == ModelDecision.Approve ? ReviewDecision.Approve : ReviewDecision.Reject,
            verdict.Reason, DateTimeOffset.UtcNow)
        {
            AiModel = options.Model,
            AiPromptVersion = PromptVersion,
            AiChecks = verdict.Checks,
        };
    }

    private async Task<ActionReview?> Abstain(ActionRecord action, string why, CancellationToken ct)
    {
        await Audit(action, "model_review_abstained", why, ct);
        return null;
    }

    private async Task<ActionReview?> Escalate(ActionRecord action, string why, CancellationToken ct)
    {
        await Audit(action, "model_review_escalated", why, ct);
        return null;
    }

    private async Task Audit(ActionRecord action, string evt, string why, CancellationToken ct) =>
        await host.Audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, evt, action.Id, action.ToolName, Subjects.Ai(name), why), ct);

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
