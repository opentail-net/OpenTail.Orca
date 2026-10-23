using System.Text.Json;
using MongoDB.Bson;
using OpenTail.Modules.MongoDb;

namespace OpenTail.Host.Tests;

/// <summary>The MongoDB module's operator policy and output limits, without a server.</summary>
public class MongoGuardTests
{
    private static readonly MongoQueryLimits Limits = new();
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("""{ "status": "open", "total": { "$gt": 100, "$lte": 500 } }""")]
    [InlineData("""{ "$or": [ { "a": 1 }, { "b": { "$in": [1, 2, 3] } } ] }""")]
    [InlineData("""{ "tags": { "$all": ["x", "y"] }, "items": { "$elemMatch": { "qty": { "$gt": 2 } } } }""")]
    [InlineData("""{ "when": { "$gte": { "$date": "2026-01-01T00:00:00Z" } }, "_id": { "$oid": "64b7f0a1c2d3e4f5a6b7c8d9" } }""")]
    [InlineData("""{ "$expr": { "$gt": [ "$spent", { "$multiply": [ "$budget", 0.9 ] } ] } }""")]
    [InlineData("""{ "name": { "$not": { "$eq": "x" } }, "deleted": { "$exists": false } }""")]
    public void Ordinary_filters_pass(string filter) => MongoGuard.CheckFilter(J(filter), Limits);

    [Theory]
    [InlineData("""{ "$where": "sleep(10000)" }""")]
    [InlineData("""{ "$expr": { "$function": { "body": "x", "args": [], "lang": "js" } } }""")]
    [InlineData("""{ "name": { "$regex": "^a" } }""")]
    [InlineData("""{ "items": { "$elemMatch": { "$where": "1" } } }""")]
    [InlineData("""{ "name": { "$not": { "$regex": "a" } } }""")]
    [InlineData("""{ "$expr": { "$cond": [ { "$function": { "body": "x" } }, 1, 0 ] } }""")]
    [InlineData("""{ "$text": { "$search": "x" } }""")]
    [InlineData("""{ "loc": { "$geoWithin": { "$center": [[0, 0], 1] } } }""")]
    [InlineData("""{ "a": { "nested": { "$where": "1" } } }""")]                                    // hidden in a literal
    [InlineData("""{ "$expr": { "$eq": [ "$$myVar", 1 ] } }""")]                                     // user variables
    [InlineData("""{ "$expr": { "$sum": [ 1, 2 ] } }""")]                                             // accumulator outside $group
    [InlineData("""{ "a": { "$sum": 1 } }""")]
    public void Refused_filters(string filter) => Assert.Throws<ArgumentException>(() => MongoGuard.CheckFilter(J(filter), Limits));

    [Theory]
    [InlineData("""[ { "$match": { "status": "open" } }, { "$group": { "_id": "$region", "n": { "$sum": 1 }, "avg": { "$avg": "$total" } } }, { "$sort": { "n": -1 } }, { "$limit": 10 } ]""")]
    [InlineData("""[ { "$project": { "name": 1, "year": { "$year": "$created" }, "label": { "$concat": [ "$first", " ", "$last" ] } } } ]""")]
    [InlineData("""[ { "$unwind": { "path": "$items", "preserveNullAndEmptyArrays": true } }, { "$sortByCount": "$items.sku" } ]""")]
    [InlineData("""[ { "$sample": { "size": 5 } }, { "$replaceRoot": { "newRoot": "$address" } }, { "$count": "n" } ]""")]
    [InlineData("""[ { "$set": { "big": { "$cond": [ { "$gt": [ "$total", 100 ] }, true, false ] } } }, { "$unset": ["internal"] } ]""")]
    public void Ordinary_pipelines_pass(string pipeline) => MongoGuard.CheckPipeline(J(pipeline), Limits);

    [Theory]
    [InlineData("""[ { "$out": "copy" } ]""", "writes")]
    [InlineData("""[ { "$merge": { "into": "copy" } } ]""", "writes")]
    [InlineData("""[ { "$lookup": { "from": "users", "localField": "a", "foreignField": "b", "as": "u" } } ]""", "joins")]
    [InlineData("""[ { "$unionWith": "other" } ]""", "joins")]
    [InlineData("""[ { "$graphLookup": { "from": "x" } } ]""", "joins")]
    [InlineData("""[ { "$facet": { "a": [ { "$match": {} } ] } } ]""", "unbounded")]
    [InlineData("""[ { "$group": { "_id": null, "all": { "$push": "$$ROOT" } } } ]""", "array")]
    [InlineData("""[ { "$group": { "_id": null, "x": { "$accumulator": { "init": "1" } } } } ]""", "JavaScript")]
    [InlineData("""[ { "$currentOp": {} } ]""", "internals")]
    [InlineData("""[ { "$documents": [ {} ] } ]""", "source")]
    [InlineData("""[ { "$project": { "x": { "$exists": true } } } ]""", "expression")]            // a filter operator in an expression
    [InlineData("""[ { "$group": { "n": { "$sum": 1 } } } ]""", "_id")]
    [InlineData("""[ { "$sample": { "size": 100000 } } ]""", "$sample")]
    [InlineData("""[ { "$sort": { "x": { "$meta": "textScore" } } } ]""", "sort")]
    [InlineData("""[ { "$match": {}, "$limit": 1 } ]""", "one key")]
    public void Refused_pipelines(string pipeline, string because)
        => Assert.Contains(because, Assert.Throws<ArgumentException>(() => MongoGuard.CheckPipeline(J(pipeline), Limits)).Message);

    [Fact]
    public void Size_bounds_are_enforced()
    {
        var bigIn = "{ \"a\": { \"$in\": [" + string.Join(",", Enumerable.Range(0, 101)) + "] } }";
        Assert.Contains("100 values", Assert.Throws<ArgumentException>(() => MongoGuard.CheckFilter(J(bigIn), Limits)).Message);

        var manyOr = "{ \"$or\": [" + string.Join(",", Enumerable.Range(0, 21).Select(i => $"{{ \"a\": {i} }}")) + "] }";
        Assert.Contains("20 clauses", Assert.Throws<ArgumentException>(() => MongoGuard.CheckFilter(J(manyOr), Limits)).Message);

        var stages = "[" + string.Join(",", Enumerable.Repeat("{ \"$match\": {} }", 21)) + "]";
        Assert.Contains("20 stages", Assert.Throws<ArgumentException>(() => MongoGuard.CheckPipeline(J(stages), Limits)).Message);

        var deep = string.Concat(Enumerable.Repeat("{ \"$and\": [", 25)) + "{}" + string.Concat(Enumerable.Repeat("] }", 25)); // 50 deep in JSON
        Assert.Contains("deeper", Assert.Throws<ArgumentException>(() => MongoGuard.CheckFilter(J(deep), Limits)).Message);

        var many = "{ \"$and\": [" + string.Join(",", Enumerable.Range(0, 20).Select(i =>
            "{ \"a" + i + "\": { \"$gt\": 1, \"$lt\": 2, \"$ne\": 3, \"$exists\": true, \"$type\": \"int\", \"$eq\": 1, \"$gte\": 0, \"$lte\": 9, \"$nin\": [], \"$in\": [1] } }")) + "] }";
        Assert.Contains("operators", Assert.Throws<ArgumentException>(() => MongoGuard.CheckFilter(J(many), Limits)).Message);
    }

    [Fact]
    public void No_allow_list_contains_a_refused_operator()
    {
        string[] refused = ["$where", "$function", "$accumulator", "$out", "$merge", "$lookup", "$graphLookup", "$unionWith", "$facet",
            "$regex", "$push", "$addToSet", "$currentOp", "$listSessions", "$collStats", "$indexStats", "$planCacheStats", "$documents", "$changeStream"];
        foreach (var list in new[] { MongoGuard.Stages, MongoGuard.FieldOperators, MongoGuard.FilterOperators, MongoGuard.ExpressionOperators, MongoGuard.Accumulators, MongoGuard.LiteralWrappers })
            Assert.Empty(list.Intersect(refused));
    }

    [Fact]
    public void Every_expression_operator_is_accepted_in_an_expression_and_refused_as_a_stage()
    {
        foreach (var op in MongoGuard.ExpressionOperators)
        {
            MongoGuard.CheckFilter(J($$"""{ "$expr": { "{{op}}": [ "$a", 1 ] } }"""), Limits);
            if (!MongoGuard.Stages.Contains(op))
                Assert.Throws<ArgumentException>(() => MongoGuard.CheckPipeline(J($$"""[ { "{{op}}": {} } ]"""), Limits));
        }
    }

    [Fact]
    public void Output_is_plain_json_with_limits()
    {
        var doc = new BsonDocument
        {
            ["_id"] = ObjectId.Parse("64b7f0a1c2d3e4f5a6b7c8d9"),
            ["when"] = new BsonDateTime(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            ["long"] = new string('x', 50),
            ["bin"] = new BsonBinaryData(new byte[10]),
            ["many"] = new BsonArray(Enumerable.Range(0, 250)),
            ["price"] = new BsonDecimal128(12.50m),
        };
        var json = JsonSerializer.Serialize(MongoOutput.ToJson(doc, maxChars: 10));
        Assert.Contains("\"_id\":\"64b7f0a1c2d3e4f5a6b7c8d9\"", json);
        Assert.Contains("2026-01-02T00:00:00", json);
        Assert.Contains("\"xxxxxxxxxx\\u2026\"", json);
        Assert.Contains("10 bytes", json); // < and > are escaped in JSON
        Assert.Contains("50 more", json);
        Assert.Contains("\"12.50\"", json);

        object nested = new BsonDocument("v", 1);
        for (var i = 0; i < 12; i++) nested = new BsonDocument("in", (BsonDocument)nested);
        Assert.Contains("document with", JsonSerializer.Serialize(MongoOutput.ToJson((BsonDocument)nested, 100)));
    }
}
