using System.Text;

namespace OpenTail.Modules.Social;

/// <summary>
/// (Copied from OpenTail.Connectivity.Social so this package does not ship the old batch connectors.)
/// Lightweight lexicon-based sentiment scorer using an AFINN-derived word list.
/// Returns scores in [-1, +1]. Zero external dependencies.
/// </summary>
internal static class SentimentScorer
{
    private static readonly Dictionary<string, int> _lexicon = BuildLexicon();

    /// <summary>Scores text in [-1, +1]. Returns 0.0 for empty or unrecognized input.</summary>
    public static double Score(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0.0;

        var tokens = Tokenize(text);
        if (tokens.Count == 0) return 0.0;

        int sum = 0;
        int hits = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (_lexicon.TryGetValue(tokens[i], out var val))
            {
                // Negation: "not good", "never great" etc. — check preceding token
                if (i > 0 && _negators.Contains(tokens[i - 1]))
                    val = -val;
                sum += val;
                hits++;
            }
        }

        if (hits == 0) return 0.0;
        // Normalize by sqrt of token count so longer texts don't dominate
        return Math.Max(-1.0, Math.Min(1.0, sum / (Math.Sqrt(tokens.Count) * 5.0)));
    }

    public static string Label(double score) =>
        score > 0.1 ? "positive" : score < -0.1 ? "negative" : "neutral";

    private static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var span = text.AsSpan();
        var sb = new StringBuilder();
        foreach (var ch in span)
        {
            if (char.IsLetter(ch) || ch == '\'')
                sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static readonly HashSet<string> _negators = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "neither", "nor", "without", "cannot", "can't",
        "won't", "don't", "doesn't", "isn't", "aren't", "wasn't", "weren't",
        "wouldn't", "couldn't", "shouldn't", "hardly", "barely", "rarely"
    };

    private static Dictionary<string, int> BuildLexicon() => new(StringComparer.Ordinal)
    {
        // Strong positive (4-5)
        ["love"] = 5, ["excellent"] = 5, ["outstanding"] = 5, ["superb"] = 5, ["amazing"] = 4,
        ["awesome"] = 4, ["brilliant"] = 4, ["fantastic"] = 4, ["wonderful"] = 4, ["perfect"] = 4,
        ["extraordinary"] = 4, ["spectacular"] = 4, ["phenomenal"] = 4, ["exceptional"] = 4,
        ["thrilled"] = 4, ["ecstatic"] = 4, ["delighted"] = 4, ["overjoyed"] = 4,
        // Positive (2-3)
        ["good"] = 3, ["great"] = 3, ["happy"] = 3, ["like"] = 2, ["best"] = 3,
        ["fun"] = 3, ["enjoy"] = 3, ["enjoyed"] = 3, ["enjoying"] = 3, ["nice"] = 3,
        ["positive"] = 2, ["better"] = 2, ["pleased"] = 3, ["glad"] = 3, ["fortunate"] = 2,
        ["celebrate"] = 3, ["celebrates"] = 3, ["celebrating"] = 3, ["celebration"] = 3,
        ["win"] = 3, ["wins"] = 3, ["won"] = 3, ["winning"] = 3, ["winner"] = 3,
        ["succeed"] = 3, ["success"] = 3, ["successful"] = 3, ["achieve"] = 3,
        ["achievement"] = 3, ["accomplished"] = 3, ["proud"] = 3, ["pride"] = 3,
        ["effective"] = 2, ["efficient"] = 2, ["impressive"] = 3, ["inspiring"] = 3,
        ["innovative"] = 2, ["creative"] = 2, ["helpful"] = 2, ["useful"] = 2,
        ["recommend"] = 2, ["recommended"] = 2, ["loyal"] = 2, ["reliable"] = 2,
        ["trust"] = 2, ["trusted"] = 2, ["trustworthy"] = 3, ["honest"] = 2,
        ["excited"] = 3, ["exciting"] = 3, ["enthusiasm"] = 3, ["enthusiastic"] = 3,
        ["motivated"] = 3, ["engaging"] = 2, ["engaged"] = 2, ["value"] = 2,
        ["valuable"] = 2, ["benefit"] = 2, ["benefits"] = 2, ["improve"] = 2,
        ["improved"] = 2, ["improvement"] = 2, ["growth"] = 2, ["growing"] = 2,
        ["support"] = 2, ["supportive"] = 2, ["safe"] = 2, ["strong"] = 2,
        ["strength"] = 2, ["opportunity"] = 2, ["comfortable"] = 2, ["satisfied"] = 3,
        ["satisfaction"] = 3, ["reward"] = 2, ["rewarding"] = 3, ["gain"] = 2,
        ["gained"] = 2, ["powerful"] = 2, ["bold"] = 2, ["forward"] = 2,
        // Mild positive (1)
        ["ok"] = 1, ["okay"] = 1, ["fine"] = 1, ["decent"] = 1, ["fair"] = 1,
        ["interesting"] = 1, ["agree"] = 1, ["smart"] = 1,
        // Mild negative (-1)
        ["wrong"] = -1, ["bad"] = -1, ["slow"] = -1, ["boring"] = -1, ["confusing"] = -1,
        ["concern"] = -1, ["concerned"] = -1, ["problem"] = -1, ["issue"] = -1,
        ["difficult"] = -1, ["hard"] = -1, ["complicated"] = -1, ["unclear"] = -1,
        ["inconsistent"] = -1, ["limited"] = -1, ["missing"] = -1, ["lack"] = -1,
        ["lacking"] = -1, ["weak"] = -1, ["minor"] = -1, ["annoying"] = -2,
        // Negative (-2 to -3)
        ["terrible"] = -3, ["awful"] = -3, ["horrible"] = -3, ["hate"] = -3,
        ["hates"] = -3, ["hated"] = -3, ["worst"] = -3,
        ["fail"] = -2, ["failed"] = -2, ["failure"] = -2, ["failing"] = -2,
        ["disappoint"] = -2, ["disappointed"] = -2, ["disappointing"] = -2,
        ["disappointment"] = -2, ["regret"] = -2, ["regrets"] = -2, ["regretted"] = -2,
        ["poor"] = -2, ["lousy"] = -2, ["useless"] = -2, ["worthless"] = -2,
        ["dislike"] = -2, ["dislikes"] = -2, ["frustrated"] = -2, ["frustrating"] = -2,
        ["frustration"] = -2, ["angry"] = -2, ["anger"] = -2, ["rage"] = -3,
        ["upset"] = -2, ["trouble"] = -2, ["complaint"] = -2, ["complain"] = -2,
        ["broken"] = -2, ["error"] = -2, ["errors"] = -2, ["bug"] = -1, ["bugs"] = -1,
        ["damage"] = -2, ["damaged"] = -2, ["hurt"] = -2, ["hurts"] = -2,
        ["suffer"] = -2, ["suffering"] = -2, ["serious"] = -1, ["severe"] = -2,
        ["crisis"] = -3, ["scandal"] = -3, ["fraud"] = -3, ["scam"] = -3,
        ["mislead"] = -3, ["misleading"] = -3, ["deceive"] = -3, ["deceptive"] = -3,
        ["manipulate"] = -2, ["manipulative"] = -2, ["abuse"] = -3, ["abusive"] = -3,
        // Strong negative (-4 to -5)
        ["disgusting"] = -4, ["horrific"] = -4, ["atrocious"] = -4, ["appalling"] = -4,
        ["outrageous"] = -4, ["catastrophic"] = -4, ["disaster"] = -4, ["disastrous"] = -4,
        ["devastating"] = -4, ["corrupt"] = -4, ["corruption"] = -4,
        ["criminal"] = -4, ["dangerous"] = -3, ["toxic"] = -3,
        // Marketing/brand specific
        ["viral"] = 3, ["trending"] = 2, ["popular"] = 2, ["launch"] = 2, ["launched"] = 2,
        ["announce"] = 2, ["announced"] = 2, ["partnership"] = 2, ["collaborate"] = 2,
        ["campaign"] = 1, ["engagement"] = 2, ["community"] = 2, ["brand"] = 1,
        ["product"] = 1, ["feature"] = 1, ["update"] = 1, ["release"] = 1,
        ["affordable"] = 2, ["premium"] = 2, ["exclusive"] = 2, ["discount"] = 2,
        ["sale"] = 1, ["offer"] = 1, ["deal"] = 2, ["free"] = 2, ["bonus"] = 2,
        ["overpriced"] = -2, ["expensive"] = -1, ["costly"] = -1, ["cheap"] = -1,
        ["unreliable"] = -2, ["unresponsive"] = -2, ["unhelpful"] = -2,
        ["delayed"] = -2, ["cancelled"] = -2, ["cancel"] = -2,
        ["refund"] = -1, ["return"] = -1, ["replacement"] = -1,
    };
}
