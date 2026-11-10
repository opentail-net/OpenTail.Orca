# Design: content hashing (a versioned, unambiguous identity)

**Status:** planned 2026-11-10. No code yet. Decisions marked **O-** are for the owner; the rest are the proposed defaults.
**System:** the identity function `SkillText` in `OpenTail.Modules.Skills`, and every identity computed from it in the Host and in the workbench.
**Not in this plan:** store behaviour beyond the hashes ([skill-store-integrity.md](skill-store-integrity.md)), outcome attribution, the library extraction ([agent-skills-library.md](agent-skills-library.md)). The library plan moves this code; this plan defines what it computes.
**Code basis:** `opentail-host` working tree on `standard-skill-files` (`SkillText.cs` and `SkillsModule.cs` are as committed at 6f1a59b and in `main` 6779f6b); `opentail-workbench` `progressive-disclosure`.
**Order with the other plans:** the store plan's policy-hash syntax check (its design item 4) must accept `sha256v2:` in the same release as step 4 here. The resources design ([skill-resources.md](skill-resources.md), "Hash binding") adds a resources term to the identity. Resources are hashed over their exact stored bytes, never with `SkillText.Normalise` (design item 10), so v1 normalisation cannot drop indentation in a resource. That design is updated to say so; whichever plan lands second must keep the rule. The library plan moves this code (O-L5) only after these decisions.

## Purpose

Two identities are used as evidence: that a report describes a version, and that a policy was tested against a text. Both must change when the content changes in a way a person would see, and must not collide for different content. Whitespace that carries meaning (indentation, Markdown hard breaks) is content. Line endings and Unicode composition are not.

Changing the function changes every recorded identity. So the change is versioned, and old identities stay verifiable.

## Findings checked

| # | Claim | Verdict | Evidence |
|---|---|---|---|
| H1 | Normalisation collapses spaces and tabs everywhere, so indentation and hard breaks are lost | **Confirmed** | `SkillText.cs:36-37`: runs of spaces and tabs become one space, then each line is trimmed at both ends. `if (x == 1)` followed by four spaces and `return true;` hashes the same as the unindented version. Two trailing spaces (a Markdown hard break) are removed. |
| H2 | An indentation-only edit republished under the same version is accepted | **Confirmed** | `SkillStore.cs:129-130` and `SkillsModule.cs:180-181` compare the normalised texts. Equal means no write and the label moves, so the label points at a version whose stored text is the old one. The reviewer's preview shows the diff, but the stored text does not change. |
| H3 | `ContentHash` joins fields with separators and no escaping | **Confirmed by reading** | `SkillText.cs:19-21`. Lists are joined with `\n`, fields with `\n\n`, and the joined text is then normalised, which collapses blank-line runs. So a `tools` entry containing a newline hashes like two entries, and an empty field disappears: `appliesTo: []` with `tools: ["x"]` hashes like `appliesTo: ["x"]` with `tools: []` (worked through by hand; no test reproduces either yet). |
| H4 | `ContentHash` ignores some metadata | **Confirmed** | `SkillText.cs:19-21` hashes the parsed fields, not `MetadataJson`, so other `skill.json` keys (`format`, the recorded policy hashes, any key `Parse` does not read) are outside it. Publish compares the normalised `MetadataJson` text (`SkillStore.cs:130`), so a change to such a key at the same version is refused at publish but does not change any hash or binding. That text comparison also treats JSON formatting as content where it keeps a space (`{"a":1}` differs from `{"a": 1}`) and ignores it where it trims (re-indented lines are equal). |
| H5 | Other identities use the same function | **Confirmed** | Workbench: `Formats/Scenario.cs:88` (scenario content hash), `Grading/AiGrader.cs:19` (grader version), `Formats/Policies.cs:43-45` (policy hash, used by `Formats/PolicyDrift.cs` for `policies --record` and the drift report). |
| H6 | A changed hash needs a migration | **Agreed** | Callers are listed below. |

## Contracts this must satisfy

- `Hash(text)`: `sha256:` plus the lowercase hex of SHA-256 over the UTF-8 bytes of `Normalise(text)`.
- `Normalise(text)`: Unicode NFC; CRLF and CR become LF; each line has runs of spaces and tabs (only those two characters, `[ \t]+`) collapsed to one space and is then trimmed with `string.Trim()`, which removes every Unicode white-space character at the ends (no-break space, ideographic space, form feed); a line left empty is blank; runs of blank lines become one; leading and trailing blank lines are removed. The output has no trailing newline. It is public and the workbench re-exports it (`Policies.Normalise`, `Formats/Policies.cs:45`; no caller found by grep on 2026-11-10).
- `ContentHash(version)`: `Hash` of the join of `Id`, `Version`, `Summary`, `AppliesTo`, `Tools`, the policy references, `Owner` and `Instructions`. Recorded policy hashes are excluded on purpose, so recording a policy hash does not change the version's identity. Keep that.
- `BindingLine(version)`: `skill-report: <id>@<version> <ContentHash>`.
- `Bindings(report)`: reads the first 40 lines. The pattern (not anchored to the line start) requires a skill id `[a-z0-9]+(-[a-z0-9]+)*`, a version `[1-9][0-9]{0,8}`, and `sha256:` plus 64 lowercase hex digits. A `sha256v2:` binding does not match it, so an unchanged Host reads a v2 report as "Not bound" (`SkillsModule.cs:222-223`) rather than refusing it: a silent downgrade, not an error. The pattern must accept v2 before any v2 report is written.
- `Diff(before, after)`: longest-common-subsequence over raw lines, not normalised. It is for display and is not part of any identity. Keep it.

**Callers** (a change to `Normalise` or `ContentHash` reaches each one)

| Caller | Use | Affected |
|---|---|---|
| Host `SkillsModule.cs:107` | policy drift: compares a policy file's hash with the recorded one | yes: recorded hashes stop matching |
| Host `SkillStore.cs:129-130`, `SkillsModule.cs:180-181` | republish idempotence | yes |
| Host `SkillsModule.cs:221-227` | report binding check at publish (`Bindings`, `ContentHash`, `BindingLine`) | yes: old reports fail |
| Workbench `Formats/Skill.cs:26, 29` | `ContentHash` and `BindingLine`, recorded per run and written into reports | yes |
| Workbench `Cli/Program.cs:158-160` | report binding check before proposing | yes |
| Workbench `Runs/ScenarioRunner.cs:131`, `Runs/RunStore.cs:82, 95-96` | each run stores `skill_hashes` (label to hash) and a row in `skill_snapshots` keyed by `(label, hash)`, written `INSERT OR IGNORE` | yes |
| Workbench `Runs/Comparison.cs:80-90` | compares the stored `SkillHashes` strings; warns when a label's hash changed | yes: every skill warns across the switch |
| Workbench `Runs/Comparison.cs:92-93`, `RunStore.cs:83, 98-99` | per-run policy hashes; `policy_snapshots` keyed by hash alone | yes: every policy is listed as changed across the switch |
| Workbench `Formats/Scenario.cs:88`, `Comparison.cs:75-76` | scenario content hash; comparisons warn when a scenario changed between batches | yes, if O-H3 moves it |
| Workbench `Grading/AiGrader.cs:19` | grader version label: `Hash(...)["sha256:".Length..][..8]` | yes: grades become incomparable; the slice assumes the `sha256:` prefix and would cut a `sha256v2:` value wrongly |
| Workbench `Formats/Policies.cs:43-45`, `Formats/PolicyDrift.cs` (`Check`, `Record`; `policies --record`, `Cli/Program.cs:154, 305-308`) | policy hash written into `skill.json`; drift is plain string equality (`PolicyDrift.cs:20`) | yes: a v1 recorded hash compared with a v2 live hash reads as drifted |
| Host and workbench `SkillText.Diff` (`SkillsModule.cs:205`, workbench `Tuning/Tuner.cs:204`) | display only | no |
| Tests: Host `StandardPublishTests.cs:42`, `SkillsModuleTests.cs:142, 269, 274`; workbench `ComparisonTests.cs:116-194`, `DeliveryTests.cs:145`, `FormatTests.cs:60-62` | identity assertions | yes |

## Branch-by-branch: normalisation (current, and proposed for v2)

| Input | Current (v1) | Proposed (v2) |
|---|---|---|
| CRLF or CR line endings | LF | LF (same) |
| composed and decomposed Unicode | NFC | NFC (same) |
| leading spaces or tabs on a line | removed | kept, exactly |
| runs of inner spaces or tabs | one space | kept, exactly |
| tab versus spaces | equal | different |
| trailing spaces (a Markdown hard break) | removed | kept, exactly |
| other Unicode white space (no-break space and so on) at a line's ends | removed (`Trim()`) | kept, exactly |
| other Unicode white space inside a line | kept (the collapse covers only space and tab) | kept, exactly |
| a line holding only spaces or tabs | blank | kept, exactly, inside the text; removed at the start or end (row below) |
| more than one blank line in a row | one | kept, exactly (see O-H1) |
| lines that are empty or hold only white space, at the start or end of the text | removed | removed |
| a final newline at the end of the text | removed | removed (one final newline and none are equal) |
| a field containing a newline, in a list | ambiguous with two entries | unambiguous (length-prefixed) |
| an empty field or empty list in `ContentHash` | vanishes in the blank-line collapse, so fields can shift (H3) | unambiguous (an empty string is `0:`, an empty list is a count of 0) |

## Design

1. **Two identities, both computable from stored text.** `HashV1` is the current function, unchanged, so existing values stay valid. `HashV2` is the new one, with the prefix `sha256v2:`. Both are derived from the stored text, not stored themselves, so nothing in the store is rewritten.
2. **Canonical text (v2).** NFC; CRLF and CR to LF; lines that are empty or hold only white space are removed at the start and at the end of the text (which removes the final newline). Nothing else changes: indentation, inner spacing, trailing spaces and blank lines inside the text are content. (This matches the table above; an earlier draft said only "the trailing newline run".)
3. **Unambiguous encoding (v2), at byte level.** The order of operations is fixed:
   a. Canonicalise each value on its own, with the v2 rules of design item 2. Each entry of `appliesTo`, `tools` and the policy references is canonicalised separately, so a newline inside an entry stays inside it.
   b. Take the UTF-8 byte length of each canonical value.
   c. Write the schema tag `skill-identity/2`, then each field in a fixed order: a string as its byte length, a colon and its bytes (an empty string is `0:`); a list as its item count, then each item the same way (an empty list is `0`).
   d. Hash the resulting byte sequence with SHA-256, as raw bytes, and write `sha256v2:` plus the lowercase hex.
   The completed encoding is never normalised. Normalising it after the lengths were written would make the lengths disagree with the bytes that are hashed; that is the failure this order prevents, and a test checks it.
4. **Fields in v2.** Identifier, version, summary, `appliesTo`, `tools`, policy references, owner, instructions. Whether metadata (`license`, `compatibility`, `metadata`) is included is **O-H2**. Stored metadata should be included once the library plan stores it.
5. **Policy text.** `policies --record` writes `sha256v2:` for new recordings. Both drift checks (the Host's `SkillsModule.cs:107` and the workbench's `PolicyDrift.Check`) accept either algorithm, decided by the prefix. Recorded `sha256:` values stay valid for as long as the policy text matches them. A Host that does not yet know `sha256v2:` reports every v2-recorded policy as changed, so the Host release that accepts v2 is deployed before the workbench records v2.
6. **Publish compares canonical text, not hashes.** The republish check compares the v2 canonical text of the stored and the proposed instructions. An indentation-only change at the same version is then refused, as H2 requires. `skill.json` is compared by its parsed JSON value (proposed default; **O-H6**), so formatting between tokens is not content and string values are compared exactly. This is the only change that alters what a publish accepts. It is the store plan's "identical content" row.
7. **Reports.** New reports carry the v2 binding line. The publish check, and the workbench's check before proposing (`Cli/Program.cs:158-160`), accept a binding in either algorithm, provided it matches the proposed version under that algorithm. Old reports remain checkable. The `Bindings` pattern accepts both prefixes before any v2 report is written (see the contract above).
8. **Comparisons across the change.** Checked in `RunStore.cs` on 2026-11-10: each run stores its `skill_hashes` (label to hash) and the workbench keeps snapshots in `skill_snapshots` (label, hash, metadata, instructions), keyed by `(label, hash)` and written `INSERT OR IGNORE`; policy texts are in `policy_snapshots`, keyed by hash alone. `Comparison.cs:84-90` warns when a skill's hash changed without a new version number, and `:92-93` lists policies whose hash changed. To avoid a false warning on every skill and policy, the comparison recomputes one algorithm for both batches from the snapshots rather than comparing the recorded strings. Limit: before the switch, two texts that differ only in whitespace have one v1 hash, so only the first is kept as a snapshot; a v2 identity recomputed for an old run is the v2 identity of that first text. Recomputing v1 for both batches has no such gap, and is the proposed default for cross-switch comparisons. Scenario hashes (`Comparison.cs:75-76`) have no snapshot: if O-H3 moves them, a comparison across the switch must say that scenario identity changed algorithm rather than warn that each scenario was edited.
9. **Grader version.** `AiGrader.Version` slices the hash after `"sha256:".Length`; if O-H4 moves it to v2, the slice is taken after the prefix, whatever its length.
10. **Resource digests (v2).** A resource (under `references/` or `assets/`) is hashed as SHA-256 over its exact stored UTF-8 bytes. `SkillText.Normalise` is not used on resources. The package identity includes, sorted by relative path, each resource's path and digest, encoded as in design item 3. A version with no resources contributes an empty list. The stored bytes are what the reviewer sees and what is published, so a resource whose line endings or indentation change is a different resource.

## Decisions for the owner

- **O-H1 Blank-line runs.** Keep them exactly (recommended: a code block can depend on them) or collapse them as today (which keeps the "cosmetic" meaning of blank lines)?
- **O-H2 Preserved frontmatter in identity.** Include `license`, `compatibility`, `metadata` and `allowed-tools` in the v2 identity once they are stored (recommended: all four, so a change to any preserved field changes the identity and cannot evade a report binding; `allowed-tools` is never acted on, but a reviewer should still see that it changed), or not? None of the four is stored today (`StandardPublish.cs:46-56` warns that each is dropped), so none can be in an identity yet; the library plan's preservation step is what makes them available.
- **O-H3 Scenario identity.** Move scenario content hashes to v2 (recommended: YAML indentation changes meaning, so a v1 scenario hash can describe different scenarios), or keep v1?
- **O-H4 Grader version.** Move `AiGrader.Version` to v2 and label the change (recommended), or keep v1 so existing grades stay comparable?
- **O-H5 Switch date.** Which release starts writing v2 identities into new records? Recommended: the same release as the republish comparison change (design item 6, work order step 3), so a whitespace change is never accepted under v1 after v2 is in use; and the workbench writes v2 only after the Host that reads it is deployed.
- **O-H6 Comparing `skill.json` at republish.** Compare the parsed JSON value (recommended: formatting is not content, values are exact; `policies --record` rewrites the file indented, `PolicyDrift.cs:41`), or the v2 canonical text (stricter: a re-indented `skill.json` at the same version is refused, where today it is accepted)?

## Tests

Golden vectors first, from the current code, so the change cannot silently move v1:

- `V1_identities_of_the_three_repository_skills_are_unchanged`: `ContentHash` of `invoice-check`, `email-triage` and `reply-draft` at their current versions, and the policy hashes, equal the values recorded before the change.

Then the new behaviour:

- `V2_is_sensitive_to_indentation`: `if (x == 1)` with four spaces before `return` differs from the unindented text.
- `V2_is_sensitive_to_a_markdown_hard_break`: a line ending in two spaces differs from the same line without them.
- `V2_ignores_line_endings_and_composition`: CRLF and LF, and NFC and NFD forms, hash the same.
- `V2_does_not_collide_on_list_boundaries`: `tools` `["x\ny"]` and `["x", "y"]` differ.
- `V2_does_not_collide_when_an_entry_moves_between_empty_fields`: `appliesTo: []`, `tools: ["x"]` and `appliesTo: ["x"]`, `tools: []` differ (and a v1 golden test records that they are equal today).
- `V2_keeps_unicode_white_space_at_line_ends`, `V2_removes_white_space_only_lines_at_start_and_end` (normalisation table rows).
- `A_v2_policy_hash_is_checked_by_both_drift_checks` (Host and workbench).
- `The_grader_version_is_eight_hex_digits_under_either_algorithm` (if O-H4 moves it).
- `V2_keeps_blank_line_runs` (if O-H1 keeps them).
- `A_republish_differing_only_in_indentation_is_refused_at_preview_and_at_publish` (H2).
- `A_v1_binding_still_verifies_the_version_it_was_made_for` and `A_v2_binding_verifies_the_version_it_was_made_for`.
- `A_binding_for_other_content_is_refused_in_both_algorithms`.
- `Comparison_of_a_v1_batch_with_a_v2_batch_warns_about_no_skill_change`.
- `A_scenario_whose_yaml_indentation_changes_gets_a_new_identity` (if O-H3 moves it).
- `V2_encoding_lengths_match_the_hashed_bytes` (design item 3: the length written for each value equals the bytes hashed for it, including for a value with a Unicode composition change).
- `A_resource_digest_is_over_exact_bytes` (design item 10: a change in indentation or line endings changes the digest; the same text in another line-ending style does too, because the bytes differ).
- `A_changed_preserved_field_changes_the_v2_identity`, for each of `license`, `compatibility`, `metadata` and `allowed-tools` (O-H2), once they are stored.

## Work order

1. Golden vectors for v1, and the failing v2 tests.
2. `HashV2` and the canonical text, with no caller changed.
3. The republish comparison (design item 6), with its tests. This is the first behaviour change; it is released with step 4.
4. Bindings and drift accept both algorithms (Host and workbench, and the store plan's hash syntax check); this release is deployed first. New reports and new recordings use v2 from the switch date (O-H5), never before.
5. Workbench: comparison recomputes identities from snapshots; scenario and grader identities as decided.
6. Documentation: the Host module README's "Identity: hashes, bindings and drift" section (`OpenTail.Modules.Skills/README.md:145-149`, and `:169`, which says to treat these functions as frozen) and the workbench's `docs/formats.md` (its "Hashing" paragraph at line 54 describes v1 rules and must say which algorithm is written when).

## Acceptance

- The golden v1 vectors pass before and after the change.
- No existing report, policy hash or run record fails verification.
- Every case in the normalisation table has a test.
- A batch recorded before the switch and one recorded after compare without a spurious warning.

## Not built

Nothing in this plan is built. Step 1 (golden vectors) needs no decision. Steps 2 to 6 wait for O-H1 to O-H6.
