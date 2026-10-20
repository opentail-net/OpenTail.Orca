using System.Text.RegularExpressions;

namespace OpenTail.Host;

/// <summary>
/// Scrubs text that flows back to the model, the audit log or telemetry. Tool exception messages are the main
/// risk: they often echo a connection string, token or argument. Best-effort; modules should not put secrets in errors.
/// </summary>
public static partial class Redaction
{
    public const int MaxMessageLength = 500;

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]{8,}")]
    private static partial Regex AuthScheme();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_\-]?key|apikey|access[_\-]?key|connectionstring)\s*[=:]\s*[^\s;,""']+")]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"(?i)\b(sk|pk|ghp|gho|xox[abp]|AKIA)[A-Za-z0-9_\-]{16,}")]
    private static partial Regex KnownTokenShapes();

    [GeneratedRegex(@"\b[A-Za-z0-9+/_\-]{40,}={0,2}")]
    private static partial Regex LongOpaque();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Known = new(StringComparer.Ordinal);

    /// <summary>A secret the Host handed out (a module credential). Every later <see cref="Scrub"/> replaces it exactly, whatever it looks
    /// like, so it cannot leak through an error message that the patterns below would miss. Values shorter than 8 characters are ignored (too likely to match ordinary text).</summary>
    public static void AddSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 8) Known.TryAdd(secret, 0);
    }

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var s in Known.Keys.OrderByDescending(k => k.Length))
            if (text.Contains(s, StringComparison.Ordinal)) text = text.Replace(s, "[redacted]", StringComparison.Ordinal);
        var t = AuthScheme().Replace(text, "$1 [redacted]");
        t = KeyValueSecret().Replace(t, m => m.Value[..(m.Value.IndexOfAny(['=', ':']) + 1)] + "[redacted]");
        t = KnownTokenShapes().Replace(t, "[redacted]");
        t = LongOpaque().Replace(t, "[redacted]");
        return t.Length > MaxMessageLength ? t[..MaxMessageLength] + "…" : t;
    }
}
