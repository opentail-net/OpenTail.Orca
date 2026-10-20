using System.Runtime.CompilerServices;

namespace OpenTail.Modules.Calendar;

/// <summary>
/// A calendar built when a tool runs, not when the module is created, so a factory never reads credentials at startup and a rotated
/// secret is picked up on the next call. Provider packages use it.
/// </summary>
public sealed class DeferredCalendar(Func<CancellationToken, ValueTask<ICalendarAdapter>> create) : ICalendarAdapter
{
    public async IAsyncEnumerable<CalendarInfo> ListCalendarsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var c = await create(ct);
        await foreach (var x in c.ListCalendarsAsync(ct)) yield return x;
    }

    public async IAsyncEnumerable<CalendarEvent> GetEventsAsync(CalendarFetchOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var c = await create(ct);
        await foreach (var x in c.GetEventsAsync(options, ct)) yield return x;
    }

    public async Task<CalendarEvent?> GetEventAsync(string calendarId, string eventId, CancellationToken ct = default) =>
        await (await create(ct)).GetEventAsync(calendarId, eventId, ct);

    public async Task<string> CreateEventAsync(CalendarEvent draft, CancellationToken ct = default) => await (await create(ct)).CreateEventAsync(draft, ct);

    public async Task UpdateEventAsync(CalendarEvent updated, CancellationToken ct = default) => await (await create(ct)).UpdateEventAsync(updated, ct);

    public async Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default) =>
        await (await create(ct)).DeleteEventAsync(calendarId, eventId, ct);

    public async Task RespondToEventAsync(string calendarId, string eventId, string response, string? comment = null, CancellationToken ct = default) =>
        await (await create(ct)).RespondToEventAsync(calendarId, eventId, response, comment, ct);
}
