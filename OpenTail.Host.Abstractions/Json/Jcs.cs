using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenTail.Host.Abstractions;

/// <summary>
/// The JSON Canonicalization Scheme (RFC 8785): the one byte sequence a JSON value has. Digests over JSON use it, so the same value
/// hashes the same way whatever its member order, whitespace or number spelling. Checked against the reference test data
/// (<c>cyberphone/json-canonicalization</c>, <c>testdata/</c>; see <c>OpenTail.Host.Abstractions.Tests</c>).
/// It refuses what RFC 8785 cannot represent: duplicate member names, unpaired surrogates, and numbers that are not finite doubles.
/// </summary>
public static class Jcs
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 256,
    };

    /// <summary>The canonical form of a JSON text. Throws <see cref="JsonException"/> when the text is not JSON, and
    /// <see cref="FormatException"/> when RFC 8785 cannot represent it.</summary>
    public static string Canonicalize(string json)
    {
        using var doc = JsonDocument.Parse(json, ParseOptions);
        return Canonicalize(doc.RootElement);
    }

    /// <summary>The canonical form of a parsed JSON value.</summary>
    public static string Canonicalize(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(element, sb);
        return sb.ToString();
    }

    private static void Write(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                // Member names sorted by UTF-16 code units (RFC 8785 section 3.2.3): ordinal comparison of .NET strings.
                var members = e.EnumerateObject().ToList();
                members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                sb.Append('{');
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0)
                    {
                        if (members[i].Name == members[i - 1].Name)
                            throw new FormatException($"Duplicate member name '{members[i].Name}' cannot be canonicalized.");
                        sb.Append(',');
                    }
                    WriteString(members[i].Name, sb);
                    sb.Append(':');
                    Write(members[i].Value, sb);
                }
                sb.Append('}');
                return;
            }
            case JsonValueKind.Array:
            {
                sb.Append('[');
                var first = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(item, sb);
                }
                sb.Append(']');
                return;
            }
            case JsonValueKind.String:
                WriteString(ReadString(e), sb);
                return;
            case JsonValueKind.Number:
                WriteNumber(e.GetDouble(), sb);
                return;
            case JsonValueKind.True:
                sb.Append("true");
                return;
            case JsonValueKind.False:
                sb.Append("false");
                return;
            case JsonValueKind.Null:
                sb.Append("null");
                return;
            default:
                throw new FormatException($"JSON value kind {e.ValueKind} cannot be canonicalized.");
        }
    }

    /// <summary>The text of a string. System.Text.Json refuses an escaped surrogate that has no partner with an
    /// <see cref="InvalidOperationException"/>; that is a value RFC 8785 cannot represent, so it is reported as one.</summary>
    private static string ReadString(JsonElement e)
    {
        try
        {
            return e.GetString() ?? throw new FormatException("A JSON string could not be read.");
        }
        catch (InvalidOperationException)
        {
            throw new FormatException("An unpaired surrogate cannot be canonicalized.");
        }
    }

    /// <summary>RFC 8785 section 3.2.2.2: only the quotation mark, the backslash and control characters are escaped; the
    /// rest of the text is written as it is, and control characters use the short forms where there are any.</summary>
    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c).Append(s[++i]);
                    continue;
                }
                throw new FormatException("An unpaired surrogate cannot be canonicalized.");
            }
            if (char.IsLowSurrogate(c)) throw new FormatException("An unpaired surrogate cannot be canonicalized.");

            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>RFC 8785 section 3.2.2.3, which is ECMAScript's Number.prototype.toString: the shortest digits that read back to
    /// the same double, laid out without an exponent for magnitudes from 1e-6 up to 1e21, and with one otherwise.</summary>
    private static void WriteNumber(double value, StringBuilder sb)
    {
        if (!double.IsFinite(value)) throw new FormatException("JSON numbers must be finite to be canonicalized.");
        if (value == 0)
        {
            sb.Append('0'); // -0 is written as 0
            return;
        }
        if (value < 0)
        {
            sb.Append('-');
            value = -value;
        }

        // "R" is the shortest round-trip form on .NET Core 3.0 and later. Pull out its digits and the decimal point position.
        var r = value.ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOfAny(['E', 'e']);
        var mantissa = e < 0 ? r : r[..e];
        var exponent = e < 0 ? 0 : int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        var intPart = dot < 0 ? mantissa : mantissa[..dot];
        var digits = dot < 0 ? intPart : intPart + mantissa[(dot + 1)..];
        var point = intPart.Length + exponent;

        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..].TrimEnd('0');
        point -= lead;

        // Value is 0.<digits> x 10^point, with k digits. This is ECMAScript's (k, n).
        var k = digits.Length;
        var n = point;
        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(n - 1 >= 0 ? '+' : '-').Append(Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture));
        }
    }
}
