using System.Text.Json;

namespace OpenTail.Host.Tests;

/// <summary>The condition language on its own: three-valued results, hostile inputs, validation.</summary>
public class ConditionTests
{
    private static Tri Eval(string condition, string args, DateTimeOffset? now = null)
    {
        var c = ConditionCompiler.Compile(JsonDocument.Parse(condition).RootElement.Clone(), "When");
        using var doc = JsonDocument.Parse(args);
        return c.Eval(new EvalContext(doc.RootElement.Clone(), now ?? DateTimeOffset.Parse("2026-10-14T10:00:00Z"), null));
    }

    private static RuleException Invalid(string condition) =>
        Assert.Throws<RuleException>(() => ConditionCompiler.Compile(JsonDocument.Parse(condition).RootElement.Clone(), "When"));

    // ---- equality and membership ----

    [Theory]
    [InlineData("""{"arg":"/x","equals":"a"}""", """{"x":"a"}""", Tri.True)]
    [InlineData("""{"arg":"/x","equals":"a"}""", """{"x":"b"}""", Tri.False)]
    [InlineData("""{"arg":"/x","equals":"a"}""", """{"x":"A"}""", Tri.False)]                 // case-sensitive by default
    [InlineData("""{"arg":"/x","equals":"a","ignoreCase":true}""", """{"x":"A"}""", Tri.True)]
    [InlineData("""{"arg":"/x","equals":"a"}""", """{"x":1}""", Tri.Unknown)]                  // a different kind is unknown, not different
    [InlineData("""{"arg":"/x","equals":"a"}""", """{}""", Tri.Unknown)]                       // missing is unknown
    [InlineData("""{"arg":"/x","equals":10}""", """{"x":10.0}""", Tri.True)]
    [InlineData("""{"arg":"/x","equals":true}""", """{"x":true}""", Tri.True)]
    [InlineData("""{"arg":"/x","equals":true}""", """{"x":"true"}""", Tri.Unknown)]
    [InlineData("""{"arg":"/x","equals":null}""", """{"x":null}""", Tri.True)]
    [InlineData("""{"arg":"/x","in":["a","b"]}""", """{"x":"b"}""", Tri.True)]
    [InlineData("""{"arg":"/x","in":["a","b"]}""", """{"x":"c"}""", Tri.False)]
    [InlineData("""{"arg":"/x","in":["a","b"]}""", """{"x":7}""", Tri.Unknown)]
    public void Equality_and_membership(string condition, string args, Tri expected) => Assert.Equal(expected, Eval(condition, args));

    // ---- strings and regex ----

    [Theory]
    [InlineData("""{"arg":"/x","startsWith":"/safe/"}""", """{"x":"/safe/a.txt"}""", Tri.True)]
    [InlineData("""{"arg":"/x","startsWith":"/safe/"}""", """{"x":"/other/a.txt"}""", Tri.False)]
    [InlineData("""{"arg":"/x","endsWith":".csv"}""", """{"x":"a.CSV"}""", Tri.False)]
    [InlineData("""{"arg":"/x","endsWith":".csv","ignoreCase":true}""", """{"x":"a.CSV"}""", Tri.True)]
    [InlineData("""{"arg":"/x","contains":"@"}""", """{"x":"a@b"}""", Tri.True)]
    [InlineData("""{"arg":"/x","contains":"@"}""", """{"x":["a@b"]}""", Tri.Unknown)]
    [InlineData("""{"arg":"/x","matches":"^[a-z]+$"}""", """{"x":"abc"}""", Tri.True)]
    [InlineData("""{"arg":"/x","matches":"^[a-z]+$"}""", """{"x":"abc1"}""", Tri.False)]
    [InlineData("""{"arg":"/x","matches":"invoice|payment","ignoreCase":true}""", """{"x":"Please pay the INVOICE"}""", Tri.True)]
    [InlineData("""{"arg":"/x","matches":"^a"}""", """{"x":5}""", Tri.Unknown)]
    public void Strings_and_regex(string condition, string args, Tri expected) => Assert.Equal(expected, Eval(condition, args));

    [Fact]
    public void A_catastrophic_regex_cannot_hang_the_host()
    {
        // (a+)+$ is the classic exponential pattern for a backtracking engine; this engine is linear.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = Eval("""{"arg":"/x","matches":"^(a+)+$"}""", $$"""{"x":"{{new string('a', 50_000)}}b"}""");
        watch.Stop();
        Assert.True(r is Tri.False or Tri.Unknown);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }

    // ---- domains: exact, strict, no tricks ----

    [Theory]
    [InlineData("a@corp.example", Tri.True)]
    [InlineData("A@CORP.EXAMPLE", Tri.True)]                              // case-insensitive domain
    [InlineData("a@corp.example.evil.com", Tri.False)]                    // not a suffix match
    [InlineData("a@sub.corp.example", Tri.False)]                         // not a subdomain match
    [InlineData("a@evil.com", Tri.False)]
    [InlineData("a@corp.example@evil.com", Tri.Unknown)]                  // two @: refuse to guess
    [InlineData("Bob <a@corp.example>", Tri.Unknown)]                     // display names: refuse to guess
    [InlineData("a@corp.example, b@evil.com", Tri.Unknown)]               // lists: refuse to guess
    [InlineData("\"a@evil.com\"@corp.example", Tri.Unknown)]              // quoted local parts: refuse to guess
    [InlineData("a@corp.example.", Tri.Unknown)]                          // trailing dot
    [InlineData("no-at-sign", Tri.Unknown)]
    [InlineData("a@b c.example", Tri.Unknown)]
    [InlineData("", Tri.Unknown)]
    public void Domain_matching_is_exact_and_refuses_what_it_cannot_parse(string address, Tri expected)
    {
        var args = JsonSerializer.Serialize(new { to = address });
        Assert.Equal(expected, Eval("""{"arg":"/to","domainIn":["corp.example"]}""", args));
    }

    [Fact]
    public void Internationalised_domains_compare_in_punycode()
    {
        Assert.Equal(Tri.True, Eval("""{"arg":"/to","domainIn":["münchen.example"]}""", """{"to":"a@xn--mnchen-3ya.example"}"""));
        Assert.Equal(Tri.True, Eval("""{"arg":"/to","domainIn":["xn--mnchen-3ya.example"]}""", """{"to":"a@München.example"}"""));
    }

    [Fact]
    public void Quantifiers_cover_every_recipient_or_any_of_them()
    {
        const string all = """{"arg":"/to","all":{"domainIn":["corp.example"]}}""";
        const string any = """{"arg":"/to","any":{"domainIn":["rival.example"]}}""";
        Assert.Equal(Tri.True, Eval(all, """{"to":["a@corp.example","b@corp.example"]}"""));
        Assert.Equal(Tri.False, Eval(all, """{"to":["a@corp.example","b@evil.com"]}"""));      // one outsider is enough
        Assert.Equal(Tri.True, Eval(all, """{"to":"a@corp.example"}"""));                      // a single string counts as one
        Assert.Equal(Tri.Unknown, Eval(all, """{"to":["a@corp.example","garbled"]}"""));       // can't read one: not true
        Assert.Equal(Tri.True, Eval(any, """{"to":["a@corp.example","b@rival.example"]}"""));
        Assert.Equal(Tri.False, Eval(any, """{"to":["a@corp.example"]}"""));
        Assert.Equal(Tri.Unknown, Eval(all, """{"to":[]}"""));                                 // all over nothing says nothing
        Assert.Equal(Tri.Unknown, Eval(any, """{"to":[]}"""));
    }

    // ---- numbers, presence, counts ----

    [Theory]
    [InlineData("""{"arg":"/n","lt":100}""", """{"n":99.99}""", Tri.True)]
    [InlineData("""{"arg":"/n","lt":100}""", """{"n":100}""", Tri.False)]
    [InlineData("""{"arg":"/n","lte":100}""", """{"n":100}""", Tri.True)]
    [InlineData("""{"arg":"/n","gt":0.1}""", """{"n":0.2}""", Tri.True)]
    [InlineData("""{"arg":"/n","gte":5}""", """{"n":"5"}""", Tri.Unknown)]                      // a number in quotes is not a number
    [InlineData("""{"arg":"/n","lt":100}""", """{"n":1e999}""", Tri.Unknown)]                    // out of range
    [InlineData("""{"arg":"/n","lt":100}""", """{}""", Tri.Unknown)]
    [InlineData("""{"arg":"/x","exists":true}""", """{"x":"a"}""", Tri.True)]
    [InlineData("""{"arg":"/x","exists":true}""", """{}""", Tri.False)]
    [InlineData("""{"arg":"/x","exists":false}""", """{}""", Tri.True)]
    [InlineData("""{"arg":"/x","absentOrEmpty":true}""", """{}""", Tri.True)]
    [InlineData("""{"arg":"/x","absentOrEmpty":true}""", """{"x":""}""", Tri.True)]
    [InlineData("""{"arg":"/x","absentOrEmpty":true}""", """{"x":[]}""", Tri.True)]
    [InlineData("""{"arg":"/x","absentOrEmpty":true}""", """{"x":["a"]}""", Tri.False)]
    [InlineData("""{"arg":"/x","countAtMost":1}""", """{"x":["a"]}""", Tri.True)]
    [InlineData("""{"arg":"/x","countAtMost":1}""", """{"x":["a","b"]}""", Tri.False)]
    [InlineData("""{"arg":"/x","countAtMost":0}""", """{}""", Tri.True)]                          // no attachments argument: zero attachments
    [InlineData("""{"arg":"/x","countAtMost":1}""", """{"x":"a"}""", Tri.Unknown)]
    [InlineData("""{"arg":"/x","countAtLeast":2}""", """{"x":["a","b"]}""", Tri.True)]
    public void Numbers_presence_and_counts(string condition, string args, Tri expected) => Assert.Equal(expected, Eval(condition, args));

    [Theory]
    [InlineData("""{"arg":"/a/b","equals":1}""", """{"a":{"b":1}}""", Tri.True)]
    [InlineData("""{"arg":"/a/0","equals":"x"}""", """{"a":["x","y"]}""", Tri.True)]
    [InlineData("""{"arg":"/a/5","equals":"x"}""", """{"a":["x","y"]}""", Tri.Unknown)]
    [InlineData("""{"arg":"/a~1b","equals":1}""", """{"a/b":1}""", Tri.True)]                      // ~1 is a slash
    [InlineData("""{"arg":"/a~0b","equals":1}""", """{"a~b":1}""", Tri.True)]                      // ~0 is a tilde
    [InlineData("""{"arg":"/a/-1","equals":"x"}""", """{"a":["x"]}""", Tri.Unknown)]
    [InlineData("""{"arg":"/a/b","equals":1}""", """{"a":5}""", Tri.Unknown)]
    public void Json_pointers(string condition, string args, Tri expected) => Assert.Equal(expected, Eval(condition, args));

    // ---- combinators: the point of three values ----

    [Theory]
    [InlineData("all", Tri.True, Tri.True, Tri.True)]
    [InlineData("all", Tri.True, Tri.False, Tri.False)]
    [InlineData("all", Tri.True, Tri.Unknown, Tri.Unknown)]
    [InlineData("all", Tri.False, Tri.Unknown, Tri.False)]
    [InlineData("any", Tri.False, Tri.False, Tri.False)]
    [InlineData("any", Tri.True, Tri.Unknown, Tri.True)]
    [InlineData("any", Tri.False, Tri.Unknown, Tri.Unknown)]
    public void All_and_any_follow_the_three_valued_tables(string op, Tri left, Tri right, Tri expected)
    {
        // Build leaves whose results are known: equals on a present value (True/False) or on a missing one (Unknown).
        static string Leaf(Tri t) => t switch
        {
            Tri.True => """{"arg":"/p","equals":1}""",
            Tri.False => """{"arg":"/p","equals":2}""",
            _ => """{"arg":"/missing","equals":1}""",
        };
        Assert.Equal(expected, Eval($$"""{"{{op}}":[{{Leaf(left)}},{{Leaf(right)}}]}""", """{"p":1}"""));
    }

    [Fact]
    public void Not_never_turns_unknown_into_true()
    {
        Assert.Equal(Tri.False, Eval("""{"not":{"arg":"/p","equals":1}}""", """{"p":1}"""));
        Assert.Equal(Tri.True, Eval("""{"not":{"arg":"/p","equals":2}}""", """{"p":1}"""));
        Assert.Equal(Tri.Unknown, Eval("""{"not":{"arg":"/missing","equals":1}}""", """{"p":1}"""));
        // The case that motivated this: "not to a rival" must not become true because the recipient is missing or garbled.
        Assert.Equal(Tri.Unknown, Eval("""{"not":{"arg":"/to","any":{"domainIn":["rival.example"]}}}""", """{"body":"x"}"""));
        Assert.Equal(Tri.Unknown, Eval("""{"not":{"arg":"/to","any":{"domainIn":["rival.example"]}}}""", """{"to":"Bob <b@rival.example>"}"""));
    }

    // ---- time ----

    [Fact]
    public void Time_windows_use_the_zone_and_may_wrap_midnight()
    {
        const string office = """{"time":{"days":["Mon","Tue","Wed","Thu","Fri"],"from":"08:00","to":"19:00","zone":"Europe/Amsterdam"}}""";
        // 2026-10-14 is a Wednesday; Amsterdam is UTC+2 in October.
        Assert.Equal(Tri.True, Eval(office, "{}", DateTimeOffset.Parse("2026-10-14T10:00:00Z")));   // 12:00 local
        Assert.Equal(Tri.False, Eval(office, "{}", DateTimeOffset.Parse("2026-10-14T18:00:00Z")));  // 20:00 local
        Assert.Equal(Tri.False, Eval(office, "{}", DateTimeOffset.Parse("2026-10-17T10:00:00Z")));  // Saturday
        Assert.Equal(Tri.False, Eval(office, "{}", DateTimeOffset.Parse("2026-10-14T05:59:00Z")));  // 07:59 local
        Assert.Equal(Tri.True, Eval(office, "{}", DateTimeOffset.Parse("2026-10-14T06:00:00Z")));   // 08:00 local: window start is inclusive

        const string night = """{"time":{"from":"22:00","to":"06:00"}}""";
        Assert.Equal(Tri.True, Eval(night, "{}", DateTimeOffset.Parse("2026-10-14T23:30:00Z")));
        Assert.Equal(Tri.True, Eval(night, "{}", DateTimeOffset.Parse("2026-10-14T02:00:00Z")));
        Assert.Equal(Tri.False, Eval(night, "{}", DateTimeOffset.Parse("2026-10-14T12:00:00Z")));
    }

    // ---- validation: every mistake is named, with where ----

    [Theory]
    [InlineData("""{"arg":"/x","equalz":"a"}""", "unknown test")]
    [InlineData("""{"arg":"x","equals":"a"}""", "JSON Pointer")]
    [InlineData("""{"arg":"/x"}""", "exactly one test")]
    [InlineData("""{"arg":"/x","equals":"a","in":["a"]}""", "exactly one test")]
    [InlineData("""{"arg":"/x","matches":"(a)\\1"}""", "matches")]                       // backreferences need a backtracking engine: refused
    [InlineData("""{"arg":"/x","matches":"("}""", "matches")]
    [InlineData("""{"arg":"/x","domainIn":["not a domain"]}""", "valid domain")]
    [InlineData("""{"arg":"/x","domainIn":[]}""", "non-empty array")]
    [InlineData("""{"arg":"/x","lt":"ten"}""", "needs a number")]
    [InlineData("""{"arg":"/x","countAtMost":-1}""", "at least 0")]
    [InlineData("""{"all":[]}""", "non-empty array")]
    [InlineData("""{"all":[{"arg":"/x","equals":1}],"any":[{"arg":"/y","equals":1}]}""", "exactly one")]
    [InlineData("""{"sometimes":[]}""", "unknown condition")]
    [InlineData("""{"time":{"zone":"Mars/Olympus"}}""", "time zone")]
    [InlineData("""{"time":{"from":"8am"}}""", "HH:mm")]
    [InlineData("""{"time":{"days":["Funday"]}}""", "not a day")]
    [InlineData("""{"time":{"hours":3}}""", "unknown setting")]
    [InlineData("""{"arg":"/x","all":{"equals":1},"equals":1}""", "put the test inside")]
    [InlineData("""[1]""", "JSON object")]
    public void Mistakes_are_refused_with_a_reason(string condition, string expectedFragment) =>
        Assert.Contains(expectedFragment, Invalid(condition).Message);

    [Fact]
    public void Errors_say_where_in_the_condition_the_problem_is()
    {
        var ex = Invalid("""{"all":[{"arg":"/a","equals":1},{"any":[{"arg":"/b","bogus":1}]}]}""");
        Assert.Contains("When/all/1/any/0", ex.Message);
    }
}
