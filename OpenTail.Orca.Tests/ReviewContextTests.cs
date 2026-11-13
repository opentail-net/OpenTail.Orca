using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OpenTail.Orca.Admin;
using OpenTail.Modules.Email;

namespace OpenTail.Orca.Tests;

/// <summary>v5 V2: the review page tells a reviewer why an action waits (and which read caused it), what kind of effect it has, what the
/// caller did just before, and how this tool's last actions were decided.</summary>
public class ReviewContextTests : IAsyncLifetime
{
    private const string Token = "reviewer-token-0123456789";
    private OpenTailHost _host = null!;
    private RunningAdmin _admin = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _host = new OpenTailHostBuilder()
            .AddModule(new EmailModule(new StubInbox(), new StubSender()))
            .UsePolicy(PolicyConfig.Parse("""
                { "TaintWindow": "00:30:00", "Tools": { "email.list_recent": { "Mode": "Execute" }, "email.send": { "Mode": "Execute" } } }
                """))
            .Build();
        _admin = await AdminServer.StartAsync(_host, new AdminOptions { Tokens = new Dictionary<string, string> { [Token] = "rita" } });
        _http = new HttpClient { BaseAddress = _admin.BaseUri };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async ValueTask DisposeAsync() { _http.Dispose(); await _admin.DisposeAsync(); }

    private Task<ActionRecord> Send(string session, string to = "a@b.co") => _host.Queue.SubmitAsync(new ActionRequest("email.send",
        System.Text.Json.JsonSerializer.Serialize(new { to = new[] { to }, subject = "Hello", body = "Hi" }), Req.Me, session));

    private async Task<JsonNode> Detail(string id) => JsonNode.Parse(await _http.GetStringAsync($"/api/actions/{id}"))!;

    [Fact]
    public async Task A_held_send_says_which_read_held_it_what_it_will_do_and_what_came_before()
    {
        var s = _host.Sessions.Start();
        Assert.Equal(ActionStatus.Succeeded, (await Send(_host.Sessions.Start())).Status);   // a clean caller is not held
        var read = await _host.Queue.SubmitAsync(Req.Call("email.list_recent", "{}", s));
        var held = await Send(s);
        Assert.Equal(ActionStatus.Pending, held.Status);

        var d = await Detail(held.Id);
        Assert.Equal("taint", d["why"]!["kind"]!.GetValue<string>());
        Assert.Contains("read outside content first (email.list_recent", d["why"]!["text"]!.GetValue<string>());
        Assert.Equal(read.Id, d["why"]!["taintSource"]!["actionId"]!.GetValue<string>());
        Assert.StartsWith("Reaches other people", d["effect"]!.GetValue<string>());
        Assert.Equal("ExternalEffect", d["risk"]!.GetValue<string>());
        var recent = d["recent"]!.AsArray();
        var first = Assert.Single(recent);
        Assert.Equal("email.list_recent", first!["tool"]!.GetValue<string>());
        Assert.True(first["readsUntrusted"]!.GetValue<bool>());
        Assert.True(first["taintSource"]!.GetValue<bool>());
        Assert.Empty(d["similar"]!.AsArray());

        // After a decision, the next held send shows it under "earlier decisions".
        var rejected = await _http.PostAsJsonAsync($"/api/actions/{held.Id}/review", new { decision = "reject", reason = "Not to that address" });
        rejected.EnsureSuccessStatusCode();
        var next = await Send(s, "c@d.co");
        var similar = (await Detail(next.Id))["similar"]!.AsArray();
        var earlier = Assert.Single(similar);
        Assert.Equal("Reject", earlier!["decision"]!.GetValue<string>());
        Assert.Equal("Not to that address", earlier["reason"]!.GetValue<string>());
        Assert.Equal("rita", earlier["reviewer"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_call_without_a_session_and_a_policy_hold_are_explained_too()
    {
        var noSession = await _host.Queue.SubmitAsync(new ActionRequest("email.send",
            """{"to":["a@b.co"],"subject":"Hi","body":"x"}""", Req.Me));
        Assert.Equal(ActionStatus.Pending, noSession.Status);
        var d = await Detail(noSession.Id);
        Assert.Equal("taint", d["why"]!["kind"]!.GetValue<string>());
        Assert.Contains("without a session", d["why"]!["text"]!.GetValue<string>());

        var host = new OpenTailHostBuilder().AddModule(new EmailModule(new StubInbox(), new StubSender()))
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "email.send": { "Mode": "Queue" } } }""")).Build();
        await using var admin = await AdminServer.StartAsync(host, new AdminOptions { Tokens = new Dictionary<string, string> { [Token] = "rita" } });
        using var http = new HttpClient { BaseAddress = admin.BaseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var queued = await host.Queue.SubmitAsync(new ActionRequest("email.send", """{"to":["a@b.co"],"subject":"Hi","body":"x"}""", Req.Me, host.Sessions.Start()));
        var p = JsonNode.Parse(await http.GetStringAsync($"/api/actions/{queued.Id}"))!;
        Assert.Equal("policy", p["why"]!["kind"]!.GetValue<string>());
        Assert.Contains("The policy for email.send asks for a reviewer", p["why"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_page_has_the_reviewer_aids_and_still_writes_text_only()
    {
        var html = await new HttpClient().GetStringAsync(new Uri(_admin.BaseUri, "/"));
        foreach (var s in new[] { "Why it is waiting", "Before this, the caller", "Earlier decisions on", "Only what I can decide", "Expiring within 2 hours", "<kbd>a</kbd> approve", "Give a reason" })
            Assert.Contains(s, html);
        Assert.DoesNotContain("innerHTML", html);
    }
}
