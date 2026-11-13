using OpenTail.Orca.Admin;
using OpenTail.Orca.Http;

namespace OpenTail.Orca.Tests;

public class HttpConfigTests
{
    [Fact]
    public void Reviewer_tokens_carry_roles_and_groups_after_a_bar()
    {
        const string raw = "rita:reviewer-token-0123456789|review,admin,group:finance;jo:junior-token-0123456789|review;old:legacy-token-0123456789";
        var tokens = DeploymentRunner.ParseTokens(raw);
        var roles = DeploymentRunner.ParseTokenRoles(raw);

        Assert.Equal("rita", tokens["reviewer-token-0123456789"]);
        Assert.Equal("jo", tokens["junior-token-0123456789"]);
        Assert.Equal("old", tokens["legacy-token-0123456789"]);
        Assert.Contains(Roles.Admin, roles["reviewer-token-0123456789"]);
        Assert.Contains("group:finance", roles["reviewer-token-0123456789"]);
        Assert.DoesNotContain(Roles.Admin, roles["junior-token-0123456789"]);
        Assert.False(roles.ContainsKey("legacy-token-0123456789")); // no roles given: keeps the single-operator behavior
    }

    [Fact]
    public void A_config_file_becomes_validated_options_without_any_secret_in_it()
    {
        var (key, hash) = ApiKeys.Generate();
        var path = Path.Combine(Path.GetTempPath(), "otail-http-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, $$"""
            {
              // comments are fine
              "Urls": [ "https://0.0.0.0:8443" ],
              "PublicBaseUrl": "https://host.example",
              "ApiKeys": [ { "Name": "alice-agent", "Hash": "{{hash}}" } ],
              "Jwt": { "Authority": "https://login.example", "Audience": "https://host.example/mcp" }
            }
            """);
        try
        {
            var options = HttpDeploymentConfig.Load(path).ToOptions("rita:reviewer-token-0123456789|review");
            Assert.Empty(options.Validate());
            Assert.Equal("alice-agent", Assert.Single(options.ApiKeys).Name);
            Assert.Equal("https://login.example", options.Jwt!.Authority);
            Assert.Contains(Roles.Review, options.Admin!.TokenRoles!["reviewer-token-0123456789"]);
            Assert.DoesNotContain(key, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_unknown_setting_in_the_config_is_an_error_not_silently_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), "otail-http-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "Urls": ["http://127.0.0.1:5600"], "AuthEnabeld": true }""");
        try { Assert.ThrowsAny<System.Text.Json.JsonException>(() => HttpDeploymentConfig.Load(path)); }
        finally { File.Delete(path); }
    }
}
