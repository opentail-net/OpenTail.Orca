using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using OpenTail.Modules.Microsoft365;

namespace OpenTail.Host.Tests;

public class GraphPagesModuleTests
{
    private const string Site = "acme.sharepoint.com,11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222";
    private const string Page = "33333333-3333-3333-3333-333333333333";

    private sealed class StubGraph : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public string Answer { get; set; } = """{"value":[]}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Urls) Urls.Add(Uri.UnescapeDataString(request.RequestUri!.PathAndQuery));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Answer, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private static (OpenTailHost Host, StubGraph Graph) Build()
    {
        var graph = new StubGraph();
        var client = new GraphServiceClient(new HttpClient(graph), new AnonymousAuthenticationProvider());
        var module = new GraphPagesModule(_ => ValueTask.FromResult(client), new GraphPagesModuleOptions { Id = "pages365", Sites = [Site] });
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "pages365.list_pages": { "Mode": "Execute" }, "pages365.read_page": { "Mode": "Execute" } } }"""))
            .Build();
        return (host, graph);
    }

    [Fact]
    public async Task Lists_the_pages_of_an_allowed_site()
    {
        var (host, graph) = Build();
        graph.Answer = """{"value":[{"id":"33333333-3333-3333-3333-333333333333","title":"Onboarding","description":"Start here","webUrl":"https://acme.sharepoint.com/SitePages/Onboarding.aspx"}]}""";
        var r = await host.Queue.SubmitAsync(Req.Call("pages365.list_pages", $$"""{"site":"{{Site}}"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Contains("Onboarding", r.ResultJson);
        Assert.StartsWith($"/v1.0/sites/{Site}/pages", Assert.Single(graph.Urls));
    }

    [Fact]
    public async Task Reads_the_text_web_parts_in_order()
    {
        var (host, graph) = Build();
        graph.Answer = $$$"""
            {"@odata.type":"#microsoft.graph.sitePage","id":"{{{Page}}}","title":"Onboarding",
             "canvasLayout":{"horizontalSections":[{"columns":[{"webparts":[
               {"@odata.type":"#microsoft.graph.textWebPart","innerHtml":"<h2>Week one</h2><p>Meet your buddy &amp; get a laptop.</p>"},
               {"@odata.type":"#microsoft.graph.standardWebPart","webPartType":"image"},
               {"@odata.type":"#microsoft.graph.textWebPart","innerHtml":"<ul><li>Badge</li><li>Wi-Fi</li></ul>"}]}]}]}}
            """;
        var r = await host.Queue.SubmitAsync(Req.Call("pages365.read_page", $$"""{"site":"{{Site}}","page":"{{Page}}"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Equal("Week one\nMeet your buddy & get a laptop.\n\nBadge\nWi-Fi", JsonNode.Parse(r.ResultJson!)!["text"]!.GetValue<string>());
        Assert.Contains("canvasLayout", Assert.Single(graph.Urls));
    }

    [Theory]
    [InlineData("pages365.list_pages", """{"site":"evil.sharepoint.com,11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222"}""", "not one of the allowed sites")]
    [InlineData("pages365.read_page", """{"site":"acme.sharepoint.com,11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222","page":"../x"}""", "not a page id")]
    public async Task Other_sites_and_odd_ids_are_refused_before_any_call(string tool, string args, string why)
    {
        var (host, graph) = Build();
        var r = await host.Queue.SubmitAsync(Req.Call(tool, args));
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Contains(why, r.Error!.Message);
        Assert.Empty(graph.Urls);
    }
}
