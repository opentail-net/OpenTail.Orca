# Design: skill resources (`references/` and `assets/`)

**Status:** proposed 2026-11-10. No code yet. Decisions marked **owner** are for the owner; the others are proposed defaults that the owner can change.
**Plan:** [skills-plan.md](skills-plan.md), step 3.
**Where it fits:** the Host's skills module (`OpenTail.Modules.Skills`), the workbench's package writer and its skill loader (`opentail-workbench`). A new Host tool, `skills.read_resource`, is the only new surface.

## Problem

A published skill version is two files, `skill.json` and `SKILL.md`. A skill that needs a long reference (a rate table, a policy's worked examples, a template) must either carry it inside `SKILL.md` or leave it out. The standard expects the reference to live in a separate file that the agent loads only when the task needs it, so that `SKILL.md` stays short. Today the Host cannot store such a file, the Host cannot serve it, and the workbench's packager cannot write it.

## Facts this relies on

Verified on 2026-11-10 from the published Agent Skills specification (agentskills.io/specification and /what-are-skills).

| Fact | Source wording (short) |
|---|---|
| A skill directory holds `SKILL.md` and may hold `scripts/`, `references/`, `assets/`, and "any additional files or directories". | Directory structure |
| `references/` holds documentation that agents read when needed; keep each file focused. | Optional directories |
| `assets/` holds static resources: templates, images, data files. | Optional directories |
| Progressive disclosure has three stages: metadata (about 100 tokens) at startup; the full `SKILL.md` on activation (recommended under 5000 tokens); resources only when required. | Progressive disclosure |
| `SKILL.md` should stay under 500 lines; detailed material goes in separate files. | Progressive disclosure |
| File references are relative to the skill root, and kept one level deep from `SKILL.md`. | File references |
| The specification does not say how an agent retrieves a resource. The client does that, by reading files. | (absence, checked on the page) |
| `allowed-tools` is experimental and is a space-separated list of pre-approved tools. | Frontmatter |

The last row matters for scope: the standard leaves retrieval to the client. A Host that serves skills to clients that have no file system needs a retrieval tool of its own.

## Goals and non-goals

**Goals**
- A version may carry text resources under `references/` and `assets/`, bound to the version the way its instructions are.
- The agent can read a resource of an activated skill through the Host, read-only, and each read is audited.
- The workbench can package those resources for native clients, and can test them.

**Non-goals**
- Executing anything. `scripts/` is refused by the store (see D3). Scripts are code, and running code is a separate design with its own sandbox.
- Binary files in this version (images, PDFs). See D1.
- Resources shared between skills, or fetched from outside the store.
- Changing what a skill may do. Resources are information, as `SKILL.md` is; policy still decides tool access.

## Contracts this must satisfy

Read from the code on 2026-11-10, at `opentail-host` `6779f6b` and `opentail-workbench` `ce65cb3`.

**Host store** (`OpenTail.Modules.Skills/SkillStore.cs`)
- `SkillStore.Publish(id, version, label, metadataJson, instructions, expectedFrom)`: a version's files never change; republishing the same content is a no-op; different content under an existing number is refused; the label moves only if `expectedFrom` still matches; the publish holds `.publish.lock` throughout.
- `SkillStore.Get(id, version)` and `Parse(metadataJson, instructions, id, version)`: `skill.json` must be format 1 (required since `1de39fe`). Each field's type is checked only from `4b8a8b7`, which is on `standard-skill-files` and not in `main`; at `6779f6b` the fields are not type-checked (checked 2026-11-10 with `git show 6779f6b:OpenTail.Modules.Skills/SkillStore.cs`). `Parse` is also used by the standard-form publish path and by the workbench.
- `SkillStore.Root` layout: `<root>/<id>/<version>/skill.json` and `SKILL.md`, and `<root>/labels.json`.

**Host hashes** (`SkillText.cs`)
- `SkillText.ContentHash(SkillVersion)` covers id, version, summary, appliesTo, tools, policy refs, owner and instructions. `SkillText.BindingLine` writes `skill-report: <id>@<version> sha256:<hex>`, and the publish preview checks a report's binding line against this hash.
- The workbench calls `SkillText` directly: `Skill.cs` (`ContentHash`, `BindingLine`), `Program.cs` (`Bindings`), `Policies.cs` and `Scenario.cs` (`Hash`), `Tuner.cs` (`Diff`), `AiGrader.cs` (`Hash`). A change to what `ContentHash` covers for a version with no resources would change every recorded report binding. So none may change (see the proposed design, below).

**Host tools** (`SkillsModule.cs`)
- `skills.find` (Read): lists skills at the served label, with `warnings` for unreadable versions.
- `skills.get` (Read, audited as `skill_served` with `served` as the detail): returns `served`, `id`, `version`, `label`, `summary`, `appliesTo`, `tools`, `policies`, `policyWarnings`, `instructions`. `SkillOutcomes` reads the `skill_served` audit and the `served` field, and only tools whose `AuditResult.Event` is `skill_served` count as serves.
- `skills.publish` (ExternalEffect, administration): previewed as a change request; the preview shows the diff, the reason and the report check.
- The suggested policy (`SkillsModuleFactory.SuggestedPolicyJson`) lists `find`, `get` and `publish`.

**Workbench** (`opentail-workbench`)
- `NativePackage.Write(skill, outFolder, checkIn)` writes `<out>/<id>/SKILL.md` only (checked: no `references` or `assets` in the file). The frontmatter is produced by `StandardSkill.Format`.
- `Skill` (`Formats/Skill.cs`) reads `skill.json`, `SKILL.md` and `policies`, and builds a `SkillVersion` for hashing. Its `skills/<id>/` folder has `examples/` documented in `docs/formats.md` but not read.
- `ScenarioRunner.cs` builds a `SkillStore` over the sandbox's store, so the sandbox serves what the store serves.
- `AgentRunner` gives the model `skills.find` and `skills.get` through the Host in tools mode; in native mode it puts the instructions in the system prompt. The `catalog` and `none` deliveries came later (skills plan, steps 2 and 4) and are not in this snapshot.

## Branch-by-branch: what must not change

| Case | Today | Under this design |
|---|---|---|
| Version with no resources: publish, republish, label move | Stored as today | Unchanged |
| `ContentHash` of such a version | Hash of the eight fields above | Unchanged under v1, which has no resource term at all. Under v2 every version carries the term, and a version with no resources contributes an empty list (hashing plan, design item 10) |
| `skills.get` of such a version | Fields listed above | Same fields, plus `resources: []` (additive) |
| `skills.find` | Unchanged | Unchanged |
| Report binding of such a version | Checked against `ContentHash` | Unchanged |
| Workbench package of a skill with no `references/`/`assets/` | `SKILL.md` only | Unchanged |
| `SkillOutcomes` | Counts `skill_served` | Unchanged: the new tool audits under a different event name |
| `scripts/` present in a skill folder | Ignored by the workbench, not read | Refused by the store at publish; the packager reports it and still writes the rest |
| Taint after a read | `skills.get` does not taint the session | `skills.read_resource` does not taint either (same reason: the business's own content) |

## Design

### Store layout

```
<root>/<id>/<version>/
  skill.json           declares its resources (below)
  SKILL.md
  resources/
    references/<file>
    assets/<file>
```

The version folder is written in full, in a temporary folder, and moved into place, as it is today.

### `skill.json` declares its resources

A new optional field, `resources`: an array of `{ "path": "references/rates.md", "sha256": "sha256:<hex>", "bytes": 1234 }`. The store refuses a version whose resource files do not match its declaration, and refuses a declared file that is missing. A file present but not declared is refused too: no undeclared file is ever served.

Declaring the digest is what binds the resource to the version. The digest is `sha256:` over the resource's exact stored bytes: a raw digest, not a text identity, so it never goes through `SkillText.Normalise`. A reviewer sees the list in the change request, and the content (see Publish).

### Hash binding

Resources are part of a version's identity only under the v2 algorithm of the hashing plan (its design item 10): the sorted list of each resource's path and digest, encoded as there. Under v1, a version without resources hashes exactly as today, so every existing binding stays valid. A v1 identity has no resource term, so it cannot cover a version with resources. The Host therefore refuses a v1 binding for such a version, and the workbench writes v2 for it. Without that rule, a v1 report could describe a version whose resources had changed.

`SkillVersion` gains an optional `Resources` property with an empty default, so existing construction sites compile unchanged. The workbench's `Skill` maps its folder's resources into it.

### Path and content rules (proposed defaults; D2 for the limits)

- Path: relative, forward slashes, no `..` segment, no leading `/` or drive letter, under `references/` or `assets/` only, at most three levels, file names of `a-z A-Z 0-9 . _ -`.
- Files: UTF-8 text only in this version (D1). Extensions allowed: `.md .txt .csv .tsv .json .yaml .yml .html`. Valid UTF-8 without NUL bytes.
- Limits: 64 KiB per file, 256 KiB per version, at most 50 files. Refused, never truncated, at publish.
- `scripts/` at the top level is refused at publish, with a message that says why.
- Resource bytes are stored exactly as sent, and their digest is over those exact bytes. `SkillText.Normalise` is not used for resources. Line endings and indentation are therefore part of a resource's identity.

### Publish

The publish arguments gain an optional `resources` object: `{ "references/rates.md": "<text>" }`. The preview puts into the change request, for each new or changed resource, its path, its size and its content: the full text of a new file, and a readable diff against the stored version of a changed one. The report check is unchanged. The preview and the execution build the resource list from the same arguments, and a test checks that the bytes shown are the bytes published. Removed resources are listed too, as `references/old-payment-rules.md — REMOVED`, with the removed content shown as removed lines, so a reference that disappears from a version cannot do so unnoticed. Approval writes the files with the version. A republish of an existing version with different resources is refused, like different instructions.

Size: the arguments travel as JSON through MCP. The 256 KiB total cap bounds that. A larger resource is a later design (D5).

### `skills.get` (additive)

`skills.get` returns `resources`: the declared paths with their bytes and hashes, so the agent knows what exists and can read it by name. No content is returned by `get`.

### `skills.read_resource` (new, Read)

- Arguments: `id`, `path`. The version is the one at the served label, as for `get`.
- Result: `{ served: "<id>@<version>", path, bytes, text }`.
- The path must be one of the version's declared paths, compared exactly after the rules above. An undeclared path is refused, with the same error as a missing one, so the tool cannot be used to probe the store.
- Read-only. No taint, as `get`.
- Audit: its own event, `skill_resource_served`, detail `<id>@<version>:<path>`. It does not use `skill_served`, so `SkillOutcomes` is unaffected.
- Policy: listed in the suggested policy as `Execute`, like `get` (D6).
- Result size: the file is returned whole, since the store caps it at 64 KiB.

### Workbench

- Loader: `skills/<id>/references/` and `skills/<id>/assets/` are read, and their files are declared in `skill.json` by the loader's own rules (the author writes the declaration; the loader checks it against the folder and refuses a mismatch).
- Packager: `NativePackage.Write` copies the resource files beside `SKILL.md`, unchanged. `scripts/` is reported and not copied.
- Sandbox: the sandboxed Host serves resources through the same store code, so scenario runs exercise `skills.read_resource`.
- Delivery: in tools mode the model discovers resources through `skills.get` and reads them with `skills.read_resource`. The catalog delivery (built in step 2) loads a skill's instructions through the harness tool `activate_skill`, which stands in for the client's file read. A resource read in the catalog will be a second harness tool, `read_resource`, with the same exemption from the outside-skills flag. It is not built: it waits for decisions D1 to D6.

## Tests (planned)

- Store: resource declared and present is served; undeclared file refused at publish; declared and missing refused; path traversal refused (`../`, absolute, drive letter, `scripts/`); binary or invalid UTF-8 refused; limit violations refused; republish with different resources refused; identity of a version without resources unchanged under v1 (compare against the current `ContentHash` for the three existing skills); a v1 binding refused for a version with resources; the approval preview shows exactly the bytes that are published.
- Tool: `read_resource` of a declared path returns text and audits `skill_resource_served`; of an undeclared path returns the same error as a missing one; unaffected by the `skill_served` count in `SkillOutcomes`.
- Workbench: a skill with `references/` loads and packages; a skill with `scripts/` is reported and the rest is written; sandbox run reads a resource.

## Decisions

- **D1 (owner).** Text resources only in this version, and binary assets later? Proposed: yes.
- **D2 (owner).** Limits: 64 KiB per file, 256 KiB per version, 50 files. Proposed: these.
- **D3 (owner).** Refuse `scripts/` in the store and report it in the packager? Proposed: yes.
- **D4 (owner).** Should the change request show resource contents, or only path, bytes and hash? Proposed, changed after an external review: show the content, the full text of a new file and a readable diff of a changed one. A hash establishes identity; it does not show a reviewer what they are approving. The limits in D2 keep the text small enough to read.
- **D5 (owner).** Larger resources, binary assets, or a chunked read: deferred. Proposed: defer.
- **D6 (owner).** Suggested policy for `skills.read_resource`: `Execute`, like `get`. Proposed: yes.

## Risks

- A resource is a second place where an instruction can hide. Mitigation: resources are declared and hashed, reviewed by their content in the change request (D4), and never executed.
- The arguments of a publish get large. Mitigation: the 256 KiB cap now; a later upload path if needed (D5).
- Reviewers may not read resources. Mitigation: the change request shows the content of each new or changed resource (D4); a test checks that the bytes shown are the bytes published.
- Existing reports bind the old hash. Mitigation: no change to the hash of a version without resources.

## Not in this design

Scripts, binary assets, shared resources between skills, remote resources, resource search, and any change to `allowed-tools`, which stays ignored as a source of permission.
