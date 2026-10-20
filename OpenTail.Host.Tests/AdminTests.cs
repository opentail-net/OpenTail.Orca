using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Models;
using OpenTail.Host.Admin;
using OpenTail.Modules.Email;

namespace OpenTail.Host.Tests;

public sealed class StubInbox : IInboxAdapter
{
    public async IAsyncEnumerable<InboxEmailRecord> FetchRecentAsync(
        InboxFetchOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public sealed class StubSender : IEmailSender
{
    public List<OutboundEmail> Sent { get; } = [];
    public Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default) { lock (Sent) Sent.Add(email); return Task.FromResult("id-" + Sent.Count); }
    public Task DeleteAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkReadAsync(string messageId, bool isRead, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveAsync(string messageId, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
}

public class AdminTests : IAsyncLifetime
{
    private const string Token = "alice-token-0123456789";
    private const string OtherToken = "bob-token-9876543210";

    private OpenTailHost _host = null!;
    private StubSender _sender = null!;
    private RunningAdmin _admin = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _sender = new StubSender();
        _host = new OpenTailHostBuilder()
            .AddModule(new EmailModule(new StubInbox(), _sender))
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "email.send": { "Mode": "Queue" } } }"""))
            .Build();
        _admin = await AdminServer.StartAsync(_host, new AdminOptions
        {
            Tokens = new Dictionary<string, string> { [Token] = "alice", [OtherToken] = "bob" },
        });
        _http = new HttpClient { BaseAddress = _admin.BaseUri };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _admin.DisposeAsync();
    }

    private async Task<ActionRecord> Queue(string to = "a@b.co", string subject = "Hello", string body = "Hi")
        => await _host.Queue.SubmitAsync(new ActionRequest("email.send",
            System.Text.Json.JsonSerializer.Serialize(new { to = new[] { to }, subject, body }),
            new ActionRequester(RequesterKind.AiSession, "anonymous")));

    private async Task<(HttpStatusCode Code, JsonNode? Body)> Review(string id, object payload, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/actions/{id}/review") { Content = JsonContent.Create(payload) };
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    // ---- access control ----

    [Fact]
    public async Task Api_requires_a_known_bearer_token()
    {
        using var anon = new HttpClient { BaseAddress = _admin.BaseUri };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/actions")).StatusCode);

        anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token-123456");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/actions")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/actions")).StatusCode);
    }

    [Fact]
    public async Task Requests_with_a_foreign_host_header_are_refused_against_dns_rebinding()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/actions");
        req.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Startup_refuses_non_loopback_binds_and_weak_tokens()
    {
        var weak = new AdminOptions { Tokens = new Dictionary<string, string> { ["short"] = "x" } };
        await Assert.ThrowsAsync<ArgumentException>(() => AdminServer.StartAsync(_host, weak));

        var ok = new AdminOptions { Tokens = new Dictionary<string, string> { [Token] = "alice" } };
        await Assert.ThrowsAsync<ArgumentException>(() => AdminServer.StartAsync(_host, ok, "http://0.0.0.0:0"));
        await Assert.ThrowsAsync<ArgumentException>(() => AdminServer.StartAsync(_host, ok, "http://example.com:0"));
    }

    [Fact]
    public async Task Page_is_public_but_ships_a_nonce_csp_and_never_uses_innerHTML()
    {
        using var anon = new HttpClient { BaseAddress = _admin.BaseUri };
        var resp = await anon.GetAsync("/");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var csp = resp.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("default-src 'none'", csp);
        var nonce = csp.Split("'nonce-")[1].Split('\'')[0];
        Assert.Contains($"nonce=\"{nonce}\"", html);
        Assert.DoesNotContain("{{nonce}}", html);
        Assert.DoesNotContain("innerHTML", html);
        Assert.Equal("no-store", resp.Headers.CacheControl!.ToString());

        // Fresh nonce every time.
        var second = (await anon.GetAsync("/")).Headers.GetValues("Content-Security-Policy").Single();
        Assert.NotEqual(csp, second);
    }

    // ---- reviewing ----

    [Fact]
    public async Task List_and_detail_show_what_a_reviewer_needs()
    {
        var a = await Queue("boss@corp.example", "Quarterly numbers", "See attached.");
        var list = JsonNode.Parse(await _http.GetStringAsync("/api/actions?status=Pending"))!.AsArray();
        var row = Assert.Single(list);
        Assert.Equal(a.Id, row!["id"]!.ToString());
        Assert.Contains("boss@corp.example", row["previewSummary"]!.ToString());
        Assert.True((bool)row["tainted"]!);
        Assert.Empty(row["sessionLabels"]!.AsArray()); // no labelled reads in this session

        var detail = JsonNode.Parse(await _http.GetStringAsync($"/api/actions/{a.Id}"))!;
        Assert.Equal("See attached.", detail["preview"]!["body"]!.ToString());
        Assert.Equal("Quarterly numbers", detail["arguments"]!["subject"]!.ToString());
        Assert.Equal("Human", detail["reviewerKind"]!.ToString());
        Assert.NotEmpty(detail["audit"]!.AsArray());
        // Reading an action's arguments is audited, with who read them (audit D5).
        Assert.Contains(detail["audit"]!.AsArray(), e => e!["event"]!.ToString() == "action_viewed");
    }

    [Fact]
    public async Task Approve_executes_once_and_records_the_reviewer_from_the_token_not_the_body()
    {
        var a = await Queue();
        var (code, body) = await Review(a.Id, new { decision = "approve", reviewer = "mallory" }, OtherToken);

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Succeeded", body!["status"]!.ToString());
        Assert.Single(_sender.Sent);
        Assert.Equal("bob", body["reviews"]![0]!["reviewer"]!.ToString());

        // A second approval is a conflict, not a second send.
        var (again, _) = await Review(a.Id, new { decision = "approve" });
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task Reject_and_request_changes_need_a_reason_the_ai_can_act_on()
    {
        var a = await Queue();
        Assert.Equal(HttpStatusCode.BadRequest, (await Review(a.Id, new { decision = "reject" })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Review(a.Id, new { decision = "request_changes", reason = "  " })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Review(a.Id, new { decision = "maybe" })).Code);

        var (code, body) = await Review(a.Id, new { decision = "request_changes", reason = "Add a greeting." });
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("ChangesRequested", body!["status"]!.ToString());
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Edit_and_approve_sends_the_edited_message_and_keeps_the_original()
    {
        var a = await Queue("boss@corp.example", "Numbers", "draft");
        var (code, body) = await Review(a.Id, new
        {
            decision = "approve",
            editedArguments = new { to = new[] { "boss@corp.example" }, subject = "Numbers", body = "Hello, final version." },
        });

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Hello, final version.", Assert.Single(_sender.Sent).Body);
        Assert.Equal("draft", body!["arguments"]!["body"]!.ToString());
        Assert.Equal("Hello, final version.", body["effectiveArguments"]!["body"]!.ToString());
        Assert.True((bool)body["reviews"]![0]!["edited"]!);
    }

    [Fact]
    public async Task An_invalid_edit_is_refused_up_front_and_the_action_stays_pending()
    {
        var a = await Queue();
        var (code, body) = await Review(a.Id, new
        {
            decision = "approve",
            editedArguments = new { to = new[] { "not-an-address" }, subject = "s", body = "b" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Contains("not valid", body!["error"]!.ToString());
        Assert.Equal(ActionStatus.Pending, (await _host.Store.GetAsync(a.Id))!.Status);
        Assert.Empty(_sender.Sent);

        // Edits only make sense when approving.
        var (code2, _) = await Review(a.Id, new { decision = "reject", reason = "no", editedArguments = new { x = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, code2);
    }

    [Fact]
    public async Task The_modules_view_lists_what_is_loaded_and_needs_sign_in()
    {
        var json = JsonNode.Parse(await _http.GetStringAsync("/api/modules"))!;
        var email = json["modules"]!.AsArray().Single()!;
        Assert.Equal("email", email["id"]!.GetValue<string>());
        Assert.Equal(HostContract.Version, email["contract"]!.GetValue<int>());
        Assert.Contains(email["tools"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "email.send" && t["mode"]!.GetValue<string>() == "Queue");

        using var anonymous = new HttpClient { BaseAddress = _admin.BaseUri };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/modules")).StatusCode);

        var page = await anonymous.GetStringAsync("/");
        Assert.Contains("id=\"modules\"", page);
        Assert.DoesNotContain("innerHTML", page);
    }

    [Fact]
    public async Task Unknown_ids_and_statuses_give_clean_errors()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/actions/act_nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Review("act_nope", new { decision = "approve" })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/actions?status=weird")).StatusCode);
    }

    [Fact]
    public async Task Explain_and_trust_endpoints_work()
    {
        var explain = JsonNode.Parse(await _http.GetStringAsync("/api/explain/email.send?tainted=true"))!;
        Assert.Equal("Queue", explain["effectiveMode"]!.ToString());
        Assert.True((bool)explain["advertised"]!);

        var trust = JsonNode.Parse(await _http.GetStringAsync("/api/trust/email.send"))!;
        Assert.False((bool)trust["eligible"]!);
        var promote = await _http.PostAsync("/api/trust/email.send/promote", null);
        Assert.Equal(HttpStatusCode.Conflict, promote.StatusCode);
    }
}

public class ReviewerRunnerTests
{
    private static OpenTailHost Build() => new OpenTailHostBuilder()
        .AddModule(new FakeInboxModule())
        .UsePolicy(PolicyConfig.Parse("""
            { "Tools": {
                "mail.send":  { "Mode": "Queue", "Reviewer": "Either", "AiMaxRisk": "ExternalEffect" },
                "flaky.op":   { "Mode": "Queue", "Reviewer": "Ai" },
                "ledger.append": { "Mode": "Queue", "Reviewer": "Human" } } }
            """))
        .Build();

    private static readonly ActionRequester Me = new(RequesterKind.User, "me");

    [Fact]
    public async Task Ai_reviewer_is_only_offered_actions_whose_policy_allows_it_and_is_stamped_by_the_runner()
    {
        var host = Build();
        var either = await host.Queue.SubmitAsync(new ActionRequest("mail.send", "{\"to\":\"a@b.c\",\"body\":\"hi\"}", Me, host.Sessions.Start()));
        var ai = await host.Queue.SubmitAsync(new ActionRequest("flaky.op", "{\"fail\":false}", Me));
        var human = await host.Queue.SubmitAsync(new ActionRequest("ledger.append", "{\"text\":\"x\"}", Me));

        var offeredTo = new List<string>();
        var reviewer = new DelegateReviewer("auto-reviewer", (a, _) =>
        {
            offeredTo.Add(a.ToolName);
            // The reviewer lies about who it is; the runner overrides that.
            return Task.FromResult<ActionReview?>(new ActionReview("the-ceo", ReviewDecision.Approve, null, DateTimeOffset.UnixEpoch));
        });

        var report = await new ReviewerRunner(host).RunOnceAsync(reviewer);

        Assert.Equal(new ReviewerRunReport(2, 2, 1), report);
        Assert.DoesNotContain("ledger.append", offeredTo);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(human.Id))!.Status);
        var done = (await host.Store.GetAsync(either.Id))!;
        Assert.Equal(ActionStatus.Succeeded, done.Status);
        Assert.Equal("auto-reviewer", done.Reviews.Single().Reviewer);
        Assert.True(done.Reviews.Single().At > DateTimeOffset.UnixEpoch.AddYears(1));
        // What it saw: the hash of the action as handed over (audit D6), not the input itself by default.
        Assert.Matches("^sha256:[0-9a-f]{64}$", done.Reviews.Single().AiInputHash!);
        Assert.Null(done.Reviews.Single().AiInput);
        Assert.Equal(ActionStatus.Succeeded, (await host.Store.GetAsync(ai.Id))!.Status);
    }

    [Fact]
    public async Task With_AiReviewInput_Full_the_reviewers_input_is_kept_and_matches_its_hash()
    {
        var host = new OpenTailHostBuilder().AddModule(new FakeInboxModule())
            .UsePolicy(PolicyConfig.Parse("""{ "AiReviewInput": "Full", "Tools": { "flaky.op": { "Mode": "Queue", "Reviewer": "Ai" } } }""")).Build();
        var a = await host.Queue.SubmitAsync(new ActionRequest("flaky.op", "{\"fail\":false}", Me));
        await new ReviewerRunner(host).RunOnceAsync(new DelegateReviewer("auto", (_, _) =>
            Task.FromResult<ActionReview?>(new ActionReview("auto", ReviewDecision.Approve, null, DateTimeOffset.UtcNow))));
        var review = (await host.Store.GetAsync(a.Id))!.Reviews.Single();
        Assert.Contains(a.Id, review.AiInput);
        Assert.Equal(review.AiInputHash, "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(review.AiInput!))));
    }

    [Fact]
    public async Task Abstaining_leaves_the_action_for_a_human_and_errors_are_audited_not_fatal()
    {
        var host = Build();
        var a = await host.Queue.SubmitAsync(new ActionRequest("mail.send", "{\"to\":\"a@b.c\",\"body\":\"hi\"}", Me, host.Sessions.Start()));
        var b = await host.Queue.SubmitAsync(new ActionRequest("flaky.op", "{\"fail\":false}", Me));

        var reviewer = new DelegateReviewer("flaky-reviewer", (act, _) =>
            act.ToolName == "mail.send" ? Task.FromResult<ActionReview?>(null) : throw new InvalidOperationException("model timed out"));
        var report = await new ReviewerRunner(host).RunOnceAsync(reviewer);

        Assert.Equal(0, report.Decided);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(a.Id))!.Status);
        Assert.Equal(ActionStatus.Pending, (await host.Store.GetAsync(b.Id))!.Status);
        Assert.Contains(((IAuditReader)host.Audit).Read(b.Id), e => e.Event == "reviewer_error");
    }

    [Fact]
    public async Task A_human_deciding_first_is_not_overridden()
    {
        var host = Build();
        var a = await host.Queue.SubmitAsync(new ActionRequest("mail.send", "{\"to\":\"a@b.c\",\"body\":\"hi\"}", Me, host.Sessions.Start()));
        var reviewer = new DelegateReviewer("slow-ai", async (act, _) =>
        {
            await host.Queue.ReviewAsync(act.Id, new ActionReview("human", ReviewDecision.Reject, "no", DateTimeOffset.UtcNow));
            return new ActionReview("slow-ai", ReviewDecision.Approve, null, DateTimeOffset.UtcNow);
        });
        var report = await new ReviewerRunner(host).RunOnceAsync(reviewer);
        Assert.Equal(0, report.Decided);
        Assert.Equal(ActionStatus.Rejected, (await host.Store.GetAsync(a.Id))!.Status);
    }
}
