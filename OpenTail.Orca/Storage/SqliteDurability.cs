namespace OpenTail.Orca;

/// <summary>
/// How hard SQLite pushes each commit to disk. The Host's database runs in WAL mode; with <see cref="Normal"/> (the default) a
/// commit survives the process crashing, but a power cut or an operating-system crash can roll back the most recent commits:
/// the database stays consistent, recent decisions may be lost. <see cref="Full"/> syncs the log on every commit, so a commit
/// that returned survives a power cut too, at the cost of one disk flush per write. <see cref="Extra"/> also syncs the directory.
/// </summary>
public enum SqliteSynchronous
{
    Normal,
    Full,
    Extra,
}

public static class SqliteDurability
{
    /// <summary><c>normal</c> (default), <c>full</c> or <c>extra</c>. Read once per store; every Host process sharing a database should
    /// set the same value.</summary>
    public const string Variable = "OPENTAIL_DB_SYNCHRONOUS";

    /// <summary>The level from <see cref="Variable"/>. An unknown value stops startup rather than quietly running less durably than
    /// asked.</summary>
    public static SqliteSynchronous FromEnvironment() => Parse(Environment.GetEnvironmentVariable(Variable));

    /// <summary>A level from its name; empty means <see cref="SqliteSynchronous.Normal"/>.</summary>
    public static SqliteSynchronous Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SqliteSynchronous.Normal;
        return value.Trim().ToLowerInvariant() switch
        {
            "normal" => SqliteSynchronous.Normal,
            "full" => SqliteSynchronous.Full,
            "extra" => SqliteSynchronous.Extra,
            _ => throw new InvalidOperationException($"{Variable} is '{value}'; use normal, full or extra."),
        };
    }

    /// <summary>The <c>PRAGMA synchronous</c> statement for a level.</summary>
    public static string Pragma(SqliteSynchronous level) => level switch
    {
        SqliteSynchronous.Full => "PRAGMA synchronous=FULL;",
        SqliteSynchronous.Extra => "PRAGMA synchronous=EXTRA;",
        _ => "PRAGMA synchronous=NORMAL;",
    };
}
