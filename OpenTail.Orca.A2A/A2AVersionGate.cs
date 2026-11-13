using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace OpenTail.Orca.A2A;

/// <summary>
/// The <c>A2A-Version</c> rule of this Host (decision D6, the owner's delegated decision). The SDK's own check accepts an empty header and
/// <c>0.3</c>, so this gate runs first, as an endpoint filter after sign-in and authorisation: an unauthenticated caller gets 401, never a
/// version error. A missing header is 1.0, as the SDK treats it. A header sent more than once, or a comma-separated list, is accepted only
/// when every value is <c>1.0</c>.
/// </summary>
internal static class A2AVersionGate
{
    public const string Header = "A2A-Version";

    /// <summary>The one version this Host serves.</summary>
    public const string Served = "1.0";

    public static bool Accepts(StringValues values)
    {
        if (values.Count == 0) return true;
        foreach (var value in values)
            foreach (var part in (value ?? "").Split(','))
                if (part.Trim() != Served) return false;
        return true;
    }

    /// <summary>The SDK's own shape for an unsupported version: a JSON-RPC error VersionNotSupported (-32009) with HTTP 200 and a null id.</summary>
    public static IResult Refusal() => new JsonRpcResponseResult(JsonRpcResponse.CreateJsonRpcErrorResponse(default, new A2AException(
        $"Protocol version is not supported. Supported version: {Served}", A2AErrorCode.VersionNotSupported)));
}
