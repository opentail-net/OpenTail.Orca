using System.Collections.Concurrent;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using OpenTail.Host.Abstractions;
using OpenTail.Modules.MongoDb;
using Xunit;

namespace OpenTail.Host.Tests.MongoDb;

internal static class Req
{
    public static readonly ActionRequester Me = new(RequesterKind.User, "me");
    public static ActionRequest Call(string tool, string args) => new(tool, args, Me, null);
}

/// <summary>
/// The MongoDB module against a real server, through the Host. <c>OTAIL_MONGODB_ADMIN</c> is a root connection string (for
/// setting up data); <c>OTAIL_MONGODB_READ</c> a connection string for a user with only the <c>read</c> role on <c>otail</c>,
/// without its password, which is in <c>OTAIL_MONGODB_READ_PASSWORD</c> (the module adds it, as in production).
/// </summary>
public sealed class MongoServerTests : IAsyncLifetime
{
    private static string? Admin => Environment.GetEnvironmentVariable("OTAIL_MONGODB_ADMIN");
    private static string? Read => Environment.GetEnvironmentVariable("OTAIL_MONGODB_READ");
    private static bool Configured => Admin is { Length: > 0 } && Read is { Length: > 0 }
                                      && Environment.GetEnvironmentVariable("OTAIL_MONGODB_READ_PASSWORD") is { Length: > 0 };

    private readonly string _orders = "orders_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _wide = "wide_" + Guid.NewGuid().ToString("N")[..8];

    public async ValueTask InitializeAsync()
    {
        if (!Configured) return;
        var db = new MongoClient(Admin).GetDatabase("otail");
        var regions = new[] { "north", "south", "east" };
        await db.GetCollection<BsonDocument>(_orders).InsertManyAsync(Enumerable.Range(1, 300).Select(i => new BsonDocument
        {
            ["n"] = i,
            ["region"] = regions[i % 3],
            ["status"] = i % 4 == 0 ? "open" : "closed",
            ["total"] = i * 10,
            ["created"] = new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i)),
        }));
        await db.GetCollection<BsonDocument>(_wide).InsertManyAsync(Enumerable.Range(1, 200).Select(i => new BsonDocument
        {
            ["n"] = i, ["a"] = new string('a', 5000), ["b"] = new string('b', 5000), ["c"] = new string('c', 5000),
        }));
    }

    public async ValueTask DisposeAsync()
    {
        if (!Configured) return;
        var db = new MongoClient(Admin).GetDatabase("otail");
        await db.DropCollectionAsync(_orders);
        await db.DropCollectionAsync(_wide);
    }

    private static void RequireServer() => Assert.SkipUnless(Configured, "Set OTAIL_MONGODB_ADMIN, OTAIL_MONGODB_READ and OTAIL_MONGODB_READ_PASSWORD.");

    /// <summary>The module from modules.json, with the password credential, as a deployment configures it.</summary>
    private async Task<OpenTailHost> HostAsync()
    {
        var modules = ModulesConfig.Parse($$"""
            { "Modules": [ { "Type": "mongodb", "Id": "mongo",
                "Credentials": { "password": { "Env": "OTAIL_MONGODB_READ_PASSWORD", "Kind": "Secret" } },
                "Config": { "ConnectionString": "{{Read}}", "Database": "otail", "Collections": [ "{{_orders}}", "{{_wide}}" ], "MaxRows": 50 } } ] }
            """, null);
        return new OpenTailHostBuilder()
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mongo.collections": { "Mode": "Execute" }, "mongo.find": { "Mode": "Execute" }, "mongo.aggregate": { "Mode": "Execute" }, "mongo.count": { "Mode": "Execute" } } }"""))
            .AddModules(await ModuleLoader.LoadAsync(modules, ModuleLoadMode.Run))
            .Build();
    }

    private static async Task<JsonElement> Ok(OpenTailHost host, string tool, object args)
    {
        var r = await host.Queue.SubmitAsync(Req.Call(tool, JsonSerializer.Serialize(args)));
        Assert.True(r.Status == ActionStatus.Succeeded, $"{r.Status}: {r.Error?.Message}");
        return JsonDocument.Parse(r.ResultJson!).RootElement;
    }

    [Fact]
    public async Task The_four_tools_read_through_the_read_only_login()
    {
        RequireServer();
        var host = await HostAsync();

        var collections = await Ok(host, "mongo.collections", new { });
        var orders = collections.GetProperty("collections").EnumerateArray().Single(c => c.GetProperty("collection").GetString() == _orders);
        Assert.Contains(orders.GetProperty("fields").EnumerateArray(), f => f.GetProperty("field").GetString() == "region");

        var found = await Ok(host, "mongo.find", new
        {
            collection = _orders,
            filter = JsonDocument.Parse("""{ "status": "open", "total": { "$gte": 1000 } }""").RootElement,
            projection = JsonDocument.Parse("""{ "_id": 0, "n": 1, "total": 1 }""").RootElement,
            sort = JsonDocument.Parse("""{ "total": -1 }""").RootElement,
            limit = 3,
        });
        var rows = found.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal([3000, 2960, 2920], rows.Select(r => r.GetProperty("total").GetInt32()));
        Assert.True(found.GetProperty("truncated").GetBoolean()); // more than 3 matched
        Assert.True(found.GetProperty("untrusted").GetBoolean());

        var grouped = await Ok(host, "mongo.aggregate", new
        {
            collection = _orders,
            pipeline = JsonDocument.Parse("""[ { "$group": { "_id": "$region", "n": { "$sum": 1 } } }, { "$sort": { "_id": 1 } } ]""").RootElement,
        });
        Assert.Equal(["east", "north", "south"], grouped.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("_id").GetString()));
        Assert.All(grouped.GetProperty("rows").EnumerateArray(), r => Assert.Equal(100, r.GetProperty("n").GetInt32()));

        var counted = await Ok(host, "mongo.count", new { collection = _orders, filter = JsonDocument.Parse("""{ "status": "open" }""").RootElement });
        Assert.Equal(75, counted.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Refused_queries_and_other_collections_never_reach_the_server()
    {
        RequireServer();
        var host = await HostAsync();
        foreach (var (tool, args) in new (string, object)[]
        {
            ("mongo.find", new { collection = _orders, filter = JsonDocument.Parse("""{ "$where": "sleep(5000) || true" }""").RootElement }),
            ("mongo.aggregate", new { collection = _orders, pipeline = JsonDocument.Parse($$"""[ { "$out": "{{_orders}}_copy" } ]""").RootElement }),
            ("mongo.aggregate", new { collection = _orders, pipeline = JsonDocument.Parse("""[ { "$lookup": { "from": "system.users", "localField": "a", "foreignField": "b", "as": "u" } } ]""").RootElement }),
            ("mongo.count", new { collection = "system.users" }),
        })
        {
            var r = await host.Queue.SubmitAsync(Req.Call(tool, JsonSerializer.Serialize(args)));
            Assert.Equal(ActionStatus.Failed, r.Status);
        }
        var names = await (await new MongoClient(Admin).GetDatabase("otail").ListCollectionNamesAsync()).ToListAsync();
        Assert.DoesNotContain(_orders + "_copy", names);
    }

    [Fact]
    public async Task A_large_result_is_cut_at_the_response_size()
    {
        RequireServer();
        var host = await HostAsync();
        var r = await Ok(host, "mongo.find", new { collection = _wide });
        Assert.True(r.GetProperty("truncated").GetBoolean());
        Assert.Contains("KB", r.GetProperty("truncatedBecause").GetString());
        Assert.True(r.GetRawText().Length < 300 * 1024);
        Assert.All(r.GetProperty("rows").EnumerateArray(), row => Assert.True(row.GetProperty("a").GetString()!.Length <= 2001)); // strings cut
    }

    [Fact]
    public async Task The_read_login_cannot_write()
    {
        RequireServer();
        var url = new MongoUrlBuilder(Read) { Password = Environment.GetEnvironmentVariable("OTAIL_MONGODB_READ_PASSWORD") };
        var orders = new MongoClient(url.ToMongoUrl()).GetDatabase("otail").GetCollection<BsonDocument>(_orders);
        await Assert.ThrowsAnyAsync<MongoException>(() => orders.InsertOneAsync(new BsonDocument("n", -1)));
        Assert.Equal(300, await new MongoClient(Admin).GetDatabase("otail").GetCollection<BsonDocument>(_orders).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Every_operation_carries_the_server_time_limit_and_the_action_id()
    {
        RequireServer();
        var sent = new ConcurrentQueue<BsonDocument>();
        var url = new MongoUrlBuilder(Read) { Password = Environment.GetEnvironmentVariable("OTAIL_MONGODB_READ_PASSWORD") };
        var settings = MongoClientSettings.FromUrl(url.ToMongoUrl());
        settings.ClusterConfigurator = cb => cb.Subscribe<CommandStartedEvent>(e =>
        {
            if (e.CommandName is "find" or "aggregate") sent.Enqueue(e.Command);
        });
        var db = new MongoClient(settings).GetDatabase("otail");
        var host = new OpenTailHostBuilder()
            .UsePolicy(PolicyConfig.Parse("""{ "DefaultReadMode": "Execute" }"""))
            .AddModule(new MongoModule(new MongoModuleOptions { Name = "m", Database = _ => ValueTask.FromResult(db), Collections = [_orders], CommandTimeoutSeconds = 7 }))
            .Build();

        var find = await host.Queue.SubmitAsync(Req.Call("m.find", JsonSerializer.Serialize(new { collection = _orders, limit = 1 })));
        var agg = await host.Queue.SubmitAsync(Req.Call("m.aggregate", JsonSerializer.Serialize(new { collection = _orders, pipeline = JsonDocument.Parse("""[ { "$match": {} } ]""").RootElement })));
        Assert.Equal(ActionStatus.Succeeded, find.Status);
        Assert.Equal(ActionStatus.Succeeded, agg.Status);

        var commands = sent.ToList();
        Assert.Contains(commands, c => c.Contains("find") && c["maxTimeMS"].ToInt64() == 7000 && c["comment"].AsString == "opentail:" + find.Id);
        Assert.Contains(commands, c => c.Contains("aggregate") && c["maxTimeMS"].ToInt64() == 7000 && c["comment"].AsString == "opentail:" + agg.Id
                                       && c["allowDiskUse"].AsBoolean == false
                                       && c["pipeline"].AsBsonArray.Last().AsBsonDocument.Contains("$limit"));
    }
}
