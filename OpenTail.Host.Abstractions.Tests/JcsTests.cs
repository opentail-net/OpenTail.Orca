using System.IO;

namespace OpenTail.Host.Abstractions.Tests;

/// <summary>
/// RFC 8785 (JSON Canonicalization Scheme). The reference vectors in <c>Rfc8785/</c> are the conformance evidence: until they pass, the
/// digests built on <see cref="Jcs"/> are not called RFC 8785 compliant (governance A.2). The other tests pin the cases the vectors cover
/// least clearly: the number format at the edges, escapes, member order, and the refusals.
/// </summary>
public class JcsTests
{
    private static string VectorDir(string name) => Path.Combine(AppContext.BaseDirectory, "Rfc8785", name);

    public static IEnumerable<object[]> VectorNames() =>
        Directory.GetFiles(VectorDir("input")).OrderBy(f => f, StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Reference_vector_canonicalizes_to_its_expected_output(string file)
    {
        var input = File.ReadAllText(Path.Combine(VectorDir("input"), file));
        var expected = File.ReadAllText(Path.Combine(VectorDir("output"), file));

        Assert.Equal(expected, Jcs.Canonicalize(input));
    }

    [Theory]
    [InlineData("1e21", "1e+21")]
    [InlineData("1e20", "100000000000000000000")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("0.0000001", "1e-7")]
    [InlineData("-0", "0")]
    [InlineData("0", "0")]
    [InlineData("5e-324", "5e-324")]
    [InlineData("1.7976931348623157e308", "1.7976931348623157e+308")]
    [InlineData("123.456", "123.456")]
    [InlineData("-1.5", "-1.5")]
    [InlineData("1E30", "1e+30")]
    [InlineData("4.50", "4.5")]
    [InlineData("2e-3", "0.002")]
    [InlineData("333333333.33333329", "333333333.3333333")]
    [InlineData("100", "100")]
    [InlineData("1.0", "1")]
    public void Numbers_are_written_as_ecmascript_writes_them(string json, string expected) =>
        Assert.Equal(expected, Jcs.Canonicalize(json));

    [Fact]
    public void Control_characters_use_the_short_escapes_and_the_rest_are_unicode_escapes()
    {
        // Input text: "\b\t\n\f\r\"\\\/" — the solidus is not escaped on output.
        var input = "\"\\b\\t\\n\\f\\r\\\"\\\\\\/\"";
        Assert.Equal("\"\\b\\t\\n\\f\\r\\\"\\\\/\"", Jcs.Canonicalize(input));
    }

    [Fact]
    public void Other_control_characters_are_lowercase_unicode_escapes_and_DEL_is_written_as_it_is()
    {
        var input = "\"\\u0000\\u001f\\u007f\"";
        Assert.Equal("\"\\u0000\\u001f\u007F\"", Jcs.Canonicalize(input));
    }

    [Fact]
    public void Members_are_sorted_by_utf16_code_units()
    {
        // Compared by UTF-16 code units: "a" (0061), "b" (0062), U+20AC (20AC), U+1F600 as the pair D83D DE00, then U+FB33 (FB33).
        var input = "{\"b\":1,\"a\":2,\"\\u20ac\":3,\"\\ufb33\":4,\"\\ud83d\\ude00\":5}";
        Assert.Equal("{\"a\":2,\"b\":1,\"\u20AC\":3,\"\uD83D\uDE00\":5,\"\uFB33\":4}", Jcs.Canonicalize(input));
    }

    [Fact]
    public void Whitespace_and_nesting_are_normalised()
    {
        var input = "{ \"x\" : [ 1 , { \"z\": null, \"y\": true } ] }";
        Assert.Equal("{\"x\":[1,{\"y\":true,\"z\":null}]}", Jcs.Canonicalize(input));
    }

    [Fact]
    public void Duplicate_member_names_are_refused()
    {
        Assert.Throws<FormatException>(() => Jcs.Canonicalize("{\"a\":1,\"a\":2}"));
    }

    [Fact]
    public void Unpaired_surrogates_are_refused()
    {
        Assert.Throws<FormatException>(() => Jcs.Canonicalize("\"\\ud800\""));
    }

    [Fact]
    public void Text_that_is_not_json_is_refused_with_a_json_exception()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Jcs.Canonicalize("{not json"));
    }
}
