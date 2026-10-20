using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenTail.Modules.OpenApi;

/// <summary>
/// What of an answer reaches the model. <c>Items</c> picks the list (one result per item; without it the whole answer is one result),
/// <c>Fields</c> names what to keep, each a path (<c>$.a.b</c>, <c>$.a[0]</c>, <c>$.a[*].b</c>), optionally <c>|number</c>, or a text
/// with paths in braces (<c>"https://example.com/t/{$.id}"</c>). Everything not named is dropped. Text is cut at <c>MaxTextChars</c>.
/// </summary>
public sealed class ResponseRule
{
    public string? Items { get; init; }
    public Dictionary<string, string>? Fields { get; init; }
    public int? MaxItems { get; init; }
    public int? MaxTextChars { get; init; }

    /// <summary>Fields whose value is a person's handle or name: replaced by a stable pseudonym.</summary>
    public List<string> Pseudonymise { get; init; } = [];

    /// <summary>Fields of free text in which @mentions and u/names are replaced by pseudonyms.</summary>
    public List<string> PseudonymiseMentions { get; init; } = [];

    internal void Validate(string at, List<string> errors)
    {
        if (Items is not null && !JsonPathLite.IsValid(Items)) errors.Add($"{at}.Items: '{Items}' is not a path such as $.items.");
        foreach (var (k, v) in Fields ?? [])
            if (!ResponseShaper.IsValidField(v)) errors.Add($"{at}.Fields.{k}: '{v}' is not a path ($.a.b, optionally |number) or a text with {{$.path}} parts.");
        foreach (var f in Pseudonymise.Concat(PseudonymiseMentions))
            if (Fields is null || !Fields.ContainsKey(f)) errors.Add($"{at}: '{f}' is pseudonymised but is not one of Fields.");
        if (MaxItems is < 1) errors.Add($"{at}.MaxItems must be at least 1.");
        if (MaxTextChars is < 1) errors.Add($"{at}.MaxTextChars must be at least 1.");
    }
}

internal static partial class ResponseShaper
{
    public static JsonNode? Shape(JsonElement answer, ResponseRule rule, Func<string, string> pseudonym)
    {
        if (rule.Items is null) return Text(rule.Fields is null ? JsonNode.Parse(answer.GetRawText()) : One(answer, rule, pseudonym), rule.MaxTextChars);
        var list = new JsonArray();
        foreach (var item in JsonPathLite.Select(answer, rule.Items).SelectMany(Spread))
        {
            if (rule.MaxItems is { } max && list.Count >= max) break;
            list.Add(Text(rule.Fields is null ? JsonNode.Parse(item.GetRawText()) : One(item, rule, pseudonym), rule.MaxTextChars));
        }
        return list;

        static IEnumerable<JsonElement> Spread(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [e];
    }

    private static JsonObject One(JsonElement item, ResponseRule rule, Func<string, string> pseudonym)
    {
        var o = new JsonObject();
        foreach (var (name, spec) in rule.Fields!)
        {
            var v = Field(item, spec);
            if (v is null) continue;
            if (rule.Pseudonymise.Contains(name) && v is JsonValue pv && pv.TryGetValue<string>(out var handle) && handle.Length > 0 && handle is not ("[deleted]" or "[removed]"))
                v = pseudonym(handle);
            if (rule.PseudonymiseMentions.Contains(name) && v is JsonValue tv && tv.TryGetValue<string>(out var text))
                v = Mentions(text, pseudonym);
            o[name] = v;
        }
        return o;
    }

    private static JsonNode? Field(JsonElement item, string spec)
    {
        if (!spec.StartsWith('$'))
            return Template().Replace(spec, m => JsonPathLite.Select(item, m.Groups[1].Value).FirstOrDefault() is { } e ? Scalar(e) : "");
        var (path, modifier) = SplitModifier(spec);
        var found = JsonPathLite.Select(item, path).ToList();
        if (found.Count == 0) return null;
        JsonNode? Convert(JsonElement e) => modifier switch
        {
            "number" => Number(e),
            "html" => e.ValueKind == JsonValueKind.String ? JsonValue.Create(HtmlToText(e.GetString() ?? "")) : null,
            "storage" => e.ValueKind == JsonValueKind.String ? JsonValue.Create(OpenTail.Connectivity.RichText.ConfluenceStorageConverter.ToText(e.GetString() ?? "")) : null,
            "adf" => e.ValueKind == JsonValueKind.Object ? JsonValue.Create(OpenTail.Connectivity.RichText.AdfTextConverter.ToText(e))
                : e.ValueKind == JsonValueKind.String ? JsonValue.Create(e.GetString()) : null,
            _ => JsonNode.Parse(e.GetRawText()),
        };
        return path.Contains("[*]") ? new JsonArray(found.Select(Convert).ToArray()) : Convert(found[0]);
    }

    /// <summary>Converters a field path may end with: <c>|number</c>, <c>|html</c> (HTML as text), <c>|storage</c> (Confluence storage
    /// format as text) and <c>|adf</c> (Atlassian Document Format, Jira and Confluence, as text).</summary>
    private static readonly string[] Modifiers = ["number", "html", "storage", "adf"];

    private static (string Path, string? Modifier) SplitModifier(string spec)
    {
        foreach (var m in Modifiers)
            if (spec.EndsWith("|" + m, StringComparison.Ordinal)) return (spec[..^(m.Length + 1)], m);
        return (spec, null);
    }

    private static JsonNode? Number(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => JsonNode.Parse(e.GetRawText()),
        JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
        JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static string Scalar(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? "" : e.GetRawText();

    /// <summary>Cuts every text in the result.</summary>
    private static JsonNode? Text(JsonNode? node, int? max)
    {
        if (max is not { } n || node is null) return node;
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s): return s.Length > n ? JsonValue.Create(s[..n] + "…") : v;
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList()) o[key] = Text(o[key]?.DeepClone(), max);
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) a[i] = Text(a[i]?.DeepClone(), max);
                return a;
            default: return node;
        }
    }

    private static string Mentions(string text, Func<string, string> pseudonym)
    {
        text = Mention().Replace(text, m => "@" + pseudonym(m.Groups[1].Value));
        return UserName().Replace(text, m => "u/" + pseudonym(m.Groups[1].Value));
    }

    /// <summary>A stable pseudonym: an HMAC of the profile name and the handle, so the same person reads the same in every answer.</summary>
    public static string Pseudonym(byte[] key, string profile, string handle) =>
        "user_" + Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(profile + ":" + handle.ToLowerInvariant())))[..10];

    /// <summary>
    /// HTML as plain text: scripts and styles dropped, block ends as line breaks, tags removed, entities decoded, blank runs
    /// collapsed. For page bodies (Confluence <c>body.view</c>, SharePoint pages), so the model reads text, not markup.
    /// </summary>
    internal static string HtmlToText(string html)
    {
        var s = HtmlDrop().Replace(html, " ");
        s = HtmlBreak().Replace(s, "\n");
        s = HtmlTag().Replace(s, " ");
        s = System.Net.WebUtility.HtmlDecode(s).Replace('\u00A0', ' ');
        var lines = s.Split('\n').Select(l => HtmlSpace().Replace(l, " ").Trim());
        return HtmlBlankLines().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    public static bool IsValidField(string spec)
    {
        if (spec.StartsWith('$'))
            return JsonPathLite.IsValid(SplitModifier(spec).Path);
        var parts = Template().Matches(spec);
        return parts.Count > 0 && parts.All(m => JsonPathLite.IsValid(m.Groups[1].Value)) && !Template().Replace(spec, "").Contains('{');
    }

    [GeneratedRegex(@"\{(\$[^{}]*)\}")]
    private static partial Regex Template();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlDrop();

    [GeneratedRegex(@"<\s*(br|/p|/div|/h[1-6]|/li|/tr|/table|/blockquote|/pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreak();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex HtmlSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex HtmlBlankLines();

    [GeneratedRegex(@"(?<![\w@])@([A-Za-z0-9_]{1,30})")]
    private static partial Regex Mention();

    [GeneratedRegex(@"(?<![\w/])/?u/([A-Za-z0-9_-]{3,20})")]
    private static partial Regex UserName();
}

/// <summary>The small path language of profiles: <c>$</c>, then <c>.name</c>, <c>[n]</c> or <c>[*]</c> steps.</summary>
internal static partial class JsonPathLite
{
    public static bool IsValid(string path) => Path().IsMatch(path);

    public static IEnumerable<JsonElement> Select(JsonElement root, string path)
    {
        IEnumerable<JsonElement> current = [root];
        foreach (Match step in Step().Matches(path[1..]))
        {
            var name = step.Groups["name"].Success ? step.Groups["name"].Value : null;
            var index = step.Groups["index"].Value;
            current = current.SelectMany(e => Next(e, name, index)).ToList();
        }
        return current.Where(e => e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
    }

    private static IEnumerable<JsonElement> Next(JsonElement e, string? name, string index)
    {
        if (name is not null)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var c)) yield return c;
            yield break;
        }
        if (e.ValueKind != JsonValueKind.Array) yield break;
        if (index == "*") { foreach (var x in e.EnumerateArray()) yield return x; yield break; }
        var i = int.Parse(index, CultureInfo.InvariantCulture);
        if (i < e.GetArrayLength()) yield return e[i];
    }

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_\-]*|\[(\d{1,6}|\*)\])*$")]
    private static partial Regex Path();

    [GeneratedRegex(@"\.(?<name>[A-Za-z_][A-Za-z0-9_\-]*)|\[(?<index>\d{1,6}|\*)\]")]
    private static partial Regex Step();
}
