using OpenTail.Modules.OpenApi;

namespace OpenTail.Orca.Tests;

/// <summary>
/// v4 P4b: profiles shipped inside packages (enabled by <c>package:Assembly/name</c>, only from what the deployment references), the
/// conformance check every profile author can run, and profile findings in <c>--lint</c>.
/// </summary>
public class ProfilePackageTests
{
    private static string RepoDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "OpenTail.Orca.slnx"))) d = d.Parent;
        return d!.FullName;
    }

    private static Task<ModuleLoadResult> Load(string json) => ModuleLoader.LoadAsync(ModulesConfig.Parse(json), ModuleLoadMode.Describe);

    [Fact]
    public async Task A_profile_inside_a_referenced_package_is_enabled_by_name()
    {
        var loaded = await Load("""
            { "Modules": [ { "Type": "openapi", "Id": "yt", "Network": { "Hosts": [ "www.googleapis.com" ] },
                "Credentials": { "youtube": { "Env": "OTAIL_NOT_SET_YT", "Kind": "Header", "HeaderName": "X-Goog-Api-Key", "Host": "www.googleapis.com" } },
                "Config": { "Profile": "package:OpenTail.Profiles.YouTube/youtube" } },
              { "Type": "openapi", "Id": "x", "Network": { "Hosts": [ "api.x.com" ] },
                "Credentials": { "x": { "Env": "OTAIL_NOT_SET_X", "Host": "api.x.com" } },
                "Config": { "Profile": "package:OpenTail.Profiles.X/x" } } ] }
            """);
        var host = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Deny).AddModules(loaded).Build();
        Assert.Equal(["x.get_post", "x.search", "yt.get_comments", "yt.get_video", "yt.search"],
            host.Catalog.All.Select(r => r.Descriptor.Name).Order().ToArray());
        var yt = host.Catalog.Modules.Single(m => m.Id == "yt").Manifest;
        Assert.Equal(["www.googleapis.com"], yt.NetworkHosts);
        Assert.Contains(yt.Notes, n => n.Contains("package:OpenTail.Profiles.YouTube/youtube"));
    }

    [Fact]
    public async Task The_jira_profile_loads_for_a_site_and_shows_only_filters_for_search()
    {
        var loaded = await Load("""
            { "Modules": [ { "Type": "openapi", "Id": "jira", "Network": { "Hosts": [ "acme.atlassian.net" ] },
                "Credentials": { "jira": { "Env": "OTAIL_NOT_SET_JIRA", "Kind": "Basic", "UserName": "bot@acme.example", "Host": "acme.atlassian.net" } },
                "Config": { "Profile": "package:OpenTail.Profiles.Jira/jira", "ServerUrl": "https://acme.atlassian.net", "Projects": [ "OPS" ] } } ] }
            """);
        var host = new OpenTailHostBuilder().UsePolicy(PolicyConfig.Deny).AddModules(loaded).Build();
        Assert.Equal(["jira.get_issue", "jira.list_comments", "jira.search_issues"], host.Catalog.All.Select(r => r.Descriptor.Name).Order().ToArray());
        Assert.True(host.Catalog.TryGet("jira.search_issues", out var search));
        var props = System.Text.Json.Nodes.JsonNode.Parse(search.Function.JsonSchema.GetRawText())!["properties"]!.AsObject();
        Assert.False(props.ContainsKey("jql"));
        Assert.Contains("project", props.Select(p => p.Key));
        Assert.Equal(["OPS"], props["project"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public async Task A_package_that_is_not_referenced_or_a_profile_it_does_not_have_is_refused_with_the_reason()
    {
        var missing = await Assert.ThrowsAnyAsync<Exception>(() => Load("""
            { "Modules": [ { "Type": "openapi", "Id": "crm", "Config": { "Profile": "package:Contoso.Profiles.Crm/crm" } } ] }
            """));
        Assert.Contains("Contoso.Profiles.Crm is not referenced", missing.Message);

        var wrong = await Assert.ThrowsAnyAsync<Exception>(() => Load("""
            { "Modules": [ { "Type": "openapi", "Id": "yt", "Config": { "Profile": "package:OpenTail.Profiles.YouTube/vimeo" } } ] }
            """));
        Assert.Contains("profiles in it: youtube", wrong.Message);
    }

    [Theory]
    [InlineData("profiles/youtube/youtube.json")]
    [InlineData("profiles/x/x.json")]
    [InlineData("package:OpenTail.Profiles.YouTube/youtube")]
    [InlineData("package:OpenTail.Profiles.X/x")]
    [InlineData("profiles/jira/jira.json")]
    [InlineData("package:OpenTail.Profiles.Jira/jira")]
    [InlineData("profiles/confluence/confluence.json")]
    [InlineData("package:OpenTail.Profiles.Confluence/confluence")]
    public void Every_shipped_profile_passes_conformance_with_no_findings(string reference)
    {
        var report = ProfileConformance.Check(reference, RepoDir());
        Assert.Empty(report.Findings);
        Assert.NotEmpty(report.Tools);
        Assert.All(report.Tools, t => Assert.Equal(ToolRisk.Read, t.Risk));
        Assert.All(report.Tools, t => Assert.True(t.ReadsUntrusted));
    }

    private const string Spec = """
        { "openapi": "3.0.1", "info": { "title": "t", "version": "1" }, "servers": [ { "url": "https://crm.example.com" } ],
          "paths": {
            "/contacts": { "get": { "operationId": "listContacts", "parameters": [ { "name": "q", "in": "query", "schema": { "type": "string" } } ] } },
            "/contacts/{id}/notes": { "post": { "operationId": "addNote",
                "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
                                { "name": "notify", "in": "query", "schema": { "type": "boolean" } } ],
                "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "text": { "type": "string" } } } } } } } } } }
        """;

    private const string OpenDoors = """
        { "Profile": 1, "Name": "crm", "Spec": "crm.openapi.json", "Hosts": [ "crm.example.com" ],
          "Tools": {
            "find": { "Operation": "listContacts", "Risk": "Read" },
            "note": { "Operation": "addNote", "Description": "Add a note.", "Risk": "ExternalEffect", "Preview": "Add a note." } } }
        """;

    private static string WriteProfile(string profile)
    {
        var dir = Path.Combine(Path.GetTempPath(), "otail-conf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "crm.openapi.json"), Spec);
        File.WriteAllText(Path.Combine(dir, "crm.json"), profile);
        return dir;
    }

    [Fact]
    public void Conformance_reports_every_door_a_profile_leaves_open()
    {
        var dir = WriteProfile(OpenDoors);
        var report = ProfileConformance.Check(Path.Combine(dir, "crm.json"));
        var all = string.Join("\n", report.Findings);
        Assert.Contains("has no Notes", all);
        Assert.Contains("tool 'find' has no Description", all);
        Assert.Contains("tool 'find' has no Response rule", all);
        Assert.Contains("tool 'note' is ExternalEffect and lets the model set parameters the profile does not name (id, notify)", all);
        Assert.Contains("tool 'note' is ExternalEffect and lets the model set any body field", all);
        Assert.Contains("tool 'note': the Preview names no argument", all);
        Assert.Contains("path parameter 'id' has no Pattern", all);
        Assert.Equal(2, report.Tools.Count);
        Assert.Contains(report.Tools, t => t.Name == "crm.note" && t.Preview && t.Risk == ToolRisk.ExternalEffect);
    }

    [Fact]
    public async Task Lint_and_list_modules_show_a_profiles_findings()
    {
        var dir = WriteProfile(OpenDoors);
        File.WriteAllText(Path.Combine(dir, "modules.json"), """
            { "Modules": [ { "Type": "openapi", "Id": "crm", "Network": { "Hosts": [ "crm.example.com" ] }, "Config": { "Profile": "crm.json" } } ] }
            """);
        var output = new StringWriter();
        var code = await OpenTail.Orca.Admin.PolicyCli.TryRunAsync(["--modules", Path.Combine(dir, "modules.json"), "--lint"], "t", _ => { }, output);
        Assert.Equal(0, code);
        Assert.Contains("profile crm: tool 'note' is ExternalEffect and lets the model set parameters the profile does not name", output.ToString());
    }
}
