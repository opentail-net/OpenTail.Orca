using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenTail.Host.Http;

namespace OpenTail.Host.A2A;

/// <summary>
/// The Host module that holds the A2A tool <c>a2a.submit</c>, registered only when A2A is enabled. The tool reads outside text
/// (<c>ReadsUntrusted</c>), so the session that submitted it is tainted. In this slice its body only records that it ran; the work item
/// comes with the next slice.
/// </summary>
internal sealed class A2ASubmitModule(A2aOptions config) : IHostModule
{
    /// <summary>The tool's name, as the policy file names it.</summary>
    public const string Tool = "a2a.submit";

    public string Id => "a2a";

    public void Register(IToolRegistry registry)
    {
        registry.Add(
            new ToolDescriptor(Tool, "A request from an A2A partner: outside text, queued for the policy's decision.", ToolRisk.Write, Id)
            {
                ReadsUntrusted = true,
            },
            AIFunctionFactory.Create((string partner, string taskId, string skill, string goal, string inputs) => "{\"accepted\":true}",
                Tool, "A request from an A2A partner."),
            new SubmitPreview(config));
    }

    /// <summary>What a reviewer sees: the partner's display name, the skill, and the goal quoted as outside text.</summary>
    private sealed class SubmitPreview(A2aOptions config) : IPreviewableTool
    {
        public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
        {
            JsonNode? args;
            try { args = JsonNode.Parse(argumentsJson); }
            catch (JsonException) { throw new ArgumentException("The request is malformed."); }
            var partnerId = args?["partner"]?.GetValue<string>() ?? "";
            var skillId = args?["skill"]?.GetValue<string>() ?? "";
            var goal = args?["goal"]?.GetValue<string>() ?? "";
            var partnerName = config.Partners.FirstOrDefault(p => string.Equals(p.Id, partnerId, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? throw new ArgumentException("The partner is not listed.");
            var skillName = config.Skills.TryGetValue(skillId, out var skill) ? skill.Name : skillId;
            return Task.FromResult(new PreviewResult(
                $"{partnerName} asks for \"{skillName}\". Their text, not checked: \"{goal}\""));
        }
    }
}
