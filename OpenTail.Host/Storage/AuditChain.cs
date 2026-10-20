using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenTail.Host;

/// <summary>One stored audit entry with its chain fields. <see cref="At"/> is kept as stored text, so the hash is recomputed exactly.</summary>
public sealed record ChainedAuditEntry(
    long Seq, string At, string Event, string? ActionId, string? Tool, string? Actor, string? Detail, string? PrevHash, string? Hash);

/// <summary>Where a chain stands: the newest entry and its hash. Publish it somewhere the database's owner cannot rewrite.</summary>
public sealed record AuditCheckpoint(long Seq, string Hash)
{
    public override string ToString() => $"{Seq}:{Hash}";

    public static AuditCheckpoint Parse(string s)
    {
        var i = s.IndexOf(':');
        if (i <= 0 || !long.TryParse(s[..i], out var seq) || s[(i + 1)..] is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException($"'{s}' is not a checkpoint (<seq>:<64 hex digits>, as --audit-checkpoint prints it).");
        return new AuditCheckpoint(seq, hash.ToLowerInvariant());
    }
}

/// <param name="Unchained">Entries before the chain starts (written by a Host older than schema 5): present, not verifiable.</param>
/// <param name="FirstBreak">The first entry whose chain does not hold, with why; null when the chain is intact.</param>
public sealed record AuditVerification(int Entries, int Unchained, AuditCheckpoint? Head, string? FirstBreak, IReadOnlyList<string> Checkpoints)
{
    public bool Intact => FirstBreak is null && Checkpoints.All(c => c.StartsWith("ok", StringComparison.Ordinal));
}

public static class AuditChain
{
    /// <summary>SHA-256 over the entry's sequence number, fields and the previous hash, as a JSON array (no ambiguity between fields).</summary>
    public static string Hash(long seq, string at, string evt, string? actionId, string? tool, string? actor, string? detail, string? prev) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Preimage(seq, at, evt, actionId, tool, actor, detail, prev))));

    /// <summary>The exact text an entry's hash covers: a JSON array of its fields and the previous hash. Exported with each entry so a
    /// verifier needs only SHA-256 and a JSON parser, not this serializer.</summary>
    public static string Preimage(long seq, string at, string evt, string? actionId, string? tool, string? actor, string? detail, string? prev) =>
        JsonSerializer.Serialize(new object?[] { seq, at, evt, actionId, tool, actor, detail, prev });

    public static AuditCheckpoint? Head(IReadOnlyList<ChainedAuditEntry> entries) =>
        entries.LastOrDefault() is { Hash: { } h } last ? new AuditCheckpoint(last.Seq, h) : null;

    /// <summary>Walks the chain and reports the first break; each checkpoint must match the entry at its sequence number.</summary>
    public static AuditVerification Verify(IReadOnlyList<ChainedAuditEntry> entries, IReadOnlyList<AuditCheckpoint>? checkpoints = null)
    {
        var unchained = 0;
        string? prev = null, broken = null;
        var started = false;
        foreach (var e in entries)
        {
            if (!started && e.Hash is null) { unchained++; continue; }
            if (e.Hash is null) { broken = $"entry {e.Seq} ({e.Event}) has no hash although the chain started earlier"; break; }
            if (!started && e.PrevHash is not null) { broken = $"entry {e.Seq} ({e.Event}) follows an entry that is no longer there: entries before it were removed"; break; }
            if (started && e.PrevHash != prev) { broken = $"entry {e.Seq} ({e.Event}) does not follow the entry before it: an entry was removed, inserted or reordered"; break; }
            if (Hash(e.Seq, e.At, e.Event, e.ActionId, e.Tool, e.Actor, e.Detail, e.PrevHash) != e.Hash) { broken = $"entry {e.Seq} ({e.Event}) was changed after it was written"; break; }
            started = true;
            prev = e.Hash;
        }

        var bySeq = entries.ToDictionary(e => e.Seq);
        var results = new List<string>();
        foreach (var cp in checkpoints ?? [])
            results.Add(!bySeq.TryGetValue(cp.Seq, out var at)
                ? $"MISSING checkpoint {cp}: entry {cp.Seq} is gone (entries were removed from the end, or the database was replaced)"
                : at.Hash == cp.Hash ? $"ok checkpoint {cp}" : $"MISMATCH checkpoint {cp}: entry {cp.Seq} now hashes to {at.Hash ?? "nothing"}");
        return new AuditVerification(entries.Count, unchained, Head(entries), broken, results);
    }
}

/// <summary>The decision records checked against the digests the audit trail holds for them (audit D3).</summary>
public sealed record DecisionRecordVerification(int Records, int Batches, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public bool Intact => Problems.Count == 0;
}

public static class DecisionRecordCheck
{
    private static readonly System.Text.RegularExpressions.Regex BatchPattern =
        new(@"batch (?<batch>purge_[0-9a-f]+), digest (?<digest>sha256:[0-9a-f]{64})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Every record's content must match its hash, every batch's records must match the digest its retention_purge entry
    /// carries, and every record must belong to a batch the trail names. A batch whose records audit retention has since removed is
    /// reported as a note, not a problem.</summary>
    public static DecisionRecordVerification Verify(IReadOnlyList<ChainedAuditEntry> chain, IReadOnlyList<(DecisionRecord Record, string Json, string Hash)> records)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in chain.Where(e => e.Event == "retention_purge" && e.Detail is not null))
            if (BatchPattern.Match(e.Detail!) is { Success: true } m) named[m.Groups["batch"].Value] = m.Groups["digest"].Value;
        var retentionRan = chain.Any(e => e.Event == "decision_records_purge");

        foreach (var (record, json, hash) in records.Where(r => DecisionRecord.HashOf(r.Json) != r.Hash))
            problems.Add($"decision record {record.ActionId} was changed (its content no longer matches its hash)");
        var byBatch = records.GroupBy(r => r.Record.Batch ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var (batch, rows) in byBatch.Where(b => !named.ContainsKey(b.Key)))
            problems.Add($"{rows.Count} decision record(s) in batch '{batch}' are not named by any audit entry");
        foreach (var (batch, digest) in named)
        {
            if (!byBatch.TryGetValue(batch, out var rows))
            {
                if (retentionRan) notes.Add($"batch {batch}: its records have been removed by audit retention");
                else problems.Add($"batch {batch}: the audit trail names it, but its decision records are gone");
                continue;
            }
            var recomputed = DecisionRecord.Digest(rows.OrderBy(r => r.Record.ActionId, StringComparer.Ordinal).Select(r => DecisionRecord.HashOf(r.Json)));
            if (recomputed != digest)
                problems.Add(retentionRan
                    ? $"batch {batch}: its records do not match the digest (some may have been removed by audit retention, or changed)"
                    : $"batch {batch}: its records do not match the digest in the audit trail (one was changed, removed or added)");
        }
        return new DecisionRecordVerification(records.Count, named.Count, problems, notes);
    }
}
