using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OpenTail.Host.Notifications;

namespace OpenTail.Host.Tests;

public sealed class ScriptedHandler : HttpMessageHandler
{
    public List<(Uri Url, string Body, Dictionary<string, string> Headers)> Requests { get; } = [];
    public Queue<HttpStatusCode> Script { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (Requests) Requests.Add((request.RequestUri!, body, headers));
        var status = Script.Count > 0 ? Script.Dequeue() : HttpStatusCode.OK;
        return new HttpResponseMessage(status) { Content = new StringContent("the receiver says: password=hunter2") };
    }
}

public class NotificationTests
{
    private const string Secret = "webhook-secret-0123456789";
    private static readonly Func<string, string?> Env = name => name switch
    {
        "HOOK_SECRET" => Secret,
        "SLACK_URL" => "https://hooks.slack.test/services/T0/B0/xyz",
        "SHORT" => "short",
        _ => null,
    };

    private const string TwoChannels = """
        {
          "PublicBaseUrl": "https://approvals.test",
          "Channels": [
            { "Name": "hook",  "Type": "webhook", "Url": "https://hooks.test/opentail", "SecretEnv": "HOOK_SECRET" },
            { "Name": "slack", "Type": "slack",   "UrlEnv": "SLACK_URL" }
          ],
          "Routes": [
            { "On": ["queued"], "To": ["hook", "slack"] },
            { "On": ["decided"], "To": ["hook"] }
          ]
        }
        """;

    private static readonly ActionRequester Me = new(RequesterKind.User, "key:alice");

    private sealed class Rig : IDisposable
    {
        public TempDb Db { get; } = new();
        public ManualClock Clock { get; } = new(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        public ScriptedHandler Handler { get; } = new();
        public Notifier Notifier { get; }
        public OpenTailHost Host { get; }
        public FakeInboxModule Module { get; } = new();
        public InMemoryAuditSink Audit { get; } = new();

        public Rig(string config = TwoChannels, string policy = """{ "DefaultReadMode": "Execute", "TaintWindow": "00:00:00", "Tools": { "mail.send": { "Mode": "Queue" } } }""")
        {
            Notifier = Notifier.Create(NotificationConfig.Parse(config), Db.Path, null, Clock, Env, Handler);
            Host = new OpenTailHostBuilder().AddModule(Module).UsePolicy(PolicyConfig.Parse(policy)).UseClock(Clock).UseAudit(Audit)
                .UseSqlite(Db.Path).UseAudit(Audit).UseEventSink(Notifier).Build();
            Notifier.Attach(Host.Store, t => Host.Catalog.TryGet(t, out var r) ? r.Descriptor.Risk : null, Audit);
        }

        public async Task<ActionRecord> QueueSend(string to = "secret-recipient@x.y", string body = "TOPSECRET-BODY") =>
            await Host.Queue.SubmitAsync(new ActionRequest("mail.send", $$"""{"to":"{{to}}","body":"{{body}}"}""", Me, Host.Sessions.Start()));

        public void Dispose() { Notifier.Dispose(); Db.Dispose(); }
    }

    // ---- delivery and signing ----

    [Fact]
    public async Task A_queued_action_is_announced_to_every_channel_on_its_route_with_a_verifiable_signature()
    {
        using var rig = new Rig();
        var action = await rig.QueueSend();

        Assert.Equal(2, await rig.Notifier.DispatchOnceAsync());
        var hook = rig.Handler.Requests.Single(r => r.Url.Host == "hooks.test");
        var slack = rig.Handler.Requests.Single(r => r.Url.Host == "hooks.slack.test");

        // The signature is t=<unix>,v1=<hmac of "t.body"> and the receiver can recompute it.
        var sig = hook.Headers["X-OpenTail-Signature"];
        var t = long.Parse(sig.Split(',')[0][2..]);
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{t}.{hook.Body}"))).ToLowerInvariant();
        Assert.Equal("v1=" + expected, sig.Split(',')[1]);
        Assert.Equal(rig.Clock.GetUtcNow().ToUnixTimeSeconds(), t);
        Assert.Equal($"queued:{action.Id}:hook", hook.Headers["X-OpenTail-Event-Id"]);

        var json = JsonNode.Parse(hook.Body)!;
        Assert.Equal("queued", json["event"]!.ToString());
        Assert.Equal(action.Id, json["action"]!["id"]!.ToString());
        Assert.Equal("mail.send", json["action"]!["tool"]!.ToString());
        Assert.Equal("ExternalEffect", json["action"]!["risk"]!.ToString());
        Assert.Equal("key:alice", json["action"]!["requester"]!.ToString());

        var blocks = JsonNode.Parse(slack.Body)!["blocks"]!.AsArray();
        Assert.Contains("mail.send", blocks[0]!["text"]!["text"]!.ToString());
    }

    [Fact]
    public async Task No_argument_value_and_no_preview_text_ever_appears_in_a_message_unless_a_route_asks()
    {
        using var rig = new Rig();
        var a = await rig.QueueSend("secret-recipient@x.y", "TOPSECRET-BODY");
        await rig.Host.Queue.ReviewAsync(a.Id, new ActionReview("rita", ReviewDecision.Approve, null, rig.Clock.GetUtcNow()));
        var b = await rig.QueueSend("other-secret@x.y", "ANOTHER-SECRET");
        await rig.Host.Queue.ReviewAsync(b.Id, new ActionReview("rita", ReviewDecision.Reject, "no thanks", rig.Clock.GetUtcNow()));
        await rig.Notifier.DispatchOnceAsync();

        Assert.NotEmpty(rig.Handler.Requests);
        foreach (var r in rig.Handler.Requests)
        {
            foreach (var secret in new[] { "secret-recipient", "TOPSECRET", "other-secret", "ANOTHER-SECRET", "Send an email to", "no thanks" })
                Assert.DoesNotContain(secret, r.Body);
        }
    }

    [Fact]
    public async Task A_route_can_opt_in_to_the_one_line_preview_and_it_is_escaped()
    {
        const string config = """
            { "Channels": [ { "Name": "slack", "Type": "slack", "UrlEnv": "SLACK_URL" } ],
              "Routes": [ { "On": ["queued"], "To": ["slack"], "IncludePreviewSummary": true } ] }
            """;
        using var rig = new Rig(config);
        await rig.QueueSend("<!channel>@x.y", "b");
        await rig.Notifier.DispatchOnceAsync();

        var text = JsonNode.Parse(rig.Handler.Requests.Single().Body)!["blocks"]![0]!["text"]!["text"]!.ToString();
        Assert.Contains("Send an email to", text);
        Assert.DoesNotContain("<!channel>", text);       // cannot ping the whole channel
        Assert.Contains("&lt;!channel&gt;", text);
    }

    [Fact]
    public void Slack_text_cannot_inject_links_formatting_or_mentions()
    {
        var evil = SlackChannel.Escape("<https://evil.example|click> *bold* _it_ `code` ~no~ <@U123> a\nb\tc\u0007");
        Assert.DoesNotContain("<", evil);
        Assert.DoesNotContain(">", evil);
        Assert.DoesNotContain("*", evil);
        Assert.DoesNotContain("`", evil);
        Assert.DoesNotContain("|", evil);
        Assert.All(evil, c => Assert.False(char.IsControl(c))); // (string Contains is culture-sensitive and ignores control characters)
        Assert.True(SlackChannel.Escape(new string('x', 500)).Length <= 121);
    }

    [Fact]
    public async Task The_review_link_is_the_public_address_plus_the_action_id_and_never_carries_a_credential()
    {
        using var rig = new Rig(TwoChannels);
        var a = await rig.QueueSend();
        await rig.Notifier.DispatchOnceAsync();
        var body = JsonNode.Parse(rig.Handler.Requests.First(r => r.Url.Host == "hooks.test").Body)!;
        Assert.Equal($"https://approvals.test/#{a.Id}", body["reviewUrl"]!.ToString());
        Assert.DoesNotContain("token", body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- retries, dead letters, dedupe, durability ----

    [Fact]
    public async Task A_failing_receiver_is_retried_with_backoff_and_a_late_success_is_delivered()
    {
        using var rig = new Rig();
        await rig.QueueSend();
        rig.Handler.Script.Enqueue(HttpStatusCode.InternalServerError); // hook, first try
        rig.Handler.Script.Enqueue(HttpStatusCode.OK);                  // slack

        Assert.Equal(1, await rig.Notifier.DispatchOnceAsync());       // slack delivered, hook failed
        Assert.Equal(new OutboxStats(1, 1, 0), rig.Notifier.Outbox.Stats());

        Assert.Equal(0, await rig.Notifier.DispatchOnceAsync());       // not due yet: backoff
        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await rig.Notifier.DispatchOnceAsync());       // retried and delivered
        Assert.Equal(new OutboxStats(0, 2, 0), rig.Notifier.Outbox.Stats());
    }

    [Fact]
    public async Task A_receiver_that_never_answers_ends_up_dead_audited_and_never_blocks_the_action()
    {
        using var rig = new Rig();
        var a = await rig.QueueSend();
        for (var i = 0; i < 40; i++) rig.Handler.Script.Enqueue(HttpStatusCode.BadGateway);

        for (var i = 0; i < NotificationOutbox.MaxAttempts + 1; i++)
        {
            await rig.Notifier.DispatchOnceAsync();
            rig.Clock.Advance(TimeSpan.FromHours(2));
        }

        Assert.Equal(2, rig.Notifier.Outbox.Stats().Dead);
        Assert.All(rig.Notifier.Outbox.Dead(), d => Assert.Equal("HTTP 502", d.Error));          // status only, never the body
        Assert.Contains(rig.Audit.Entries, e => e.Event == "notification_dead");
        Assert.DoesNotContain(rig.Audit.Entries, e => (e.Detail ?? "").Contains("hunter2"));
        Assert.Equal(ActionStatus.Pending, (await rig.Host.Store.GetAsync(a.Id))!.Status);       // the queue is untouched
    }

    [Fact]
    public async Task Enqueueing_the_same_event_twice_sends_it_once()
    {
        using var rig = new Rig();
        var a = await rig.QueueSend();
        var evt = new ActionEvent(ActionEventKind.Queued, a.Id, a.ToolName, a.ModuleId, ToolRisk.ExternalEffect, a.Requester.Id, ActionStatus.Pending, false, a.CreatedAt);
        await rig.Notifier.PublishAsync(evt);
        await rig.Notifier.PublishAsync(evt);
        await rig.Notifier.DispatchOnceAsync();
        await rig.Notifier.DispatchOnceAsync();
        Assert.Equal(2, rig.Handler.Requests.Count); // one per channel, not four
    }

    [Fact]
    public async Task Undelivered_notifications_survive_a_restart()
    {
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var first = Notifier.Create(NotificationConfig.Parse(TwoChannels), db.Path, null, clock, Env, new ScriptedHandler());
        await first.PublishAsync(new ActionEvent(ActionEventKind.Queued, "act_1", "mail.send", "fake", ToolRisk.ExternalEffect, "key:alice", ActionStatus.Pending, false, clock.GetUtcNow()));
        first.Dispose();
        TempDb.ClearPools(db.Path);

        var handler = new ScriptedHandler();
        using var second = Notifier.Create(NotificationConfig.Parse(TwoChannels), db.Path, null, clock, Env, handler);
        Assert.Equal(2, await second.DispatchOnceAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void A_claimed_item_whose_sender_died_is_picked_up_again_after_the_lease()
    {
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var outbox = new NotificationOutbox(db.Path, clock);
        outbox.Enqueue("k1", "hook", "{}");

        Assert.Single(outbox.ClaimDue(10, TimeSpan.FromMinutes(1)));
        Assert.Empty(outbox.ClaimDue(10, TimeSpan.FromMinutes(1)));        // someone else holds it
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Single(outbox.ClaimDue(10, TimeSpan.FromMinutes(1)));       // the holder is presumed dead
    }

    // ---- reconciliation and reminders ----

    private sealed class DropSink : IActionEventSink
    {
        public Task PublishAsync(ActionEvent evt, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task An_action_whose_event_was_lost_is_announced_by_the_next_scheduler_pass()
    {
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var handler = new ScriptedHandler();
        var notifier = Notifier.Create(NotificationConfig.Parse(TwoChannels), db.Path, null, clock, Env, handler);

        // A host that crashed between storing the action and publishing the event: the event sink saw nothing.
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock)
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }""")).UseSqlite(db.Path).UseEventSink(new DropSink()).Build();
        var pending = await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"x"}""", Me, host.Sessions.Start()));
        notifier.Attach(host.Store, t => ToolRisk.ExternalEffect);

        Assert.Equal(2, await notifier.SchedulerOnceAsync());      // one per channel
        Assert.Equal(0, await notifier.SchedulerOnceAsync());      // idempotent
        Assert.Equal(2, await notifier.DispatchOnceAsync());
        Assert.All(handler.Requests, r => Assert.Contains(pending.Id, r.Body));
        notifier.Dispose();
    }

    [Fact]
    public async Task A_healthy_event_is_not_announced_twice_by_the_scheduler()
    {
        using var rig = new Rig();
        await rig.QueueSend();
        Assert.Equal(0, await rig.Notifier.SchedulerOnceAsync()); // the event already enqueued it
        await rig.Notifier.DispatchOnceAsync();
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task Actions_that_have_waited_longer_than_the_window_are_not_announced_when_notifications_are_turned_on()
    {
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule()).UseClock(clock)
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }""")).UseSqlite(db.Path).Build();
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"x"}""", Me, host.Sessions.Start()));
        clock.Advance(TimeSpan.FromHours(7));

        using var notifier = Notifier.Create(NotificationConfig.Parse(TwoChannels), db.Path, null, clock, Env, new ScriptedHandler());
        notifier.Attach(host.Store, _ => null);
        Assert.Equal(0, await notifier.SchedulerOnceAsync());
    }

    [Fact]
    public async Task Reminders_are_sent_once_each_and_only_the_latest_when_the_server_was_down()
    {
        const string config = """
            { "Channels": [ { "Name": "slack", "Type": "slack", "UrlEnv": "SLACK_URL" } ],
              "Routes": [ { "On": ["reminder"], "To": ["slack"] } ],
              "Reminder": { "After": "15m", "Every": "1h", "Max": 3 } }
            """;
        using var rig = new Rig(config);
        await rig.QueueSend();

        rig.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(0, await rig.Notifier.SchedulerOnceAsync());           // too early

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await rig.Notifier.SchedulerOnceAsync());           // reminder 1
        Assert.Equal(0, await rig.Notifier.SchedulerOnceAsync());           // not again

        rig.Clock.Advance(TimeSpan.FromHours(3));                           // the server "slept" through reminders 2 and 3
        Assert.Equal(1, await rig.Notifier.SchedulerOnceAsync());           // a single nudge, numbered by how far along we are
        rig.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, await rig.Notifier.SchedulerOnceAsync());           // capped at Max: reminder 3 was the last one, nothing more ever

        await rig.Notifier.DispatchOnceAsync();
        var texts = rig.Handler.Requests.Select(r => JsonNode.Parse(r.Body)!["blocks"]![0]!["text"]!["text"]!.ToString()).ToList();
        Assert.Contains(texts, t => t.Contains("reminder 1"));
        Assert.Contains(texts, t => t.Contains("reminder 3"));
    }

    [Fact]
    public async Task A_decided_route_hears_outcomes_and_a_queued_route_does_not()
    {
        const string config = """
            { "Channels": [ { "Name": "hook", "Type": "webhook", "Url": "https://hooks.test/o", "SecretEnv": "HOOK_SECRET" } ],
              "Routes": [ { "On": ["decided"], "To": ["hook"] } ] }
            """;
        using var rig = new Rig(config);
        var a = await rig.QueueSend();
        await rig.Notifier.DispatchOnceAsync();
        Assert.Empty(rig.Handler.Requests);                                  // queued is not a decided event

        await rig.Host.Queue.ReviewAsync(a.Id, new ActionReview("rita", ReviewDecision.Approve, null, rig.Clock.GetUtcNow()));
        await rig.Notifier.DispatchOnceAsync();
        var events = rig.Handler.Requests.Select(r => JsonNode.Parse(r.Body)!["event"]!.ToString()).ToList();
        Assert.Equal(["succeeded"], events);                                 // approved is not an outcome; succeeded is
    }

    [Fact]
    public async Task Routes_filter_by_tool_pattern_and_minimum_risk()
    {
        const string config = """
            { "Channels": [ { "Name": "slack", "Type": "slack", "UrlEnv": "SLACK_URL" } ],
              "Routes": [ { "On": ["queued"], "Tools": ["files.*"], "To": ["slack"] },
                          { "On": ["queued"], "Tools": ["mail.*"], "MinRisk": "Destructive", "To": ["slack"] } ] }
            """;
        using var rig = new Rig(config);
        await rig.QueueSend();                    // mail.send is ExternalEffect >= Destructive and matches mail.*
        Assert.Equal(1, await rig.Notifier.DispatchOnceAsync());

        const string low = """
            { "Channels": [ { "Name": "slack", "Type": "slack", "UrlEnv": "SLACK_URL" } ],
              "Routes": [ { "On": ["queued"], "Tools": ["files.*"], "To": ["slack"] }, { "On": ["queued"], "MinRisk": "ExternalEffect", "Tools": ["calendar.*"], "To": ["slack"] } ] }
            """;
        using var rig2 = new Rig(low);
        await rig2.QueueSend();
        Assert.Equal(0, await rig2.Notifier.DispatchOnceAsync());            // no route matches mail.send
    }

    // ---- configuration is checked before anything is sent ----

    [Theory]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "webhook", "Url": "http://hooks.test/x", "SecretEnv": "HOOK_SECRET" } ] }""", "must be https")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "webhook", "Url": "https://hooks.test/x" } ] }""", "needs SecretEnv")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "webhook", "Url": "https://hooks.test/x", "SecretEnv": "SHORT" } ] }""", "at least 16 characters")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "webhook", "Url": "https://hooks.test/x", "SecretEnv": "NOPE" } ] }""", "at least 16 characters")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "UrlEnv": "NOPE" } ] }""", "NOPE is not set")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "Url": "https://x.test/y", "UrlEnv": "SLACK_URL" } ] }""", "not both")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "UrlEnv": "SLACK_URL", "SecretEnv": "HOOK_SECRET" } ] }""", "does not use SecretEnv")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "UrlEnv": "SLACK_URL" }, { "Name": "a", "Type": "slack", "UrlEnv": "SLACK_URL" } ] }""", "Duplicate channel")]
    [InlineData("""{ "Channels": [ { "Name": "a b", "Type": "slack", "UrlEnv": "SLACK_URL" } ] }""", "must be 1-64")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "UrlEnv": "SLACK_URL" } ], "Routes": [ { "On": ["queued"], "To": ["b"] } ] }""", "unknown channel 'b'")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "UrlEnv": "SLACK_URL" } ], "Routes": [ { "On": ["queud"], "To": ["a"] } ] }""", "unknown event 'queud'")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "UrlEnv": "SLACK_URL" } ], "Routes": [ { "On": [], "To": ["a"] } ] }""", "at least one event")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "UrlEnv": "SLACK_URL" } ], "Routes": [ { "On": ["queued"], "To": [] } ] }""", "at least one channel")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "UrlEnv": "SLACK_URL" } ], "Routes": [ { "On": ["queued"], "To": ["a"] } ], "Reminder": { "After": "15m" } }""", "no route listens for 'reminder'")]
    [InlineData("""{ "Channels": [ { "Name": "a", "Type": "slack", "UrlEnv": "SLACK_URL" } ], "Routes": [ { "On": ["reminder"], "To": ["a"] } ], "Reminder": { "Max": 99 } }""", "between 1 and 20")]
    public void A_bad_configuration_is_refused_with_the_reason(string json, string expected)
    {
        var errors = NotificationConfig.Parse(json).Validate(Env);
        Assert.Contains(errors, e => e.Contains(expected));
        using var db = new TempDb();
        Assert.Throws<InvalidOperationException>(() => Notifier.Create(NotificationConfig.Parse(json), db.Path, null, null, Env));
    }

    [Fact]
    public void A_typo_in_the_notifications_file_is_an_error()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => NotificationConfig.Parse("""{ "Chanels": [] }"""));
    }

    [Fact]
    public void Secrets_are_never_in_the_file_and_never_in_errors()
    {
        var json = TwoChannels;
        Assert.DoesNotContain(Secret, json);
        var bad = NotificationConfig.Parse("""{ "Channels": [ { "Name": "a", "Type": "webhook", "Url": "https://x.test/y", "SecretEnv": "SHORT" } ] }""");
        Assert.DoesNotContain("short", string.Join(" ", bad.Validate(Env)).Replace("SHORT", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void The_team_sample_notifications_file_is_valid()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OpenTail.Host.slnx"))) root = root.Parent;
        var config = NotificationConfig.Load(Path.Combine(root!.FullName, "OpenTail.Host.Sample.TeamHttp", "notifications.team.json"));
        var env = new Dictionary<string, string> { ["SLACK_REVIEW_WEBHOOK"] = "https://hooks.slack.test/x", ["AUDIT_HOOK_SECRET"] = "0123456789abcdef" };
        Assert.Empty(config.Validate(n => env.GetValueOrDefault(n)));
    }

    // ---- audit checkpoints ----

    private const string WithCheckpoints = """
        {
          "Channels": [
            { "Name": "hook",  "Type": "webhook", "Url": "https://hooks.test/opentail", "SecretEnv": "HOOK_SECRET" },
            { "Name": "slack", "Type": "slack",   "UrlEnv": "SLACK_URL" }
          ],
          "Routes": [ { "On": ["queued"], "To": ["slack"] }, { "On": ["audit_checkpoint"], "To": ["hook", "slack"] } ]
        }
        """;

    [Fact]
    public async Task The_audit_checkpoint_goes_out_hourly_once_per_hour_and_matches_the_chain()
    {
        using var db = new TempDb();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T09:10:00Z"));
        var handler = new ScriptedHandler();
        using var notifier = Notifier.Create(NotificationConfig.Parse(WithCheckpoints), db.Path, "https://approvals.test", clock, Env, handler);
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }""")).UseSqlite(db.Path).UseEventSink(notifier).Build();
        var audit = (SqliteAuditSink)host.Audit;
        notifier.Attach(host.Store, _ => null, host.Audit).AttachAuditCheckpoint(() => audit.Head()?.ToString());
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"b"}""", Me));

        await notifier.SchedulerOnceAsync();
        await notifier.SchedulerOnceAsync(); // the same hour: nothing new
        await notifier.DispatchOnceAsync();

        var hook = Assert.Single(handler.Requests, r => r.Url.Host == "hooks.test");
        var checkpoint = JsonNode.Parse(hook.Body)!["checkpoint"]!.ToString();
        Assert.Equal(audit.Head()!.ToString(), checkpoint);
        Assert.True(AuditChain.Verify(audit.ReadChain(), [AuditCheckpoint.Parse(checkpoint)]).Intact);
        Assert.Contains("Audit checkpoint", handler.Requests.Single(r => r.Url.Host == "hooks.slack.test" && r.Body.Contains("checkpoint")).Body);
        Assert.DoesNotContain("approvals.test", hook.Body); // no review link: it is not about an action

        clock.Advance(TimeSpan.FromHours(1));
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"c"}""", Me));
        await notifier.SchedulerOnceAsync();
        await notifier.DispatchOnceAsync();
        Assert.Equal(2, handler.Requests.Count(r => r.Url.Host == "hooks.test"));
        Assert.Empty(notifier.Outbox.Dead());
    }

    private const string WithStream = """
        {
          "Channels": [ { "Name": "logs", "Type": "webhook", "Url": "https://logs.test/ingest", "SecretEnv": "HOOK_SECRET" } ],
          "Routes": [ { "On": ["audit_entry"], "To": ["logs"] } ]
        }
        """;

    [Fact]
    public async Task Every_audit_entry_is_streamed_to_the_log_store_once_in_order_and_matches_the_chain()
    {
        using var db = new TempDb();
        var handler = new ScriptedHandler();
        using var notifier = Notifier.Create(NotificationConfig.Parse(WithStream), db.Path, "https://approvals.test", null, Env, handler);
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "mail.send": { "Mode": "Queue" } } }""")).UseSqlite(db.Path).UseEventSink(notifier).Build();
        var audit = (SqliteAuditSink)host.Audit;
        notifier.Attach(host.Store, _ => null, host.Audit)
            .AttachAuditStream(seq => [.. audit.ReadChainAfter(seq).Select(e => new AuditStreamEntry(e.Seq, e.At, e.Event, e.ActionId, e.Tool, e.Actor, e.Detail, e.PrevHash, e.Hash))]);
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"b"}""", Me));

        await notifier.SchedulerOnceAsync();
        await notifier.SchedulerOnceAsync();   // nothing new: no duplicates
        await notifier.DispatchOnceAsync();
        var chain = audit.ReadChain();
        var sent = handler.Requests.Where(r => r.Url.Host == "logs.test").Select(r => JsonNode.Parse(r.Body)!["entry"]!).ToList();
        Assert.Equal(chain.Select(e => (e.Seq, e.Hash)), sent.Select(e => ((long)e["seq"]!, (string?)e["hash"])));
        Assert.DoesNotContain(sent, e => e.ToJsonString().Contains("a@b.c"));   // entries never hold argument values

        // Later entries go out after the last one sent, even through a new notifier (the cursor is kept).
        await host.Queue.SubmitAsync(new ActionRequest("mail.send", """{"to":"a@b.c","body":"c"}""", Me));
        using var restarted = Notifier.Create(NotificationConfig.Parse(WithStream), db.Path, "https://approvals.test", null, Env, handler);
        restarted.Attach(host.Store, _ => null, host.Audit)
            .AttachAuditStream(seq => [.. audit.ReadChainAfter(seq).Select(e => new AuditStreamEntry(e.Seq, e.At, e.Event, e.ActionId, e.Tool, e.Actor, e.Detail, e.PrevHash, e.Hash))]);
        Assert.Equal(audit.ReadChain().Count - chain.Count, restarted.EnqueueAuditEntries());
    }

    [Fact]
    public void Audit_entries_go_only_to_webhook_channels()
    {
        var errors = NotificationConfig.Parse("""
            { "Channels": [ { "Name": "slack", "Type": "slack", "UrlEnv": "SLACK_URL" } ], "Routes": [ { "On": ["audit_entry"], "To": ["slack"] } ] }
            """).Validate(Env);
        Assert.Contains(errors, e => e.Contains("goes to a webhook channel"));
    }

    [Fact]
    public void Audit_checkpoints_cannot_go_to_the_Teams_bot()
    {
        var errors = NotificationConfig.Parse("""
            { "Channels": [ { "Name": "bot", "Type": "teams-bot" } ], "Routes": [ { "On": ["audit_checkpoint"], "To": ["bot"] } ] }
            """).Validate(Env);
        Assert.Contains(errors, e => e.Contains("posts about actions only"));
    }
}
