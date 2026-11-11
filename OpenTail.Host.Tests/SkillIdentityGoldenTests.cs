using OpenTail.Modules.Skills;

namespace OpenTail.Host.Tests;

/// <summary>Golden v1 identities: the values the current code produces for fixed inputs. Every recorded report binding and policy hash is one
/// of these values, so a change here changes what existing evidence means. Such a change must be a new, versioned identity, never an edit.</summary>
public class SkillIdentityGoldenTests
{
    private static SkillVersion Sample() => new("invoice-check", "1",
        "Check whether an unpaid invoice is genuine before anyone pays it.",
        ["an invoice or payment demand arrives by email", "a supplier asks to change their bank details"],
        ["mail.read", "servicenow.create_record"],
        [new SkillPolicy("policies/payment-verification.md", "sha256:" + new string('a', 64))],
        "finance-ops",
        "Never pay from an email.\n\nCheck the bank details against the vendor record.\n",
        "{ \"format\": 1 }");

    [Fact]
    public void V1_content_hash_of_a_fixed_version_is_unchanged()
        => Assert.Equal("sha256:a23631cbd88e37780aacd1b4e56239f06e3ca7eb7b7a2d9f9ec5450d50ab7cb0", SkillText.ContentHash(Sample()));

    [Fact]
    public void V1_binding_line_of_a_fixed_version_is_unchanged()
        => Assert.Equal("skill-report: invoice-check@1 sha256:a23631cbd88e37780aacd1b4e56239f06e3ca7eb7b7a2d9f9ec5450d50ab7cb0", SkillText.BindingLine(Sample()));

    [Fact]
    public void V1_text_hash_of_a_fixed_text_is_unchanged()
        => Assert.Equal("sha256:594887a940c42960c95e89b046cb083569b65a77744063add3576e10b6650a25", SkillText.Hash("Check the bank details.\n\n    Indented line stays.\n"));

    [Fact]
    public void V1_text_hash_ignores_indentation_as_it_does_today()
        // Documents the v1 behaviour that the v2 plan changes: indentation and inner spacing are not part of a v1 identity.
        => Assert.Equal(SkillText.Hash("if (x == 1)\n    return true;"), SkillText.Hash("if (x == 1)\nreturn true;"));
}
