using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace OpenTail.Orca;

/// <summary>
/// HTTP clients for one module (see <see cref="IModuleHttpClientFactory"/>): allowed hosts only, https unless an http origin is listed,
/// public addresses unless the operator allowed private ones (checked on the address actually connected to, so DNS rebinding does not help),
/// redirects followed only for GET/HEAD and re-checked hop by hop, credentials attached only for their bound host, 401 drops a cached token.
/// A guard rail, not containment: in-process code can always open its own sockets.
/// </summary>
public sealed class GuardedHttpClientFactory : IModuleHttpClientFactory, IDisposable
{
    private readonly string _moduleId;
    private readonly IReadOnlyList<string> _hosts;
    private readonly ModuleNetwork _network;
    private readonly ModuleCredentials _credentials;
    private readonly SocketsHttpHandler _sockets;

    public GuardedHttpClientFactory(string moduleId, IReadOnlyList<string> allowedHosts, ModuleNetwork network, ModuleCredentials credentials)
    {
        _moduleId = moduleId;
        _hosts = allowedHosts;
        _network = network;
        _credentials = credentials;
        _sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
    }

    public IReadOnlyList<string> AllowedHosts => _hosts;

    /// <summary>For tests: route through another handler (a stub server in memory). Host checks still apply.</summary>
    internal HttpMessageHandler? InnerOverride { get; set; }

    /// <summary>For tests: the name lookup, instead of DNS.</summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; set; } = (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    /// <summary>How long one address may take to connect while others are left to try: an address that drops packets would
    /// otherwise use up the whole request timeout.</summary>
    internal TimeSpan ConnectAttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public HttpClient Create(string? credentialName = null)
    {
        if (credentialName is not null && !_credentials.Names.Contains(credentialName))
            throw new InvalidOperationException($"Module '{_moduleId}' asked for credential '{credentialName}', which is not mapped to it in the modules file.");
        var guard = new GuardHandler(this, credentialName) { InnerHandler = InnerOverride ?? _sockets };
        return new HttpClient(guard, disposeHandler: false)
        {
            Timeout = _network.Timeout,
            MaxResponseContentBufferSize = _network.MaxResponseBytes,
        };
    }

    public void Dispose() => _sockets.Dispose();

    internal bool Allowed(Uri uri) => uri.IsAbsoluteUri && string.IsNullOrEmpty(uri.UserInfo) && _hosts.Any(h => NetworkRules.Matches(h, uri));

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        IPAddress[] addresses;
        // An IP literal reached this point only by matching an origin the operator listed explicitly: that is consent enough.
        var literal = IPAddress.TryParse(host.Trim('[', ']'), out var lit);
        addresses = literal ? [lit!] : await Resolve(host, ct);
        if (!literal && !_network.AllowPrivateNetwork && addresses.Any(a => !NetworkRules.IsPublic(a)))
            throw new HttpRequestException($"Module '{_moduleId}': {host} resolves to a private or local address (set Network.AllowPrivateNetwork for an on-premises server).");
        if (addresses.Length == 0) throw new HttpRequestException($"Module '{_moduleId}': {host} did not resolve.");

        // Every address passed the check above, so each is tried in turn: the first one can be unreachable (an IPv6 address on a
        // network without IPv6) while another works.
        var failures = new List<Exception>();
        for (var i = 0; i < addresses.Length; i++)
        {
            var address = addresses[i];
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (i < addresses.Length - 1) attempt.CancelAfter(ConnectAttemptTimeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                failures.Add(ex);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException($"Module '{_moduleId}': could not connect to {host} at any of its {addresses.Length} addresses.",
            new AggregateException(failures));
    }

    private sealed class GuardHandler(GuardedHttpClientFactory owner, string? credentialName) : DelegatingHandler
    {
        private const int MaxRedirects = 3;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var current = request;
            for (var hop = 0; ; hop++)
            {
                var uri = current.RequestUri ?? throw new HttpRequestException("A request needs an absolute URI.");
                if (!owner.Allowed(uri))
                    throw new HttpRequestException($"Module '{owner._moduleId}' may not reach {uri.Scheme}://{uri.Authority} (allowed: {string.Join(", ", owner._hosts)}).");

                var attached = await AttachAsync(current, uri, ct);
                var response = await base.SendAsync(current, ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized && attached) owner._credentials.Invalidate(credentialName!);

                var code = (int)response.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } loc
                    && (current.Method == HttpMethod.Get || current.Method == HttpMethod.Head))
                {
                    if (hop >= MaxRedirects) { response.Dispose(); throw new HttpRequestException($"More than {MaxRedirects} redirects."); }
                    var next = new HttpRequestMessage(current.Method, new Uri(uri, loc));
                    foreach (var h in current.Headers)
                        if (!h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !IsCredentialHeader(h.Key))
                            next.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    response.Dispose();
                    if (!ReferenceEquals(current, request)) current.Dispose();
                    current = next;
                    continue;
                }
                return response;
            }
        }

        private bool IsCredentialHeader(string header) =>
            credentialName is not null && owner._credentials.HeaderNameOf(credentialName) is { } hn
            && header.Equals(hn, StringComparison.OrdinalIgnoreCase);

        /// <summary>Attaches the credential when the request is for its bound host. Returns whether it did.</summary>
        private async Task<bool> AttachAsync(HttpRequestMessage req, Uri uri, CancellationToken ct)
        {
            if (credentialName is null) return false;
            var bound = owner._credentials.BoundHost(credentialName);
            if (bound is null || !NetworkRules.Matches(bound, uri)) return false;
            var c = await owner._credentials.GetAsync(credentialName, ct);
            switch (c.Kind)
            {
                case CredentialKind.Bearer:
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Secret);
                    return true;
                case CredentialKind.Basic:
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes((c.UserName ?? "") + ":" + c.Secret)));
                    return true;
                case CredentialKind.Header when c.HeaderName is { } hn:
                    req.Headers.Remove(hn);
                    req.Headers.TryAddWithoutValidation(hn, c.Secret);
                    return true;
                default:
                    return false;
            }
        }
    }
}
