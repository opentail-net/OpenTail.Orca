using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.A2A;

/// <summary>
/// The Host module that holds the A2A tools, registered only when A2A is enabled:
/// <c>a2a.submit</c> (a partner's request; it reads outside text, so the session is tainted; an approved request becomes a work item), and
/// <c>a2a.reply</c> (the answer to the partner, an exit: the policy must queue it for a person, or deny it). The bodies run through
/// <paramref name="work"/>, which the front door binds once the host exists (the tools are registered before that).
/// </summary>
internal sealed class A2ASubmitModule(A2aOptions config, Func<A2AWork> work) : IHostModule
{
    /// <summary>The submit tool's name, as the policy file names it.</summary>
    public const string Tool = "a2a.submit";

    /// <summary>The reply tool's name, as the policy file names it.</summary>
    public const string ReplyTool = "a2a.reply";

    public string Id => "a2a";

    public void Register(IToolRegistry registry)
    {
        registry.Add(
            new ToolDescriptor(Tool, "A request from an A2A partner: outside text, queued for the policy's decision. An approved request becomes a work item.",
                ToolRisk.Write, Id)
            {
                ReadsUntrusted = true,
            },
            AIFunctionFactory.Create(SubmitBody, Tool, "A request from an A2A partner."),
            new SubmitPreview(config));
        registry.Add(
            new ToolDescriptor(ReplyTool, "The answer to an A2A partner's task: an exit, always reviewed or denied by the policy.", ToolRisk.ExternalEffect, Id),
            AIFunctionFactory.Create(ReplyBody, ReplyTool, "The answer to an A2A partner's task."),
            new ReplyPreview(config));
    }

    private Task<object> SubmitBody(string partner, string taskId, string skill, string goal, string inputs, AIFunctionArguments? arguments = null) =>
        work().SubmitAsync(partner, taskId, skill, goal, inputs, arguments);

    private object ReplyBody(string partner, string taskId, string text, AIFunctionArguments? arguments = null) =>
        work().Reply(partner, taskId, arguments);

    private static string PartnerName(A2aOptions config, string partnerId) =>
        config.Partners.FirstOrDefault(p => string.Equals(p.Id, partnerId, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? throw new ArgumentException("The partner is not listed.");

    private static JsonNode ParseArguments(string argumentsJson)
    {
        try { return JsonNode.Parse(argumentsJson) ?? new JsonObject(); }
        catch (JsonException) { throw new ArgumentException("The request is malformed."); }
    }

    /// <summary>What a reviewer sees: the partner's display name, the skill, and the goal quoted as outside text.</summary>
    private sealed class SubmitPreview(A2aOptions config) : IPreviewableTool
    {
        public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            var args = ParseArguments(argumentsJson);
            var partnerName = PartnerName(config, args["partner"]?.GetValue<string>() ?? "");
            var skillId = args["skill"]?.GetValue<string>() ?? "";
            var goal = args["goal"]?.GetValue<string>() ?? "";
            var skillName = config.Skills.TryGetValue(skillId, out var skill) ? skill.Name : skillId;
            return Task.FromResult(new PreviewResult(
                $"{partnerName} asks for \"{skillName}\". Their text, not checked: \"{goal}\""));
        }
    }

    /// <summary>What a reviewer sees: the partner's display name and the exact text the partner will receive.</summary>
    private sealed class ReplyPreview(A2aOptions config) : IPreviewableTool
    {
        public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            var args = ParseArguments(argumentsJson);
            var partnerName = PartnerName(config, args["partner"]?.GetValue<string>() ?? "");
            var text = args["text"]?.GetValue<string>() ?? "";
            return Task.FromResult(new PreviewResult($"The answer to {partnerName}, exactly as it will be sent: \"{text}\""));
        }
    }
}
