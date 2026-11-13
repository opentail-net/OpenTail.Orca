using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OpenTail.Orca.Tests;

/// <summary>A real HTTP server on 127.0.0.1 with one handler, for testing modules end to end through the Host's guarded clients.</summary>
public sealed class StubServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubServer(WebApplication app, Uri baseUri) { _app = app; BaseUri = baseUri; }

    public Uri BaseUri { get; }

    /// <summary><c>http://127.0.0.1:port</c>, the form a Network.Hosts origin takes.</summary>
    public string Origin => BaseUri.GetLeftPart(UriPartial.Authority);

    public List<(string Method, string PathAndQuery, string? Authorization, string Body)> Requests { get; } = [];

    public static async Task<StubServer> StartAsync(Func<HttpContext, Task> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        StubServer? self = null;
        app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            lock (self!.Requests)
                self.Requests.Add((ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null, body));
            ctx.Items["body"] = body;
            await handler(ctx);
        });
        await app.StartAsync();
        var address = app.Urls.First();
        self = new StubServer(app, new Uri(address.EndsWith('/') ? address : address + "/"));
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
