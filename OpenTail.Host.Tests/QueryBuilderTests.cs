using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenTail.Modules.OpenApi;

namespace OpenTail.Host.Tests;

/// <summary>
/// The query builder: the model sets typed filters and the profile's builder writes the filter expression, quoting every value for
/// the language. Hostile values must stay values: they never add a clause, an operator or an ORDER BY.
/// </summary>
public class QueryBuilderTests
{
    private const string Spec = """
        { "openapi": "3.0.1", "info": { "title": "Search", "version": "1" },
          "paths": {
            "/search": { "get": { "operationId": "search",
                "parameters": [ { "name": "jql", "in": "query", "schema": { "type": "string" } },
                                { "name": "maxResults", "in": "query", "schema": { "type": "integer" } } ] } },
            "/query": { "post": { "operationId": "query",
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object",
                    "properties": { "q": { "type": "string" }, "limit": { "type": "integer" } } } } } } } }
          } }
        """;

    private static string Profile(string origin, string language, string filters, string extra = "", string target = "jql", string operation = "search") => $$"""
        { "Profile": 1, "Name": "search", "Spec": "search.openapi.json", "Server": "{{origin}}", "Hosts": [ "{{origin}}" ],
          "Config": { "Projects": { "Type": "string[]" } },
          "Tools": {
            "find": { "Operation": "{{operation}}", "Description": "Find things.", "Risk": "Read",
                      "Parameters": { "maxResults": { "Fixed": 20 }, "*": { "Forbidden": true } },
                      "Query": { "Language": "{{language}}", "Target": "{{target}}", {{extra}} "Filters": { {{filters}} } } }
          } }
        """;

    private const string JiraFilters = """
        "project": { "Field": "project", "AllowedFrom": "Config.Projects", "Required": true },
        "status": { "Field": "status", "Enum": [ "Open", "In Progress", "Done" ] },
        "assignee": { "Field": "assignee" },
        "updated_since": { "Field": "updated", "Type": "date", "Op": "ge" },
        "priority_at_least": { "Field": "priority", "Type": "number", "Op": "ge" },
        "labels": { "Field": "labels", "Type": "list" },
        "text": { "Type": "text" }
        """;

    private sealed class Recorder
    {
        public readonly List<(string PathAndQuery, string Body)> Requests = [];
        public StubServer Server { get; private set; } = null!;

        public string Answer { get; set; } = """{ "issues": [] }""";

        public static async Task<Recorder> StartAsync()
        {
            var r = new Recorder();
            r.Server = await StubServer.StartAsync(async ctx =>
            {
                lock (r.Requests) r.Requests.Add((ctx.Request.Path + ctx.Request.QueryString.Value, (string)ctx.Items["body"]!));
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(r.Answer);
            });
            return r;
        }

        public string LastQuery => Uri.UnescapeDataString(Requests.Last().PathAndQuery.Replace('+', ' '));
    }

    private static async Task<OpenTailHost> HostFor(Recorder r, string profile)
    {
        var d = Path.Combine(Path.GetTempPath(), "otail-qb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "search.openapi.json"), Spec);
        File.WriteAllText(Path.Combine(d, "search.json"), profile);
        var modules = $$"""
            { "Modules": [ { "Type": "openapi", "Id": "s", "Network": { "Hosts": [ "{{r.Server.Origin}}" ] },
                "Config": { "Profile": "search.json", "AllowInsecureHttp": true, "Projects": [ "OPS", "IT" ] } } ] }
            """;
        var loaded = await ModuleLoader.LoadAsync(ModulesConfig.Parse(modules, d), ModuleLoadMode.Run);
        return new OpenTailHostBuilder().UsePolicy(PolicyConfig.Parse("""{ "Tools": { "s.find": { "Mode": "Execute" } } }""")).AddModules(loaded).Build();
    }

    private static async Task<ActionRecord> Find(OpenTailHost host, string args) => await host.Queue.SubmitAsync(Req.Call("s.find", args));

    [Fact]
    public async Task Jql_is_written_by_the_builder_and_the_model_sees_only_filters()
    {
        var r = await Recorder.StartAsync();
        await using var server = r.Server;
        var host = await HostFor(r, Profile(r.Server.Origin, "jql", JiraFilters, """ "Where": "issuetype != Epic", "OrderBy": "ORDER BY updated DESC", """));

        Assert.True(host.Catalog.TryGet("s.find", out var reg));
        var schema = JsonNode.Parse(reg.Function.JsonSchema.GetRawText())!.AsObject();
        var props = schema["properties"]!.AsObject();
        Assert.False(props.ContainsKey("jql"));                                   // the target is never the model's
        Assert.Equal(["OPS", "IT"], props["project"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("array", props["labels"]!["type"]!.GetValue<string>());
        Assert.Equal(["project"], schema["required"]!.AsArray().Select(x => x!.GetValue<string>()));

        var ok = await Find(host, """{"project":"OPS","status":"In Progress","updated_since":"2026-10-01","priority_at_least":2,"labels":["vpn","urgent"],"text":"printer jam"}""");
        Assert.Equal(ActionStatus.Succeeded, ok.Status);
        Assert.Equal(
            "/search?jql=(issuetype != Epic) AND project = \"OPS\" AND status = \"In Progress\" AND updated >= \"2026-10-01\" " +
            "AND priority >= 2 AND labels in (\"vpn\", \"urgent\") AND text ~ \"printer jam\" ORDER BY updated DESC&maxResults=20",
            r.LastQuery);
    }

    [Theory]
    [InlineData("""{"project":"OPS","assignee":"x\" OR project = SECRET OR assignee = \"y"}""", "assignee = \"x\\\" OR project = SECRET OR assignee = \\\"y\"")]
    [InlineData("""{"project":"OPS","assignee":"a\\\" ORDER BY created"}""", "assignee = \"a\\\\\\\" ORDER BY created\"")]
    [InlineData("""{"project":"OPS","text":"x\") OR (project = SECRET"}""", "text ~ \"x   OR  project = SECRET\"")]                  // quote and brackets become spaces: only words remain
    public async Task Hostile_jql_values_stay_inside_their_quotes(string args, string expectedClause)
    {
        var r = await Recorder.StartAsync();
        await using var server = r.Server;
        var host = await HostFor(r, Profile(r.Server.Origin, "jql", JiraFilters));
        var res = await Find(host, args);
        Assert.Equal(ActionStatus.Succeeded, res.Status);
        Assert.Contains(expectedClause, r.LastQuery);
    }

    [Theory]
    [InlineData("""{"status":"Open"}""", "Missing required filter 'project'")]
    [InlineData("""{"project":"SECRET"}""", "must be one of the configured Projects")]
    [InlineData("""{"project":"OPS","status":"Open\" OR 1=1"}""", "must be one of Open")]
    [InlineData("""{"project":"OPS","assignee":"a\nb"}""", "control characters")]
    [InlineData("""{"project":"OPS","updated_since":"yesterday"}""", "must be a date")]
    [InlineData("""{"project":"OPS","jql":"project = SECRET"}""", "cannot be set")]
    [InlineData("""{"project":"OPS","labels":[]}""", "at least one value")]
    public async Task Bad_filter_values_are_refused_before_any_call(string args, string why)
    {
        var r = await Recorder.StartAsync();
        await using var server = r.Server;
        var host = await HostFor(r, Profile(r.Server.Origin, "jql", JiraFilters));
        var res = await Find(host, args);
        Assert.Equal(ActionStatus.Failed, res.Status);
        Assert.Contains(why, res.Error!.Message);
        Assert.Empty(r.Requests);
    }

    [Theory]
    [InlineData("odata", """ "name": { "Field": "Name" }, "since": { "Field": "Modified", "Type": "datetime", "Op": "gt" }, "kw": { "Field": "Title", "Type": "text" } """,
        """{"name":"O'Brien' or 1 eq 1","since":"2026-10-01T08:00:00Z","kw":"it's"}""",
        "Name eq 'O''Brien'' or 1 eq 1' and Modified gt 2026-10-01T08:00:00Z and contains(Title,'it''s')")]
    [InlineData("zendesk", """ "status": { "Field": "status", "Type": "list" }, "updated": { "Field": "updated", "Type": "date", "Op": "gt" }, "words": { "Type": "text" } """,
        """{"status":["open","pending"],"updated":"2026-10-01","words":"printer jam"}""",
        "type:ticket status:\"open\" status:\"pending\" updated>2026-10-01 \"printer jam\"")]
    [InlineData("freshdesk", """ "status": { "Field": "status", "Type": "number" }, "tag": { "Field": "tag" } """,
        """{"status":2,"tag":"vpn"}""",
        "\"status:2 AND tag:'vpn'\"")]
    public async Task Other_languages_quote_their_own_way(string language, string filters, string args, string expected)
    {
        var r = await Recorder.StartAsync();
        await using var server = r.Server;
        var where = language == "zendesk" ? """ "Where": "type:ticket", """ : "";
        var host = await HostFor(r, Profile(r.Server.Origin, language, filters, where));
        var res = await Find(host, args);
        Assert.Equal(ActionStatus.Succeeded, res.Status);
        Assert.EndsWith("jql=" + expected + "&maxResults=20", r.LastQuery);
    }

    [Fact]
    public async Task Cql_is_quoted_as_jql_and_html_fields_reach_the_model_as_text()
    {
        var r = await Recorder.StartAsync();
        await using var server = r.Server;
        r.Answer = """{ "results": [ { "title": "Runbook", "excerpt": "<p>Restart the <b>printer</b> &amp; wait.</p><script>steal()</script><p>Then call&nbsp;IT.</p>" } ] }""";
        var profile = Profile(r.Server.Origin, "cql", """ "space": { "Field": "space", "AllowedFrom": "Config.Projects", "Required": true }, "text": { "Type": "text" } """,
            """ "Where": "type = page", """)
            .Replace("\"Query\":", "\"Response\": { \"Items\": \"$.results\", \"Fields\": { \"title\": \"$.title\", \"excerpt\": \"$.excerpt|html\" } }, \"Query\":");
        var host = await HostFor(r, profile);

        var res = await Find(host, """{"space":"OPS","text":"printer \"jam\""}""");

        Assert.Equal(ActionStatus.Succeeded, res.Status);
        Assert.Contains("jql=(type = page) AND space = \"OPS\" AND text ~ \"printer  jam\"", r.LastQuery);
        Assert.Equal("Restart the printer & wait.\nThen call IT.", JsonNode.Parse(res.ResultJson!)!["result"]![0]!["excerpt"]!.GetValue<string>());
        Assert.DoesNotContain("steal", res.ResultJson);
    }

    [Fact]
    public async Task Confluence_storage_format_and_adf_fields_reach_the_model_as_text()
    {
        var r = await Recorder.StartAsync();
        await using var server = r.Server;
        r.Answer = """
            { "results": [ {
                "title": "Runbook",
                "storage": "<h1>Printer</h1><p>Restart it.</p><ac:structured-macro ac:name=\"code\"><ac:plain-text-body><![CDATA[lpstat -t]]></ac:plain-text-body></ac:structured-macro>",
                "adf": { "type": "doc", "version": 1, "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "Call IT." } ] } ] } } ] }
            """;
        var profile = Profile(r.Server.Origin, "cql", """ "space": { "Field": "space", "AllowedFrom": "Config.Projects", "Required": true } """, """ "Where": "type = page", """)
            .Replace("\"Query\":", "\"Response\": { \"Items\": \"$.results\", \"Fields\": { \"title\": \"$.title\", \"body\": \"$.storage|storage\", \"note\": \"$.adf|adf\" } }, \"Query\":");
        var host = await HostFor(r, profile);

        var res = await Find(host, """{"space":"OPS"}""");

        Assert.Equal(ActionStatus.Succeeded, res.Status);
        var item = JsonNode.Parse(res.ResultJson!)!["result"]![0]!;
        var body = item["body"]!.GetValue<string>();
        Assert.Contains("Printer", body);
        Assert.Contains("Restart it.", body);
        Assert.Contains("lpstat -t", body);
        Assert.DoesNotContain("<", body);
        Assert.Equal("Call IT.", item["note"]!.GetValue<string>().Trim());
    }

    [Fact]
    public async Task Soql_goes_into_a_body_field_with_like_wildcards_escaped()
    {
        var r = await Recorder.StartAsync();
        await using var server = r.Server;
        var profile = Profile(r.Server.Origin, "soql",
            """ "subject": { "Field": "Subject", "Type": "text" }, "status": { "Field": "Status" } """,
            """ "Select": "SELECT Id, Subject FROM Case", "OrderBy": "ORDER BY LastModifiedDate DESC LIMIT 50", """,
            target: "body.q", operation: "query").Replace("\"maxResults\": { \"Fixed\": 20 }, ", "");
        var host = await HostFor(r, profile);
        var res = await Find(host, """{"subject":"50%_off\\' OR Name != '","status":"New"}""");
        Assert.Equal(ActionStatus.Succeeded, res.Status);
        var q = JsonNode.Parse(r.Requests.Last().Body)!["q"]!.GetValue<string>();
        Assert.Equal("SELECT Id, Subject FROM Case WHERE Subject LIKE '%50\\%\\_off\\\\\\' OR Name != \\'%' AND Status = 'New' ORDER BY LastModifiedDate DESC LIMIT 50", q);
    }

    [Theory]
    [InlineData("zendesk", """ "who": { "Field": "requester" } """, """{"who":"a\" OR status:closed"}""", "cannot contain a double quote")]
    [InlineData("freshdesk", """ "tag": { "Field": "tag" } """, """{"tag":"a' OR status:5"}""", "cannot contain quotes")]
    public async Task Values_a_language_cannot_escape_are_refused(string language, string filters, string args, string why)
    {
        var r = await Recorder.StartAsync();
        await using var server = r.Server;
        var host = await HostFor(r, Profile(r.Server.Origin, language, filters));
        var res = await Find(host, args);
        Assert.Equal(ActionStatus.Failed, res.Status);
        Assert.Contains(why, res.Error!.Message);
        Assert.Empty(r.Requests);
    }

    [Theory]
    [InlineData("sql", """ "a": { "Field": "x" } """, "", "Language must be one of")]
    [InlineData("soql", """ "a": { "Field": "x" } """, "", "SOQL needs Select")]
    [InlineData("jql", """ "a": { "Field": "x; DROP" } """, "", "Field 'x; DROP'")]
    [InlineData("odata", """ "a": { "Type": "text" } """, "", "Field is required")]
    [InlineData("freshdesk", """ "a": { "Field": "x", "Op": "ge" } """, "", "only eq, gt")]
    [InlineData("jql", """ "a": { "Field": "x", "Type": "list", "Op": "gt" } """, "", "takes no Op")]
    public void Bad_query_declarations_fail_when_the_profile_loads(string language, string filters, string extra, string why)
    {
        var ex = Assert.Throws<ArgumentException>(() => OpenApiProfile.Parse(Profile("https://api.example.com", language, filters, extra)));
        Assert.Contains(why, ex.Message);
    }
}
