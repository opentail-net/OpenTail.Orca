# Design: skill store integrity and discovery

**Status:** planned 2026-11-10. No code yet. Decisions marked **O-** are for the owner; the rest are the proposed defaults.
**System:** the Host's skill store and the tools that read it: `OpenTail.Modules.Skills` (`SkillStore`, `SkillsModule`).
**Not in this plan:** the content hash ([skill-content-hashing.md](skill-content-hashing.md)), outcome attribution ([skill-outcome-attribution.md](skill-outcome-attribution.md)), extracting the standard into a library ([agent-skills-library.md](agent-skills-library.md)), and resources ([skill-resources.md](skill-resources.md)).
**Code basis:** `opentail-host` on `standard-skill-files` at 4b8a8b7 (committed; not in `main`). The strict-type changes in `SkillStore.cs` were uncommitted when this plan was first written and are in that commit. Where `main` (6779f6b, which is `origin/main`; the local `main` ref is older, 6081d5b) differs, it is noted. Checked 2026-11-10: within the skills module, 4b8a8b7 differs from 6f1a59b only in `SkillStore.cs` and the module README; `SkillsModule.cs` and `Standard/` are unchanged.
**Order with the other plans:** this plan changes the store in place, in the Host, first. The library plan does not move the store (O-L4: the Host keeps it). It adds optional `skill.json` keys (O-L3), which this store reads and writes, and it moves the identity code (O-L5), as changed by the hashing plan (this plan does not change it). The hashing plan changes the republish comparison (its design item 6) and adds the `sha256v2:` policy-hash prefix; design item 4 below must accept whatever prefixes the hashing plan has introduced by then.

## Purpose

A damaged store fails one item at a time. One bad label, one bad version or one unreadable file must not hide the others, and nothing damaged may be served. What a failed publish leaves behind is defined in the next section. It is deliberately not all-or-nothing, because the move of the version folder and the replacement of `labels.json` are separate filesystem operations.

## The publish guarantee

1. **A failed publish never moves a label.** Whatever fails, the label is either the one it was, or the one a successful publish set.
2. **A handled failure leaves no temporary file, no temporary folder and no new version folder.** Temporary items are removed in a `finally`. A version folder created by this publish is removed if the label write does not complete. Guarantee 2 is what the tests check.
3. **An unhandled failure (a crash or a kill) may leave a new version folder with no label, and may leave a temporary folder (`<version>.tmp-<guid>`) or `labels.json.tmp`.** The temporary items are never read (the folder name is not a version number; the next label write overwrites the file), and the store check reports them. The version folder is immutable, it is never served (no label points at it), a later publish of the same version with the same content is a no-op that labels it, and the store check reports it as unlabelled.
4. **Nothing is promised across the two filesystem operations** (the folder move and the labels replacement). The design does not claim all-or-nothing behaviour across them.

## Findings checked

Each claim from the external review was checked against the code on 2026-11-10.

| # | Claim | Verdict | Evidence |
|---|---|---|---|
| S1 | One damaged labels entry can take down discovery | **Confirmed** | `SkillsModule.cs:62` calls `ReadLabels()` outside the `try`. `SkillStore.cs:39` throws `JsonException` on malformed JSON, and `InvalidOperationException` when the root is valid JSON but not an object (the `["skills"]` indexer on an array or a value; .NET behaviour, to confirm in the first test). `SkillStore.cs:41` `l.Value!.ToString()` throws `NullReferenceException` on a null label. `SkillStore.cs:40-41` silently treats a non-object skill entry as empty. Because `ReadLabels` builds the whole map, one bad entry breaks every caller, including every publish (`SkillStore.cs:119` through `LabelledVersion`). |
| S2 | Discovery isolates damaged versions only for `ArgumentException` | **Confirmed** | `SkillsModule.cs:70` catches `ArgumentException` only. `SkillStore.cs:54` `File.ReadAllText` can throw `IOException` or `UnauthorizedAccessException` (not an `IOException`), and both escape `Find`. |
| S3 | Malformed skill JSON is not handled consistently | **Partly fixed** | Commit 4b8a8b7 checks every field's type (`SkillStore.cs:58-104`). On `main`, fields accepted numbers. Still open: `List` accepts blank strings in `appliesTo` and `tools` (`SkillStore.cs:75-77`); `appliesTo` is checked only at publish (through `StandardSkill.Describe`), not when read; `policies[].hash` is type-checked but not checked for `sha256:` syntax (`SkillStore.cs:86-88`). |
| S4 | Publish leaves temporary folders and files after a failure | **Confirmed** | `SkillStore.cs:136-141` creates `<dir>.tmp-<guid>` with no `try/finally`. `SkillStore.cs:151-153` writes `labels.json.tmp` the same way. |
| S5 | Policy references resolve by file name only | **Confirmed, medium** | `SkillsModule.cs:105` uses `Path.GetFileName(p.Ref)`. This is not a traversal (the name is joined to `PolicyFolder`), but two references with the same file name read one file, so the drift verdict can be wrong. Nothing detects it. |
| S6 | Backup covers the SQLite database but not the store | **Partly** | `docs/guides/skills.md:82` says to back up the store with the database and to copy it when no approval is running. `OPERATIONS.md:233` covers the database. The two are not one procedure, and no restore has been tested. |
| S7 | The cross-process lock needs a proper test | **Partly** | `SkillsModuleTests.cs:374` tests a lock held in-process (`FileShare.None`). `:414` has a child PowerShell process hold the lock. `:420` skips that test when PowerShell is absent, so on such a machine it asserts nothing. |
| S8 | No test covers a corrupt labels file | **Confirmed** | The only test that writes `labels.json` by hand (`SkillsModuleTests.cs:360`, inside the format test at 353) writes valid JSON. |
| S9 | Publish can overwrite damaged labels and lose other labels (found in this review, not in the external one) | **Confirmed by reading** | `SkillStore.cs:147-149`: a root that is not an object, a `skills` value that is not an object, or a skill entry that is not an object is replaced by a new empty object, and the file is rewritten. With `expectedFrom` given, a non-object root fails earlier at 119; a non-object `skills` or skill entry reads as "no label", so `expectedFrom: "none"` passes and the entry, with any other label it held, is overwritten. With `expectedFrom` null (the workbench's `ScenarioRunner.cs:144` and several tests call it so), malformed JSON is found only at 147, after the version folder has been moved into place. |

## Contracts this must satisfy

**`SkillStore`** (public members, and who calls them)

- `Root`, `IsId`, `IsVersion`, `Labels` (`production`, `staging`), `NoVersion` (`"none"`), `SupportedFormat` (`1`).
- `ReadLabels()` returns the label map per skill id. It promises an empty map when the file is absent. Today it also throws on malformed content (S1). Callers (grep, 2026-11-10): `SkillsModule.cs:62` (`Find`) and `LabelledVersion`; nothing outside the module. Design item 1 changes its return type; no outside caller breaks.
- `LabelledVersion(id, label)` returns the version or null. Callers: `SkillsModule.cs:97` (`skills.get`), `:137` (the `Publish` tool, outside the lock), `:186` (preview); `SkillStore.cs:119, 121` (publish, under the lock); tests.
- `Get(id, version)` returns a `SkillVersion`, or null when the id or version is not valid or either file is absent, and throws `ArgumentException` when they are damaged. The promise is broken for I/O and access errors (S2). Callers: `SkillsModule.cs:68` (`Find`), `:98` (`skills.get`), `:140` (the `Publish` tool decides `isNewVersion`, outside the lock), `:169`, `:190` (preview); `SkillStore.cs:122` (publish, under the lock); tests.
- `Parse(metadataJson, instructions, id, version)` throws `ArgumentException` for each refusal. Callers: `SkillStore.Get`, `SkillStore.Publish`, `StandardPublish.Prepare` (`StandardPublish.cs:21`); tests in both repositories (`SkillsModuleTests.cs:265-266`; workbench `ComparisonTests.cs:193`).
- `Publish(id, version, label, metadataJson, instructions, expectedFrom)`, under `.publish.lock`: content equal after `SkillText.Normalise` is a no-op; different content for an existing version is refused; the label moves only if `expectedFrom` still matches; with `expectedFrom` null the label is not checked at all. Callers: `SkillsModule.cs:142`; the workbench's `ScenarioRunner.PublishToTemporaryStore` (`ScenarioRunner.cs:144`, its own temporary store, `expectedFrom` null); tests (`SkillOutcomesTests`, `ShadowTests`, `ReviewOutcomeExportTests`, `SkillsModuleTests`).
- `TakeStoreLock` (`SkillStore.cs:162-174`; called only by `Publish`): opens `.publish.lock` without waiting, and reports any `IOException` as "busy". The publish table changes its message and its handling of access errors; O-S4 would change its waiting.
- `SkillVersion` and `SkillPolicy` records. Callers: `SkillText`, `SkillsModule`, `StandardPublish`, and the workbench through `Skill.AsVersion()`.

**`SkillsModule` tools**

- `skills.find` (Read): returns `label`, `skills`, `warnings` (always present) and `next`. Agents read `warnings`.
- `skills.get` (Read, audited as `skill_served`): refuses a missing or damaged version with `ArgumentException` or `InvalidOperationException`.
- `skills.publish` (approval): the preview runs the same `Prepare`, `Parse` and republish comparison the store runs, and the label check; execution checks again. The preview does not take the lock and does not see an incomplete version folder (see the publish table), so "checks everything" is not true today.

## Branch-by-branch: discovery (`skills.find`)

| Case | Today | Required |
|---|---|---|
| labels file absent | empty list, no warning | unchanged |
| labels file is empty or not JSON | `JsonException` escapes, so the whole call fails (`SkillStore.cs:39`, called at `SkillsModule.cs:62`) | warning naming `labels.json`; empty list; no label served from it |
| labels root is valid JSON but not an object (array, string, number) | `InvalidOperationException` escapes (indexer at `SkillStore.cs:39`) | as above |
| labels root is the JSON literal `null` | treated as empty, silently (`?.` at `:39`) | warning naming `labels.json`; empty list |
| labels file unreadable (`IOException`, `UnauthorizedAccessException`) | escapes | warning naming `labels.json` and the reason; empty list |
| `skills` in the labels file is absent or not an object | treated as empty, silently | absent: unchanged (empty list); not an object: warning naming `labels.json`, empty list |
| one skill entry is not an object | silently skipped | warning naming the skill; other skills served |
| a skill key is not a valid id (for example `../x`) | `Get` returns null (`SkillStore.cs:50`), reported as "files are missing" (misleading) | warning "not a skill id"; not served |
| a skill has labels, but none for the served label | skipped, no warning (`SkillsModule.cs:64`) | unchanged |
| a label value is null | `NullReferenceException` escapes (`SkillStore.cs:41`) | warning naming skill and label; other labels and skills served |
| a label value is a number, boolean, object or array | converted silently by `ToString()` (`1` becomes `"1"`, an object becomes its JSON text) | warning; not served (labels must be JSON strings) |
| a label value is a string but not a version number | `Get` returns null, reported as "files are missing" (misleading) | warning "not a version number"; not served |
| the `format` key of `labels.json` | not read at all | unchanged in this plan (noted: a future labels format is not detected) |
| version folder missing, or only one of `skill.json` and `SKILL.md` present | warning "files are missing" (`SkillStore.cs:54` returns null) | unchanged |
| `skill.json` or `SKILL.md` damaged (`ArgumentException`) | warning | unchanged |
| `skill.json` or `SKILL.md` unreadable (`IOException`, `UnauthorizedAccessException`) | escapes; the whole call fails | warning naming the version and the reason; others served |
| `SKILL.md` is not valid UTF-8 | read with replacement characters, served (`File.ReadAllText`) | unchanged in this plan (the resources design refuses invalid UTF-8 for resources; the same rule for `SKILL.md` is a later decision) |
| version in another format | warning | unchanged |
| a label other than production or staging is present | ignored | unchanged |

## Branch-by-branch: `skills.get`

| Case | Today | Required |
|---|---|---|
| no label for the id | `ArgumentException`, "No published skill" | unchanged |
| id is not a valid id | same message (`SkillsModule.cs:97`, `LabelledVersion` not called) | unchanged |
| labels file malformed, or this skill's entry damaged | unhandled exception from `LabelledVersion` (`:97`) | `ArgumentException` naming `labels.json` (and the skill) |
| label value is not a version number | `InvalidOperationException` "files are missing" (misleading) | `ArgumentException` "not a version number" |
| files missing | `InvalidOperationException` | unchanged |
| files damaged | `ArgumentException` | unchanged |
| files unreadable (`IOException`, `UnauthorizedAccessException`) | unhandled | refused with the reason |
| `PolicyFolder` not set, or a policy has no recorded hash | no drift check for it, no warning (`:104`) | unchanged |
| policy file missing | warning | unchanged |
| policy file present, hash differs | warning | unchanged (the hashing plan adds: a `sha256v2:` hash is compared with the v2 function) |
| policy file unreadable | `IOException` escapes from `File.ReadAllText` (`:107`); the skill is not served | warning naming the policy and the reason; the skill is served |
| policy ref ends in `/` (empty file name) | `Path.Combine` gives the folder itself; `File.Exists` is false; "not found" warning | unchanged |
| two policy references in one skill share a file name | one file is read for both | warning naming both references; drift is not judged for them (see O-S1) |

## Branch-by-branch: publish

| Case | Today | Required |
|---|---|---|
| invalid id, version or label | `ArgumentException` before the lock (`SkillStore.cs:114`) | unchanged |
| lock held by another process | `InvalidOperationException` "busy", nothing written (`:169-172`) | unchanged |
| lock file cannot be opened for another I/O reason | same "busy" message (any `IOException` is caught at `:169`); `UnauthorizedAccessException` escapes | the message names the reason; access errors refused the same way |
| labels file malformed, through the tool | exception from `SkillsModule.cs:137` (`LabelledVersion`), before the lock; the preview fails the same way at `:186` | refused before any write, `ArgumentException` naming `labels.json` |
| labels file malformed, through the store with `expectedFrom` given | exception from `SkillStore.cs:119`, under the lock, before any write | as above |
| labels file malformed, through the store with `expectedFrom` null | the version folder is written and moved into place (`:135-141`); then `:147` throws; the version stays, the label does not move | refused before any write |
| labels root, `skills`, or this skill's entry not an object (S9) | replaced by an empty object at `:147-149`; other labels in it are lost | root or `skills`: refused (design item 2); this skill's entry: refused for this skill only |
| a label value is null anywhere in the file | `NullReferenceException` from `ReadLabels` at `:119` (with `expectedFrom`); with `expectedFrom` null the file is not read through `ReadLabels`, so a null for this label is overwritten at `:150` and one elsewhere is written back as it was | refused with a message naming the skill and label; for another skill's entry, see design item 2 |
| this skill's label value is not a string (a number, boolean, object or array) | with `expectedFrom`: compared as its `ToString()` text (`SkillStore.cs:41`), so `1` matches `expectedFrom: "1"`; then overwritten at `:150`. With `expectedFrom` null: overwritten | refused for this skill (a damaged entry, design item 2); repair is O-S6 |
| labels file absent when the label is written | created as `{ "format": 1, "skills": { ... } }` (`:147`) | unchanged |
| through the tool, `expectedFrom` omitted | `ArgumentException` "expectedFrom is required" (`SkillsModule.cs:142`), raised after `LabelledVersion` (`:137`) and `Prepare` (`:140`) have run, before the store is called; nothing written | unchanged |
| through the tool, `StandardPublish.Prepare` refuses the content | raised at `SkillsModule.cs:140`, before the store's lock; nothing written (exception type not checked for this plan) | unchanged |
| `expectedFrom` does not match | `InvalidOperationException` under the lock | unchanged |
| only one of `metadataJson` and `instructions` given | `ArgumentException` (`:125`) | unchanged |
| new content fails `Parse` | `ArgumentException`, nothing written (`:126`) | unchanged |
| the existing version is damaged | `ArgumentException` from `Get` at `:122` | unchanged |
| the existing version is unreadable | `IOException` escapes from `Get` | refused with the reason |
| identical content (equal after `SkillText.Normalise`) | no write; label moves | unchanged here; the hashing plan (design item 6) changes the comparison, so "identical" will mean equal under its rule |
| different content for an existing version | refused | unchanged (the hashing plan narrows what counts as equal) |
| no content, version exists (a rollback) | label moves | unchanged |
| no content, version does not exist | `ArgumentException` (`:144`) | unchanged |
| failure after the temporary version folder is created (a write fails) | folder left behind | folder removed; label unchanged |
| the version folder exists but is incomplete (`Get` returned null because one file is missing), so `Directory.Move` fails | `IOException`; temporary folder left; the publish is refused with an I/O error, not an `ArgumentException` | temporary folder removed; refused with a message naming the incomplete folder (the store check reports it) |
| the version folder appears between `Get` and the move | not possible between Host publishes (both are under the lock); possible only from another writer | covered by the row above |
| failure while writing `labels.json.tmp`, or in the final `File.Move` | file left behind; the version folder stays (versions are immutable; a version with no label is not served) | handled failure: temporary file removed, label unchanged, and the new version folder removed (guarantee 2); unhandled failure (a crash): the folder stays unlabelled, is never served, and the store check reports it (guarantee 3) |

## Branch-by-branch: preview (the `skills.publish` preview)

The preview is what the reviewer approves. It is a snapshot: the store may change before execution, so execution checks again under the lock (the publish table above). The preview must also be read-only.

| Preview condition | Required behaviour | Today |
|---|---|---|
| malformed labels file | refuse the preview; name the problem | the preview calls `LabelledVersion` (`SkillsModule.cs:186`), which throws; the reason is not shaped for the reviewer (exception type to verify) |
| a damaged skill label entry (null, or not a version) | refuse the preview; do not assume there is no label | `ReadLabels` throws for a null (`SkillStore.cs:41`); a non-version string reads as "files are missing" |
| the current label points at a missing or incomplete version | refuse, and identify the broken version | the preview reports "first publication" (`SkillsModule.cs:190-193`), because `Get` returns null for the label's version; the diff and the wording misstate the change |
| the target version folder exists but is incomplete | refuse; do not treat it as new | `Get` returns null (`:169`), so the preview treats the version as new; the later move cannot succeed |
| the target version is unreadable or corrupt | refuse, with the reason | `Get` throws `ArgumentException` (refused) or `IOException` (escapes, a gap noted above) |
| `expectedFrom` missing or stale | refuse the preview | refused (`:187-189`) |
| the existing version has different content | refuse the preview | refused (`:180-182`) |
| valid first publication | an accurate first-publication preview | shown |
| valid update or rollback | an accurate before and after diff | shown; `kind` from the version numbers (`:192-193`) |
| the label moves after the preview and before execution | execution refuses under the store lock; nothing is published | the store rechecks `expectedFrom` under the lock (`SkillStore.cs:119`); a test must prove it |
| the preview itself | writes nothing: no file, no folder, no label | takes no lock; a test must prove nothing is written |

Tests (new): `A_preview_with_a_label_pointing_at_a_missing_version_is_refused_and_names_it`; `A_preview_with_an_incomplete_target_folder_is_refused_not_shown_as_new`; `A_preview_with_a_stale_expectedFrom_is_refused`; `A_label_that_moves_after_the_preview_is_refused_at_execution_and_nothing_is_published`; `A_preview_writes_nothing`.

## Design

1. **Labels are read as a result, not an exception.** `ReadLabels` returns the valid labels together with problems, each naming the file, the skill and the label where known. Callers decide: `Find` and `Get` report them; publish handles them as below. `LabelledVersion` keeps its signature and throws `ArgumentException` naming `labels.json` when the file, or this skill's entry or label, is damaged.
2. **Publish fails closed on the file, and per skill on an entry.** A malformed `labels.json` (not JSON, root or `skills` not an object) refuses every publish, because the current state of any label is unknown. A damaged entry refuses publishes for that skill only, because its label cannot be checked against `expectedFrom`. The labels are read before any version folder is written, whether or not `expectedFrom` is given, and the write at `SkillStore.cs:147-149` never replaces a damaged value: it refuses instead. A publish therefore cannot repair a damaged file or entry; how it is repaired is **O-S6**.
3. **Each skill is isolated.** `Find` wraps each skill (not each version) and catches the explicit set `ArgumentException`, `IOException`, `UnauthorizedAccessException` and `JsonException`. Each caught case becomes a warning. Discovery never throws for stored content; only programming errors propagate.
4. **Values are checked when read.** Each `appliesTo` and `tools` entry must be a non-empty string. A policy hash must be null or a known algorithm prefix followed by 64 lowercase hex digits: `sha256:` today, and `sha256v2:` once the hashing plan's step 4 is built (that plan's design item 5). The two plans must land this rule together; a reader that accepts only `sha256:` would report every skill recorded with v2 as damaged. A version that fails is reported as damaged, with the reason. Publish applies the same checks (in `Parse`, so the workbench's direct `SkillStore.Publish` is covered too), so a damaged version cannot be created.
5. **Publish cleans up.** The temporary folder and temporary labels file are removed in a `finally` when anything after their creation fails. The final move happens only after every file is complete. An incomplete version folder already in place is not overwritten or repaired by a publish; it is refused and named. If the label write fails after this publish moved its version folder into place, the new folder is removed when the failure is handled; if the process dies, it stays unlabelled (guarantee 3).
6. **Policy references.** Detect a clash of file names within one skill, and warn. Whether references should resolve by relative path instead is **O-S1**. The existing references all have the form `policies/<name>.md`, so the resolution rule decides what `PolicyFolder` must contain. Changing it is not part of the first step.
7. **Store check.** A command that scans a store and lists every problem the read path would report, without serving anything. It also lists what the read path never sees: version folders with no label, incomplete version folders, and leftover temporary folders and `labels.json.tmp` (guarantee 3). It is run before a release with the stricter reading (design items 1 to 4) is deployed on an existing store (work order step 3, O-S3). Where the command lives is not fixed: the admin command line is the candidate, and its contract is read and added to "Contracts" before step 3 starts.
8. **Backup.** Two kinds of writer change the state a backup must capture: skill-store publishes, which take `.publish.lock`, and every Host database write, which the lock does not cover. So the procedure is one of these:
   - **Option A (recommended): quiesce the Host.** Stop the Host, copy the store folder, take the database snapshot (`sqlite3 queue.db ".backup copy.db"`, from `OPERATIONS.md:233`), and restart. The two copies are consistent with each other. (To verify: that the Host has no pause mode, so a stop is the only quiesce.)
   - **Option B (weaker, and labelled as such): keep the Host running.** Copy the store while holding `.publish.lock`, then take the database snapshot. An action approved between the two copies can be in one copy and not the other. The restore must reconcile that from the audit trail (to verify that the audit trail identifies it).
   The restore note records the option, the times and both copies. Facts the procedure must state (from `SkillStore.cs:162-174`): the lock is taken without waiting, so an approved publish that executes while it is held fails with "the skill store is busy" and must be resubmitted; the lock is advisory on Unix and binds only processes that take it; on Windows the held `.publish.lock` cannot be copied, so the copy excludes it. A restore drill is run once, dated, and repeated when the procedure changes.
9. **Lock test without PowerShell.** The cross-process test starts the test assembly itself in a helper mode (or a small helper console project, referenced by the test project), so it does not depend on PowerShell and cannot skip. The in-process test stays.

## Decisions for the owner

- **O-S1 Policy references.** Keep file-name resolution (with a clash warning), or resolve references relative to `PolicyFolder` and refuse `..`? Recommended: decide after checking how the production deployment lays out `PolicyFolder`; the first step does not change it.
- **O-S2 Refusing publishes on a damaged labels file.** Recommended: refuse as in design item 2.
- **O-S3 Existing stores.** Run the store check on the production store before enabling the stricter read checks. Recommended: yes, and fix or report what it finds first.
- **O-S4 Backup window (Option B only).** Option B holds the publish lock while the store is copied, so a publish approved during that copy fails and must be resubmitted. Accept that (recommended: the copy is short and the failure says "resubmit"), or make publish wait for the lock with a timeout (a behaviour change to `TakeStoreLock`)?
- **O-S5 Backup consistency.** Quiesce the Host for each backup (Option A, recommended), or accept Option B's window, which the restore must reconcile from the audit trail?
- **O-S6 Repairing a damaged labels file or entry.** With design item 2, no publish can overwrite damage, so something else must repair it. Recommended: by hand, with the Host stopped, following a short procedure in `docs/guides/skills.md` (the audit trail of approved publishes says where each label should point; to verify that it does), then the store check. A repair command is not part of the first step.

## Tests (new, written before the change)

- `A_malformed_labels_file_is_named_by_find_and_nothing_is_served_from_it` (S1, S8)
- `A_null_label_names_that_skill_and_serves_the_others` (S1)
- `A_skill_entry_that_is_not_an_object_is_named` (S1)
- `A_numeric_label_value_is_refused_and_named` (S1)
- `A_version_whose_files_cannot_be_read_does_not_hide_the_others` (S2; the test holds `skill.json` open exclusively, and releases it in a `finally`)
- `A_blank_applies_to_or_tools_entry_is_named_when_read` (S3)
- `A_policy_hash_that_is_not_sha256_syntax_is_named_when_read_and_refused_at_publish` (S3)
- `A_failed_publish_after_the_folder_is_created_leaves_no_folder_and_no_label_change` (S4; the test makes `labels.json` a directory, so the final write fails)
- `A_publish_is_refused_before_any_write_when_labels_is_malformed` (S1, S9, publish table; run through the tool, through the store with `expectedFrom`, and through the store with `expectedFrom` null)
- `A_publish_never_replaces_a_damaged_labels_entry_and_other_labels_survive` (S9)
- `A_publish_into_an_incomplete_version_folder_is_refused_and_leaves_no_temporary_folder` (publish table)
- `A_handled_label_failure_removes_the_new_version_folder_and_moves_no_label` (guarantee 2)
- `An_unlabelled_version_folder_is_never_served_and_the_store_check_reports_it` (guarantee 3; the test leaves a folder as a crash would)
- `A_label_that_is_not_a_version_number_is_named_as_such_by_find_and_get`
- `An_unreadable_policy_file_is_a_warning_not_a_failure_of_get`
- `Two_policy_references_with_one_file_name_are_named` (S5)
- `The_store_check_lists_every_problem_and_serves_nothing` (design item 7)
- `The_lock_is_tested_across_processes_without_skipping` (S7)
- Restore drill, once, by hand, recorded with a date in `docs/guides/skills.md`.

## Work order

1. Write the failing tests above, including the branch-table rows that have no test today.
2. Implement design items 1–5, then 9 (the lock test).
3. Implement the store check (item 7) and run it against the repository's three skills and, with the owner, the production store. Steps 2 and 3 are done in this order and released together: the stricter reading of step 2 is not deployed on an existing store before the check has run on it (O-S3).
4. Update the backup section (item 8) and run the restore drill.
5. Update `OpenTail.Modules.Skills/README.md` (the known limits and the invariants table).

## Acceptance

- Every row in the three branch tables has a test or a stated reason it needs none.
- The current Host test suite passes before the change starts, and after it, with the new tests included. (The count is in `skills-plan.md`; re-run it, do not copy it.)
- Discovery returns a warning, never an exception, for every damage listed in the discovery table.
- A failed publish never moves a label. After a handled failure no temporary file, temporary folder or new version folder remains. After an unhandled failure a new version folder may remain, unlabelled, and so may a temporary folder or `labels.json.tmp`: none is served, and the store check reports each. The tests cover both cases.

## Not built

Nothing in this plan is built. Design item 2 waits for O-S2, and for O-S6 (the repair procedure must exist before publishes start refusing damage). Design item 6 waits for O-S1 (the clash warning alone does not). Item 7 (the store check) needs no decision to build; running it on the production store waits for O-S3. Item 8 waits for O-S4 and O-S5. Nothing in this plan changes the hash or the stored format. Two rows depend on the hashing plan: the republish comparison ("identical content") and the policy-hash syntax in design item 4.
