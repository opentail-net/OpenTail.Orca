using OpenTail.Orca;

namespace OpenTail.Orca.Admin;

/// <summary>
/// The shared <c>Main</c> for a deployment: policy file, SQLite database, loopback admin page, MCP over stdio.
/// A deployment is a tiny program that names its modules and calls this. stdout belongs to MCP; messages go to stderr.
/// <code>
///   --policy &lt;file&gt;      policy JSON (required)
///   --db &lt;file&gt;          SQLite file (default ./opentail-orca.db)
///   --admin-port &lt;n&gt;     loopback port for the approval page (default 5599; 0 turns it off)
///   --modules &lt;file&gt;     modules.json: modules enabled by configuration (see <see cref="ModulesConfig"/>)
///   env OPENTAIL_ADMIN_TOKENS   "alice:token-of-16-chars-or-more;bob:another-long-token"
///   --explain, --replay-policy   look at policy without serving (see <see cref="PolicyCli"/>)
/// </code>
/// </summary>
public static class DeploymentRunner
{
    public const string TokensVariable = "OPENTAIL_ADMIN_TOKENS";

    /// <summary>A deployment whose modules all come from <c>--modules</c>: the program names none in code.</summary>
    public static Task<int> RunAsync(string[] args, string name, CancellationToken ct = default) => RunAsync(args, name, _ => { }, ct);

    /// <summary>Who started the Host, for the <c>policy_loaded</c> audit entry: <c>--by &lt;name&gt;</c>, else the operating-system account.</summary>
    public static string StartedBy(string[] args)
    {
        var i = Array.IndexOf(args, "--by");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : "os:" + Environment.UserName;
    }

    public static async Task<int> RunAsync(string[] args, string name, Action<OpenTailHostBuilder> addModules, CancellationToken ct = default)
    {
        if (await ModuleWorker.TryRunAsync(args, ct: ct) is { } workerExit) return workerExit;
        if (await PolicyCli.TryRunAsync(args, name, addModules, Console.Out) is { } policyExit) return policyExit;

        string? policyPath = null, dbPath = "opentail-orca.db";
        var adminPort = 5599;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--policy" when i + 1 < args.Length: policyPath = args[++i]; break;
                case "--db" when i + 1 < args.Length: dbPath = args[++i]; break;
                case "--modules" when i + 1 < args.Length: i++; break;
                case "--admin-port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): adminPort = p; i++; break;
                case "--by" when i + 1 < args.Length: i++; break;
                default:
                    await Console.Error.WriteLineAsync($"Unknown or incomplete argument: {args[i]}");
                    return Usage(name);
            }
        }
        if (policyPath is null) return Usage(name);

        OpenTailHost host;
        try
        {
            var builder = new OpenTailHostBuilder().WithName(name).UsePolicy(PolicyConfig.Load(policyPath)).UseSqlite(dbPath)
                .StartedBy(StartedBy(args));
            await ModulesCli.ApplyAsync(builder, args, ModuleLoadMode.Run, Console.Error, ct);
            if (args.Contains("--shadow")) builder.UseShadow();
            addModules(builder);
            host = builder.Build();
            foreach (var line in ModulesCli.Describe(host)) await Console.Error.WriteLineAsync(line);
            foreach (var w in host.StartupWarnings) await Console.Error.WriteLineAsync("warning: " + w);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or System.Text.Json.JsonException)
        {
            await Console.Error.WriteLineAsync($"Cannot start: {ex.Message}");
            return 2;
        }

        RunningAdmin? admin = null;
        if (adminPort > 0)
        {
            var tokens = ParseTokens(Environment.GetEnvironmentVariable(TokensVariable));
            if (tokens.Count == 0)
                await Console.Error.WriteLineAsync($"Admin page disabled: set {TokensVariable} (name:token;name:token, tokens of 16+ characters) to enable it.");
            else
            {
                try
                {
                    var roles = ParseTokenRoles(Environment.GetEnvironmentVariable(TokensVariable));
                    admin = await AdminServer.StartAsync(host, new AdminOptions { Tokens = tokens, TokenRoles = roles }, $"http://127.0.0.1:{adminPort}", ct);
                    await Console.Error.WriteLineAsync($"Approval page: {admin.BaseUri}");
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
                {
                    await Console.Error.WriteLineAsync($"Admin page failed to start: {ex.Message}");
                    return 2;
                }
            }
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; linked.Cancel(); };
            await host.RunStdioAsync(linked.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        finally { if (admin is not null) await admin.DisposeAsync(); }
    }

    /// <summary>Token to reviewer name. Entries look like <c>name:token</c> or <c>name:token|review,admin,group:finance</c>.</summary>
    public static IReadOnlyDictionary<string, string> ParseTokens(string? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return map;
        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = pair.Split('|', 2)[0];
            var at = entry.IndexOf(':');
            if (at <= 0 || at == entry.Length - 1) continue;
            map[entry[(at + 1)..]] = entry[..at];
        }
        return map;
    }

    /// <summary>Roles after the <c>|</c>: <c>review</c>, <c>admin</c>, <c>group:name</c>. A token with no <c>|</c> part is absent from
    /// the result and keeps the single-operator behavior (reviewer and admin).</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ParseTokenRoles(string? raw)
    {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return map;
        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('|', 2);
            if (parts.Length < 2) continue;
            var at = parts[0].IndexOf(':');
            if (at <= 0 || at == parts[0].Length - 1) continue;
            var roles = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => r switch { "review" => Roles.Review, "admin" => Roles.Admin, _ => r })
                .ToHashSet(StringComparer.Ordinal);
            map[parts[0][(at + 1)..]] = roles;
        }
        return map;
    }

    private static int Usage(string name)
    {
        Console.Error.WriteLine($"Usage: {name} --policy <file> [--db <file>] [--admin-port <n>] [--modules <modules.json>]");
        Console.Error.WriteLine($"       {name} --policy <file> [--modules <file>] --list-modules | --lint | --explain <tool> '<json>' | --test-policy <cases.json>");
        Console.Error.WriteLine($"       {name} --policy <file> --modules <file> --adopt-policy <module id> [--allow-execute] [--replace]");
        Console.Error.WriteLine($"       env {TokensVariable}=\"alice:<16+ char token>;bob:<16+ char token>\"");
        return 64;
    }
}
