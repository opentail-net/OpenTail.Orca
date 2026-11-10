# OpenTail.Modules.Skills

The Host module that serves a business's **skills** to its agents. A skill's live version changes only through approval.

A **skill** is a versioned procedure for one kind of situation, such as "an invoice arrived by email". It holds instructions for the agent, the situations it applies to, and the policies it reads. A skill guides the agent. It never changes what policy allows: a tool a skill names is still governed by its own policy entry.

Written from reading `opentail-host` on 2026-11-10, after commit `1464f70`. Where a statement comes from a design document rather than from the code, the text says so.

**Read next:** [setup, publishing and operations](../docs/guides/skills.md) · [writing a skill version](../docs/guides/writing-skill-versions.md)

## Status

| Part | State |
|---|---|
| `skills.find`, `skills.get`, `skills.publish` with approval, rollback, policy drift warning, `skill_served` audit | Built. Tests in `OpenTail.Host.Tests/SkillsModuleTests.cs` |
| Publish checks: `expectedFrom`, report binding, no edits to a publish | Built 2026-10-18 ([decisions](../docs/design/skills-publish-questions.md)) |
| Standard `SKILL.md` form: reader, validator, formatter, publish check | Built in `dc957f7`. Decisions 3, 4 and 7 are provisional and await the owner's review ([design](../docs/design/standard-skill-files.md)) |
| `name` and `description` in `skills.get` (standard phase 3) | Not built. Optional |
| Authoring skills in the standard form (standard phase 4) | Not designed. Only if the owner decides |

## Files in this folder

| File | Holds |
|---|---|
| `SkillsModule.cs` | The three tools, their arguments, the publish preview (the change request), what `skills.get` returns |
| `SkillsModuleFactory.cs` | The `"Type": "skills"` factory: config, startup checks, the suggested policy, server instructions |
| `SkillStore.cs` | The store on disk: versions, labels, the write path, the id and version patterns |
| `SkillText.cs` | Normalisation, hashes, the content hash, the report binding line, the line diff |
| `Standard/StandardSkill.cs` | Reader, validator, description and formatter for the standard `SKILL.md` form |
| `Standard/StandardPublish.cs` | What a publish's instructions become: plain text or standard frontmatter |
| `HostContract.AssemblyInfo.cs` | Declares that the module is built against Host contract 1 |
| `OpenTail.Modules.Skills.csproj` | The project. YamlDotNet is its only package, pinned at 17.1.0 in `Directory.Packages.props` (17.1.0 is the minimum the Connectivity packages need) |

The module also relies on code outside this folder:
- [`OpenTail.Host/Runtime/SkillOutcomes.cs`](../OpenTail.Host/Runtime/SkillOutcomes.cs) reads the `skill_served` audit and the decisions after it, for `--skill-outcomes`.
- Contract members from [PACKAGING.md](../OpenTail.Host/PACKAGING.md): `AuditResult`, `ServerInstructions`, `Administers`.
- The fixed rules for administration tools, which `skills.publish` must meet or the Host refuses to start: [POLICY.md](../OpenTail.Host/POLICY.md#administration-tools).

## Configuration

In `modules.json`:

```json
{ "Modules": [
    { "Type": "skills", "Id": "skills",
      "Config": { "Store": "skills-store", "Label": "production", "PolicyFolder": "policies" } }
] }
```

| Setting | Default | Meaning |
|---|---|---|
| `Store` | required | The store folder. Created at startup if missing. Relative paths are relative to `modules.json`. |
| `Label` | `production` | The label that `skills.find` and `skills.get` serve: `production` or `staging`. Any other value stops startup. |
| `PolicyFolder` | none | Folder of the live policy files that skills read. Must exist, or startup fails. Setting it turns on the drift warning. |
| `AllowPublish` | `true` | `false` does not register `skills.publish`, so the deployment only serves skills. |

Use the id `skills`. The factory's notes say why: skill text and the server instructions refer to `skills.find` by that name. [MODULES.md](../OpenTail.Host/MODULES.md) describes the rest of the file format.

**The suggested policy** (`--adopt-policy skills` writes it to `policy.d/skills.json`):

- `find` and `get`: `Execute`.
- `publish`: `Queue`, `Reviewer: Human`, `ReviewerGroup: skill-approvers`, `SelfApproval: Forbid`, `AllowEdits: false`, `ExpireAfter: 7.00:00:00`. A rule requires **two** approvals unless the label is `staging`.

If the policy for `skills.publish` is looser than this, the Host refuses to start and names what is missing. Test: `Publish_is_an_administration_tool_and_a_loose_policy_for_it_stops_startup`.

## The store

```
<Store>/
  labels.json                  which version each label points at; the only file that moves
  invoice-check/
    1/
      skill.json               metadata, as sent
      SKILL.md                 instructions, as sent (or the body of a standard file)
    2/
      skill.json
      SKILL.md
```

```json
{ "format": 1, "skills": { "invoice-check": { "production": "2", "staging": "3" } } }
```

| Rule | Enforced in |
|---|---|
| A version's files never change. Republishing the same content is a no-op. Different instructions or `skill.json` under an existing number are refused. | `SkillsModule.PublishPreview` at request time, and `SkillStore.Publish` again at approval. Both compare instructions and `skill.json`. |
| A label moves only if it still points where the reviewer saw it (`expectedFrom`). | `PublishPreview` at request time. `SkillStore.Publish` at approval, under the lock. |
| A version is written in full before it appears: a temporary folder, then one move. `labels.json` is replaced by one move too. | `SkillStore.Publish` |
| Only an approved `skills.publish` writes here. The module never deletes a version. | The factory's notes; `SkillStore` has no delete method |

There is no cache. Each `find` or `get` reads `labels.json` and the files it needs. `skill.json` is kept as sent. The Host reads `id`, `version`, `summary`, `appliesTo`, `tools`, `policies` and `owner`, and it does not read `format`.

## The tools

### `skills.find` (Read)

Argument: `situation`, optional. Omit it to list every skill.

- Lists every skill that has a version at the served label. Nothing is filtered out. With no match, every skill still comes back, with the lowest scores last.
- **Score.** The situation is split on spaces, commas, full stops and semicolons. Words of three or more characters count. A word scores when it appears, ignoring case and inside a longer word, in the summary, the `appliesTo` entries or the id (dashes read as spaces). The score is the number of words that score. Ties sort by id. `tools` and `policies` are not searched.
- Returns `{ label, skills: [ { id, version, summary, appliesTo } ], warnings, next }`. `next` tells the agent to call `skills.get`. `warnings` is always present, and empty when nothing is wrong.
- A skill whose label points at a version with missing files is left out of `skills` and named in `warnings`: `The store lists <id>@<v> but its files are missing; it is not offered.` `skills.get` fails for the same skill.

### `skills.get` (Read)

Argument: `id`.

- Serves the version at the served label: `{ served: "id@version", id, version, label, summary, appliesTo, tools, policies, policyWarnings, instructions }`. `policies` lists the refs only.
- An unknown id, or no version at the label, gives the error `No published skill '<id>' (label production).`
- **Drift check.** Runs only for policies with a recorded `hash`, and only when `PolicyFolder` is set. The file is found by the file name of the ref: `policies/payment-verification.md` looks for `payment-verification.md` in the folder. A missing file, or a different normalised hash, adds a warning. The warning tells the agent to follow the policy as it is now and to say that the skill may be out of date.
- **Audited** as `skill_served`, with `served` as the detail. `SkillOutcomes` reads that audit and the `served` field, so renaming either one breaks `--skill-outcomes`.
- Does not taint the session, because the content is the business's own, not outside text. Test: `Publishing_waits_for_approval_then_the_skill_is_served_and_audited`.

### `skills.publish` (ExternalEffect, administration tool)

Proposes a version or a label move. Nothing is written until a person approves. The reviewer's change request is the tool's preview.

| Argument | Required | Meaning |
|---|---|---|
| `skillId` | yes | The skill id |
| `version` | yes | A whole number from 1 to 999,999,999, with no leading zero |
| `reason` | yes | What changed and what the scenarios showed. The reviewer reads it first |
| `expectedFrom` | yes | The version the label points at now, or `none` |
| `label` | no | `production` (default) or `staging` |
| `skillJson` | with `instructions` | The version's `skill.json`. Omit both for a version already in the store |
| `instructions` | with `skillJson` | The version's `SKILL.md` text. It may begin with standard frontmatter |
| `report` | no | A scenario report in Markdown. Its binding line is checked |
| `ignoreFrontmatterKeys` | no | Unknown frontmatter keys to acknowledge, by exact name |

**The flow:**

1. **Checked at once.** A refusal returns `invalid_arguments` with the reason, and nothing is queued. The messages and their fixes are in the [runbook](../docs/guides/skills.md#when-a-publish-is-refused).
2. **Queued.** The preview is the change request. It holds a summary line such as `Skill invoice-check: move 'production' from version 2 to version 3 (update). Reason: …`, the kind of change, the changed lines of `SKILL.md` (for an update, only the changed lines, up to 400), the policies with their tested hashes and which of them changed, the report check, the report itself cut at 20,000 characters, and any standard-form warnings.
3. **Decided.** Approve, reject or request changes. Edits to the arguments are refused, because `AllowEdits` is `false`. Test: `A_reviewer_cannot_edit_a_publish`.
4. **Executed on approval.** The module prepares the content again and checks `expectedFrom` again. It writes the version if the number is new and moves the label. The result is `{ published, label, previous }`.

The kind of change is decided by numbers: `no change` if the label already points there, `first publication` if the label is empty, `rollback` if the new number is lower than the current one, and `update` otherwise.

Approval follows the suggested policy: one approval for `staging`, and two different people for `production`. Test: `The_suggested_policy_needs_two_approvals_for_production_and_one_for_staging`.

- **Rollback** is a publish of an earlier number, with `expectedFrom` set to the current one and no content. It goes through the same approval.
- **Promotion** from staging to production is a publish of the same version to `production`, with no content. The test does this.

## Identity: hashes, bindings and drift

- **`SkillText.Hash`** returns `sha256:` and the 64 lower-case hex digits of the SHA-256 of the normalised text. Normalising means: Unicode NFC; line endings become `\n`; runs of spaces and tabs become one space; leading and trailing spaces on each line are removed; blank lines are collapsed. Cosmetic edits leave the hash alone, and a change of wording changes it. The workbench's policy hashes use the same function.
- **`ContentHash`** of a version is the hash of its id, version, summary, `appliesTo`, `tools`, policy refs, owner and instructions, joined together. Editing any of those changes it. Changing a policy's recorded `hash` does not. Other `skill.json` fields, such as `format`, are not in it.
- **The binding line** is `skill-report: <id>@<version> sha256:<hex>`. A report is **bound** when one of its first 40 lines names this skill, this version and this content hash. A binding for this skill with another version or hash refuses the publish. A report with no binding line is shown as **not bound** and is not refused. Bindings for other skills are ignored. The Host checks which content a report names, not the numbers in it.
- **Drift** is checked by `skills.get`, described above.

## The standard form

The `instructions` of a publish may begin with a frontmatter block: a `---` line, the fields, then a closing `---` line. When it does:

- `name` must equal the skill id.
- `description` must equal the text the Host derives from `summary` and `appliesTo`: `<summary>. Use when <entry>; or when <entry>.`, with trailing full stops dropped.
- `metadata.version`, if given, must equal the version.
- Unknown top-level keys are refused unless they are listed in `ignoreFrontmatterKeys`.
- Only the body after the closing `---` is stored.

Without frontmatter, the instructions are stored exactly as sent. The Host still checks that a standard form could be written from them: the description must be buildable and at most 1024 characters.

Rollbacks are not re-checked. Every error code and warning is listed in the [authoring guide](../docs/guides/writing-skill-versions.md#refusal-and-warning-codes).

## Contracts other code relies on

- **The module contract.** `IHostModule` (`Id`, `Manifest`, `Register`) and `IHostModuleFactory` (`Type` is `skills`, plus `Describe` and `CreateAsync`). The module is built against contract 1 (`HostContract.AssemblyInfo.cs`), so it loads with any Host that supports contract 1.
- **The workbench compiles against this module.** It uses `SkillText` (`Hash`, `Normalise`, `ContentHash`, `BindingLine`, `Bindings`, `Diff`); `SkillStore` in its scenario runner; `SkillsModule` in its sandboxed Host, so scenarios run the real tools; and the `Standard` namespace in its package writer (`NativePackage.cs`). A change to what `SkillText` computes changes every recorded hash and report binding, and breaks the workbench's checks. Treat those functions as frozen.
- **`skill_served`.** `SkillOutcomes` depends on the event name, the tool's `AuditResult`, and the `served` field. See `skills.get`.

## Invariants and their tests

| Invariant | Where it is enforced | Test (`SkillsModuleTests.cs` unless noted) |
|---|---|---|
| Nothing is written before approval | The action queue runs the publish only after approval | `Publishing_waits_for_approval_then_the_skill_is_served_and_audited` |
| `skills.get` is audited with the version served | `SkillsModule.Get` | same as above |
| Serving a skill does not taint the session | `skills.get` returns no outside content | same as above |
| A version cannot change under its number | Preview (at request time) and store (at approval) | `A_version_cannot_be_republished_with_different_content_and_bad_requests_are_refused_at_once`; `A_metadata_change_to_a_published_version_is_refused_when_requested_not_at_approval` |
| A broken label is named, not hidden | `SkillsModule.Find` | `Find_names_a_label_whose_version_files_are_missing_instead_of_hiding_it` |
| A label moves only from the version that was reviewed | Preview and store | `Approving_an_old_proposal_after_the_label_moved_publishes_nothing`; `A_publish_without_expectedFrom_or_with_a_wrong_one_is_refused_at_once` |
| A report describes the content proposed | `CheckReport` | `A_report_bound_to_other_content_is_refused_and_a_matching_one_is_marked_bound` |
| A rollback is an ordinary publish | `Publish` | `Rolling_back_is_publishing_an_earlier_version_through_the_same_approval` |
| Production needs two different approvers; a publish cannot be edited | Suggested policy; the Host's rules for administration tools | `The_suggested_policy_needs_two_approvals_for_production_and_one_for_staging`; `A_reviewer_cannot_edit_a_publish`; `Publish_is_an_administration_tool_and_a_loose_policy_for_it_stops_startup` |
| The drift warning is shown | `skills.get` | `A_policy_changed_since_the_skill_was_tested_is_reported_when_the_skill_is_served` |
| Server instructions mention skills only when the module is advertised | Factory and Host | `The_server_instructions_point_at_skills_only_when_the_module_is_advertised` |
| The module loads from configuration with its suggested policy | Factory | `The_module_loads_from_configuration_with_its_suggested_policy` |
| The standard form: name, description, version, unknown keys | `StandardPublish.Prepare` | `StandardPublishTests.cs` and `StandardSkillTests.cs` (about 45 cases with theories, per the design record) |

## Known limits and gaps

Found by reading the code while writing this. The first draft listed ten; the first two were fixed in `1464f70` (a metadata-only change is now refused at request time, and `find` names a broken label). The rest are open. Each open one is a candidate for a decision.

1. **The write lock covers one process only.** `SkillStore.WriteLock` is a static object (`SkillStore.cs:22`). Two Host processes on one store folder are not serialised, so the `expectedFrom` check and the write of `labels.json` can interleave. The roles design assumes one writer. That is an operating rule, not something the code enforces.
2. **The Host does not read `format` in `skill.json`.** `SkillStore.Parse` ignores it. The workbench's format notes say a reader refuses an unknown format. That is not true of the Host.
3. **The store is not in the SQLite backup.** [OPERATIONS.md](../OpenTail.Host/OPERATIONS.md#what-to-back-up-and-how-to-upgrade) covers the database file only. The [runbook](../docs/guides/skills.md#backups-and-recovery) covers the store.
4. **A cosmetic change to a stored version is accepted and changes nothing.** The stored text stays as it was first published, while the approval still moves the label. This is intended. It is listed so that it is not a surprise.
5. **The report is read from its first 40 lines only**, so a binding line further down does not count. The change request shows only the first 20,000 characters of a report.
6. **Matching is loose.** It is substring matching on words of three or more characters, so it can find skills the situation does not really concern. Every skill is listed anyway, and the agent decides.
7. **The standard form differs from the reference validator.** The reference validator reads YAML with `strictyaml` (Python). The Host uses YamlDotNet's event parser. The two may disagree on edge inputs (per the design record, not yet checked).
8. **Frontmatter detection is line-based.** Instructions that begin with a Markdown rule (`---`) and have another `---` line later are read as frontmatter, and are then refused or misread. Start the instructions with a heading.

## Tests

- `OpenTail.Host.Tests/SkillsModuleTests.cs`: 14 tests for the tools, approval, rollback, drift, configuration, the report binding, metadata changes and broken labels.
- `OpenTail.Host.Tests/StandardSkillTests.cs` and `StandardPublishTests.cs`: the standard form.
- `OpenTail.Host.Tests/SkillOutcomesTests.cs`: the tests for `SkillOutcomes.cs`.
- To run the Skills tests: `dotnet test OpenTail.Host.Tests --filter "FullyQualifiedName~Skill|FullyQualifiedName~Standard"`. On 2026-11-10 that gave 63 passing, and the whole `OpenTail.Host.Tests` project gave 1037 passing.
- **Restoring the test project needs the GitHub package feed.** `nuget.config` reads `GITHUB_PACKAGES_TOKEN` for the `OpenTail.Connectivity.*` packages. Set it for the command; the comment in `nuget.config` says how to get a token. On 2026-11-10, `gh auth token` gave one that worked. Do not write the token to a file.

## Related

- [Setup, publishing and operations](../docs/guides/skills.md)
- [Writing a skill version](../docs/guides/writing-skill-versions.md)
- [skills.publish questions](../docs/design/skills-publish-questions.md): why report binding, `expectedFrom` and no edits
- [Standard skill files](../docs/design/standard-skill-files.md): the standard form, its decisions and its status
- [Roles and permissions](../docs/design/roles-and-permissions.md): who may call `skills.publish`
- [Skills and scenarios](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/design/skills-and-scenarios.md): the original design. Its status line still says "proposal", though much of it is built.
- The workbench (`opentail-workbench`, a separate private repository) writes skill versions, tests them against this module, and prepares the publish arguments with `workbench propose`.
