using System.Text.RegularExpressions;

namespace OpenTail.Orca.Http;

/// <summary>
/// The <c>AiReviewer</c> section of <c>http.json</c> (section 5.1, R2): a model-backed reviewer. Off unless the section is present. The key is
/// not in the file: <see cref="KeyVariable"/> names the environment variable that holds it, and the file has no property that could carry it
/// (unknown properties are refused when the file is read).
/// </summary>
public sealed class ModelReviewerConfig
{
    /// <summary>The reviewer's name: the part after <c>ai:</c>, which the policy's <c>AiReviewers</c> lists name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The provider. Not one is built into Orca; see the startup notice.</summary>
    public string Provider { get; init; } = "";

    /// <summary>Where the model is served: https, or http on a loopback address.</summary>
    public string Endpoint { get; init; } = "";

    public string Model { get; init; } = "";

    /// <summary>The name of the environment variable that holds the key.</summary>
    public string KeyVariable { get; init; } = "";

    /// <summary>How often the reviewer looks at the queue, from one second to an hour.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one model call may take before the reviewer abstains, from one second to five minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The problems with this section, as the startup message gives them. Empty when it is well formed.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!AiReviewRules.IsValidReviewerName(Name))
            errors.Add($"AiReviewer: '{Name}' is not a valid AI reviewer name (letters, digits, '.', '_' and '-', starting with a letter or digit, at most 64 characters).");
        if (string.IsNullOrWhiteSpace(Provider)) errors.Add("AiReviewer needs a Provider.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri.Host))))
            errors.Add("AiReviewer.Endpoint must be https, or http on a loopback address.");
        if (string.IsNullOrWhiteSpace(Model)) errors.Add("AiReviewer needs a Model.");
        if (!Regex.IsMatch(KeyVariable, "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
            errors.Add("AiReviewer.KeyVariable must name an environment variable (letters, digits and '_').");
        if (PollInterval < TimeSpan.FromSeconds(1) || PollInterval > TimeSpan.FromHours(1))
            errors.Add("AiReviewer.PollInterval must be from 1 second to 1 hour.");
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(5))
            errors.Add("AiReviewer.Timeout must be from 1 second to 5 minutes.");
        return errors;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip));
}
