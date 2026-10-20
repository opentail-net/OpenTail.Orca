using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OpenTail.Host.Admin;

namespace OpenTail.Host.Tests;

/// <summary>Plan v7 A1: read-only administration views on the review page, for admins only.</summary>
public class AdminViewsTests
{
    private const string AdminToken = "admin-token-0123456789";
    private const string ReviewerToken = "review-token-0123456789";

    private const string PolicyA = """{ "Tools": { "mail.send": { "Mode": "Queue" } }, "Agents": { "clerk": { "Tools": [ "inbox.*" ] } } }""";
    private const string PolicyB = """
        { "Tools": { "mail.send": { "Mode": "Queue" } },
          "Agents": { "clerk": { "Tools": [ "inbox.*", "mail.*" ], "Requestable": [ "mail.send" ] } },
          "People": { "rita": [ "admin:rita", "key:rita-bot" ] } }
        """;

    private static async Task<(OpenTailHost Host, RunningAdmin Admin, TempDb Db)> Start()
    {
        var db = new TempDb();
        var a = Hosts.BuildSqlite(new FakeInboxModule(), PolicyA, db.Path);
        await a.RunMaintenanceOnceAsync();
        TempDb.ClearPools(db.Path);
        var host = Hosts.BuildSqlite(new FakeInboxModule(), PolicyB, db.Path);
        await host.RunMaintenanceOnceAsync();
        await AccessChanges.RecordKeysAsync(host.AccessSnapshots, host.Audit, [new KeyAccess("rita-bot", "ab12cd", "clerk")], DateTimeOffset.UtcNow, "ops");
        var admin = await AdminServer.StartAsync(host, new AdminOptions
        {
            Tokens = new Dictionary<string, string> { [AdminToken] = "ops", [ReviewerToken] = "rev" },
            TokenRoles = new Dictionary<string, IReadOnlySet<string>>
            {
                [AdminToken] = new HashSet<string> { Roles.Review, Roles.Admin },
                [ReviewerToken] = new HashSet<string> { Roles.Review },
            },
        });
        return (host, admin, db);
    }

    private static async Task<(HttpStatusCode Code, JsonNode? Body)> Get(RunningAdmin admin, string path, string token)
    {
        using var http = new HttpClient { BaseAddress = admin.BaseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    [Fact]
    public async Task A_reviewer_is_refused_every_admin_view_and_told_they_are_not_an_admin()
    {
        var (host, admin, db) = await Start();
        await using (admin)
        using (db)
        {
            var me = await Get(admin, "/api/me", ReviewerToken);
            Assert.False(me.Body!["isAdmin"]!.GetValue<bool>());
            foreach (var path in new[] { "/api/admin/overview", "/api/admin/policy-versions", "/api/admin/policy-versions/diff?from=x", "/api/admin/usage" })
                Assert.Equal(HttpStatusCode.Forbidden, (await Get(admin, path, ReviewerToken)).Code);
            Assert.True((await Get(admin, "/api/me", AdminToken)).Body!["isAdmin"]!.GetValue<bool>());
        }
        TempDb.ClearPools(db.Path);
        _ = host;
    }

    [Fact]
    public async Task The_overview_shows_profiles_resolved_people_and_keys_without_key_material()
    {
        var (host, admin, db) = await Start();
        await using (admin)
        using (db)
        {
            var (code, o) = await Get(admin, "/api/admin/overview", AdminToken);
            Assert.Equal(HttpStatusCode.OK, code);
            Assert.Equal(host.Policy.PolicyHash, o!["policy"]!["hash"]!.GetValue<string>());

            var clerk = o["agents"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "clerk")!;
            var tools = clerk["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["mode"]!.GetValue<string>());
            Assert.Equal("Queue", tools["mail.send"]);
            Assert.Equal("Deny", tools["inbox.read"]); // in the profile, but the policy denies it: shown, so an admin sees why the agent lacks it
            Assert.Equal(["mail.send"], clerk["requestable"]!.AsArray().Select(x => x!.GetValue<string>()));

            Assert.Equal("rita", o["people"]![0]!["person"]!.GetValue<string>());
            var key = o["keys"]![0]!;
            Assert.Equal("rita-bot", key["name"]!.GetValue<string>());
            Assert.Equal("ab12cd", key["hashPrefix"]!.GetValue<string>());
            Assert.Equal("clerk", key["profile"]!.GetValue<string>());
            Assert.Contains(o["health"]!["status"]!.GetValue<string>(), new[] { "ok", "degraded" });
        }
        TempDb.ClearPools(db.Path);
    }

    [Fact]
    public async Task Policy_versions_list_both_and_the_diff_names_the_changed_profile_and_the_new_person()
    {
        var (host, admin, db) = await Start();
        await using (admin)
        using (db)
        {
            var versions = (await Get(admin, "/api/admin/policy-versions", AdminToken)).Body!["versions"]!.AsArray();
            Assert.Equal(2, versions.Count);
            var old = versions.Single(v => !v!["running"]!.GetValue<bool>())!["hash"]!.GetValue<string>();

            var changes = (await Get(admin, "/api/admin/policy-versions/diff?from=" + Uri.EscapeDataString(old), AdminToken)).Body!["changes"]!.AsArray();
            var lines = changes.Select(c => $"{c!["kind"]} {c["section"]}/{c["key"]}").ToList();
            Assert.Contains("changed Agents/clerk", lines);
            Assert.Contains("added People/rita", lines);
            Assert.DoesNotContain(lines, l => l.Contains("Tools/", StringComparison.Ordinal));

            Assert.Equal(HttpStatusCode.NotFound, (await Get(admin, "/api/admin/policy-versions/diff?from=sha256:nope", AdminToken)).Code);
        }
        TempDb.ClearPools(db.Path);
        _ = host;
    }

    [Fact]
    public async Task The_sqlite_audit_tail_matches_the_end_of_the_full_trail()
    {
        var (host, admin, db) = await Start();
        await using (admin)
        using (db)
        {
            var reader = (IAuditReader)host.Audit;
            var all = reader.Read();
            Assert.True(all.Count > 3);
            Assert.Equal(all.TakeLast(3), reader.ReadRecent(3));
            Assert.Equal(all, reader.ReadRecent(10_000));
        }
        TempDb.ClearPools(db.Path);
    }

    [Fact]
    public void The_diff_reports_added_removed_and_changed_entries_and_whole_sections()
    {
        var changes = PolicyDiff.Compare(
            """{ "Agents": { "a": { "Tools": ["x"] }, "b": { "Tools": ["y"] } }, "TaintWindow": "00:30:00" }""",
            """{ "Agents": { "a": { "Tools": ["x", "z"] }, "c": { "Tools": ["y"] } }, "TaintWindow": "01:00:00" }""");
        Assert.Equal(["changed Agents/a", "removed Agents/b", "added Agents/c", "changed TaintWindow/"],
            changes.Select(c => $"{c!["kind"]} {c["section"]}/{c["key"]}"));
    }
}
