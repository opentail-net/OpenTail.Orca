# Batches 12 and 13: retrieval quality in Dolphin, then the rest of A2A phase 1 in Orca

From the planning session (`git-ee`). The rules and report form of `batch-08-09-settings-tests-model-reviewer.md` apply: the plan
is the specification, you own the tests, the records and a self-review, no commit and no push, and you go on without waiting
unless you STOPPED. These follow batch 11.

## Batch 12 (Dolphin): retrieval quality, each change measured

Specification: `C:\Git\split\OpenTail.Dolphin\docs\design\ingestion-ideas-2026-11.md`, section 6, the group "Then: retrieval
quality", with section 9. Nothing here comes from Airbyte; the rule about its source stands all the same.

Fixed points:

- **Measure first.** Run `MemoryQualityGate` over its corpus before any change and record the numbers in the report. Every change
  below is then measured the same way. A change that makes the numbers worse is not switched on by default: keep it behind its
  option, default off, and report the numbers.
- **The chunk limit becomes an option** on `DefaultMemoryIngestionChunker` (default 480, as today, so nothing changes unless set).
- **B2, tables.** In `DefaultMemoryIngestionChunker`, before the sentence split: recognise a Markdown table (a header row, a
  separator row, data rows) and a delimited block (three or more consecutive lines with the same number of tab or pipe
  separators). Cut on row boundaries within the limit; repeat the header row at the top of each piece; tag the pieces through
  `MemoryChunk.Tags` (`table:<n>` and, from the second piece, `continuation`). A row longer than the limit is one chunk on its
  own, never cut mid-row. Text that only looks like a table in one line is not a table. Add table cases to the quality corpus if
  it has none, and say so.
- **D1, step 1.** An optional `IndexText` on `MemoryChunk` (null by default): what is embedded and keyword-indexed when it is set,
  while `Content` stays what is stored and shown. The default chunker fills it with the envelope's title, then the summary when
  there is one, then the content, behind an option that is **off by default** until the measurement says otherwise. Every
  backend, the conformance suite and the retriever must use `IndexText ?? Content` for indexing and `Content` for display; list
  each place you changed. A backend that cannot hold it must say so in its capabilities rather than drop it silently.
- **D1, step 2** (the model hook) is **not** in this batch.
- **C3.** One sentence in the embedder contract's documentation: embed the index text or the content, never a metadata value.
- `MemoryChunk` is a public record: keep the change source-compatible, and list the public changes.
- Records: tick the boxes in the ideas document; update `docs/design/memory.md` where it describes chunking.

## Batch 13 (Orca): the rest of A2A phase 1

Specification: `docs/design/a2a.md`: the design sections 3 (skills), 4 (inbound tasks), 7 and 8, and in "The plan" the group
"Open: the rest of phase 1". Read `docs/briefs/a2a-sdk-facts.md` before touching the SDK edge.

In scope:

- **More than one skill** per Orca, and **the skill-selection extension** so a partner can name the skill
  (`https://opentail.net/a2a/extensions/skill-selection/v1`). With one skill configured, a message that names none is served as
  today. With several, a message that names none, or names one the caller's profile may not use, gets the generic error the
  design gives. The card lists only the skills the caller's profile may use, and declares the extension.
- **Which profiles may use an inbox skill:** a per-skill list of profiles in the A2A configuration, optional; without it, any
  profile that lists `a2a.submit`, as today. (Planner's decision, for the owner to confirm; record it in the plan.)
- **Contexts and multi-turn:** a context table (one schema bump); a continuation on a known context of the same partner is
  accepted and becomes a new task on that context; taint and labels are stored with the context and re-applied on every message;
  a continuation on another partner's context, or an unknown one, gets the same answer as an unknown task. Test: a context
  survives a session expiry with its labels.

Not in scope: JWT partners, tool skills, the extended card, signing, HTTP+JSON, push, outbound.

Fixed points: nothing a partner sends chooses a profile or a tool; every message still reaches the queue as `a2a.submit` and is
reviewed as today; the per-partner caps and the rate limit apply per message, not per context; no partner text reaches the audit.
STOP if the SDK cannot carry the extension as `a2a-sdk-facts.md` describes it.

Records: `a2a.md` (tick the plan, update the design sections and the "not built" statements), the A2A section of `STATUS.md`,
`docs/guides/a2a.md` and the sample, `a2a-history.md` (a record line), the briefs index.

## Report

One per batch, to `git-ee`, at most 40 lines, in the usual form. For batch 12, the before and after numbers of the quality gate.
