namespace OpenTail.Orca.Http;

public static class HostHttpExtensions
{
    /// <summary>
    /// Makes the Host treat each MCP request's authenticated principal as the caller (JWT subject or API-key name). Required for
    /// <see cref="HostHttp.StartAsync"/>: without it every caller would share one identity and ownership would mean nothing.
    /// </summary>
    public static OpenTailHostBuilder UseHttpSignIn(this OpenTailHostBuilder builder) => builder.UseCallerResolver(HostClaims.CallerOf);
}
