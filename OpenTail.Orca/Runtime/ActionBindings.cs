using System.Text.Json;
using OpenTail.Orca.Abstractions;

namespace OpenTail.Orca;

/// <summary>
/// The binding digest of an action (<see cref="ActionBinding"/>), built in one place: when an action is submitted, when a reviewer's edit is
/// recorded, and when an approved action is checked at its claim. The three must produce the same digest for the same action, so they all
/// come through here.
/// </summary>
internal static class ActionBindings
{
    /// <summary>The binding of a tool call. Null when the tool has no schema (it is not loaded), or when the arguments cannot be canonicalised
    /// (they are not JSON, or they repeat a member name). Such an action cannot run, so there is nothing to bind.</summary>
    /// <param name="toolSchemaVersion">The tool's schema version from the catalog; null when the tool is not loaded.</param>
    public static string? Digest(string agentId, string toolName, string? toolSchemaVersion, string argumentsJson, string? policyVersion)
    {
        if (toolSchemaVersion is null) return null;
        try
        {
            return new ActionBinding(ActionBinding.ToolCall, agentId, null, toolName, toolSchemaVersion, null, argumentsJson, policyVersion)
                .Digest();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }
}
