using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OpenTail.Core.Tools.Abstractions;

/// <summary>
/// Wraps an <see cref="IOpenTailTool"/> with per-call input/output guardrails.
/// Use <see cref="OpenTailToolGuardExtensions.WithGuards"/> to create instances.
/// </summary>
public sealed class GuardedToolWrapper(
    IOpenTailTool inner,
    IReadOnlyList<IToolInputGuardrail>? inputGuardrails = null,
    IReadOnlyList<IToolOutputGuardrail>? outputGuardrails = null) : IOpenTailTool
{
    public string Name => inner.Name;
    public string Description => inner.Description;

    public AIFunction ToAIFunction()
    {
        var innerFn = inner.ToAIFunction();
        if (inputGuardrails is null && outputGuardrails is null)
            return innerFn;
        return new GuardedAIFunction(innerFn, inner.Name, inputGuardrails, outputGuardrails);
    }

    private sealed class GuardedAIFunction(
        AIFunction innerFn,
        string toolName,
        IReadOnlyList<IToolInputGuardrail>? inputs,
        IReadOnlyList<IToolOutputGuardrail>? outputs) : AIFunction
    {
        public override string Name => innerFn.Name;
        public override string Description => innerFn.Description;
        public override JsonElement JsonSchema => innerFn.JsonSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (inputs is not null && inputs.Count > 0)
            {
                var serialized = JsonSerializer.Serialize(
                    arguments.ToDictionary(k => k.Key, v => v.Value));
                foreach (var guard in inputs)
                {
                    var r = await guard.CheckInputAsync(toolName, serialized, cancellationToken);
                    if (!r.Passed)
                        throw new ToolGuardrailTripwireException(guard.Key, toolName, r.Violation ?? "Input blocked.");
                }
            }

            var rawResult = await innerFn.InvokeAsync(arguments, cancellationToken);

            if (outputs is not null && outputs.Count > 0)
            {
                var resultStr = rawResult?.ToString() ?? string.Empty;
                foreach (var guard in outputs)
                {
                    var r = await guard.CheckOutputAsync(toolName, resultStr, cancellationToken);
                    if (!r.Passed)
                        throw new ToolGuardrailTripwireException(guard.Key, toolName, r.Violation ?? "Output blocked.");
                    if (r.RewrittenValue is not null)
                        resultStr = r.RewrittenValue;
                }
                return resultStr;
            }

            return rawResult;
        }
    }
}

public static class OpenTailToolGuardExtensions
{
    public static IOpenTailTool WithGuards(
        this IOpenTailTool tool,
        IReadOnlyList<IToolInputGuardrail>? inputGuardrails = null,
        IReadOnlyList<IToolOutputGuardrail>? outputGuardrails = null)
        => new GuardedToolWrapper(tool, inputGuardrails, outputGuardrails);
}
