namespace OpenTail.Modules.Skills;

/// <summary>
/// The skill store's command line: <c>--check-skill-store &lt;folder&gt;</c> lists every problem in a store and what nothing reads. It serves
/// nothing and writes nothing; only a problem makes it fail (exit 1). The Host's server program dispatches it, so the admin package stays
/// independent of the modules (store plan, design item 7).
/// </summary>
public static class SkillStoreCli
{
    /// <summary>Returns the exit code when the command line is this command, and null otherwise.</summary>
    public static int? TryRun(string[] args, TextWriter output)
    {
        var flag = Array.IndexOf(args, "--check-skill-store");
        if (flag < 0) return null;
        if (flag + 1 >= args.Length || args[flag + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Usage: opentail-orca --check-skill-store <store folder>");
            return 64;
        }

        var check = new SkillStore(args[flag + 1]).Check();
        foreach (var p in check.Problems) output.WriteLine("PROBLEM: " + p);
        foreach (var n in check.Notes) output.WriteLine("note: " + n);
        output.WriteLine(check.Problems.Count == 0 && check.Notes.Count == 0
            ? "The skill store has no problems."
            : $"{check.Problems.Count} problem(s), {check.Notes.Count} note(s). Nothing was served or written.");
        return check.Problems.Count == 0 ? 0 : 1;
    }
}
