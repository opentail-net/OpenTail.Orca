using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenTail.Modules.OpenApi;

/// <summary>
/// A query for an API that takes one filter expression (Jira JQL, OData <c>$filter</c>, Salesforce SOQL, Zendesk and Freshdesk
/// search). <b>The model never writes the expression.</b> It sets typed filters the profile declares; the builder writes the
/// expression, quoting and escaping every value for the language, and refuses a value it cannot quote safely. Field names, the
/// fixed clause and the ordering come from the profile, never from the model.
/// </summary>
public sealed partial class ProfileQuery
{
    /// <summary><c>jql</c>, <c>cql</c> (Confluence; quoted as JQL), <c>odata</c>, <c>soql</c>, <c>zendesk</c> or <c>freshdesk</c>.</summary>
    public string Language { get; init; } = "";

    /// <summary>Where the expression goes: a query parameter of the operation, or <c>body.&lt;field&gt;</c>.</summary>
    public string Target { get; init; } = "";

    /// <summary>SOQL only: the query's start, for example <c>SELECT Id, Subject FROM Case</c>. Required for SOQL.</summary>
    public string? Select { get; init; }

    /// <summary>A clause always ANDed in, written by the profile (<c>project = OPS</c>, <c>type:ticket</c>). Trusted as written.</summary>
    public string? Where { get; init; }

    /// <summary>Appended as written: <c>ORDER BY updated DESC</c> (JQL), <c>ORDER BY LastModifiedDate DESC LIMIT 50</c> (SOQL).</summary>
    public string? OrderBy { get; init; }

    /// <summary>The filters the model may set, by argument name.</summary>
    public Dictionary<string, QueryFilter> Filters { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Longest expression sent; longer is refused. Default 2000 (Freshdesk allows 512: set it).</summary>
    public int MaxLength { get; init; } = 2000;

    internal string? BodyField => Target.StartsWith("body.", StringComparison.Ordinal) ? Target[5..] : null;

    internal static readonly string[] Languages = ["jql", "cql", "odata", "soql", "zendesk", "freshdesk"];

    /// <summary>Confluence's CQL quotes and joins exactly as JQL does.</summary>
    private string Dialect => Language == "cql" ? "jql" : Language;

    internal void Validate(string at, Dictionary<string, ProfileSetting> config, List<string> errors)
    {
        if (!Languages.Contains(Language)) errors.Add($"{at}: Language must be one of {string.Join(", ", Languages)}.");
        if (string.IsNullOrWhiteSpace(Target)) errors.Add($"{at}: Target (the parameter or body.field that receives the expression) is required.");
        if (Language == "soql" && string.IsNullOrWhiteSpace(Select)) errors.Add($"{at}: SOQL needs Select (SELECT ... FROM ...).");
        if (Language != "soql" && Select is not null) errors.Add($"{at}: Select is for SOQL only.");
        if (Filters.Count == 0) errors.Add($"{at}: Filters must declare at least one filter.");
        foreach (var (name, f) in Filters)
        {
            var fat = $"{at}.Filters.{name}";
            if (!FilterName().IsMatch(name)) errors.Add($"{fat}: filter names are lower case letters, digits and _.");
            f.Validate(fat, Dialect, config, errors);
        }
    }

    /// <summary>Builds the expression from the model's filter values. Throws <see cref="ArgumentException"/> for anything it cannot write safely.</summary>
    internal string Build(IReadOnlyDictionary<string, JsonElement> given, IReadOnlyDictionary<string, IReadOnlyList<string>> settings, Dictionary<string, string> previewValues)
    {
        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(Where)) clauses.Add(Dialect is "odata" or "soql" or "jql" or "freshdesk" ? "(" + Where.Trim() + ")" : Where.Trim());
        foreach (var (name, f) in Filters)
        {
            if (!given.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                if (f.Required) throw new ArgumentException($"Missing required filter '{name}'.");
                continue;
            }
            clauses.Add(f.Clause(name, v, Dialect, settings));
            previewValues[name] = v.ValueKind == JsonValueKind.Array ? string.Join(", ", v.EnumerateArray().Select(e => e.ToString())) : v.ToString();
        }

        var expression = Dialect switch
        {
            "jql" => Join(clauses, " AND ") + Suffix(),
            "odata" => Join(clauses, " and "),
            "soql" => Select!.Trim() + (clauses.Count > 0 ? " WHERE " + Join(clauses, " AND ") : "") + Suffix(),
            "zendesk" => Join(clauses, " "),
            "freshdesk" => "\"" + Join(clauses, " AND ") + "\"",
            _ => throw new InvalidOperationException(),
        };
        if (expression.Length > MaxLength) throw new ArgumentException($"The query is {expression.Length} characters; at most {MaxLength} are allowed. Use fewer filters or values.");
        previewValues["query"] = expression;
        return expression;
    }

    private string Suffix() => string.IsNullOrWhiteSpace(OrderBy) ? "" : " " + OrderBy.Trim();

    private static string Join(List<string> clauses, string with) => string.Join(with, clauses);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$")]
    private static partial Regex FilterName();
}

/// <summary>One filter the model may set.</summary>
public sealed partial class QueryFilter
{
    /// <summary>The field the filter compares (the API's name). Required except for free text in JQL and Zendesk.</summary>
    public string? Field { get; init; }

    /// <summary><c>string</c>, <c>number</c>, <c>bool</c>, <c>date</c> (yyyy-MM-dd), <c>datetime</c>, <c>text</c> (free-text search) or <c>list</c> (several strings, any of them).</summary>
    public string Type { get; init; } = "string";

    /// <summary><c>eq</c> (default), <c>ne</c>, <c>gt</c>, <c>ge</c>, <c>lt</c>, <c>le</c>. Not for <c>text</c> or <c>list</c>.</summary>
    public string Op { get; init; } = "eq";

    public bool Required { get; init; }
    public string? Description { get; init; }
    public List<string>? Enum { get; init; }
    public string? AllowedFrom { get; init; }
    public string? Pattern { get; init; }
    public int? MaxLength { get; init; } = 200;
    public double? Min { get; init; }
    public double? Max { get; init; }

    /// <summary>Most values a <c>list</c> filter takes. Default 20.</summary>
    public int MaxItems { get; init; } = 20;

    [JsonIgnore] private ParameterRule rule = new();

    private static readonly string[] Types = ["string", "number", "bool", "date", "datetime", "text", "list"];
    private static readonly string[] Ops = ["eq", "ne", "gt", "ge", "lt", "le"];

    internal void Validate(string at, string language, Dictionary<string, ProfileSetting> config, List<string> errors)
    {
        if (!Types.Contains(Type)) errors.Add($"{at}: Type must be one of {string.Join(", ", Types)}.");
        if (!Ops.Contains(Op)) errors.Add($"{at}: Op must be one of {string.Join(", ", Ops)}.");
        if (Type is "text" or "list" && Op != "eq") errors.Add($"{at}: a {Type} filter takes no Op.");
        var textWithoutField = Type == "text" && language is "jql" or "zendesk";
        if (string.IsNullOrWhiteSpace(Field) && !textWithoutField) errors.Add($"{at}: Field is required.");
        if (Field is { } f && !FieldName().IsMatch(f)) errors.Add($"{at}: Field '{f}' must be letters, digits, '_', '.', '-' or a JQL custom field (cf[123]).");
        if (language == "freshdesk" && Type == "text") errors.Add($"{at}: Freshdesk filter queries have no free-text search.");
        if (language == "freshdesk" && Op is "ne" or "ge" or "le") errors.Add($"{at}: Freshdesk filter queries support only eq, gt (:>) and lt (:<).");
        if (Type is "bool" && Op != "eq" && Op != "ne") errors.Add($"{at}: a bool filter takes eq or ne.");
        rule = new ParameterRule { Enum = Enum, AllowedFrom = AllowedFrom, Pattern = Pattern, MaxLength = MaxLength, Min = Min, Max = Max };
        rule.Validate(at, config, errors);
    }

    /// <summary>The model-facing schema of this filter.</summary>
    internal JsonObject Schema(IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        JsonObject item = Type switch
        {
            "number" => new JsonObject { ["type"] = "number" },
            "bool" => new JsonObject { ["type"] = "boolean" },
            "date" => new JsonObject { ["type"] = "string", ["format"] = "date" },
            "datetime" => new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            _ => new JsonObject { ["type"] = "string" },
        };
        rule.Narrow(item, settings);
        item.Remove("description");
        var schema = Type == "list" ? new JsonObject { ["type"] = "array", ["items"] = item, ["maxItems"] = MaxItems } : item;
        schema["description"] = Description ?? DefaultDescription();
        return schema;
    }

    private string DefaultDescription() => Type switch
    {
        "text" => "Free-text search.",
        "list" => $"Any of these values of {Field}.",
        _ => $"{Field} {Op switch { "eq" => "equals", "ne" => "is not", "gt" => "after / greater than", "ge" => "on or after / at least", "lt" => "before / less than", _ => "on or before / at most" }} this value.",
    };

    internal string Clause(string name, JsonElement value, string language, IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        if (Type == "list")
        {
            if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException($"'{name}' must be a list of strings.");
            var items = value.EnumerateArray().ToList();
            if (items.Count == 0) throw new ArgumentException($"'{name}' needs at least one value.");
            if (items.Count > MaxItems) throw new ArgumentException($"'{name}' takes at most {MaxItems} values.");
            foreach (var i in items) rule.Check(name, i, settings);
            var texts = items.Select(i => Safe(name, i.ValueKind == JsonValueKind.String ? i.GetString()! : throw new ArgumentException($"'{name}' must be a list of strings."))).ToList();
            return language switch
            {
                "jql" => $"{Field} in ({string.Join(", ", texts.Select(Jql))})",
                "odata" => "(" + string.Join(" or ", texts.Select(t => $"{Field} eq {OData(t)}")) + ")",
                "soql" => $"{Field} IN ({string.Join(", ", texts.Select(t => Soql(t)))})",
                "zendesk" => string.Join(" ", texts.Select(t => $"{Field}:{Zendesk(name, t)}")),
                "freshdesk" => "(" + string.Join(" OR ", texts.Select(t => $"{Field}:{Freshdesk(name, t)}")) + ")",
                _ => throw new InvalidOperationException(),
            };
        }

        rule.Check(name, value, settings);
        if (Type == "text")
        {
            var text = Safe(name, Str(name, value));
            return language switch
            {
                // JQL text search runs Lucene syntax inside the quotes; its operator characters are made spaces, so the value is only words.
                "jql" => $"{Field ?? "text"} ~ {Jql(LuceneOperators().Replace(text, " ").Trim())}",
                "odata" => $"contains({Field},{OData(text)})",
                "soql" => $"{Field} LIKE {Soql(text, like: true)}",
                "zendesk" => (Field is null ? "" : Field + ":") + Zendesk(name, text),
                _ => throw new InvalidOperationException(),
            };
        }

        var literal = Literal(name, value, language);
        if (language == "zendesk")
        {
            var op = Op switch { "eq" => ":", "ne" => ":", "gt" => ">", "ge" => ">=", "lt" => "<", "le" => "<=", _ => ":" };
            return (Op == "ne" ? "-" : "") + Field + op + literal;
        }
        if (language == "freshdesk")
            return Field + (Op switch { "gt" => ":>", "lt" => ":<", _ => ":" }) + literal;

        var sym = (language, Op) switch
        {
            ("odata", _) => " " + Op + " ",
            (_, "eq") => " = ",
            (_, "ne") => " != ",
            (_, "gt") => " > ",
            (_, "ge") => " >= ",
            (_, "lt") => " < ",
            _ => " <= ",
        };
        return Field + sym + literal;
    }

    private string Literal(string name, JsonElement value, string language)
    {
        switch (Type)
        {
            case "number":
                if (value.ValueKind != JsonValueKind.Number) throw new ArgumentException($"'{name}' must be a number.");
                return value.GetDouble().ToString("R", CultureInfo.InvariantCulture);
            case "bool":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"'{name}' must be true or false.");
                return value.ValueKind == JsonValueKind.True ? "true" : "false";
            case "date":
            case "datetime":
                var raw = Str(name, value);
                if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                    throw new ArgumentException($"'{name}' must be a date (yyyy-MM-dd{(Type == "datetime" ? ", or with a time" : "")}).");
                var date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var time = d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                return language switch
                {
                    "jql" => Jql(Type == "date" ? date : d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                    "odata" or "soql" => Type == "date" ? date : time,
                    "zendesk" => Type == "date" ? date : time,
                    _ => Freshdesk(name, date), // Freshdesk filters compare dates as 'yyyy-MM-dd'
                };
            default:
                var text = Safe(name, Str(name, value));
                return language switch
                {
                    "jql" => Jql(text),
                    "odata" => OData(text),
                    "soql" => Soql(text),
                    "zendesk" => Zendesk(name, text),
                    _ => Freshdesk(name, text),
                };
        }
    }

    private static string Str(string name, JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : throw new ArgumentException($"'{name}' must be a string.");

    /// <summary>No control characters anywhere: a newline or NUL has no business in a filter value and some parsers end a clause there.</summary>
    private static string Safe(string name, string s)
    {
        if (s.Length == 0) throw new ArgumentException($"'{name}' must not be empty.");
        if (s.Any(char.IsControl)) throw new ArgumentException($"'{name}' must not contain control characters.");
        return s;
    }

    /// <summary>JQL string literal: double quotes, with backslash and quote escaped.</summary>
    internal static string Jql(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>OData string literal: single quotes, a quote doubled.</summary>
    internal static string OData(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>SOQL string literal: single quotes, backslash escapes; in LIKE, % and _ are escaped too and the value is wrapped in %.</summary>
    internal static string Soql(string s, bool like = false)
    {
        var e = s.Replace("\\", "\\\\").Replace("'", "\\'");
        if (like) e = "%" + e.Replace("%", "\\%").Replace("_", "\\_") + "%";
        return "'" + e + "'";
    }

    /// <summary>Zendesk search value: always in double quotes. Zendesk has no escape for a quote inside, so a value with one is refused.</summary>
    internal static string Zendesk(string name, string s)
    {
        if (s.Contains('"')) throw new ArgumentException($"'{name}' cannot contain a double quote (Zendesk search cannot escape it).");
        return "\"" + s + "\"";
    }

    /// <summary>Freshdesk filter value: single quotes; quotes of either kind cannot be escaped in a filter query, so they are refused.</summary>
    internal static string Freshdesk(string name, string s)
    {
        if (s.Contains('\'') || s.Contains('"')) throw new ArgumentException($"'{name}' cannot contain quotes (Freshdesk filter queries cannot escape them).");
        return "'" + s + "'";
    }

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_.\-]{0,99}|cf\[\d{1,10}\])$")]
    private static partial Regex FieldName();

    [GeneratedRegex(@"[+\-&|!(){}\[\]^~*?\\:/""]")]
    private static partial Regex LuceneOperators();
}
