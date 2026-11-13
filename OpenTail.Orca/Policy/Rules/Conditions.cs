using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenTail.Orca;

/// <summary>Three-valued truth. <see cref="Unknown"/> is what anything the Host cannot evaluate becomes.</summary>
public enum Tri
{
    False = 0,
    True = 1,
    Unknown = 2,
}

/// <summary>A rule in the policy file is not valid. The message names the rule and where in its condition the problem is.</summary>
public sealed class RuleException(string message) : Exception(message);

internal sealed class EvalContext(JsonElement args, DateTimeOffset now, List<string>? trace)
{
    public JsonElement Args { get; } = args;
    public DateTimeOffset Now { get; } = now;
    public void Note(string line) => trace?.Add(line);
}

/// <summary>A node of a rule condition. Evaluation never throws: whatever goes wrong becomes <see cref="Tri.Unknown"/>.</summary>
internal abstract class Condition
{
    public abstract Tri Eval(EvalContext c);

    /// <summary>Top-level argument names this condition looks at.</summary>
    public abstract void CollectArgs(HashSet<string> topLevel);
}

internal sealed class AllCondition(IReadOnlyList<Condition> children) : Condition
{
    public override Tri Eval(EvalContext c)
    {
        var result = Tri.True;
        foreach (var child in children)
        {
            var r = child.Eval(c);
            if (r == Tri.False) return Tri.False;
            if (r == Tri.Unknown) result = Tri.Unknown;
        }
        return result;
    }

    public override void CollectArgs(HashSet<string> topLevel) { foreach (var ch in children) ch.CollectArgs(topLevel); }
}

internal sealed class AnyCondition(IReadOnlyList<Condition> children) : Condition
{
    public override Tri Eval(EvalContext c)
    {
        var result = Tri.False;
        foreach (var child in children)
        {
            var r = child.Eval(c);
            if (r == Tri.True) return Tri.True;
            if (r == Tri.Unknown) result = Tri.Unknown;
        }
        return result;
    }

    public override void CollectArgs(HashSet<string> topLevel) { foreach (var ch in children) ch.CollectArgs(topLevel); }
}

internal sealed class NotCondition(Condition inner) : Condition
{
    public override Tri Eval(EvalContext c) => inner.Eval(c) switch
    {
        Tri.True => Tri.False,
        Tri.False => Tri.True,
        _ => Tri.Unknown, // not(unknown) is unknown: a missing field can never flip a rule on
    };

    public override void CollectArgs(HashSet<string> topLevel) => inner.CollectArgs(topLevel);
}

internal sealed class TimeCondition(HashSet<DayOfWeek> days, TimeOnly from, TimeOnly to, TimeZoneInfo zone) : Condition
{
    public override Tri Eval(EvalContext c)
    {
        var local = TimeZoneInfo.ConvertTime(c.Now, zone);
        var ok = days.Contains(local.DayOfWeek) && InWindow(TimeOnly.FromTimeSpan(local.TimeOfDay));
        c.Note($"time {local:ddd HH:mm} {zone.Id} => {(ok ? Tri.True : Tri.False)}");
        return ok ? Tri.True : Tri.False;
    }

    private bool InWindow(TimeOnly t) => from == to || (from < to ? t >= from && t < to : t >= from || t < to); // from == to: the whole day; a window may wrap midnight
    public override void CollectArgs(HashSet<string> topLevel) { }
}

/// <summary>A test of one value. <see cref="OnMissing"/> is what the test says when the argument is not there at all.</summary>
internal sealed record ValueOp(string Name, Func<JsonElement, Tri> Test, Tri OnMissing = Tri.Unknown);

internal enum Quantifier { None, All, Any }

internal sealed class LeafCondition(string pointer, JsonPointer path, Quantifier quantifier, ValueOp op) : Condition
{
    public override Tri Eval(EvalContext c)
    {
        Tri r;
        try
        {
            if (!path.TryResolve(c.Args, out var value)) r = op.OnMissing;
            else if (quantifier == Quantifier.None) r = op.Test(value);
            else
            {
                var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
                if (items.Count == 0) r = Tri.Unknown; // a quantifier over nothing says nothing
                else
                {
                    var results = items.Select(op.Test).ToList();
                    r = quantifier == Quantifier.All
                        ? results.Contains(Tri.False) ? Tri.False : results.Contains(Tri.Unknown) ? Tri.Unknown : Tri.True
                        : results.Contains(Tri.True) ? Tri.True : results.Contains(Tri.Unknown) ? Tri.Unknown : Tri.False;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            r = Tri.Unknown;
        }
        c.Note($"{pointer} {(quantifier == Quantifier.None ? "" : quantifier.ToString().ToLowerInvariant() + " ")}{op.Name} => {r}");
        return r;
    }

    public override void CollectArgs(HashSet<string> topLevel)
    {
        if (path.FirstSegment is { } first) topLevel.Add(first);
    }
}

/// <summary>RFC 6901 JSON Pointer, read-only.</summary>
internal sealed class JsonPointer
{
    private readonly string[] _segments;

    private JsonPointer(string[] segments) => _segments = segments;

    public string? FirstSegment => _segments.Length > 0 ? _segments[0] : null;

    public static JsonPointer Parse(string pointer)
    {
        if (pointer.Length == 0) return new JsonPointer([]);
        if (pointer[0] != '/') throw new RuleException($"'{pointer}' is not a JSON Pointer (it must start with '/').");
        var segments = pointer[1..].Split('/').Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        return new JsonPointer(segments);
    }

    public bool TryResolve(JsonElement root, out JsonElement value)
    {
        value = root;
        if (root.ValueKind == JsonValueKind.Undefined) return false;
        foreach (var seg in _segments)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object when value.TryGetProperty(seg, out var next):
                    value = next;
                    break;
                case JsonValueKind.Array when int.TryParse(seg, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < value.GetArrayLength():
                    value = value[i];
                    break;
                default:
                    return false;
            }
        }
        return true;
    }
}

/// <summary>Compiles the JSON in a rule's <c>When</c> into conditions. Every mistake is reported with its path.</summary>
internal static class ConditionCompiler
{
    private static readonly HashSet<string> OpNames = new(StringComparer.Ordinal)
    {
        "equals", "in", "startsWith", "endsWith", "contains", "matches", "domainIn",
        "lt", "lte", "gt", "gte", "exists", "absentOrEmpty", "countAtMost", "countAtLeast",
    };

    public static Condition Compile(JsonElement e, string at)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new RuleException($"{at}: a condition must be a JSON object.");
        var keys = e.EnumerateObject().Select(p => p.Name).ToList();

        if (keys.Contains("arg")) return CompileLeaf(e, at);

        if (keys.Count != 1) throw new RuleException($"{at}: a condition has exactly one of all, any, not, time (or an 'arg' test); found {string.Join(", ", keys)}.");
        var key = keys[0];
        var value = e.GetProperty(key);
        switch (key)
        {
            case "all":
            case "any":
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                    throw new RuleException($"{at}/{key}: needs a non-empty array of conditions.");
                var children = value.EnumerateArray().Select((c, i) => Compile(c, $"{at}/{key}/{i}")).ToList();
                return key == "all" ? new AllCondition(children) : new AnyCondition(children);
            }
            case "not":
                return new NotCondition(Compile(value, $"{at}/not"));
            case "time":
                return CompileTime(value, $"{at}/time");
            default:
                throw new RuleException($"{at}: unknown condition '{key}'.");
        }
    }

    private static Condition CompileLeaf(JsonElement e, string at)
    {
        var pointerText = e.GetProperty("arg").ValueKind == JsonValueKind.String ? e.GetProperty("arg").GetString()! : throw new RuleException($"{at}/arg: must be a JSON Pointer string.");
        JsonPointer pointer;
        try { pointer = JsonPointer.Parse(pointerText); }
        catch (RuleException ex) { throw new RuleException($"{at}/arg: {ex.Message}"); }

        var quantifier = Quantifier.None;
        JsonElement? opObject = null;
        var ownOps = new List<string>();
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name == "arg") continue;
            if (p.Name is "all" or "any")
            {
                if (quantifier != Quantifier.None) throw new RuleException($"{at}: use either all or any, not both.");
                quantifier = p.Name == "all" ? Quantifier.All : Quantifier.Any;
                opObject = p.Value;
            }
            else if (OpNames.Contains(p.Name) || p.Name == "ignoreCase") ownOps.Add(p.Name);
            else throw new RuleException($"{at}: unknown test '{p.Name}'.");
        }

        JsonElement opHolder;
        if (quantifier != Quantifier.None)
        {
            if (ownOps.Count > 0) throw new RuleException($"{at}: with {quantifier.ToString().ToLowerInvariant()}, put the test inside it.");
            if (opObject!.Value.ValueKind != JsonValueKind.Object) throw new RuleException($"{at}/{quantifier.ToString().ToLowerInvariant()}: must be an object holding one test.");
            opHolder = opObject.Value;
        }
        else opHolder = e;

        var op = CompileOp(opHolder, quantifier == Quantifier.None ? at : $"{at}/{quantifier.ToString().ToLowerInvariant()}");
        return new LeafCondition(pointerText, pointer, quantifier, op);
    }

    private static ValueOp CompileOp(JsonElement holder, string at)
    {
        var ignoreCase = holder.TryGetProperty("ignoreCase", out var ic) && ic.ValueKind == JsonValueKind.True;
        var tests = holder.EnumerateObject().Where(p => OpNames.Contains(p.Name)).ToList();
        if (tests.Count != 1) throw new RuleException($"{at}: needs exactly one test ({string.Join(", ", OpNames)}); found {tests.Count}.");
        var name = tests[0].Name;
        var arg = tests[0].Value.Clone();
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        string Str() => arg.ValueKind == JsonValueKind.String ? arg.GetString()! : throw new RuleException($"{at}/{name}: needs a string.");
        decimal Num() => arg.ValueKind == JsonValueKind.Number && arg.TryGetDecimal(out var d) ? d : throw new RuleException($"{at}/{name}: needs a number.");
        int Int() => arg.ValueKind == JsonValueKind.Number && arg.TryGetInt32(out var i) && i >= 0 ? i : throw new RuleException($"{at}/{name}: needs a whole number of at least 0.");

        switch (name)
        {
            case "equals":
                if (arg.ValueKind is JsonValueKind.Object or JsonValueKind.Array) throw new RuleException($"{at}/equals: needs a string, number, true/false or null.");
                return new ValueOp(name, v => Equal(v, arg, cmp));

            case "in":
            {
                if (arg.ValueKind != JsonValueKind.Array || arg.GetArrayLength() == 0 || arg.EnumerateArray().Any(x => x.ValueKind is JsonValueKind.Object or JsonValueKind.Array))
                    throw new RuleException($"{at}/in: needs a non-empty array of strings, numbers, booleans or null.");
                var options = arg.EnumerateArray().ToList();
                return new ValueOp(name, v =>
                {
                    var results = options.Select(o => Equal(v, o, cmp)).ToList();
                    return results.Contains(Tri.True) ? Tri.True : results.Contains(Tri.Unknown) ? Tri.Unknown : Tri.False;
                });
            }

            case "startsWith": { var s = Str(); return new ValueOp(name, v => v.ValueKind == JsonValueKind.String ? Bool(v.GetString()!.StartsWith(s, cmp)) : Tri.Unknown); }
            case "endsWith": { var s = Str(); return new ValueOp(name, v => v.ValueKind == JsonValueKind.String ? Bool(v.GetString()!.EndsWith(s, cmp)) : Tri.Unknown); }
            case "contains": { var s = Str(); return new ValueOp(name, v => v.ValueKind == JsonValueKind.String ? Bool(v.GetString()!.Contains(s, cmp)) : Tri.Unknown); }

            case "matches":
            {
                Regex re;
                try
                {
                    // NonBacktracking runs in linear time, so a hostile argument cannot make a pattern explode; the timeout is a second guard.
                    re = new Regex(Str(), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : 0),
                        TimeSpan.FromMilliseconds(50));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    throw new RuleException($"{at}/matches: {ex.Message}");
                }
                return new ValueOp(name, v =>
                {
                    if (v.ValueKind != JsonValueKind.String) return Tri.Unknown;
                    try { return Bool(re.IsMatch(v.GetString()!)); }
                    catch (RegexMatchTimeoutException) { return Tri.Unknown; }
                });
            }

            case "domainIn":
            {
                if (arg.ValueKind != JsonValueKind.Array || arg.GetArrayLength() == 0 || arg.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                    throw new RuleException($"{at}/domainIn: needs a non-empty array of domain names.");
                var domains = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in arg.EnumerateArray())
                {
                    if (!Domains.TryNormalise(d.GetString()!, out var norm)) throw new RuleException($"{at}/domainIn: '{d.GetString()}' is not a valid domain name.");
                    domains.Add(norm);
                }
                return new ValueOp(name, v =>
                    v.ValueKind == JsonValueKind.String && Domains.TryDomainOfAddress(v.GetString()!, out var domain)
                        ? Bool(domains.Contains(domain))
                        : Tri.Unknown);
            }

            case "lt": { var n = Num(); return new ValueOp(name, v => Number(v, x => x < n)); }
            case "lte": { var n = Num(); return new ValueOp(name, v => Number(v, x => x <= n)); }
            case "gt": { var n = Num(); return new ValueOp(name, v => Number(v, x => x > n)); }
            case "gte": { var n = Num(); return new ValueOp(name, v => Number(v, x => x >= n)); }

            case "exists":
            {
                if (arg.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new RuleException($"{at}/exists: needs true or false.");
                var want = arg.ValueKind == JsonValueKind.True;
                return new ValueOp(name, v => Bool((v.ValueKind != JsonValueKind.Null) == want), OnMissing: Bool(!want));
            }

            case "absentOrEmpty":
            {
                if (arg.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new RuleException($"{at}/absentOrEmpty: needs true or false.");
                var want = arg.ValueKind == JsonValueKind.True;
                return new ValueOp(name, v => Bool(IsEmpty(v) == want), OnMissing: Bool(want));
            }

            case "countAtMost": { var n = Int(); return new ValueOp(name, v => v.ValueKind == JsonValueKind.Array ? Bool(v.GetArrayLength() <= n) : Tri.Unknown, OnMissing: Tri.True); }
            case "countAtLeast": { var n = Int(); return new ValueOp(name, v => v.ValueKind == JsonValueKind.Array ? Bool(v.GetArrayLength() >= n) : Tri.Unknown, OnMissing: Bool(n == 0)); }
        }
        throw new RuleException($"{at}: unknown test '{name}'.");
    }

    private static Condition CompileTime(JsonElement e, string at)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new RuleException($"{at}: must be an object with days, from, to and zone.");
        foreach (var p in e.EnumerateObject())
            if (p.Name is not ("days" or "from" or "to" or "zone")) throw new RuleException($"{at}: unknown setting '{p.Name}'.");

        var days = new HashSet<DayOfWeek>();
        if (e.TryGetProperty("days", out var d))
        {
            if (d.ValueKind != JsonValueKind.Array || d.GetArrayLength() == 0) throw new RuleException($"{at}/days: needs a non-empty array like [\"Mon\",\"Tue\"].");
            foreach (var x in d.EnumerateArray())
            {
                var name = x.ValueKind == JsonValueKind.String ? x.GetString()! : "";
                if (name.Length < 3 || !Enum.TryParse<DayOfWeek>(DayName(name), true, out var dow)) throw new RuleException($"{at}/days: '{name}' is not a day (use Mon, Tue, ...).");
                days.Add(dow);
            }
        }
        else foreach (var dow in Enum.GetValues<DayOfWeek>()) days.Add(dow);

        TimeOnly Time(string key, TimeOnly fallback) =>
            !e.TryGetProperty(key, out var v) ? fallback
            : v.ValueKind == JsonValueKind.String && TimeOnly.TryParseExact(v.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t
            : throw new RuleException($"{at}/{key}: use 24-hour HH:mm, like \"08:30\".");

        var from = Time("from", TimeOnly.MinValue);
        var to = Time("to", TimeOnly.MinValue); // neither given: from == to, which means the whole day

        TimeZoneInfo zone;
        try { zone = e.TryGetProperty("zone", out var z) && z.ValueKind == JsonValueKind.String ? TimeZoneInfo.FindSystemTimeZoneById(z.GetString()!) : TimeZoneInfo.Utc; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new RuleException($"{at}/zone: unknown time zone. Use an IANA name like Europe/Amsterdam."); }

        return new TimeCondition(days, from, to, zone);
    }

    private static string DayName(string s) => s.Length == 3
        ? s.ToLowerInvariant() switch { "mon" => "Monday", "tue" => "Tuesday", "wed" => "Wednesday", "thu" => "Thursday", "fri" => "Friday", "sat" => "Saturday", "sun" => "Sunday", _ => s }
        : s;

    private static Tri Bool(bool b) => b ? Tri.True : Tri.False;

    private static Tri Number(JsonElement v, Func<decimal, bool> test) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? Bool(test(d)) : Tri.Unknown;

    private static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => v.GetString()!.Length == 0,
        JsonValueKind.Array => v.GetArrayLength() == 0,
        JsonValueKind.Object => !v.EnumerateObject().Any(),
        _ => false,
    };

    /// <summary>Equality between a value and a literal. Values of a different kind are not "different", they are unknown.</summary>
    private static Tri Equal(JsonElement v, JsonElement literal, StringComparison cmp)
    {
        switch (literal.ValueKind)
        {
            case JsonValueKind.String:
                return v.ValueKind == JsonValueKind.String ? Bool(string.Equals(v.GetString(), literal.GetString(), cmp)) : Tri.Unknown;
            case JsonValueKind.Number:
                return v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var a) && literal.TryGetDecimal(out var b) ? Bool(a == b) : Tri.Unknown;
            case JsonValueKind.True:
            case JsonValueKind.False:
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? Bool(v.ValueKind == literal.ValueKind) : Tri.Unknown;
            case JsonValueKind.Null:
                return Bool(v.ValueKind == JsonValueKind.Null);
            default:
                return Tri.Unknown;
        }
    }
}

/// <summary>Strict address and domain handling for <c>domainIn</c>: exact domains only, no tricks.</summary>
internal static class Domains
{
    // Exactly one '@', nothing that could hide a second address (display names, lists, comments, quoting).
    private static readonly Regex BareAddress = new(@"^[^@\s<>,;""()\[\]\\]+@[^@\s<>,;""()\[\]\\]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly IdnMapping Idn = new();

    public static bool TryDomainOfAddress(string address, out string domain)
    {
        domain = "";
        if (!BareAddress.IsMatch(address)) return false;
        return TryNormalise(address[(address.LastIndexOf('@') + 1)..], out domain);
    }

    /// <summary>Lower-case, punycode, no trailing dot, plausible labels.</summary>
    public static bool TryNormalise(string domain, out string normalised)
    {
        normalised = "";
        if (domain.Length == 0 || domain.EndsWith('.') || domain.StartsWith('.') || domain.Contains("..", StringComparison.Ordinal)) return false;
        try { normalised = Idn.GetAscii(domain).ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
        return normalised.Length > 0 && normalised.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');
    }
}
