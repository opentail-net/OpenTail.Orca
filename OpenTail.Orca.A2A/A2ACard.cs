using A2A;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.A2A;

/// <summary>
/// The agent card the Host serves at <c>/.well-known/agent-card.json</c> (batch 04, A1 and A2). It names one interface (JSON-RPC 1.0 at
/// <c>PublicBaseUrl</c> + <c>/a2a</c>), the capabilities this slice has (none beyond JSON-RPC), no skills, and the two credentials an API key
/// is sent in (the bearer token, and the <c>X-API-Key</c> header, as <c>StaticTokenAuthentication</c> reads them). Nothing about tools,
/// profiles, partners, policy or reviewer groups goes into the card.
/// </summary>
internal static class A2ACard
{
    /// <summary>The card for this configuration. <paramref name="publicBaseUrl"/> is the address clients reach the Host at (checked at startup).</summary>
    public static AgentCard Build(A2aCardOptions card, string publicBaseUrl)
    {
        var modes = new List<string> { "text/plain", "application/json" };
        return new AgentCard
        {
            Name = card.Name,
            Description = card.Description,
            Version = card.Version,
            SupportedInterfaces =
            [
                new AgentInterface
                {
                    ProtocolBinding = "JSONRPC",
                    ProtocolVersion = "1.0",
                    Url = publicBaseUrl.TrimEnd('/') + A2AFrontDoor.Path,
                },
            ],
            Capabilities = new AgentCapabilities { Streaming = false, PushNotifications = false, ExtendedAgentCard = false },
            DefaultInputModes = modes,
            DefaultOutputModes = modes,
            Skills = [],
            SecuritySchemes = new Dictionary<string, SecurityScheme>
            {
                ["apiKey"] = new SecurityScheme
                {
                    ApiKeySecurityScheme = new ApiKeySecurityScheme
                    {
                        Location = "header",
                        Name = "X-API-Key",
                        Description = "An API key issued by the operator.",
                    },
                },
                ["bearer"] = new SecurityScheme
                {
                    HttpAuthSecurityScheme = new HttpAuthSecurityScheme
                    {
                        Scheme = "bearer",
                        Description = "The same API key, sent as a bearer token.",
                    },
                },
            },
            SecurityRequirements =
            [
                new SecurityRequirement { Schemes = new Dictionary<string, StringList> { ["apiKey"] = new StringList() } },
                new SecurityRequirement { Schemes = new Dictionary<string, StringList> { ["bearer"] = new StringList() } },
            ],
        };
    }
}
