using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Host;

/// <summary>What an export wrote: the folder, how many of each, and the chain's head at the time.</summary>
public sealed record AuditExportResult(string Folder, int AuditEntries, int DecisionRecords, int PolicyVersions, AuditCheckpoint? Head);

/// <summary>
/// Audit D7: one folder an auditor can check without trusting the Host or us. It holds the audit trail (each entry with the exact text
/// its hash covers), the decision records for the period (kept ones with their stored hash, and ones built from actions not yet purged),
/// every policy version in force during the period, the chain's head, a manifest with every file's hash, and <c>verify.py</c>, a short
/// script in plain Python that checks all of it.
/// </summary>
public static class AuditExport
{
    public static AuditExportResult Write(string folder, SqliteActionStore store, DateTimeOffset from, DateTimeOffset until)
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "policies"));
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Put(string name, string text)
        {
            File.WriteAllText(Path.Combine(folder, name), text, new UTF8Encoding(false));
            files[name.Replace('\\', '/')] = Sha(text);
        }

        // The whole chain up to the end of the period: entries hold no argument values, and a chain can only be checked from its start.
        var chain = new SqliteAuditSink(store).ReadChain()
            .Where(e => DateTimeOffset.Parse(e.At, System.Globalization.CultureInfo.InvariantCulture) < until).ToList();
        var audit = new StringBuilder();
        foreach (var e in chain)
            audit.AppendLine(new JsonObject
            {
                ["seq"] = e.Seq, ["at"] = e.At, ["event"] = e.Event, ["actionId"] = e.ActionId, ["tool"] = e.Tool, ["actor"] = e.Actor,
                ["detail"] = e.Detail, ["prevHash"] = e.PrevHash, ["hash"] = e.Hash,
                ["preimage"] = e.Hash is null ? null : AuditChain.Preimage(e.Seq, e.At, e.Event, e.ActionId, e.Tool, e.Actor, e.Detail, e.PrevHash),
                ["inPeriod"] = DateTimeOffset.Parse(e.At, System.Globalization.CultureInfo.InvariantCulture) >= from,
            }.ToJsonString());
        Put("audit.jsonl", audit.ToString());

        // Decision records: kept ones (bound into the chain by their batch's digest), and the actions of the period still stored.
        var decisions = new StringBuilder();
        var count = 0;
        // Whole purge batches: one with any record in the period goes in complete, so its digest can be checked.
        var all = store.ReadDecisionRecords();
        var touched = all.Where(r => r.Record.CreatedAt >= from && r.Record.CreatedAt < until).Select(r => r.Record.Batch).ToHashSet(StringComparer.Ordinal);
        foreach (var (record, json, hash) in all.Where(r => touched.Contains(r.Record.Batch)))
        {
            decisions.AppendLine(new JsonObject { ["hash"] = hash, ["batch"] = record.Batch, ["record"] = json }.ToJsonString());
            count++;
        }
        foreach (var a in store.QueryAsync(new ActionQuery { Limit = int.MaxValue }).GetAwaiter().GetResult()
                     .Where(a => a.CreatedAt >= from && a.CreatedAt < until).OrderBy(a => a.CreatedAt))
        {
            var json = DecisionRecord.From(a, batch: "live").ToJson();
            decisions.AppendLine(new JsonObject { ["hash"] = DecisionRecord.HashOf(json), ["batch"] = "live", ["record"] = json }.ToJsonString());
            count++;
        }
        Put("decisions.jsonl", decisions.ToString());

        // Every policy loaded during the period, and the one in force when it began.
        var versions = new SqlitePolicyVersionStore(store);
        var loads = chain.Where(e => e.Event == "policy_loaded" && e.Detail is not null)
            .Select(e => (At: DateTimeOffset.Parse(e.At, System.Globalization.CultureInfo.InvariantCulture), Hash: e.Detail!.Split(' ', 2)[0].TrimEnd(';'))).ToList();
        var inForce = loads.Where(l => l.At < from).Select(l => l.Hash).TakeLast(1).Concat(loads.Where(l => l.At >= from).Select(l => l.Hash))
            .Distinct(StringComparer.Ordinal).ToList();
        var written = 0;
        foreach (var h in inForce)
            if (versions.Get(h) is { } v)
            {
                Put($"policies/{h["sha256:".Length..]}.json", v.Json);
                written++;
            }

        var head = AuditChain.Head(chain);
        Put("head.txt", (head?.ToString() ?? "none") + "\n" +
            "Compare this with the checkpoints held outside the Host (the audit_checkpoint notifications): a checkpoint at or before the head\n" +
            "must match the entry with the same sequence number in audit.jsonl.\n");
        Put("verify.py", VerifyScript);

        var manifest = new JsonObject
        {
            ["format"] = "opentail-audit-export/1",
            ["periodFrom"] = from.ToUniversalTime().ToString("O"),
            ["periodUntil"] = until.ToUniversalTime().ToString("O"),
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["host"] = PolicyLoad.HostVersion,
            ["head"] = head?.ToString(),
            ["files"] = new JsonObject(files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
            ["verify"] = "python3 verify.py <this folder>",
        };
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return new AuditExportResult(folder, chain.Count, count, written, head);
    }

    private static string Sha(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The verifier shipped in every export. Plain Python 3, standard library only. It needs none of our code: each hash is
    /// checked over the exact text it covers, and that text is parsed to confirm it says what the entry says.</summary>
    public const string VerifyScript = """
        #!/usr/bin/env python3
        # Verifies an OpenTail audit export without OpenTail. Standard library only.
        # Checks: every file against manifest.json; the audit hash chain (each entry's hash over its preimage, the preimage saying what the
        # entry says, each entry following the one before); decision records against their hashes and, for kept ones, their purge batch's
        # digest in the chain; policy files against the hash in their name.
        import hashlib, json, os, re, sys

        def sha(text):
            return "sha256:" + hashlib.sha256(text.encode("utf-8")).hexdigest()

        def read(folder, name):
            with open(os.path.join(folder, name), encoding="utf-8", newline="") as f:
                return f.read()

        def main(folder):
            problems = []
            manifest = json.loads(read(folder, "manifest.json"))
            for name, expected in manifest["files"].items():
                if sha(read(folder, name)) != expected:
                    problems.append(f"{name} does not match the manifest")

            prev, started, entries = None, False, []
            for line in read(folder, "audit.jsonl").splitlines():
                e = json.loads(line)
                entries.append(e)
                if e["hash"] is None:
                    if started:
                        problems.append(f"entry {e['seq']} has no hash although the chain started earlier")
                    continue
                if not started and e["prevHash"] is not None:
                    problems.append(f"entry {e['seq']} follows an entry that is not in the export")
                if started and e["prevHash"] != prev:
                    problems.append(f"entry {e['seq']} does not follow the entry before it")
                # Chain hashes are stored as bare hex (no "sha256:" prefix).
                if hashlib.sha256(e["preimage"].encode("utf-8")).hexdigest() != e["hash"]:
                    problems.append(f"entry {e['seq']} does not match its hash")
                fields = json.loads(e["preimage"])
                if fields != [e["seq"], e["at"], e["event"], e["actionId"], e["tool"], e["actor"], e["detail"], e["prevHash"]]:
                    problems.append(f"entry {e['seq']} says something other than what its hash covers")
                started, prev = True, e["hash"]

            digests = {}
            for e in entries:
                if e["event"] == "retention_purge" and e["detail"]:
                    m = re.search(r"batch (purge_[0-9a-f]+), digest (sha256:[0-9a-f]{64})", e["detail"])
                    if m:
                        digests[m.group(1)] = m.group(2)
            batches = {}
            records = 0
            for line in read(folder, "decisions.jsonl").splitlines():
                d = json.loads(line)
                records += 1
                if sha(d["record"]) != d["hash"]:
                    problems.append(f"a decision record does not match its hash: {json.loads(d['record'])['ActionId']}")
                if d["batch"] != "live":
                    batches.setdefault(d["batch"], []).append((json.loads(d["record"])["ActionId"], d["hash"]))
            for batch, rows in batches.items():
                if batch not in digests:
                    problems.append(f"batch {batch} is not named in the audit trail")
                    continue
                joined = "\n".join(h for _, h in sorted(rows, key=lambda r: r[0].encode("utf-8")))
                if sha(joined) != digests[batch]:
                    problems.append(f"batch {batch} does not match its digest in the audit trail")

            policies = 0
            pdir = os.path.join(folder, "policies")
            for name in sorted(os.listdir(pdir)) if os.path.isdir(pdir) else []:
                policies += 1
                if sha(read(pdir, name)) != "sha256:" + name[:-5]:
                    problems.append(f"policies/{name} does not match its hash")

            print(f"{len(entries)} audit entries, {records} decision records, {policies} policy versions; head {manifest['head']}")
            for p in problems:
                print("PROBLEM: " + p)
            print("Intact." if not problems else f"{len(problems)} problem(s).")
            print("Also compare head.txt with the checkpoints kept outside the Host.")
            return 0 if not problems else 1

        if __name__ == "__main__":
            sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "."))

        """;
}
