namespace OpenTail.Core.Tools.Abstractions;

/// <summary>
/// Thrown by <see cref="IToolInputGuardrail"/> or <see cref="IToolOutputGuardrail"/>
/// to hard-halt tool execution and surface an error to the caller.
/// </summary>
public sealed class ToolGuardrailTripwireException(string guardrailKey, string toolName, string reason)
    : Exception($"Tool guardrail '{guardrailKey}' tripped on tool '{toolName}': {reason}")
{
    public string GuardrailKey { get; } = guardrailKey;
    public string ToolName { get; } = toolName;
    public string Reason { get; } = reason;
}
