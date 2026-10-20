using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenTail.Host.Notifications;

/// <summary>One audit entry with its chain fields, as the audit stream sends it (the Host's chained entry, without depending on it).</summary>
public sealed record AuditStreamEntry(long Seq, string At, string Event, string? ActionId, string? Tool, string? Actor, string? Detail, string? PrevHash, string? Hash);

/// <summary>
/// Turns action events into notifications. Events are written to a durable outbox first and delivered afterwards, with retries; a
/// periodic pass reconciles (an action that is still pending but whose notification was lost, for instance in a crash between the
/// state change and the event, gets one) and sends reminders. Use it as the Host's event sink, attach the store, then run it.
/// </summary>
public sealed class Notifier : IActionEventSink, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private sealed record Payload(string Name, ActionEvent Event, bool IncludePreview, int? Reminder);

    private readonly NotificationConfig _config;
    private readonly NotificationOutbox _outbox;
    private readonly Dictionary<string, INotificationChannel> _channels;
    private readonly string? _publicBaseUrl;
    private readonly TimeProvider _clock;
    private readonly List<(RouteConfig Route, Regex[] Tools)> _routes;

    private IActionStore? _store;
    private Func<string, ToolRisk?> _riskOf = _ => null;
    private IAuditSink? _audit;
    private Func<string?>? _checkpoint;
    private Func<long, IReadOnlyList<AuditStreamEntry>>? _entriesAfter;

    private Notifier(NotificationConfig config, NotificationOutbox outbox, Dictionary<string, INotificationChannel> channels, string? publicBaseUrl, TimeProvider clock)
    {
        _config = config;
        _outbox = outbox;
        _channels = channels;
        _publicBaseUrl = publicBaseUrl?.TrimEnd('/');
        _clock = clock;
        _routes = config.Routes.Select(r => (r, r.Tools.Select(Glob).ToArray())).ToList();
    }

    /// <summary>Raised for problems that have nowhere else to go (a dispatch pass failing). Never carries secrets.</summary>
    public event Action<string>? Error;

    public NotificationOutbox Outbox => _outbox;

    /// <summary>For the Host's health check: degraded when a notification created in the last day could not be delivered.</summary>
    public bool DeliveryFailingRecently() => _outbox.DeadSince(_clock.GetUtcNow() - TimeSpan.FromDays(1)) > 0;

    /// <param name="env">Resolves environment variables for secrets and URLs (defaults to the process environment).</param>
    /// <param name="handler">Replaces the HTTP handler (tests).</param>
    public static Notifier Create(NotificationConfig config, string dbPath, string? publicBaseUrl = null, TimeProvider? clock = null,
        Func<string, string?>? env = null, HttpMessageHandler? handler = null, Func<ChannelConfig, INotificationChannel>? botChannel = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var errors = config.Validate(env);
        if (errors.Count > 0) throw new InvalidOperationException("Invalid notifications configuration: " + string.Join(" ", errors));

        var channels = new Dictionary<string, INotificationChannel>(StringComparer.Ordinal);
        foreach (var c in config.Channels)
        {
            if (c.Type == ChannelType.TeamsBot)
            {
                channels[c.Name] = botChannel?.Invoke(c)
                    ?? throw new InvalidOperationException($"Channel '{c.Name}' is a teams-bot channel: it needs the HTTP host with a Teams section (--http).");
                continue;
            }
            var url = c.Url ?? env(c.UrlEnv!)!;
            channels[c.Name] = c.Type switch
            {
                ChannelType.Slack => new SlackChannel(c.Name, url, handler),
                ChannelType.Teams => new TeamsChannel(c.Name, url, handler),
                _ => new WebhookChannel(c.Name, url, env(c.SecretEnv!)!, handler, clock),
            };
        }
        var clk = clock ?? TimeProvider.System;
        return new Notifier(config, new NotificationOutbox(dbPath, clk), channels, publicBaseUrl ?? config.PublicBaseUrl, clk);
    }

    /// <summary>Gives the notifier the queue to reconcile against, a way to look up a tool's risk, and (optionally) an audit sink.</summary>
    public Notifier Attach(IActionStore store, Func<string, ToolRisk?> riskOf, IAuditSink? audit = null)
    {
        _store = store;
        _riskOf = riskOf;
        _audit = audit;
        return this;
    }

    /// <summary>Where the audit chain stands (<c>&lt;seq&gt;:&lt;hash&gt;</c>, or null), for the hourly <c>audit_checkpoint</c> notification.</summary>
    public Notifier AttachAuditCheckpoint(Func<string?> checkpoint)
    {
        _checkpoint = checkpoint;
        return this;
    }

    /// <summary>Where to read audit entries from, for the <c>audit_entry</c> stream to the organisation's log store (audit D8).</summary>
    public Notifier AttachAuditStream(Func<long, IReadOnlyList<AuditStreamEntry>> entriesAfter)
    {
        _entriesAfter = entriesAfter;
        return this;
    }

    private const string StreamCursor = "audit_entry.cursor";

    /// <summary>Queues a <c>work_ready</c> wake-up for each route listening for it whose <c>Work</c> patterns match who the item is for.
    /// The body carries ids only (work id, for, kind), never the goal or a note: a leaked URL leaks nothing. Returns how many.</summary>
    public int EnqueueWorkReady(string workId, string forWhom, string kind)
    {
        var detail = new System.Text.Json.Nodes.JsonObject { ["workId"] = workId, ["for"] = forWhom, ["kind"] = kind }.ToJsonString();
        var evt = new ActionEvent(ActionEventKind.Queued, workId, "work", "host", null, "system", ActionStatus.Pending, false, _clock.GetUtcNow(), detail);
        var added = 0;
        foreach (var (route, _) in _routes.Where(r => r.Route.On.Contains(NotificationEvents.WorkReady)
                     && (r.Route.Work.Count == 0 || r.Route.Work.Any(w => Glob(w).IsMatch(forWhom)))))
            foreach (var channel in route.To.Distinct())
                // One wake-up per opening: the key holds the time, so a reopened item wakes again.
                if (_outbox.Enqueue($"{NotificationEvents.WorkReady}:{workId}:{_clock.GetUtcNow().UtcTicks}:{channel}", channel,
                        JsonSerializer.Serialize(new Payload(NotificationEvents.WorkReady, evt, false, null), Json))) added++;
        return added;
    }

    /// <summary>Queues every audit entry written since the last call for each route listening for <c>audit_entry</c>, oldest first, and
    /// remembers how far it got (across restarts). Delivery goes through the outbox with its retries, so a log store that is down
    /// catches up. Returns how many were queued.</summary>
    public int EnqueueAuditEntries()
    {
        var routes = _routes.Where(r => r.Route.On.Contains(NotificationEvents.AuditEntry)).ToList();
        if (_entriesAfter is null || routes.Count == 0) return 0;
        var cursor = long.TryParse(_outbox.GetState(StreamCursor), out var c) ? c : 0;
        var added = 0;
        while (true)
        {
            var batch = _entriesAfter(cursor);
            if (batch.Count == 0) break;
            foreach (var e in batch)
            {
                var entry = new System.Text.Json.Nodes.JsonObject
                {
                    ["seq"] = e.Seq, ["at"] = e.At, ["event"] = e.Event, ["actionId"] = e.ActionId, ["tool"] = e.Tool, ["actor"] = e.Actor,
                    ["detail"] = e.Detail, ["prevHash"] = e.PrevHash, ["hash"] = e.Hash,
                }.ToJsonString();
                var evt = new ActionEvent(ActionEventKind.Succeeded, "audit", "audit", "host", null, "system", ActionStatus.Succeeded, false, _clock.GetUtcNow(), entry);
                foreach (var (route, _) in routes)
                    foreach (var channel in route.To.Distinct())
                        if (_outbox.Enqueue($"{NotificationEvents.AuditEntry}:{e.Seq}:{channel}", channel,
                                JsonSerializer.Serialize(new Payload(NotificationEvents.AuditEntry, evt, false, null), Json))) added++;
                cursor = e.Seq;
            }
            _outbox.SetState(StreamCursor, cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return added;
    }

    /// <summary>Queues an <c>audit_checkpoint</c> for every route listening for it, unless this checkpoint was already queued. Returns how
    /// many were queued. The scheduler calls it hourly; the runner calls it once at shutdown.</summary>
    public int EnqueueAuditCheckpoint()
    {
        if (_checkpoint?.Invoke() is not { } cp) return 0;
        var evt = new ActionEvent(ActionEventKind.Succeeded, "audit", "audit", "host", null, "system", ActionStatus.Succeeded, false, _clock.GetUtcNow(), cp);
        var added = 0;
        foreach (var (route, _) in _routes.Where(r => r.Route.On.Contains(NotificationEvents.AuditCheckpoint)))
            foreach (var channel in route.To.Distinct())
                if (_outbox.Enqueue($"{NotificationEvents.AuditCheckpoint}:{cp}:{channel}", channel,
                        JsonSerializer.Serialize(new Payload(NotificationEvents.AuditCheckpoint, evt, false, null), Json))) added++;
        return added;
    }

    // ---- sink: write to the outbox, nothing else ----

    public Task PublishAsync(ActionEvent evt, CancellationToken ct = default)
    {
        Enqueue(NotificationEvents.NameOf(evt.Kind), evt, reminder: null);
        return Task.CompletedTask;
    }

    private int Enqueue(string name, ActionEvent evt, int? reminder)
    {
        var added = 0;
        foreach (var (route, tools) in _routes)
        {
            if (!Listens(route, name) || !Matches(route, tools, evt)) continue;
            foreach (var channel in route.To.Distinct())
            {
                var key = reminder is { } n ? $"remind:{evt.ActionId}:{n}:{channel}" : $"{name}:{evt.ActionId}:{channel}";
                var payload = JsonSerializer.Serialize(new Payload(name, evt, route.IncludePreviewSummary, reminder), Json);
                if (_outbox.Enqueue(key, channel, payload)) added++;
            }
        }
        return added;
    }

    private static bool Listens(RouteConfig r, string name) => r.On.Contains(name) || (r.On.Contains("decided") && NotificationEvents.IsDecided(name));

    private static bool Matches(RouteConfig r, Regex[] tools, ActionEvent e) =>
        (e.Risk is null || e.Risk >= r.MinRisk) && (tools.Length == 0 || tools.Any(t => t.IsMatch(e.ToolName)));

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // ---- delivery ----

    /// <summary>Sends everything that is due. Returns how many were delivered.</summary>
    // The Host's source name (HostTelemetry.Name): this package depends only on the abstractions, so it opens its own handle to it.
    private static readonly System.Diagnostics.ActivitySource Spans = new("OpenTail.Host");

    public async Task<int> DispatchOnceAsync(CancellationToken ct = default)
    {
        var sent = 0;
        foreach (var item in _outbox.ClaimDue(20, TimeSpan.FromMinutes(1)))
        {
            ct.ThrowIfCancellationRequested();
            using var activity = Spans.StartActivity("notification.deliver");
            activity?.SetTag("opentail.channel", item.Channel);
            activity?.SetTag("opentail.attempt", item.Attempts + 1);
            try
            {
                var payload = JsonSerializer.Deserialize<Payload>(item.PayloadJson, Json)!;
                activity?.SetTag("opentail.action_id", payload.Event.ActionId);
                activity?.SetTag("opentail.event", payload.Name);
                if (!_channels.TryGetValue(item.Channel, out var channel)) throw new InvalidOperationException("channel no longer configured");
                var message = new NotificationMessage(item.DedupeKey, payload.Name, payload.Event,
                    _publicBaseUrl is null || payload.Name == NotificationEvents.AuditCheckpoint ? null : $"{_publicBaseUrl}/#{payload.Event.ActionId}",
                    payload.IncludePreview ? await PreviewOfAsync(payload.Event.ActionId, ct) : null,
                    payload.Reminder);
                await channel.SendAsync(message, ct);
                _outbox.MarkSent(item.Id);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, Describe(ex));
                var dead = _outbox.MarkFailed(item.Id, item.Attempts, Describe(ex));
                if (dead && _audit is not null)
                    await _audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "notification_dead", null, null, "system", item.Channel), ct);
            }
        }
        return sent;
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException h => h.Message.StartsWith("HTTP ", StringComparison.Ordinal) ? h.Message : "connection failed",
        TaskCanceledException or OperationCanceledException => "timeout",
        _ => ex.GetType().Name,
    };

    private async Task<string?> PreviewOfAsync(string actionId, CancellationToken ct)
    {
        if (_store is null) return null;
        var record = await _store.GetAsync(actionId, ct);
        if (record?.PreviewJson is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(record.PreviewJson);
            return doc.RootElement.TryGetProperty("Summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // ---- reconcile + reminders ----

    /// <summary>
    /// For every action still waiting: make sure its "queued" notification exists (recent ones only), and send the due reminder.
    /// Idempotent: every notification has a key, so running this twice, or after a crash, never sends anything twice.
    /// </summary>
    public async Task<int> SchedulerOnceAsync(CancellationToken ct = default)
    {
        if (_store is null) return 0;
        var now = _clock.GetUtcNow();
        var pending = await _store.QueryAsync(new ActionQuery { Status = ActionStatus.Pending, Limit = int.MaxValue }, ct);
        var added = 0;
        foreach (var p in pending)
        {
            var age = now - p.CreatedAt;
            var evt = new ActionEvent(ActionEventKind.Queued, p.Id, p.ToolName, p.ModuleId, _riskOf(p.ToolName), p.Requester.Id,
                ActionStatus.Pending, p.Tainted, p.CreatedAt);

            if (age <= _config.ReconcileWindow) added += Enqueue("queued", evt, null);

            if (_config.Reminder is { } rem && age >= rem.After)
            {
                // Only the latest reminder that is due: if the server was down for hours, one nudge, not a burst.
                var n = Math.Min(rem.Max, 1 + (int)((age - rem.After) / rem.Every));
                added += Enqueue("reminder", evt with { At = now }, n);
            }
        }
        // Hourly audit checkpoint: the key holds the hour, so a restart within the hour sends nothing new.
        var hour = $"{NotificationEvents.AuditCheckpoint}-hour:{now.UtcDateTime:yyyyMMddHH}";
        if (_checkpoint is not null && !_outbox.Exists(hour))
        {
            added += EnqueueAuditCheckpoint();
            _outbox.Enqueue(hour, "", "{}");
            _outbox.MarkSentByKey(hour);
        }
        added += EnqueueAuditEntries();
        _outbox.Purge(now - TimeSpan.FromDays(30));
        return added;
    }

    /// <summary>Runs the dispatcher (every 2 s) and the scheduler (every 30 s) until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var scheduler = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                do
                {
                    try { await SchedulerOnceAsync(ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Error?.Invoke("notification scheduler: " + ex.GetType().Name); }
                }
                while (await timer.WaitForNextTickAsync(ct));
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        using var dispatchTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                try { await DispatchOnceAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Error?.Invoke("notification dispatch: " + ex.GetType().Name); }
            }
            while (await dispatchTimer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        await scheduler;
    }

    public void Dispose()
    {
        foreach (var c in _channels.Values.OfType<IDisposable>()) c.Dispose();
    }
}
