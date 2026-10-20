namespace OpenTail.Core.Tools.Abstractions;

/// <summary>Validates a tool's input arguments before the tool executes.</summary>
public interface IToolInputGuardrail
{
    string Key { get; }

    /// <summary>
    /// Inspect <paramref name="serializedArguments"/> (JSON) and return whether the call may proceed.
    /// Return <c>Passed = false</c> to block execution; throw
    /// <see cref="ToolGuardrailTripwireException"/> to halt and surface a hard error.
    /// </summary>
    Task<ToolGuardrailResult> CheckInputAsync(
        string toolName,
        string serializedArguments,
        CancellationToken ct = default);
}

/// <summary>Validates or sanitises a tool's return value before it is surfaced to the agent.</summary>
public interface IToolOutputGuardrail
{
    string Key { get; }

    /// <summary>
    /// Inspect <paramref name="result"/> and return a (possibly rewritten) result string.
    /// Return <c>Passed = false</c> to block the result; throw
    /// <see cref="ToolGuardrailTripwireException"/> to halt and surface a hard error.
    /// </summary>
    Task<ToolGuardrailResult> CheckOutputAsync(
        string toolName,
        string result,
        CancellationToken ct = default);
}

public sealed record ToolGuardrailResult
{
    public bool Passed { get; init; }
    public string? Violation { get; init; }

    /// <summary>Replacement value written back when the guardrail rewrites output instead of blocking.</summary>
    public string? RewrittenValue { get; init; }

    public static ToolGuardrailResult Ok() => new() { Passed = true };
    public static ToolGuardrailResult Reject(string violation) => new() { Passed = false, Violation = violation };
    public static ToolGuardrailResult Rewrite(string newValue) => new() { Passed = true, RewrittenValue = newValue };
}
