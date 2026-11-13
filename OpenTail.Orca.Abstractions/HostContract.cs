namespace OpenTail.Orca.Abstractions;

/// <summary>
/// Version of the module/host contract. A module assembly records the version it was compiled against with
/// <see cref="HostContractVersionAttribute"/>; the Host refuses modules outside <see cref="MinSupportedVersion"/>..<see cref="Version"/>.
/// </summary>
public static class HostContract
{
    public const int Version = 1;

    /// <summary>The oldest module contract this Host still loads.</summary>
    public const int MinSupportedVersion = 1;

    /// <summary>Version of the persisted action record schema (forward-only migrations).</summary>
    public const int ActionSchemaVersion = 1;

    /// <summary>Key in <c>AIFunctionArguments.Context</c> carrying the idempotency key a tool should pass downstream
    /// (the caller's key, or the action id). Tools that declare <c>IdempotentByKey</c> must honour it.</summary>
    public const string IdempotencyContextKey = "opentail.idempotency_key";

    /// <summary>Key in <c>AIFunctionArguments.Context</c> carrying the action id being run (for correlation in downstream logs).</summary>
    public const string ActionIdContextKey = "opentail.action_id";

    /// <summary>Key in <c>AIFunctionArguments.Context</c> carrying the namespaced subject who asked (see <see cref="Subjects"/>).
    /// Taken from the stored action, so it is right during approval runs and crash recovery too. Read-only information:
    /// a tool must not use it to grant anything.</summary>
    public const string RequesterContextKey = "opentail.requester";
}
