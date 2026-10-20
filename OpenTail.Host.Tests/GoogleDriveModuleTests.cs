using System.Net;
using System.Text;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using OpenTail.Modules.Google;

namespace OpenTail.Host.Tests;

/// <summary>drive-google against a stubbed Drive API: allowed shared drives only (checked on the file, not just the argument), quoting, exports.</summary>
public class GoogleDriveModuleTests
{
    private const string Allowed = "0AAllowedDrive";

    private sealed class StubDrive : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public Func<string, HttpResponseMessage> Respond { get; set; } = _ => Json("""{"files":[]}""");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Replace('+', ' ');
            lock (Urls) Urls.Add(url);
            var r = Respond(url);
            r.RequestMessage = request;
            return Task.FromResult(r);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : Google.Apis.Http.HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(Google.Apis.Http.CreateHttpClientArgs args) => handler;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(string text) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    private static (OpenTailHost Host, StubDrive Stub) Build()
    {
        var stub = new StubDrive();
        var service = new DriveService(new BaseClientService.Initializer { HttpClientFactory = new StubFactory(stub), ApplicationName = "test" });
        var module = new GoogleDriveModule(_ => ValueTask.FromResult(service), new GoogleDriveModuleOptions { Id = "drive", SharedDrives = [Allowed], MaxFileBytes = 1000 });
        var host = new OpenTailHostBuilder().AddModule(module)
            .UsePolicy(PolicyConfig.Parse("""{ "Tools": { "drive.search": { "Mode": "Execute" }, "drive.list": { "Mode": "Execute" }, "drive.read": { "Mode": "Execute" } } }"""))
            .Build();
        return (host, stub);
    }

    private const string Doc = """{"id":"doc1","name":"Plan","mimeType":"application/vnd.google-apps.document","driveId":"0AAllowedDrive"}""";

    [Fact]
    public async Task Search_stays_in_the_allowed_drive_and_quotes_the_words()
    {
        var (host, stub) = Build();
        stub.Respond = _ => Json($$"""{"files":[{{Doc}}]}""");
        var r = await host.Queue.SubmitAsync(Req.Call("drive.search", """{"query":"o'brien \\ budget"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        var url = Assert.Single(stub.Urls);
        Assert.Contains("q=fullText contains 'o\\'brien \\\\ budget' and trashed = false", url);
        Assert.Contains("corpora=drive", url);
        Assert.Contains("driveId=0AAllowedDrive", url);
    }

    [Fact]
    public async Task A_google_doc_is_exported_as_text()
    {
        var (host, stub) = Build();
        stub.Respond = url => url.Contains("/export") ? Bytes("Roadmap for Q4") : Json(Doc);
        var r = await host.Queue.SubmitAsync(Req.Call("drive.read", """{"file":"doc1"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Contains("Roadmap for Q4", r.ResultJson);
        Assert.Contains(stub.Urls, u => u.Contains("/files/doc1/export") && u.Contains("mimeType=text/plain"));
    }

    [Fact]
    public async Task A_file_in_another_drive_is_refused_even_when_the_account_can_see_it()
    {
        var (host, stub) = Build();
        stub.Respond = _ => Json("""{"id":"x1","name":"secret.txt","mimeType":"text/plain","driveId":"0AOtherDrive"}""");
        var r = await host.Queue.SubmitAsync(Req.Call("drive.read", """{"file":"x1"}"""));
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Contains("not in one of the allowed shared drives", r.Error!.Message);
        Assert.DoesNotContain(stub.Urls, u => u.Contains("alt=media"));

        stub.Respond = _ => Json("""{"id":"x2","name":"mine.txt","mimeType":"text/plain"}"""); // My Drive: no driveId
        var mine = await host.Queue.SubmitAsync(Req.Call("drive.read", """{"file":"x2"}"""));
        Assert.Equal(ActionStatus.Failed, mine.Status);
    }

    [Fact]
    public async Task Binary_files_are_downloaded_within_the_size_cap()
    {
        var (host, stub) = Build();
        stub.Respond = url => url.Contains("alt=media") ? Bytes("plain notes") : Json("""{"id":"t1","name":"notes.txt","mimeType":"text/plain","size":11,"driveId":"0AAllowedDrive"}""");
        var ok = await host.Queue.SubmitAsync(Req.Call("drive.read", """{"file":"t1"}"""));
        Assert.Contains("plain notes", ok.ResultJson);

        stub.Respond = _ => Json("""{"id":"big","name":"big.pdf","mimeType":"application/pdf","size":5000,"driveId":"0AAllowedDrive"}""");
        var big = await host.Queue.SubmitAsync(Req.Call("drive.read", """{"file":"big"}"""));
        Assert.Contains("up to 1000 bytes", big.Error!.Message);
    }

    [Theory]
    [InlineData("drive.search", """{"query":"x","drive":"0AOtherDrive"}""", "not one of the allowed shared drives")]
    [InlineData("drive.list", """{"drive":"0AOtherDrive"}""", "not one of the allowed shared drives")]
    [InlineData("drive.read", """{"file":"x' or name contains '"}""", "not a file id")]
    public async Task Other_drives_and_odd_ids_are_refused_before_any_call(string tool, string args, string why)
    {
        var (host, stub) = Build();
        var r = await host.Queue.SubmitAsync(Req.Call(tool, args));
        Assert.Equal(ActionStatus.Failed, r.Status);
        Assert.Contains(why, r.Error!.Message);
        Assert.Empty(stub.Urls);
    }

    [Fact]
    public async Task List_uses_the_drive_as_the_top_folder()
    {
        var (host, stub) = Build();
        var r = await host.Queue.SubmitAsync(Req.Call("drive.list", """{"drive":"0AAllowedDrive"}"""));
        Assert.Equal(ActionStatus.Succeeded, r.Status);
        Assert.Contains("q='0AAllowedDrive' in parents and trashed = false", Assert.Single(stub.Urls));
    }
}
