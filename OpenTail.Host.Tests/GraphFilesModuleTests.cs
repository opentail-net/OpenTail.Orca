using System.Net;
using System.Text;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using OpenTail.Modules.Microsoft365;

namespace OpenTail.Host.Tests;

/// <summary>files-graph against a stubbed Graph: only the allowed drives, ids that cannot change the URL, size caps, text extraction.</summary>
public class GraphFilesModuleTests
{
    private const string Drive = "b!drive1";

    private sealed class StubGraph : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public Func<string, HttpResponseMessage> Respond { get; set; } = _ => Json("""{"value":[]}""");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            lock (Urls) Urls.Add(url);
            var r = Respond(url);
            r.RequestMessage = request;
            return Task.FromResult(r);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (OpenTailHost Host, StubGraph Graph) Build(long maxFileBytes = 10_000, int maxTextChars = 1000)
    {
        var graph = new StubGraph();
        var client = new GraphServiceClient(new HttpClient(graph), new AnonymousAuthenticationProvider());
        var module = new GraphFilesModule(_ => ValueTask.FromResult(client), new GraphFilesModuleOptions
        {
            Id = "files365",
            Drives = [Drive],
            MaxFileBytes = maxFileBytes,
            MaxTextChars = maxTextChars,
        });
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "files365.search": { "Mode": "Execute" }, "files365.list": { "Mode": "Execute" }, "files365.read": { "Mode": "Execute" } } }"""))
            .Build();
        return (host, graph);
    }

    [Fact]
    public async Task Search_asks_each_allowed_drive_and_quotes_the_words()
    {
        var (host, graph) = Build();
        graph.Respond = _ => Json("""{"value":[{"id":"01ABC","name":"Q3 report.docx","size":1234,"webUrl":"https://acme.sharepoint.com/x","file":{}}]}""");
        var s = host.Sessions.Start();
        var r = await host.Queue.SubmitAsync(Req.Call("files365.search", """{"query":"o'brien budget"}""", s));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Contains("Q3 report.docx", r.ResultJson);
        Assert.Contains("/drives/b!drive1/search(q='o''brien budget')", Assert.Single(graph.Urls));
        Assert.True(host.Sessions.IsTainted(s));
    }

    [Theory]
    [InlineData("files365.search", """{"query":"x","drive":"b!other"}""", "not one of the allowed drives")]
    [InlineData("files365.list", """{"drive":"b!other"}""", "not one of the allowed drives")]
    [InlineData("files365.read", """{"drive":"b!drive1","item":"../../sites/root"}""", "not an item id")]
    [InlineData("files365.read", """{"drive":"b!drive1","item":"01A?x=1"}""", "not an item id")]
    [InlineData("files365.search", """{"query":"a\nb"}""", "control characters")]
    public async Task Other_drives_and_odd_ids_are_refused_before_any_call(string tool, string args, string why)
    {
        var (host, graph) = Build();
        var r = await host.Queue.SubmitAsync(Req.Call(tool, args));
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Contains(why, r.Error!.Message);
        Assert.Empty(graph.Urls);
    }

    [Fact]
    public async Task Read_downloads_the_file_and_returns_its_text()
    {
        var (host, graph) = Build(maxTextChars: 10);
        graph.Respond = url => url.EndsWith("/content", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Quarterly revenue rose.")) }
            : Json("""{"id":"01ABC","name":"notes.txt","size":23,"file":{}}""");
        var r = await host.Queue.SubmitAsync(Req.Call("files365.read", """{"drive":"b!drive1","item":"01ABC"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Contains("\"text\":\"Quarterly \"", r.ResultJson);
        Assert.Contains("\"truncated\":true", r.ResultJson);
        Assert.Equal(["/v1.0/drives/b!drive1/items/01ABC", "/v1.0/drives/b!drive1/items/01ABC/content"], graph.Urls.Select(u => u.Split('?')[0]));
    }

    [Fact]
    public async Task Folders_and_oversized_files_are_not_downloaded()
    {
        var (host, graph) = Build(maxFileBytes: 100);
        graph.Respond = _ => Json("""{"id":"01DIR","name":"Reports","folder":{"childCount":3}}""");
        var folder = await host.Queue.SubmitAsync(Req.Call("files365.read", """{"drive":"b!drive1","item":"01DIR"}"""));
        Assert.Contains("folder", folder.Error!.Message);

        graph.Respond = _ => Json("""{"id":"01BIG","name":"big.pdf","size":5000,"file":{}}""");
        var big = await host.Queue.SubmitAsync(Req.Call("files365.read", """{"drive":"b!drive1","item":"01BIG"}"""));
        Assert.Contains("up to 100 bytes", big.Error!.Message);
        Assert.DoesNotContain(graph.Urls, u => u.Contains("/content"));
    }

    [Fact]
    public async Task List_shows_the_top_folder_by_default()
    {
        var (host, graph) = Build();
        graph.Respond = _ => Json("""{"value":[{"id":"01DIR","name":"Reports","folder":{"childCount":3}}]}""");
        var r = await host.Queue.SubmitAsync(Req.Call("files365.list", """{"drive":"b!drive1"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Contains("\"kind\":\"folder\"", r.ResultJson);
        Assert.StartsWith("/v1.0/drives/b!drive1/items/root/children", Assert.Single(graph.Urls));
    }

    [Fact]
    public void A_module_without_drives_does_not_start() =>
        Assert.Throws<ArgumentException>(() => new GraphFilesModule(_ => throw new InvalidOperationException(), new GraphFilesModuleOptions()));
}
