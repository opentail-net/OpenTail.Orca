using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenTail.Orca.Abstractions;

/// <summary>
/// Durations in policy files, written the way people write them: <c>"24:00:00"</c> (hours:minutes:seconds, hours may exceed 23),
/// <c>"1.00:00:00"</c> (days.hh:mm:ss), or shorthand <c>"90s"</c>, <c>"30m"</c>, <c>"8h"</c>, <c>"2d"</c>.
/// (The standard JSON TimeSpan format rejects <c>"24:00:00"</c>, which is exactly what people type for a day.)
/// </summary>
public sealed class DurationConverter : JsonConverter<TimeSpan>
{
    private static readonly Regex Hms = new(@"^(\d{1,5}):([0-5]\d):([0-5]\d)$", RegexOptions.CultureInvariant);
    private static readonly Regex Short = new(@"^(\d{1,6})([smhd])$", RegexOptions.CultureInvariant);

    public static bool TryParse(string text, out TimeSpan value)
    {
        value = default;
        text = text.Trim();
        if (Hms.Match(text) is { Success: true } m)
        {
            value = new TimeSpan(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 24,
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) % 24,
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            return true;
        }
        if (Short.Match(text) is { Success: true } s)
        {
            var n = int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture);
            value = s.Groups[2].Value switch { "s" => TimeSpan.FromSeconds(n), "m" => TimeSpan.FromMinutes(n), "h" => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n) };
            return true;
        }
        return TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out value);
    }

    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && TryParse(reader.GetString()!, out var v)) return v;
        throw new JsonException("A duration looks like \"24:00:00\", \"1.00:00:00\", \"90s\", \"30m\", \"8h\" or \"2d\".");
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
}
