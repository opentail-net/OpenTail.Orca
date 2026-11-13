namespace OpenTail.Orca.Abstractions;

/// <summary>What a tool can do to the world. Declared by the module author; policy decides what to do about it.</summary>
public enum ToolRisk
{
    /// <summary>No side effects.</summary>
    Read = 0,
    /// <summary>Changes state that the deployment owns and can reasonably restore.</summary>
    Write = 1,
    /// <summary>Removes or overwrites data in a way that is hard to undo.</summary>
    Destructive = 2,
    /// <summary>Has an effect outside the deployment (sends mail, calls a third party, spends money).</summary>
    ExternalEffect = 3,
}
