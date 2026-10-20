namespace OpenTail.Host;

/// <summary>When people are around to stop a delayed action (policy <c>WorkingHours</c>).</summary>
public sealed class WorkingHours
{
    public List<string> Days { get; init; } = ["Mon", "Tue", "Wed", "Thu", "Fri"];
    public string From { get; init; } = "08:00";
    public string To { get; init; } = "18:00";

    /// <summary>An IANA or Windows time zone id. Default UTC.</summary>
    public string Zone { get; init; } = "UTC";

    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    internal IEnumerable<string> Errors()
    {
        if (Days.Count == 0 || Days.Any(d => !DayNames.Contains(d))) yield return "WorkingHours.Days must be names such as Mon, Tue.";
        if (!TimeOnly.TryParse(From, System.Globalization.CultureInfo.InvariantCulture, out var f) || !TimeOnly.TryParse(To, System.Globalization.CultureInfo.InvariantCulture, out var t) || f >= t)
            yield return "WorkingHours.From and To must be times (HH:mm), From before To.";
        string? zoneError = null;
        try { TimeZoneInfo.FindSystemTimeZoneById(Zone); } catch (Exception) { zoneError = $"WorkingHours.Zone '{Zone}' is not a known time zone."; }
        if (zoneError is not null) yield return zoneError;
    }

    /// <summary>True when <paramref name="at"/> falls inside working hours.</summary>
    public bool Contains(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(Zone));
        var time = TimeOnly.FromDateTime(local.DateTime);
        return Days.Contains(DayNames[(int)local.DayOfWeek]) && time >= TimeOnly.Parse(From, System.Globalization.CultureInfo.InvariantCulture)
               && time < TimeOnly.Parse(To, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The next opening at or after <paramref name="at"/> (itself, if it is inside working hours).</summary>
    public DateTimeOffset NextOpening(DateTimeOffset at)
    {
        if (Contains(at)) return at;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var from = TimeOnly.Parse(From, System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i <= 7; i++)
        {
            var day = local.Date.AddDays(i);
            if (!Days.Contains(DayNames[(int)day.DayOfWeek])) continue;
            var open = day + from.ToTimeSpan();
            var openAt = new DateTimeOffset(open, zone.GetUtcOffset(open));
            if (openAt > at) return openAt;
        }
        return at; // unreachable with at least one working day
    }
}
