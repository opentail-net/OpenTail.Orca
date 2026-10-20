using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenTail.Host;

/// <summary>
/// Measures a successful result for the usage counters: its size, and the counts its tool's <see cref="ResultCounts"/> point at.
/// Reads only the paths declared; values never leave this class except as salted hashes of entity keys.
/// </summary>
internal static class ResultMeasure
{
    /// <summary>Entity keys counted per result, at most: enough for "distinct customers", bounded for a huge result.</summary>
    public const int MaxEntityKeys = 1000;

    public static long Bytes(string resultJson) => Encoding.UTF8.GetByteCount(resultJson);

    public static (ResultStats? Stats, IReadOnlyList<string> EntityHashes) Measure(ResultCounts? counts, string argumentsJson, string resultJson, byte[]? salt)
    {
        if (counts is null) return (null, []);
        int? records = null, recipients = null;
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var result = JsonDocument.Parse(resultJson);
            if (counts.Records is { } rp && Select(result.RootElement, rp).FirstOrDefault() is { ValueKind: not JsonValueKind.Undefined } r)
                records = r.ValueKind switch
                {
                    JsonValueKind.Array => r.GetArrayLength(),
                    JsonValueKind.Number when r.TryGetInt32(out var n) && n >= 0 => n,
                    _ => null,
                };
            if (counts.EntityKeys is { } ep && salt is not null)
                foreach (var k in Select(result.RootElement, ep))
                {
                    if (hashes.Count >= MaxEntityKeys) break;
                    if (k.ValueKind is JsonValueKind.String or JsonValueKind.Number && k.ToString() is { Length: > 0 } key)
                        hashes.Add(Hash(salt, key));
                }
        }
        catch (JsonException) { /* not JSON: nothing to count */ }

        if (counts.RecipientArgs.Count > 0)
            try
            {
                using var args = JsonDocument.Parse(argumentsJson);
                var n = 0;
                foreach (var p in counts.RecipientArgs)
                    foreach (var v in Select(args.RootElement, p))
                        n += v.ValueKind switch { JsonValueKind.Array => v.GetArrayLength(), JsonValueKind.String => 1, _ => 0 };
                recipients = n;
            }
            catch (JsonException) { }

        int? entities = counts.EntityKeys is not null && salt is not null ? hashes.Count : null;
        return records is null && recipients is null && entities is null ? (null, []) : (new ResultStats(records, recipients, entities), hashes.ToList());
    }

    private static string Hash(byte[] salt, string key) => Convert.ToHexStringLower(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(key)))[..32];

    /// <summary>A JSON Pointer with <c>*</c> for every array item. Missing steps select nothing.</summary>
    internal static IEnumerable<JsonElement> Select(JsonElement root, string pointer)
    {
        IEnumerable<JsonElement> current = [root];
        if (pointer.Length == 0) return current;
        if (pointer[0] != '/') return [];
        foreach (var raw in pointer[1..].Split('/'))
        {
            var step = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current.SelectMany(e => Step(e, step)).ToList();
        }
        return current;

        static IEnumerable<JsonElement> Step(JsonElement e, string step)
        {
            if (step == "*" && e.ValueKind == JsonValueKind.Array) return e.EnumerateArray();
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(step, out var v)) return [v];
            if (e.ValueKind == JsonValueKind.Array && int.TryParse(step, out var i) && i >= 0 && i < e.GetArrayLength()) return [e[i]];
            return [];
        }
    }
}
