using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenTail.Orca;

/// <summary>
/// The deterministic pre-scan of section 5.1: text aimed at the reviewer. Every string in the packet is scanned, whatever field it is in. A hit
/// goes into the packet, and it makes the model reviewer escalate. It never decides an action by itself.
/// </summary>
public static class InjectionScan
{
    public const string ImperativeApproval = "imperative approval language";
    public const string ClaimedApproval = "a claim of an earlier approval";
    public const string RoleMarker = "a role or instruction marker";
    public const string InvisibleText = "invisible or bidirectional characters";
    public const string EncodedBlob = "an encoded blob";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex ImperativeRe = new(@"\b(approve|authori[sz]e|greenlight)\b", Options);
    private static readonly Regex ClaimedRe = new(
        @"\b(already|previously|earlier|pre-?approved|has been|have been|was|were)\b[^.\n]{0,40}\b(approved|authori[sz]ed|signed off|cleared)\b|\bapproved by\b", Options);
    private static readonly Regex RoleRe = new(
        @"\bsystem\s*:|\bassistant\s*:|\buser\s*:|</?\s*(system|reviewer|policy|instructions?)\b|\[(system|reviewer|admin)\]|\bai:[a-z0-9_.\-]+|" +
        @"\bignore (all )?(prior|previous|above) (rules|instructions)\b|\bsystem override\b|\byou are (now )?an? ", Options);
    private static readonly Regex BlobRe = new(@"[A-Za-z0-9+/]{40,}={0,2}|\b[0-9a-fA-F]{64,}\b", RegexOptions.CultureInvariant);

    /// <summary>The kinds of reviewer-directed text found in one string, in a fixed order.</summary>
    public static IReadOnlyList<string> ScanText(string text)
    {
        var found = new List<string>();
        if (ImperativeRe.IsMatch(text)) found.Add(ImperativeApproval);
        if (ClaimedRe.IsMatch(text)) found.Add(ClaimedApproval);
        if (RoleRe.IsMatch(text)) found.Add(RoleMarker);
        // Format and control characters, bidirectional controls included. Tab, carriage return and line feed are ordinary text.
        if (text.Any(c => c is not ('\t' or '\r' or '\n') && char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Control))
            found.Add(InvisibleText);
        if (BlobRe.IsMatch(text)) found.Add(EncodedBlob);
        return found;
    }

    /// <summary>The kinds of reviewer-directed text found in any string of the packet, in a fixed order, each once.</summary>
    public static IReadOnlyList<string> Scan(JsonNode? packet)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in Strings(packet))
            foreach (var kind in ScanText(text))
                found.Add(kind);
        return [.. found.OrderBy(k => Array.IndexOf(Order, k))];
    }

    private static readonly string[] Order = [ImperativeApproval, ClaimedApproval, RoleMarker, InvisibleText, EncodedBlob];

    /// <summary>Every string value in the tree, at any depth. Keys are not scanned: the Host writes them.</summary>
    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => Strings(p.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => [],
    };
}
