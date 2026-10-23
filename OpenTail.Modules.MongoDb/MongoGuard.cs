using System.Text.Json;

namespace OpenTail.Modules.MongoDb;

/// <summary>Bounds on what the model may send. Defaults are the plan's (docs/design in opentail-connectivity, section 30).</summary>
public sealed record MongoQueryLimits(
    int MaxRequestBytes = 16 * 1024,
    int MaxDepth = 32,
    int MaxStages = 20,
    int MaxListValues = 100,
    int MaxClauses = 20,
    int MaxOperators = 200,
    int MaxSample = 200);

/// <summary>
/// The operator policy for model-supplied filters, projections, sorts and pipelines: every key starting with <c>$</c> must be on
/// the allow-list <b>for the context it appears in</b>; anything else is refused, so an operator MongoDB adds later stays refused
/// until reviewed. A document walker, not a parser: the input is JSON, so there is no quoting to misread.
/// </summary>
/// <remarks>
/// The first version covers one collection per query: no <c>$lookup</c>, <c>$graphLookup</c>, <c>$unionWith</c>, <c>$facet</c>,
/// <c>$regex</c>, <c>$push</c> or <c>$addToSet</c>. Server-side JavaScript (<c>$where</c>, <c>$function</c>, <c>$accumulator</c>)
/// and writes (<c>$out</c>, <c>$merge</c>) are never allowed. The guard is the second line; the first is a login with the
/// <c>read</c> role.
/// </remarks>
public static class MongoGuard
{
    public static readonly IReadOnlySet<string> Stages = Set(
        "$match", "$project", "$addFields", "$set", "$unset", "$group", "$sort", "$limit", "$skip", "$unwind", "$count",
        "$sortByCount", "$replaceRoot", "$replaceWith", "$sample");

    /// <summary>Operators on a field in a filter (<c>{ age: { $gt: 30 } }</c>).</summary>
    public static readonly IReadOnlySet<string> FieldOperators = Set(
        "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$in", "$nin", "$not", "$exists", "$type", "$all", "$elemMatch", "$size");

    /// <summary>Operators at the top of a filter document.</summary>
    public static readonly IReadOnlySet<string> FilterOperators = Set("$and", "$or", "$nor", "$expr");

    public static readonly IReadOnlySet<string> ExpressionOperators = Set(
        // arithmetic
        "$add", "$subtract", "$multiply", "$divide", "$mod", "$abs", "$ceil", "$floor", "$round", "$trunc",
        // comparison
        "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$cmp",
        // boolean and conditional
        "$and", "$or", "$not", "$cond", "$ifNull", "$switch",
        // string
        "$concat", "$substrCP", "$strLenCP", "$toLower", "$toUpper", "$trim",
        // date
        "$year", "$month", "$dayOfMonth", "$dayOfWeek", "$week", "$hour", "$minute", "$dateToString", "$dateTrunc", "$dateDiff",
        // type
        "$type", "$toString", "$toInt", "$toLong", "$toDouble", "$toDecimal", "$toDate", "$toBool",
        // array: reading, never building
        "$size", "$arrayElemAt", "$in", "$first", "$last",
        "$literal");

    /// <summary>In <c>$group</c> only. Not <c>$push</c> or <c>$addToSet</c>: they build arrays as large as the group.</summary>
    public static readonly IReadOnlySet<string> Accumulators = Set("$sum", "$avg", "$min", "$max", "$count", "$first", "$last");

    /// <summary>Extended JSON type wrappers, allowed wherever a literal value is (<c>{ "$date": "2026-01-02T00:00:00Z" }</c>).</summary>
    public static readonly IReadOnlySet<string> LiteralWrappers = Set("$oid", "$date", "$numberLong", "$numberInt", "$numberDouble", "$numberDecimal");

    /// <summary>Variables an expression may read. Others (user variables, <c>$$SEARCH_META</c>) are refused.</summary>
    public static readonly IReadOnlySet<string> Variables = Set("$$ROOT", "$$CURRENT", "$$NOW", "$$REMOVE");

    /// <summary>Named so the refusal says why; anything else not on a list is refused anyway.</summary>
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["$where"] = "it runs JavaScript on the server",
        ["$function"] = "it runs JavaScript on the server",
        ["$accumulator"] = "it runs JavaScript on the server",
        ["$out"] = "it writes",
        ["$merge"] = "it writes",
        ["$lookup"] = "joins are not in the first version (one collection per query)",
        ["$graphLookup"] = "joins are not in the first version (one collection per query)",
        ["$unionWith"] = "joins are not in the first version (one collection per query)",
        ["$facet"] = "its branches build unbounded arrays inside one document",
        ["$regex"] = "regular expressions are not in the first version",
        ["$push"] = "it builds an array as large as the group",
        ["$addToSet"] = "it builds an array as large as the group",
        ["$currentOp"] = "it reads server internals",
        ["$listSessions"] = "it reads server internals",
        ["$collStats"] = "it reads server internals",
        ["$indexStats"] = "it reads server internals",
        ["$planCacheStats"] = "it reads server internals",
        ["$documents"] = "it reads from another source",
        ["$changeStream"] = "it reads from another source",
    };

    private static IReadOnlySet<string> Set(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    public static void CheckFilter(JsonElement filter, MongoQueryLimits limits) => new Walker(limits).Filter(filter, 1);

    public static void CheckProjection(JsonElement projection, MongoQueryLimits limits) => new Walker(limits).Projection(projection, 1);

    public static void CheckSort(JsonElement sort, MongoQueryLimits limits) => new Walker(limits).Sort(sort, 1);

    public static void CheckPipeline(JsonElement pipeline, MongoQueryLimits limits) => new Walker(limits).Pipeline(pipeline);

    private sealed class Walker(MongoQueryLimits limits)
    {
        private int _operators;

        private static ArgumentException Refuse(string message) => new(message);

        private void Operator(string name, IReadOnlySet<string> allowed, string context)
        {
            if (!allowed.Contains(name))
                throw Refuse(Reasons.TryGetValue(name, out var why)
                    ? $"'{name}' is not allowed: {why}."
                    : $"'{name}' is not allowed in {context}.");
            if (++_operators > limits.MaxOperators)
                throw Refuse($"The query has more than {limits.MaxOperators} operators.");
        }

        private void Depth(int depth)
        {
            if (depth > limits.MaxDepth) throw Refuse($"The query is nested deeper than {limits.MaxDepth} levels.");
        }

        private static JsonElement Object(JsonElement e, string what)
            => e.ValueKind == JsonValueKind.Object ? e : throw Refuse($"{what} must be a JSON object.");

        // -- filters ---------------------------------------------------------------------------------

        public void Filter(JsonElement doc, int depth)
        {
            Depth(depth);
            foreach (var p in Object(doc, "A filter").EnumerateObject())
            {
                if (p.Name.StartsWith('$'))
                {
                    Operator(p.Name, FilterOperators, "a filter");
                    if (p.Name == "$expr")
                        Expression(p.Value, depth + 1);
                    else
                        Clauses(p.Name, p.Value, depth + 1);
                }
                else
                    FieldCondition(p.Value, depth + 1);
            }
        }

        private void Clauses(string name, JsonElement value, int depth)
        {
            if (value.ValueKind != JsonValueKind.Array) throw Refuse($"'{name}' takes an array of filters.");
            if (value.GetArrayLength() > limits.MaxClauses) throw Refuse($"'{name}' has more than {limits.MaxClauses} clauses.");
            foreach (var clause in value.EnumerateArray())
                Filter(clause, depth + 1); // the array is a level of its own
        }

        /// <summary>A field's condition: a literal (equality) or a document of field operators.</summary>
        private void FieldCondition(JsonElement value, int depth)
        {
            Depth(depth);
            if (!IsOperatorDocument(value))
            {
                Literal(value, depth);
                return;
            }
            foreach (var p in value.EnumerateObject())
            {
                if (LiteralWrappers.Contains(p.Name))
                {
                    Literal(value, depth); // { "$date": ... } as an equality value
                    return;
                }
                Operator(p.Name, FieldOperators, "a field condition");
                switch (p.Name)
                {
                    case "$in" or "$nin" or "$all":
                        if (p.Value.ValueKind != JsonValueKind.Array) throw Refuse($"'{p.Name}' takes an array.");
                        if (p.Value.GetArrayLength() > limits.MaxListValues) throw Refuse($"'{p.Name}' has more than {limits.MaxListValues} values.");
                        foreach (var v in p.Value.EnumerateArray()) Literal(v, depth + 1);
                        break;
                    case "$not":
                        if (!IsOperatorDocument(p.Value)) throw Refuse("'$not' takes a document of operators.");
                        FieldCondition(p.Value, depth + 1);
                        break;
                    case "$elemMatch":
                        if (IsOperatorDocument(p.Value)) FieldCondition(p.Value, depth + 1);
                        else Filter(p.Value, depth + 1);
                        break;
                    case "$exists":
                        if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Refuse("'$exists' takes true or false.");
                        break;
                    default:
                        Literal(p.Value, depth + 1);
                        break;
                }
            }
        }

        private static bool IsOperatorDocument(JsonElement value)
            => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any(p => p.Name.StartsWith('$'));

        /// <summary>A value taken as data. A key starting with <c>$</c> must be a type wrapper.</summary>
        private void Literal(JsonElement value, int depth)
        {
            Depth(depth);
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in value.EnumerateObject())
                    {
                        if (p.Name.StartsWith('$') && !LiteralWrappers.Contains(p.Name))
                            throw Refuse(Reasons.TryGetValue(p.Name, out var why) ? $"'{p.Name}' is not allowed: {why}." : $"'{p.Name}' is not allowed in a value.");
                        Literal(p.Value, depth + 1);
                    }
                    break;
                case JsonValueKind.Array:
                    if (value.GetArrayLength() > limits.MaxListValues) throw Refuse($"An array value has more than {limits.MaxListValues} elements.");
                    foreach (var v in value.EnumerateArray()) Literal(v, depth + 1);
                    break;
                case JsonValueKind.String when value.GetString()!.StartsWith("$$", StringComparison.Ordinal):
                    throw Refuse("Variables ('$$...') are not values; use them in expressions only.");
            }
        }

        // -- expressions -----------------------------------------------------------------------------

        private void Expression(JsonElement value, int depth)
        {
            Depth(depth);
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var s = value.GetString()!;
                    if (s.StartsWith("$$", StringComparison.Ordinal) && !Variables.Contains(s.Split('.')[0]))
                        throw Refuse($"The variable '{s}' is not allowed.");
                    break;
                case JsonValueKind.Array:
                    if (value.GetArrayLength() > limits.MaxListValues) throw Refuse($"An array has more than {limits.MaxListValues} elements.");
                    foreach (var v in value.EnumerateArray()) Expression(v, depth + 1);
                    break;
                case JsonValueKind.Object:
                    var props = value.EnumerateObject().ToList();
                    if (props.Count == 1 && props[0].Name.StartsWith('$'))
                    {
                        var name = props[0].Name;
                        if (LiteralWrappers.Contains(name)) { Literal(value, depth); break; }
                        Operator(name, ExpressionOperators, "an expression");
                        if (name == "$literal") Literal(props[0].Value, depth + 1);
                        else if (name is "$and" or "$or" && props[0].Value.ValueKind == JsonValueKind.Array && props[0].Value.GetArrayLength() > limits.MaxClauses)
                            throw Refuse($"'{name}' has more than {limits.MaxClauses} clauses.");
                        else Expression(props[0].Value, depth + 1);
                    }
                    else
                    {
                        foreach (var p in props)
                        {
                            if (p.Name.StartsWith('$')) throw Refuse($"'{p.Name}' must be the only key of its expression.");
                            Expression(p.Value, depth + 1); // an object of named expressions ($switch branches, $dateToString options)
                        }
                    }
                    break;
            }
        }

        // -- projections and sorts -------------------------------------------------------------------

        public void Projection(JsonElement doc, int depth)
        {
            Depth(depth);
            foreach (var p in Object(doc, "A projection").EnumerateObject())
            {
                if (p.Name.StartsWith('$')) throw Refuse($"'{p.Name}' is not a field name.");
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                        break;
                    case JsonValueKind.Object when !IsOperatorDocument(p.Value):
                        Projection(p.Value, depth + 1);
                        break;
                    default:
                        Expression(p.Value, depth + 1);
                        break;
                }
            }
        }

        public void Sort(JsonElement doc, int depth)
        {
            Depth(depth);
            foreach (var p in Object(doc, "A sort").EnumerateObject())
                if (p.Name.StartsWith('$') || p.Value.ValueKind != JsonValueKind.Number || p.Value.GetDouble() is not (1 or -1))
                    throw Refuse("A sort maps field names to 1 or -1.");
        }

        // -- pipelines -------------------------------------------------------------------------------

        public void Pipeline(JsonElement pipeline)
        {
            if (pipeline.ValueKind != JsonValueKind.Array) throw Refuse("A pipeline is a JSON array of stages.");
            if (pipeline.GetArrayLength() > limits.MaxStages) throw Refuse($"The pipeline has more than {limits.MaxStages} stages.");
            foreach (var stage in pipeline.EnumerateArray())
            {
                var props = Object(stage, "A stage").EnumerateObject().ToList();
                if (props.Count != 1) throw Refuse("Each stage has exactly one key, the stage's name.");
                var (name, body) = (props[0].Name, props[0].Value);
                Operator(name, Stages, "a pipeline");
                switch (name)
                {
                    case "$match": Filter(body, 2); break;
                    case "$project" or "$addFields" or "$set": Projection(body, 2); break;
                    case "$sort": Sort(body, 2); break;
                    case "$group": Group(body); break;
                    case "$limit" or "$skip":
                        if (body.ValueKind != JsonValueKind.Number || !body.TryGetInt64(out var n) || n < 0) throw Refuse($"'{name}' takes a whole number.");
                        break;
                    case "$sample":
                        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("size", out var size) || !size.TryGetInt32(out var k) || k < 1 || k > limits.MaxSample)
                            throw Refuse($"'$sample' takes {{ \"size\": n }} with n from 1 to {limits.MaxSample}.");
                        break;
                    case "$count":
                        if (body.ValueKind != JsonValueKind.String || body.GetString()!.StartsWith('$')) throw Refuse("'$count' takes a field name.");
                        break;
                    case "$unset":
                        foreach (var f in body.ValueKind == JsonValueKind.Array ? body.EnumerateArray().ToList() : [body])
                            if (f.ValueKind != JsonValueKind.String || f.GetString()!.StartsWith('$')) throw Refuse("'$unset' takes field names.");
                        break;
                    case "$unwind":
                        Unwind(body);
                        break;
                    case "$sortByCount" or "$replaceWith":
                        Expression(body, 2);
                        break;
                    case "$replaceRoot":
                        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("newRoot", out var root) || body.EnumerateObject().Count() != 1)
                            throw Refuse("'$replaceRoot' takes { \"newRoot\": expression }.");
                        Expression(root, 2);
                        break;
                }
            }
        }

        private void Group(JsonElement body)
        {
            var hasId = false;
            foreach (var p in Object(body, "'$group'").EnumerateObject())
            {
                if (p.Name == "_id") { hasId = true; Expression(p.Value, 2); continue; }
                if (p.Name.StartsWith('$')) throw Refuse($"'{p.Name}' is not a field name.");
                var acc = Object(p.Value, $"The accumulator for '{p.Name}'").EnumerateObject().ToList();
                if (acc.Count != 1) throw Refuse($"'{p.Name}' takes exactly one accumulator.");
                Operator(acc[0].Name, Accumulators, "'$group'");
                Expression(acc[0].Value, 3);
            }
            if (!hasId) throw Refuse("'$group' needs an _id.");
        }

        private static void Unwind(JsonElement body)
        {
            if (body.ValueKind == JsonValueKind.String && body.GetString()!.StartsWith('$') && !body.GetString()!.StartsWith("$$", StringComparison.Ordinal))
                return;
            if (body.ValueKind != JsonValueKind.Object) throw Refuse("'$unwind' takes a field path or { \"path\": ... }.");
            foreach (var p in body.EnumerateObject())
            {
                var ok = p.Name switch
                {
                    "path" => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.StartsWith('$') && !p.Value.GetString()!.StartsWith("$$", StringComparison.Ordinal),
                    "preserveNullAndEmptyArrays" => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "includeArrayIndex" => p.Value.ValueKind == JsonValueKind.String && !p.Value.GetString()!.StartsWith('$'),
                    _ => false,
                };
                if (!ok) throw Refuse($"'$unwind' does not take '{p.Name}' like that.");
            }
        }
    }
}
