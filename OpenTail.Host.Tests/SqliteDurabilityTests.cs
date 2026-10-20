namespace OpenTail.Host.Tests;

/// <summary>OPENTAIL_DB_SYNCHRONOUS: how hard each commit is pushed to disk (power-cut durability).</summary>
public class SqliteDurabilityTests
{
    [Theory]
    [InlineData(null, SqliteSynchronous.Normal)]
    [InlineData("", SqliteSynchronous.Normal)]
    [InlineData("normal", SqliteSynchronous.Normal)]
    [InlineData(" FULL ", SqliteSynchronous.Full)]
    [InlineData("extra", SqliteSynchronous.Extra)]
    public void Parse_KnownValues(string? value, SqliteSynchronous expected)
        => Assert.Equal(expected, SqliteDurability.Parse(value));

    [Fact]
    public void Parse_UnknownValueStopsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SqliteDurability.Parse("fast"));
        Assert.Contains(SqliteDurability.Variable, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqliteSynchronous.Normal, 1L)]
    [InlineData(SqliteSynchronous.Full, 2L)]
    [InlineData(SqliteSynchronous.Extra, 3L)]
    public void Store_AppliesTheLevelOnItsConnections(SqliteSynchronous level, long pragma)
    {
        using var db = new TempDb();
        var store = new SqliteActionStore(db.Path, synchronous: level);

        Assert.Equal(level, store.Synchronous);
        Assert.Equal(pragma, store.ReadSynchronous());
    }

    [Fact]
    public async Task Store_WorksAtFull()
    {
        using var db = new TempDb();
        var store = new SqliteActionStore(db.Path, synchronous: SqliteSynchronous.Full);

        var now = DateTimeOffset.UtcNow;
        var created = await store.CreateAsync(new ActionRecord
        {
            Id = "act_" + Guid.NewGuid().ToString("N"),
            ToolName = "mail.send",
            ModuleId = "fake",
            ArgumentsJson = "{\"to\":\"a@b.c\"}",
            ArgumentsHash = "h1",
            Requester = new ActionRequester(RequesterKind.User, "me"),
            Status = ActionStatus.Pending,
            PolicyMode = ToolMode.Queue,
            CreatedAt = now,
            UpdatedAt = now,
        }, TestContext.Current.CancellationToken);
        TempDb.ClearPools(db.Path);
        var reopened = new SqliteActionStore(db.Path, synchronous: SqliteSynchronous.Full);

        Assert.NotNull(await reopened.GetAsync(created.Id, TestContext.Current.CancellationToken));
    }
}
