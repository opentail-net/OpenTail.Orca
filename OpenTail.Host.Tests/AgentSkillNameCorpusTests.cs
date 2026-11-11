using OpenTail.AgentSkills;

namespace OpenTail.Host.Tests;

/// <summary>
/// The name rules, with the outcome of each case. The cases follow the reference validator's rules as recorded in the agent-skills-library plan
/// (strip, NFKC, then lowercase, hyphen and letter checks; the directory compared after NFKC; length counted after NFKC). The reference's own corpus
/// was not fetched: these are our cases, written from those rules, and a divergence is recorded in the library's README.
/// </summary>
public sealed class AgentSkillNameCorpusTests
{
    public static TheoryData<string, string, bool> Cases() => new()
    {
        { "pdf-processing", "pdf-processing", true },
        { "pdf2text", "pdf2text", true },
        { "café", "café", true },                         // a Unicode letter is accepted when read
        { "PDF", "PDF", false },                                    // not lowercase
        { "a--b", "a--b", false },                                  // two hyphens in a row
        { "-ab", "-ab", false },                                    // a hyphen at the start
        { "ab-", "ab-", false },                                    // a hyphen at the end
        { "pdf_tool", "pdf_tool", false },                          // an underscore
        { "a b", "a b", false },                                    // a space
        { "pdf", "other", false },                                  // the directory name differs
        { "pdf", "PDF", false },                                    // the directory name differs in case
        { "ｐｄｆ", "pdf", true },                      // fullwidth letters: NFKC gives "pdf", which matches the directory
        { "ｐｄｆ", "ｐｄｆ", true },       // fullwidth letters match a fullwidth directory, as before
        { "ﬁle", "file", true },                               // the "fi" ligature: NFKC gives "file"
        { "Ⅻ", "xii", false },                                 // the roman numeral XII: NFKC gives uppercase letters, not lowercase
        { new string('a', 64), new string('a', 64), true },
        { new string('a', 65), new string('a', 65), false },        // 65 characters
        { string.Concat(Enumerable.Repeat("ﬁ", 33)), string.Concat(Enumerable.Repeat("fi", 33)), false }, // NFKC gives 66 characters
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_name_is_judged_after_nfkc_as_the_reference_validator_judges_it(string name, string directory, bool valid)
    {
        var file = new StandardSkillFile(name, "Extracts text from PDFs.", null, null, new Dictionary<string, string>(), null, "body", []);

        var nameFindings = AgentSkill.Check(file, directory).Where(d => d.Code.StartsWith("SKILL-NAME", StringComparison.Ordinal)).Select(d => d.Code).ToList();

        Assert.Equal(valid, nameFindings.Count == 0);
    }
}
