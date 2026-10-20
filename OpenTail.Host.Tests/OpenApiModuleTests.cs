using System.Net;
using System.Text;
using OpenTail.Modules.OpenApi;

namespace OpenTail.Host.Tests;

public class OpenApiModuleTests
{
    private const string Spec = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Customers", "version": "1" },
          "servers": [ { "url": "https://api.example.test/v1" } ],
          "paths": {
            "/customers/{id}": {
              "get": {
                "operationId": "getCustomer", "summary": "Get one customer",
                "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
                "responses": { "200": { "description": "ok" } }
              },
              "delete": {
                "operationId": "deleteCustomer", "summary": "Delete a customer",
                "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
                "responses": { "204": { "description": "gone" } }
              }
            },
            "/customers": {
              "get": {
                "operationId": "searchCustomers", "summary": "Search",
                "parameters": [ { "name": "q", "in": "query", "required": true, "schema": { "type": "string" } },
                                { "name": "limit", "in": "query", "schema": { "type": "integer" } } ],
                "responses": { "200": { "description": "ok" } }
              },
              "post": {
                "operationId": "createCustomer", "summary": "Create a customer",
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object", "required": [ "name" ],
                  "properties": { "name": { "type": "string" }, "email": { "type": "string" } } } } } },
                "responses": { "201": { "description": "created" } }
              }
            },
            "/old": { "get": { "operationId": "oldThing", "deprecated": true, "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (OpenTailHost Host, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null, string? envVar = null, string policy = DefaultPolicy)
    {
        var handler = new StubHandler(respond ?? (_ => Json(HttpStatusCode.OK, "{\"id\":\"c1\",\"name\":\"Ada\"}")));
        var module = OpenApiModule.Create(Spec, new OpenApiModuleOptions
        {
            Namespace = "crm",
            HttpClient = new HttpClient(handler),
            AuthTokenEnvVar = envVar,
        });
        var host = new OpenTailHostBuilder().AddModule(module).UsePolicy(PolicyConfig.Parse(policy)).Build();
        return (host, handler);
    }

    // Reads execute, writes queue, delete has no entry so it is denied.
    private const string DefaultPolicy = """
        { "DefaultReadMode": "Execute",
          "Tools": { "crm.createCustomer": { "Mode": "Queue" } } }
        """;

    private static ActionRequest Call(string tool, string args, string? session = null) =>
        new(tool, args, new ActionRequester(RequesterKind.User, "me"), session);

    [Fact]
    public void Operations_become_tools_with_verb_derived_risk_and_deprecated_ones_are_skipped()
    {
        var (host, _) = Build();
        var tools = host.Catalog.All.ToDictionary(t => t.Descriptor.Name, t => t.Descriptor);
        Assert.Equal(["crm.createCustomer", "crm.deleteCustomer", "crm.getCustomer", "crm.searchCustomers"], tools.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(ToolRisk.Read, tools["crm.getCustomer"].Risk);
        Assert.Equal(ToolRisk.Read, tools["crm.searchCustomers"].Risk);
        Assert.Equal(ToolRisk.ExternalEffect, tools["crm.createCustomer"].Risk);
        Assert.Equal(ToolRisk.Destructive, tools["crm.deleteCustomer"].Risk);
        Assert.All(tools.Values, t => Assert.True(t.ReadsUntrusted));
    }

    [Fact]
    public void Input_schema_reflects_parameters_and_body_and_forbids_extras()
    {
        var (host, _) = Build();
        var create = host.Catalog.All.Single(t => t.Descriptor.Name == "crm.createCustomer").Function.JsonSchema;
        Assert.Equal("object", create.GetProperty("type").GetString());
        Assert.Equal("string", create.GetProperty("properties").GetProperty("body").GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("body", create.GetProperty("required")[0].GetString());
        Assert.False(create.GetProperty("additionalProperties").GetBoolean());

        var search = host.Catalog.All.Single(t => t.Descriptor.Name == "crm.searchCustomers").Function.JsonSchema;
        Assert.Equal("integer", search.GetProperty("properties").GetProperty("limit").GetProperty("type").GetString());
        Assert.Equal(["q"], search.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Policy_by_verb_get_executes_post_queues_delete_is_denied()
    {
        var (host, handler) = Build();

        var get = await host.Queue.SubmitAsync(Call("crm.getCustomer", "{\"id\":\"c1\"}"));
        Assert.Equal(ActionStatus.Succeeded, get.Status);
        Assert.Contains("Ada", get.ResultJson);
        Assert.Equal("https://api.example.test/v1/customers/c1", handler.Requests.Single().RequestUri!.ToString());

        var post = await host.Queue.SubmitAsync(Call("crm.createCustomer", "{\"body\":{\"name\":\"Bob\"}}"));
        Assert.Equal(ActionStatus.Pending, post.Status);
        Assert.Single(handler.Requests); // nothing sent yet
        Assert.Contains("POST https://api.example.test/v1/customers", post.PreviewJson);

        var del = await host.Queue.SubmitAsync(Call("crm.deleteCustomer", "{\"id\":\"c1\"}"));
        Assert.Equal(ActionStatus.Denied, del.Status);
        Assert.Single(handler.Requests);

        var approved = await host.Queue.ReviewAsync(post.Id, new ActionReview("alice", ReviewDecision.Approve, null, DateTimeOffset.UtcNow));
        Assert.Equal(ActionStatus.Succeeded, approved.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("{\"name\":\"Bob\"}", handler.Bodies[1]);
    }

    [Fact]
    public async Task Reading_via_an_api_taints_the_session_so_a_later_write_cannot_skip_review()
    {
        var (host, _) = Build(policy: """
            { "DefaultReadMode": "Execute", "Tools": { "crm.createCustomer": { "Mode": "Execute" } } }
            """);
        var s = host.Sessions.Start();
        Assert.Equal(ActionStatus.Succeeded, (await host.Queue.SubmitAsync(Call("crm.createCustomer", "{\"body\":{\"name\":\"A\"}}", s))).Status);
        await host.Queue.SubmitAsync(Call("crm.getCustomer", "{\"id\":\"c1\"}", s));
        Assert.Equal(ActionStatus.Pending, (await host.Queue.SubmitAsync(Call("crm.createCustomer", "{\"body\":{\"name\":\"B\"}}", s))).Status);
    }

    [Theory]
    [InlineData("..%2f..%2fadmin")]
    [InlineData("x?evil=1")]
    [InlineData("@evil.example")]
    [InlineData("a b#frag")]
    public async Task Hostile_but_slashless_values_are_escaped_into_one_path_segment(string id)
    {
        var (host, handler) = Build();
        await host.Queue.SubmitAsync(Call("crm.getCustomer", System.Text.Json.JsonSerializer.Serialize(new { id })));
        var uri = handler.Requests.Single().RequestUri!;
        Assert.Equal("api.example.test", uri.Host);
        Assert.Equal("", uri.Query);
        Assert.Equal("", uri.Fragment);
        Assert.Equal(3, uri.AbsolutePath.Trim('/').Split('/').Length); // v1 / customers / <one segment>
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("../../other")]
    [InlineData("..")]
    [InlineData("a\\b")]
    public async Task Values_that_could_traverse_are_refused_without_a_request(string id)
    {
        var (host, handler) = Build();
        var r = await host.Queue.SubmitAsync(Call("crm.getCustomer", System.Text.Json.JsonSerializer.Serialize(new { id })));
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Contains("must not contain", r.Error!.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Query_parameters_are_escaped_and_optional_ones_omitted()
    {
        var (host, handler) = Build();
        await host.Queue.SubmitAsync(Call("crm.searchCustomers", "{\"q\":\"a&b=c d\"}"));
        Assert.Equal("?q=a%26b%3Dc%20d", handler.Requests.Single().RequestUri!.Query);
    }

    [Fact]
    public async Task Auth_token_comes_from_the_environment_and_never_reaches_the_model_or_the_audit()
    {
        const string name = "OTAIL_TEST_API_TOKEN";
        Environment.SetEnvironmentVariable(name, "tok-123456789");
        try
        {
            var (host, handler) = Build(envVar: name);
            var r = await host.Queue.SubmitAsync(Call("crm.getCustomer", "{\"id\":\"c1\"}"));
            Assert.Equal("Bearer tok-123456789", handler.Requests.Single().Headers.GetValues("Authorization").Single());
            Assert.DoesNotContain("tok-123456789", ActionViews.Response(r));
            Assert.DoesNotContain("tok-123456789", r.ArgumentsJson);
            Assert.DoesNotContain("tok-123456789", host.Catalog.All.Single(t => t.Descriptor.Name == "crm.getCustomer").Function.JsonSchema.GetRawText());
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public async Task Http_errors_become_failed_actions_with_a_scrubbed_message()
    {
        var (host, _) = Build(_ => Json(HttpStatusCode.Unauthorized, "{\"error\":\"bad token: password=hunter2\"}"));
        var r = await host.Queue.SubmitAsync(Call("crm.getCustomer", "{\"id\":\"c1\"}"));
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Contains("HTTP 401", r.Error!.Message);
        Assert.DoesNotContain("hunter2", r.Error.Message);
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        var (host, _) = Build(_ =>
        {
            var m = new HttpResponseMessage(HttpStatusCode.Found);
            m.Headers.Location = new Uri("https://evil.example/steal");
            return m;
        });
        var r = await host.Queue.SubmitAsync(Call("crm.getCustomer", "{\"id\":\"c1\"}"));
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Contains("redirect", r.Error!.Message);
    }

    [Fact]
    public async Task Oversized_responses_are_truncated()
    {
        var (host, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 200_000), Encoding.UTF8, "text/plain"),
        });
        var r = await host.Queue.SubmitAsync(Call("crm.getCustomer", "{\"id\":\"c1\"}"));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Contains("\"truncated\":true", r.ResultJson);
        Assert.True(r.ResultJson!.Length < 70_000);
    }

    [Fact]
    public async Task Bad_arguments_to_a_queued_operation_are_refused_before_review()
    {
        var (host, handler) = Build();
        var missing = await host.Queue.SubmitAsync(Call("crm.createCustomer", "{}"));
        Assert.Equal(ActionStatus.Denied, missing.Status);
        Assert.Equal("invalid_arguments", missing.Error!.Code);

        var extra = await host.Queue.SubmitAsync(Call("crm.createCustomer", "{\"body\":{\"name\":\"x\"},\"admin\":true}"));
        Assert.Equal("invalid_arguments", extra.Error!.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Plain_http_servers_are_refused_unless_explicitly_allowed()
    {
        var http = Spec.Replace("https://api.example.test/v1", "http://api.example.test/v1");
        Assert.Throws<ArgumentException>(() => OpenApiModule.Create(http, new OpenApiModuleOptions { Namespace = "crm" }));
        _ = OpenApiModule.Create(http, new OpenApiModuleOptions { Namespace = "crm", AllowInsecureHttp = true, HttpClient = new HttpClient() });
    }

    [Fact]
    public void Operator_can_override_risk_and_limit_the_operation_set()
    {
        var module = OpenApiModule.Create(Spec, new OpenApiModuleOptions
        {
            Namespace = "crm",
            HttpClient = new HttpClient(),
            IncludeOperations = new HashSet<string> { "getCustomer", "createCustomer" },
            RiskOverrides = new Dictionary<string, ToolRisk> { ["createCustomer"] = ToolRisk.Write },
        });
        var catalog = new ToolCatalog();
        module.Register(catalog);
        Assert.Equal(2, catalog.All.Count);
        Assert.Equal(ToolRisk.Write, catalog.All.Single(t => t.Descriptor.Name == "crm.createCustomer").Descriptor.Risk);
    }

    [Fact]
    public async Task Works_over_real_mcp_with_the_schema_the_client_sees()
    {
        var (host, _) = Build();
        await using var pair = await McpPair.StartAsync(host);
        var tools = (await pair.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("crm.getCustomer", tools);
        Assert.DoesNotContain("crm.deleteCustomer", tools); // denied: not advertised

        var r = await pair.CallAsync("crm.getCustomer", ("id", "c1"));
        Assert.Equal("succeeded", r["status"]!.ToString());
        Assert.Equal("Ada", r["result"]!["body"]!["name"]!.ToString());
    }
}
