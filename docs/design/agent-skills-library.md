# Design: an independent .NET library for Agent Skills

**Status:** planned 2026-11-10. No code yet. Decisions marked **O-** are for the owner; the rest are the proposed defaults.
**System:** a new library, `OpenTail.AgentSkills` (name and repository for the owner, O-L1). It reads, validates and packages skill directories, and preserves every file in them. It does not hold a store or govern anything: the Host keeps its version store, labels, lock and approvals (O-L4).
**Not in this plan:** approval, audit, labels moving through approval, and the MCP tools (these stay in the Host); the workbench's evaluation (stays in the workbench); script execution (excluded); a permissions model (excluded).
**Code basis:** `opentail-host` on `standard-skill-files` at 4b8a8b7 (committed; not in `main`): `OpenTail.Modules.Skills/Standard/StandardSkill.cs`, `StandardPublish.cs` (both unchanged since 6f1a59b and the same in `main` 6779f6b), `SkillStore.cs` (its strict-type changes are in 4b8a8b7), `SkillText.cs`; `opentail-workbench`: `Formats/NativePackage.cs`, `Formats/Skill.cs`, `Agent/AgentRunner.cs`.
**Scope note (rule 4):** the library is one system. Three of its steps change other systems: step 3 changes the Host's stored format and the identity (store and hashing plans), step 5 changes `Format`, which stays Host code, and O-L5 moves identity code those plans change. O-L4 (the Host keeps its store) moves no store code. **Order:** the store plan and the hashing plan's decisions land first, in the Host; step 1 here moves the code as it is then, with its tests. O-L4 (recommended: the Host keeps its store) means the store code does not move at all; O-L5 moves the identity code afterwards, unchanged in behaviour. Step 3 needs O-L3 and O-H2 together.

## Purpose

Any .NET developer should be able to take an ordinary Agent Skills directory, load it, validate it, read its metadata, retrieve its resources, and use it with their own agent, without installing the Host or any other OpenTail product. The Host becomes one consumer of the library, and does not own the format.

## Spec facts this relies on

Checked on 2026-11-10 against the specification at agentskills.io/specification, and against the reference validator's source (`skills-ref/src/skills_ref/validator.py` in github.com/agentskills/agentskills).

| Fact | Source |
|---|---|
| A skill is a directory with `SKILL.md` required. Optional: `scripts/`, `references/`, `assets/`, and "any additional files or directories". | specification, directory structure |
| Frontmatter: `name` (1 to 64 characters; lowercase letters, digits and hyphens; not starting or ending with a hyphen; no `--`; must match the parent directory name). | specification, `name` |
| `name` wording: "unicode lowercase alphanumeric characters (`a-z`, `0-9`)". The wording is ambiguous, because it says Unicode and then lists ASCII. | specification, `name` |
| The reference validator strips the name, applies NFKC, then checks length, `name != name.lower()`, edge and double hyphens, and `c.isalnum() or c == "-"`; it compares the directory name after NFKC; it accepts Unicode letters. Re-read 2026-11-10 (raw `validator.py` on `main`). | `validator.py` |
| The reference validator's `compatibility` check is "is a string" and "at most 500"; the excerpt read shows no check for an empty value, which the specification forbids ("1-500 characters if provided"). Our `Check` refuses an empty or blank value. Probable divergence; to confirm with the corpus. | `validator.py` |
| `description`: 1 to 1024 characters, non-empty. | specification |
| `license` (optional, free text). `compatibility` (optional, 1 to 500 characters). `metadata` (optional, a map from string keys to string values). `allowed-tools` (optional, experimental, a space-separated string). | specification |
| The reference validator accepts exactly the top-level keys `name`, `description`, `license`, `allowed-tools`, `metadata`, `compatibility`, and reports any other as an error. | `validator.py` |
| Progressive disclosure: metadata at startup (about 100 tokens); the body on activation (under 5000 tokens recommended; under 500 lines); resources only when needed. References are one level deep from `SKILL.md`. | specification |
| The specification does not say how an agent retrieves a resource. The client reads files. | specification (absence, checked on the page) |
| The reference parser tries the exact names `SKILL.md`, then `skill.md`, and no other spelling. | `parser.py`, `find_skill_md`, read 2026-11-10 |

**Conclusion on Unicode names.** The reference implementation accepts Unicode letters. The reviewer is right about the reference validator's behaviour; the specification's own wording is unclear, so what "the standard" requires is not settled. Our reader already accepts them (`StandardSkill.cs`, `Check`: `Rune.IsLetterOrDigit`). Only the save-time rule refuses them (`SKILL-NAME-ASCII`), and that rule is the Host's, not the standard's.

## Contracts this must satisfy (current code)

**`StandardSkill`**

- `Parse(text, ignoreKeys)` returns a `StandardSkillFile` with diagnostics. It does not throw for a bad file; it catches `YamlException` and reports it (`StandardSkill.cs:66-73`). (`Next`, `:378-383`, throws `InvalidOperationException` if the event stream ends early, which is not caught; YamlDotNet's parser should report such input as a `YamlException` first. To confirm with a truncated-input test.) Unknown keys become `SKILL-UNKNOWN-KEY` errors unless listed in `ignoreKeys`, which turns them into `SKILL-KEY-IGNORED` warnings (`:83-89`); ignoring a standard key, or a key not present, is an error (`:76-82`). **Decision (external review): this acknowledgement moves to the Host's publish layer** (`StandardPublish.Prepare`, which already passes it in). The library's `Parse` reports every unknown top-level key as an error, as the reference does, and returns each key with its value as data, so a caller can keep it. See Design.
- `ReadFrontmatter` (`:257-327`, read 2026-11-10) works on parser events and deserialises nothing. It refuses anchors and tags on the root mapping (`:275`), on keys (`:283`), on standard values (`:304`), on `metadata` and its entries (`:337`, `:345`) and anywhere inside an unknown key's value (`:316-318`); aliases as values (`:295`); duplicate keys (`:293`, `:352`); more than one document (`:322-326`); a non-mapping root (`:270`); non-scalar standard values and nested `metadata` (`:308`, `:347`). It does not refuse plain scalars that YAML would type (`name: 123`, `license: true`): they are read as their text. Every value is trimmed (`:305`, `:352`).
- `Check(file, directoryName, saving)` applies the spec's rules, with these differences from the reference:
  1. The name is not NFKC-normalised before its checks (the reference normalises first).
  2. The directory name is normalised, but compared with an un-normalised name (the reference normalises both).
  3. With `saving`, a Host-only rule refuses non-ASCII names, with a suggested name (`SKILL-NAME-ASCII`).
  4. Unknown keys are errors unless the caller opts in (the reference errors too; this matches).
  5. Character classes: `Rune.IsLetterOrDigit` (letters and decimal digits) is narrower than Python's `isalnum` (which also accepts other numeric characters, such as categories Nl and No). Lowercase is tested with `ToLowerInvariant`, not Python's `lower()`; the two differ for some characters. Probable divergences; the corpus decides.
  6. An empty `compatibility` is refused here; see the spec facts above.
- **Value trimming.** The reference strips the required `name` and `description`, and no other value: its optional fields and `metadata` values are returned as written (`parser.py`, read 2026-11-10). Ours trims every value (`StandardSkill.cs:305`, `:352`). This divergence is recorded in step 2, and storage keeps the parsed value as it is (see Design, Exact values).
- **`SKILL.md` name.** The Host reads only `SKILL.md` (`SkillStore.cs:53`). The reference also accepts `skill.md` (see O-L7).
- `Describe(summary, appliesTo)` builds a description from the Host's summary and situations. This is a Host rule, not part of the standard. It stays in the Host; the library gets only the generic description rule (1 to 1024 characters). The workbench calls it directly (`Agent/AgentRunner.cs:159`, the catalog delivery), so the workbench keeps a reference to the Host module for it, or the rule moves to a shared Host-and-workbench place; not to the library.
- `Format(id, version, summary, appliesTo, instructions, bodyPrefix)` writes a standard file, used by the workbench's `NativePackage` (`NativePackage.cs:20`). It calls `Describe` and `Check(..., saving: true)`, so it is Host packaging, not a standard rule: it stays with `Describe`, and calls the library's generic writer.
- `SuggestSaveName` (public); `SplitFrontmatter` (`internal`, used by `StandardPublish`); `IsSaveable` (`private`). Only `SuggestSaveName` is public API today; the save-time rule and its suggestion are Host rules.

**`StandardPublish.Prepare`** (`StandardPublish.cs:15`) stores only the body. It warns that `license`, `compatibility`, `allowed-tools` and every metadata key except `version` are "not stored, so not served" (`:46-56`). The stored `skill.json` is the Host's own format.

**`SkillStore.Parse`** reads format 1 and ignores keys it does not read. So an additive optional key is safe for the current Host reader.

**`SkillText`** (identity): see [skill-content-hashing.md](skill-content-hashing.md). This library takes it over only if O-L5 is decided that way, and only after the hashing plan's decisions; until then it stays in the Host.

## Branch-by-branch: standard frontmatter fields

The columns "Read", "Stored by the Host", "Served" and "Exported" describe the code as built today. "Library requirement" is planned.

| Field | Read | Stored by the Host | Served | Exported | Library requirement |
|---|---|---|---|---|---|
| `name` | yes | as the skill id, ASCII only at save | as `id` | yes | read Unicode letters after NFKC (the reference's behaviour) |
| `description` | yes (trimmed) | derived from summary and appliesTo; must equal the built text at publish, for a new version with frontmatter (`StandardPublish.cs:34-39`) | as `summary` and `appliesTo` | yes | read as given; whether surrounding white space is kept is fixed with the round-trip test (today every value is trimmed) |
| `license` | yes | no (warning) | no | no | **preserve** |
| `compatibility` | yes | no (warning) | no | no | **preserve** |
| `metadata` | yes; only `version` is checked, against the version | no (the store keeps no frontmatter) | no | `version` only (written by `Format`) | **preserve** as an ordered string map, arbitrary keys |
| `allowed-tools` | yes | no (warning) | no | no | **preserve** as a descriptive string; never authority |
| body (`SKILL.md`) | yes | yes, body only | yes, as `instructions` | yes | read and write exactly |
| `references/` | no (today the Host reads no folder of a skill) | no (planned: text resources, per the resources design) | no | no (`NativePackage.Write` writes `SKILL.md` only) | **read and preserved as opaque bytes** |
| `assets/` | no | no (planned: text only; binary assets refused by the Host, D1) | no | no | **read and preserved as opaque bytes** |
| `scripts/` | no | no (planned: the Host refuses it, D3) | no | no | **read and preserved as opaque bytes; never executed** |
| other top-level keys | error or ignored, by caller | no | no | no | as the caller decides |

## Design

**Project layout (proposed; confirm in step 1).**

```
OpenTail.AgentSkills/                  net10.0 (O-L6); no reference to Host or Microsoft.Extensions.AI
  Frontmatter/                         SKILL.md reader and writer; diagnostics
  Directory/                           SkillDirectory: open, list, safe reads
  Model/                               SkillPackage, SkillMetadata, SkillResource, SkillDiagnostic
  Validation/                          the standard's rules (reference-compatible)
  Identity/                            content identity (see the hashing plan), if O-L5 moves it here
OpenTail.AgentSkills.Tests/
  Corpus/valid/*, Corpus/invalid/*     skill directories with the reference's expected outcome
```

**Public surface (sketch, to be fixed in step 1).**

```csharp
public static class SkillFile
{
    public static SkillFrontmatter Parse(string text);   // diagnostics, never throws for content; unknown keys are errors, returned as data (ignoreKeys is a Host option)
    public static string Write(SkillMetadata metadata, string body);                                    // round-trips every preserved field
}

public sealed class SkillDirectory
{
    public static SkillDirectory Open(string path, SkillOpenOptions? options = null);   // refuses reparse points on any component
    public SkillPackage Load();                                                       // metadata, body, resource listing, diagnostics
    public ReadOnlyMemory<byte> ReadFile(string relativePath, long maxBytes = long.MaxValue);   // any file or folder in the package, as opaque bytes; see below
}

public sealed record SkillMetadata(string Name, string Description, string? License, string? Compatibility,
    IReadOnlyDictionary<string, string> Metadata, string? AllowedTools);
public sealed record SkillPackage(SkillMetadata Metadata, string Body, IReadOnlyList<SkillResource> Resources,
    IReadOnlyList<SkillDiagnostic> Diagnostics);
```

`Metadata` is a dictionary the caller can read in full. `AllowedTools` is a string the caller may show, never a permission.

**Exact values.** The library stores each value as the YAML parser returns it: a quoted scalar keeps its surrounding spaces, and nothing is trimmed for storage. The checks that the reference makes on a trimmed name or description run on a copy. Round-trip tests cover white space at both ends and inside values, and folded multi-line values.

**Package file reads (library side).**

- The path is relative, with forward slashes; it has no root, no drive letter, and no `..` segment.
- Any file or folder in the package may be read: `references/`, `assets/`, `scripts/` and other folders alike. The library returns bytes; it does not interpret, parse or run them. Which files a deployment serves is the caller's decision. The Host refuses `scripts/` and binary assets (resources design D1 and D3), and that rule is Host code, not library code.
- Every path component, and the final file, must not be a symbolic link, junction or other reparse point. Windows junctions and Unix symlinks are both refused. Checked in the .NET API documentation (`FileSystemInfo.LinkTarget`, 2026-11-10): "Symbolic links and junctions are the types of links this API can resolve"; for any other reparse point it returns null. So the check tests `FileAttributes.ReparsePoint` on each component, not `LinkTarget` alone. Still to verify by test: that .NET reports `ReparsePoint` for a Unix symlink, and the effect on folders synced by cloud clients (OneDrive placeholders are reparse points, and would be refused).
- After resolution, the full path must still lie under the skill root.
- A size cap is an argument of the call. The Host passes the resources design's limits (D2: 64 KiB per file); the library imposes no cap beyond the one the caller passes. Decoding bytes as text is the caller's job; the Host refuses invalid UTF-8 for resources (D1).
- Names that differ only in case must not both resolve to the same file on a case-insensitive file system. To test on Windows.
- The library does not audit. The Host audits its own tool that calls it (`skill_resource_served`, per the resources design).

**Store (O-L4).** Recommended, changed after the external review: the Host keeps its store. Labels, immutable version folders, `.publish.lock` and the label rules are governance, not part of the standard, so they stay in the Host. The library reads and preserves standard directories and packages, and does not hold a store. The workbench uses the library to read and write packages. It keeps using the Host's store when it tests the Host's serving behaviour; its `ScenarioRunner` publishes to a temporary Host store today (`ScenarioRunner.cs:144`), and that stays.

**Preserved fields (O-L3).** `skill.json` gains optional keys: `license`, `compatibility`, `metadata` (a string map) and `allowedTools` (a string). The Host reader ignores unknown keys today (`SkillStore.Parse` reads named keys only), so an old reader reads a new file, but the new keys are dropped. Recommended: remain on format 1, documenting the additive keys. The format number changes only for incompatible changes. Two effects to state with the change: the new keys are outside `ContentHash` until O-H2 adds them, so a report binding does not cover them; and they are inside the republish comparison (today the normalised `skill.json` text; see O-H6), so changing one at the same version is refused.

**Unicode names (O-L2).** The library accepts the reference's Unicode letters after NFKC. The Host's store identifiers stay ASCII (`SkillStore.IsId`, `^[a-z0-9]+(-[a-z0-9]+)*$`). A skill with a non-ASCII name is read by the library, and refused by the Host at save with the existing suggested name. Whether Host identifiers should follow the standard is for the owner.

**What the library must not contain.** Host types (`IHostModule`, `ModuleManifest`, `ToolDescriptor`); Microsoft.Extensions.AI; ModelContextProtocol; any code that runs a script.

**Dependencies.** YamlDotNet (the Host's central pin is 17.1.0). Nothing else. An architecture test enforces the list, as `OpenTail.Host.Abstractions.Tests/ArchitectureTests.cs` does for modules.

## Gate before any code

The scaffold waits for O-L1 (licence, repository, package name) and O-L6 (target framework, checked against Stingray's actual target, which this repository does not contain). It also waits for O-L4 to be confirmed, as the gates table in `skills-plan.md` says: the library reads and preserves packages; the Host keeps the store and the governance. Step 0 below records that gate.

## Decisions for the owner

- **O-L1 Licence, repository and package name.** Host and workbench are PolyForm Strict, which permits noncommercial use only. A library other .NET developers, or Stingray, would use commercially generally needs a permissive licence. This is a business decision; this plan does not make it. Until it is made, the library cannot be published.
- **O-L2 Unicode names.** Recommended: the library accepts them; Host identifiers stay ASCII for now.
- **O-L3 Additive fields.** Recommended: keep format 1 and document the keys.
- **O-L4 Store owner.** Recommended, changed after the external review: the Host. The store is governance (labels, immutable version folders, the lock, the label rules), not part of the standard; the library reads and preserves packages and holds no store.
- **O-L5 Identity owner.** Recommended: the library, after the hashing plan's decisions (O-H1 to O-H6).
- **O-L6 Target framework.** net10.0 is assumed because the Host (`OpenTail.Modules.Skills.csproj:3`) and the workbench (`Directory.Build.props:3`) use it. Stingray's target is not known from this repository; it decides whether an older target is needed.
- **O-L7 `SKILL.md` name.** The reference accepts exactly `SKILL.md` and `skill.md`, and tries `SKILL.md` first. Accept both in the library (recommended, as the reference does), with `SKILL.md` preferred? The Host keeps writing `SKILL.md` and reads only that name (`SkillStore.cs:53`).

## Verification still to do

- NativeAOT: whether YamlDotNet's use in `StandardSkill` survives a `PublishAot` build of a small consumer. Not verified; claimed nowhere until tested. What is known from the code: `StandardSkill` uses only `YamlDotNet.Core` and `YamlDotNet.Core.Events` (the event parser), not the reflection-based serializer, which narrows the question. If it fails, the frontmatter syntax needed is small, but YAML's traps (aliases, tags, anchors) are why the reader was built event-based. A replacement parser would need the same refusals.
- Windows junction handling in `ReadFile` (see above).
- The reference validator's behaviour on the corpus, run from a clean Python environment. The validator is a command-line tool (`skills-ref validate`); its output is the expected outcome for each corpus case.

## Migration (each step ships with its tests)

0. **Gate.** O-L1, O-L6 and O-L4 (the boundary above) are decided, and the gates table in `skills-plan.md` records them. No code before this.
1. **Extract with no behaviour change.** Move the standard parts of `Standard/` (`Parse` without `ignoreKeys`, which the Host's `StandardPublish.Prepare` applies; `ReadFrontmatter`; `SplitFrontmatter`; `Check` without the save-time rule; the diagnostics types) and their tests into the library. `Describe`, `Format`, the save-time ASCII rule (`SKILL-NAME-ASCII`, `SuggestSaveName`) and `StandardPublish` stay in the Host, calling the library (`StandardPublish.Prepare` calls `SkillStore.Parse`, a Host type). The Host and the workbench reference the library. What the Host and the workbench do does not change: values are still trimmed as today (exact values come in step 3), and the Host's tools return what they returned. The API does change in one place, so "unchanged" is not literally true of the tests: tests that call `StandardSkill.Parse` with `ignoreKeys` are retargeted at the Host's publish layer with the same assertions, and every other existing test passes as written. The count of retargeted tests is recorded in the step. Add the architecture test.
2. **Align the name rules with the reference.** NFKC on the name before the checks, and on both sides of the directory comparison. Add the corpus and its expected outcomes. Record each remaining divergence, with its reason, in the library's README. The save-time ASCII rule stays a Host rule.
3. **Preserve the fields.** The Host's store and the identity gain the optional keys (O-L3, O-H2); the library reads and writes them. The "not stored, so not served" warnings are removed. The "Exact values" rule starts here: until this step the reader trims every value, as today. A round-trip test reads a file, writes it, and reads it again, and every field is equal.
4. **Package file reads.** The file listing (`SkillPackage.Resources` in the sketch above; no separate `ListFiles` member is sketched, and step 1 fixes which it is), `ReadFile` and the safe-path rules, with their tests. The Host's `skills.read_resource` tool, its audit and its serving rules (`scripts/` and binary assets refused) come later, from the resources design, and are Host code.
5. **Packaging.** `Format` writes the preserved fields, so a published skill can be exported as a standard directory without losing its metadata.
6. **Publish as a package.** After O-L1 is decided.

## Tests

- **Corpus agreement.** Every corpus case gives the reference validator's outcome, except the divergences listed in step 2.
- **Round trip.** `license`, `compatibility`, `metadata` with arbitrary string keys, and `allowed-tools` are read, written and read again, unchanged, including white space at both ends and inside values, quoted and folded values.
- **Package file reads.** `../x`, an absolute path, `C:\x`, `references/../../x`, a junction or symlink on any path component, `scripts/x` (read as opaque bytes, and unchanged on a round trip; the Host's refusal to serve it is tested in the Host), a file over a caller's cap, invalid UTF-8 (returned as bytes), and names that differ only in case.
- **`SKILL.md` names.** `SKILL.md` is preferred; `skill.md` is read when it is the only file (O-L7). The Host's store reads only `SKILL.md`.
- **Unknown keys.** Each unknown top-level key is an error, with its key and value returned. `ignoreKeys` is not part of the library's API.
- **Trimming divergence.** Storage keeps the parsed value, and the reference's checks on a trimmed name or description run on a copy. Both behaviours are tested.
- **Diagnostics, not exceptions.** `Parse`, `Check` and `Load` return diagnostics for every bad input in the corpus. One test per diagnostic code.
- **Architecture.** The library references only YamlDotNet and the framework.
- **AOT smoke test** (after the verification above).

## Acceptance

- A .NET developer can open a skill directory from the corpus, read its metadata, retrieve a reference file, and see every diagnostic, with no OpenTail package installed.
- The Host and the workbench pass their existing tests, using the library.
- The corpus agreement holds, and the divergences are documented.
- No preserved field and no package file is dropped by a read, a write or a publish.

## Not built

Nothing in this plan is built. The Host and the workbench still use the current code until step 1. The `StandardSkill` behaviour described under "Contracts" is built (committed in the Host); the library, its API and every migration step are planned. No NativeAOT claim is made until a `PublishAot` consumer builds and runs (the verification list above), and the scaffold waits for the gate above.
