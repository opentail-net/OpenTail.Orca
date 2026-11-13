using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenTail.Orca;

/// <summary>
/// A closed piece of the audit trail (reviewer gate plan, D-41). The entries <see cref="FirstSeq"/> to <see cref="LastSeq"/> left the live database
/// after they were written to <see cref="ArchiveFile"/> and verified there. <see cref="LastHash"/> is the anchor the live trail continues from, which
/// is why a cut is recorded in the live trail itself, and why its hash should also be published (the audit checkpoints) where the database's owner
/// cannot rewrite it.
/// </summary>
public sealed record AuditCut(long FirstSeq, long LastSeq, string LastHash, string ArchiveFile, string Digest, DateTimeOffset ArchivedAt, bool Removed)
{
    public AuditCheckpoint Anchor => new(LastSeq, LastHash);
}

/// <summary>The archive file: a header with the ends of the piece and a digest of its hashes, then the entries as they were written.</summary>
public sealed record ArchiveFile(string Format, long FirstSeq, long LastSeq, string? AnchorHash, string LastHash, int Count, string Digest,
    DateTimeOffset CreatedAt, IReadOnlyList<ChainedAuditEntry> Entries);

/// <summary>The archive file format and its verifier. A file checks on its own: its entries link to each other from its anchor, each entry's hash
/// recomputes, and the digest matches. The live trail is checked against the cut separately (<see cref="AuditChain.Verify"/>).</summary>
public static class AuditArchive
{
    public const string FormatName = "opentail-audit-archive/1";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>SHA-256 over the entries' hashes, one per line, in order.</summary>
    public static string DigestOf(IEnumerable<string> hashes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", hashes))));

    /// <summary>Writes the entries (a run of the chain that starts after <paramref name="anchor"/>) to a new file in <paramref name="directory"/>. The
    /// file is written under a temporary name, flushed to disk, and then renamed, so a half-written file never has the archive's name.</summary>
    public static string Write(string directory, IReadOnlyList<ChainedAuditEntry> entries, AuditCheckpoint? anchor, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        var first = entries[0];
        var last = entries[^1];
        var file = Path.Combine(directory, $"audit-{first.Seq:D12}-{last.Seq:D12}.json");
        var document = new ArchiveFile(FormatName, first.Seq, last.Seq, anchor?.Hash, last.Hash!, entries.Count,
            DigestOf(entries.Select(e => e.Hash!)), now, entries);
        var temp = file + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, Json));
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, file, overwrite: true);
        return file;
    }

    /// <summary>What is wrong with an archive file, if anything: an empty list means it verifies on its own.</summary>
    public static IReadOnlyList<string> Verify(string file)
    {
        ArchiveFile? document;
        try
        {
            document = JsonSerializer.Deserialize<ArchiveFile>(File.ReadAllText(file, Encoding.UTF8), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [$"{Path.GetFileName(file)} cannot be read: {ex.Message}"];
        }
        if (document is null) return [$"{Path.GetFileName(file)} is empty"];
        if (document.Format != FormatName) return [$"{Path.GetFileName(file)} is not an audit archive of format {FormatName}"];
        // A file that parses can still be malformed: the verifier reports that, it never throws on it.
        if (document.Entries is null) return [$"{Path.GetFileName(file)} has no entries list"];
        if (document.Entries.Any(e => e is null || e.At is null || e.Event is null))
            return [$"{Path.GetFileName(file)} has an entry without its time or event"];

        var problems = new List<string>();
        if (document.Entries.Count != document.Count) problems.Add("the number of entries does not match the header");
        if (document.Entries.Count == 0) return [.. problems, "the archive holds no entries"];

        var previous = document.AnchorHash;
        var expected = document.FirstSeq;
        foreach (var e in document.Entries)
        {
            if (e.Seq != expected) { problems.Add($"entry {e.Seq} is out of place (expected {expected})"); break; }
            if (e.PrevHash != previous) { problems.Add($"entry {e.Seq} does not follow the entry before it"); break; }
            if (e.Hash is null || AuditChain.Hash(e.Seq, e.At, e.Event, e.ActionId, e.Tool, e.Actor, e.Detail, e.PrevHash) != e.Hash)
            { problems.Add($"entry {e.Seq} was changed after it was archived"); break; }
            previous = e.Hash;
            expected++;
        }
        if (document.Entries[^1].Seq != document.LastSeq) problems.Add("the last entry does not match the header");
        if (previous != document.LastHash) problems.Add("the last hash does not match the header");
        if (DigestOf(document.Entries.Select(e => e.Hash ?? "")) != document.Digest) problems.Add("the digest does not match the entries");
        return problems;
    }
}

/// <summary>
/// Moves the old part of the audit trail out of the live database and into archive files (D-41), and removes archive files once they are past their
/// time. A cut archives the entries older than the live window, oldest first, and removes them from the live database in one transaction with a
/// <c>audit_archived</c> entry that records the cut. Nothing is removed unless the chain up to the cut verifies, and the archive file is written and
/// verified first.
/// </summary>
public sealed class AuditArchiver(SqliteActionStore store)
{
    /// <summary>Every cut made so far, oldest first.</summary>
    public IReadOnlyList<AuditCut> Cuts()
    {
        using var c = store.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT first_seq,last_seq,last_hash,archive_file,digest,archived_at,removed FROM audit_cuts ORDER BY last_seq";
        var cuts = new List<AuditCut>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            cuts.Add(new AuditCut(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
                DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture), r.GetInt64(6) != 0));
        return cuts;
    }

    /// <summary>The newest cut, or null when none has been made: the live trail continues from it.</summary>
    public AuditCut? Latest() => Cuts().LastOrDefault();

    /// <summary>Archives the entries older than <paramref name="liveWindow"/> and removes them from the live trail. Returns the cut, or null when
    /// there is nothing old enough. Throws, and removes nothing, when the chain up to the cut does not verify or the archive file does not verify.</summary>
    /// <param name="maxEntries">The most entries one cut takes; a later cut takes the rest.</param>
    public AuditCut? Cut(DateTimeOffset now, string directory, TimeSpan liveWindow, int maxEntries = 100_000)
    {
        var cutoff = now - liveWindow;
        var anchorCut = Latest();
        var anchor = anchorCut?.Anchor;
        var sink = new SqliteAuditSink(store);

        // Oldest first, from just after the last cut, up to the first entry that is still inside the live window.
        var candidates = new List<ChainedAuditEntry>();
        var after = anchor?.Seq ?? 0;
        var reachedLive = false;
        while (!reachedLive && candidates.Count < maxEntries)
        {
            var batch = sink.ReadChainAfter(after, 500);
            if (batch.Count == 0) break;
            foreach (var e in batch)
            {
                if (DateTimeOffset.Parse(e.At, CultureInfo.InvariantCulture) >= cutoff) { reachedLive = true; break; }
                candidates.Add(e);
                after = e.Seq;
                if (candidates.Count >= maxEntries) break;
            }
        }
        if (candidates.Count == 0) return null;
        // Entries written before the chain existed have no hash and cannot be archived as part of it: they stay where they are.
        if (candidates.Any(e => e.Hash is null)) return null;

        var verification = AuditChain.Verify(candidates, null, anchor);
        if (verification.FirstBreak is { } broken)
            throw new InvalidOperationException($"The audit chain does not verify up to the cut, so nothing was archived: {broken}.");

        var file = AuditArchive.Write(directory, candidates, anchor, now);
        var problems = AuditArchive.Verify(file);
        if (problems.Count > 0)
        {
            File.Delete(file);
            throw new InvalidOperationException("The archive file did not verify, so nothing was removed: " + string.Join("; ", problems) + ".");
        }

        var first = candidates[0];
        var last = candidates[^1];
        var digest = AuditArchive.DigestOf(candidates.Select(e => e.Hash!));
        using var c = store.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false);
        // The cut is written into the chain after the last entry that stays, so the live trail has a head and records what left it.
        SqliteAuditSink.Append(c, tx, new AuditEntry(now, "audit_archived", null, null, "system",
            $"entries {first.Seq} to {last.Seq} moved to {Path.GetFileName(file)}; digest {digest}; the trail continues from {last.Seq}:{last.Hash}"));
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO audit_cuts(last_seq,first_seq,last_hash,archive_file,digest,archived_at,removed) VALUES($l,$f,$h,$a,$d,$t,0)";
            insert.Parameters.AddWithValue("$l", last.Seq);
            insert.Parameters.AddWithValue("$f", first.Seq);
            insert.Parameters.AddWithValue("$h", last.Hash!);
            insert.Parameters.AddWithValue("$a", file);
            insert.Parameters.AddWithValue("$d", digest);
            insert.Parameters.AddWithValue("$t", now.ToString("O"));
            insert.ExecuteNonQuery();
        }
        using (var delete = c.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM audit WHERE seq BETWEEN $f AND $l";
            delete.Parameters.AddWithValue("$f", first.Seq);
            delete.Parameters.AddWithValue("$l", last.Seq);
            delete.ExecuteNonQuery();
        }
        tx.Commit();
        return new AuditCut(first.Seq, last.Seq, last.Hash!, file, digest, now, false);
    }

    /// <summary>Removes the archive files of cuts archived more than <paramref name="keep"/> ago. The cut stays in the chain, with the time its file
    /// went, so the trail still shows that the piece existed. Returns how many files were removed.</summary>
    public int ExpireArchives(DateTimeOffset now, TimeSpan keep)
    {
        var expired = Cuts().Where(cut => !cut.Removed && cut.ArchivedAt + keep <= now).ToList();
        // The record comes first, in the trail, and the file after it. A crash after the record leaves a file the trail already calls removed;
        // the pass below deletes it. Deleting first would leave a record that says the file is there when it is gone.
        foreach (var cut in expired)
        {
            using var c = store.OpenConnection();
            using var tx = c.BeginTransaction(deferred: false);
            SqliteAuditSink.Append(c, tx, new AuditEntry(now, "archive_expired", null, null, "system",
                $"entries {cut.FirstSeq} to {cut.LastSeq} removed from the archive ({Path.GetFileName(cut.ArchiveFile)}), kept for {keep.TotalDays:0} days"));
            using var update = c.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE audit_cuts SET removed=1 WHERE last_seq=$l";
            update.Parameters.AddWithValue("$l", cut.LastSeq);
            update.ExecuteNonQuery();
            tx.Commit();
        }
        // Every cut recorded as removed whose file is still there (this pass, or a pass that crashed after recording). Deleting is idempotent.
        foreach (var cut in Cuts().Where(cut => cut.Removed && File.Exists(cut.ArchiveFile)))
            File.Delete(cut.ArchiveFile);
        return expired.Count;
    }
}
