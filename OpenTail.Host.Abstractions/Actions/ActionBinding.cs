using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenTail.Host.Abstractions;

/// <summary>
/// The whole action, as one thing an approval can name (governance A.2, the naming plan's phase 1). Its canonical keys are the ones in
/// <c>docs/design/governance-naming-plan.md</c>: <c>schema_version</c>, <c>operation</c>, <c>agent_id</c>, <c>subject_id</c>,
/// <c>target</c> (<c>tool_name</c>, <c>tool_schema_version</c>, <c>resource</c>), <c>parameters</c> and <c>policy_version</c>.
/// <see cref="Digest"/> is SHA-256 over the RFC 8785 canonical form of that object. It sits beside <see cref="ActionRecord.ArgumentsHash"/>,
/// which is unchanged.
/// </summary>
/// <param name="AgentId">The requester's identity (the credential's id, for example <c>key:bot</c>).</param>
/// <param name="SubjectId">The person the action is for. Null until delegation exists; the key stays so a later delegation changes the digest.</param>
/// <param name="Resource">The resource the action touches. Null: the Host has no resource model yet.</param>
/// <param name="ParametersJson">The arguments as JSON. They are canonicalised with the rest of the object.</param>
/// <param name="PolicyVersion">The policy hash the action is decided under.</param>
public sealed record ActionBinding(
    string Operation,
    string AgentId,
    string? SubjectId,
    string ToolName,
    string ToolSchemaVersion,
    string? Resource,
    string ParametersJson,
    string? PolicyVersion)
{
    /// <summary>The version of this binding's key set. A change of keys is a new version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The operation of a tool call.</summary>
    public const string ToolCall = "tool_call";

    /// <summary>The RFC 8785 canonical form of the binding object.</summary>
    public string CanonicalJson() => Jcs.Canonicalize(ToJsonText());

    /// <summary>SHA-256 of <see cref="CanonicalJson"/>: <c>sha256:</c> and 64 lower-case hex digits.</summary>
    public string Digest() =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson())));

    /// <summary>The schema version of a tool, from its parameter schema as JSON: <c>sha256:</c> over the canonical form of that schema.</summary>
    public static string SchemaVersionOf(string schemaJson) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Jcs.Canonicalize(schemaJson))));

    private string ToJsonText()
    {
        // Canonicalising the arguments first validates them (duplicate names and bad JSON are refused here, before any object is built).
        var parameters = JsonNode.Parse(Jcs.Canonicalize(ParametersJson));
        var node = new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["operation"] = Operation,
            ["agent_id"] = AgentId,
            ["subject_id"] = SubjectId,
            ["target"] = new JsonObject
            {
                ["tool_name"] = ToolName,
                ["tool_schema_version"] = ToolSchemaVersion,
                ["resource"] = Resource,
            },
            ["parameters"] = parameters,
            ["policy_version"] = PolicyVersion,
        };
        return node.ToJsonString();
    }
}
