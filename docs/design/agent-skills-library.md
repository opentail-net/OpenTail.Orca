# Design: an independent .NET library for Agent Skills

**Status:** planned 2026-11-10. No code yet. Decisions marked **O-** are for the owner; the rest are the proposed defaults.
**System:** a new library, `OpenTail.AgentSkills` (name and repository for the owner, O-L1). It reads, validates and packages skill directories, and preserves every file in them. It does not hold a store or govern anything: Orca keeps its version store, labels, lock and approvals (O-L4).
**Not in this plan:** approval, audit, labels moving through approval, and the MCP tools (these stay in Orca); the workbench's evaluation (stays in the workbench); script execution (excluded); a permissions model (excluded).
**Code basis:** `opentail-orca` on `standard-skill-files` at 4b8a8b7 (committed; not in `main`): `OpenTail.Modules.Skills/Standard/StandardSkill.cs`, `StandardPublish.cs` (both unchanged since 6f1a59b and the same in `main` 6779f6b), `SkillStore.cs` (its strict-type changes are in 4b8a8b7), `SkillText.cs`; `opentail-workbench`: `Formats/NativePackage.cs`, `Formats/Skill.cs`, `Agent/AgentRunner.cs`.
**Scope note (rule 4):** the library is one system. Three of its steps change other systems: step 3 changes Orca's stored format and the identity (store and hashing plans), step 5 changes `Format`, which stays Orca code, and O-L5 moves identity code those plans change. O-L4 (Orca keeps its store) moves no store code. **Order:** the store plan and the hashing plan's decisions land first, in Orca; step 1 here moves the code as it is then, with its tests. O-L4 (recommended: Orca keeps its store) means the store code does not move at all; O-L5 moves the identity code afterwards, unchanged in behaviour. Step 3 needs O-L3 and O-H2 together.

## Purpose

Any .NET developer should be able to take an ordinary Agent Skills directory, load it, validate it, read its metadata, retrieve its resources, and use it with their own agent, without installing Orca or any other OpenTail product. Orca becomes one consumer of the library, and does not own the format.

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

**Conclusion on Unicode names.** The reference implementation accepts Unicode letters. The reviewer is right about the reference validator's behaviour; the specification's own wording is unclear, so what "the standard" requires is not settled. Our reader already accepts them (`StandardSkill.cs`, `Check`: `Rune.IsLetterOrDigit`). Only the save-time rule refuses them (`SKILL-NAME-ASCII`), and that rule is Orca's, not the standard's.

## Contracts this must satisfy (current code)

**`StandardSkill`**

- `Parse(text, ignoreKeys)` returns a `StandardSkillFile` with diagnostics. It does not throw for a bad file; it catches `YamlException` and reports it (`StandardSkill.cs:66-73`). (`Next`, `:378-383`, throws `InvalidOperationException` if the event stream ends early, which is not caught; YamlDotNet's parser should report such input as a `YamlException` first. To confirm with a truncated-input test.) Unknown keys become `SKILL-UNKNOWN-KEY` errors unless listed in `ignoreKeys`, which turns them into `SKILL-KEY-IGNORED` warnings (`:83-89`); ignoring a standard key, or a key not present, is an error (`:76-82`). **Decision (external review): this acknowledgement moves to Orca's publish layer** (`StandardPublish.Prepare`, which already passes it in). The library's `Parse` reports every unknown top-level key as an error, as the reference does, and returns each key with its value as data, so a caller can keep it. See Design.
- `ReadFrontmatter` (`:257-327`, read 2026-11-10) works on parser events and deserialises nothing. It refuses anchors and tags on the root mapping (`:275`), on keys (`:283`), on standard values (`:304`), on `metadata` and its entries (`:337`, `:345`) and anywhere inside an unknown key's value (`:316-318`); aliases as values (`:295`); duplicate keys (`:293`, `:352`); more than one document (`:322-326`); a non-mapping root (`:270`); non-scalar standard values and nested `metadata` (`:308`, `:347`). It does not refuse plain scalars that YAML would type (`name: 123`, `license: true`): they are read as their text. Every value is trimmed (`:305`, `:352`).
- `Check(file, directoryName, saving)` applies the spec's rules, with these differences from the reference:
  1. The name is not NFKC-normalised before its checks (the reference normalises first).
  2. The directory name is normalised, but compared with an un-normalised name (the reference normalises both).
  3. With `saving`, Orca-only rule refuses non-ASCII names, with a suggested name (`SKILL-NAME-ASCII`).
  4. Unknown keys are errors unless the caller opts in (the reference errors too; this matches).
  5. Character classes: `Rune.IsLetterOrDigit` (letters and decimal digits) is narrower than Python's `isalnum` (which also accepts other numeric characters, such as categories Nl and No). Lowercase is tested with `ToLowerInvariant`, not Python's `lower()`; the two differ for some characters. Probable divergences; the corpus decides.
  6. An empty `compatibility` is refused here; see the spec facts above.
- **Value trimming.** The reference strips the required `name` and `description`, and no other value: its optional fields and `metadata` values are returned as written (`parser.py`, read 2026-11-10). Ours trims every value (`StandardSkill.cs:305`, `:352`). This divergence is recorded in step 2, and storage keeps the parsed value as it is (see Design, Exact values).
- **`SKILL.md` name.** Orca reads only `SKILL.md` (`SkillStore.cs:53`). The reference also accepts `skill.md` (see O-L7).
- `Describe(summary, appliesTo)` builds a description from Orca's summary and situations. This is Orca rule, not part of the standard. It stays in Orca; the library gets only the generic description rule (1 to 1024 characters). The workbench calls it directly (`Agent/AgentRunner.cs:159`, the catalog delivery), so the workbench keeps a reference to Orca module for it, or the rule moves to a shared Host-and-workbench place; not to the library.
- `Format(id, version, summary, appliesTo, instructions, bodyPrefix)` writes a standard file, used by the workbench's `NativePackage` (`NativePackage.cs:20`). It calls `Describe` and `Check(..., saving: true)`, so it is Orca packaging, not a standard rule: it stays with `Describe`, and calls the library's generic writer.
- `SuggestSaveName` (public); `SplitFrontmatter` (`internal`, used by `StandardPublish`); `IsSaveable` (`private`). Only `SuggestSaveName` is public API today; the save-time rule and its suggestion are Orca rules.

**`StandardPublish.Prepare`** (`StandardPublish.cs:15`) stores only the body. It warns that `license`, `compatibility`, `allowed-tools` and every metadata key except `version` are "not stored, so not served" (`:46-56`). The stored `skill.json` is Orca's own format.

**`SkillStore.Parse`** reads format 1 and ignores keys it does not read. So an additive optional key is safe for the current Orca reader.

**`SkillText`** (identity): see [skill-content-hashing.md](skill-content-hashing.md). This library takes it over only if O-L5 is decided that way, and only after the hashing plan's decisions; until then it stays in Orca.

## Branch-by-branch: standard frontmatter fields

The columns "Read", "Stored by Orca", "Served" and "Exported" describe the code as built today. "Library requirement" is planned.

| Field | Read | Stored by Orca | Served | Exported | Library requirement |
|---|---|---|---|---|---|
| `name` | yes | as the skill id, ASCII only at save | as `id` | yes | read Unicode letters after NFKC (the reference's behaviour) |
| `description` | yes (trimmed) | derived from summary and appliesTo; must equal the built text at publish, for a new version with frontmatter (`StandardPublish.cs:34-39`) | as `summary` and `appliesTo` | yes | read as given; whether surrounding white space is kept is fixed with the round-trip test (today every value is trimmed) |
| `license` | yes | no (warning) | no | no | **preserve** |
| `compatibility` | yes | no (warning) | no | no | **preserve** |
| `metadata` | yes; only `version` is checked, against the version | no (the store keeps no frontmatter) | no | `version` only (written by `Format`) | **preserve** as an ordered string map, arbitrary keys |
| `allowed-tools` | yes | no (warning) | no | no | **preserve** as a descriptive string; never authority |
| body (`SKILL.md`) | yes | yes, body only | yes, as `instructions` | yes | read and write exactly |
| `references/` | no (today Orca reads no folder of a skill) | no (planned: text resources, per the resources design) | no | no (`NativePackage.Write` writes `SKILL.md` only) | **read and preserved as opaque bytes** |
| `assets/` | no | no (planned: text only; binary assets refused by Orca, D1) | no | no | **read and preserved as opaque bytes** |
| `scripts/` | no | no (planned: Orca refuses it, D3) | no | no | **read and preserved as opaque bytes; never executed** |
| other top-level keys | error or ignored, by caller | no | no | no | as the caller decides |

## Design

**Project layout (proposed; confirm in step 1).**

```
OpenTail.AgentSkills/                  net10.0 (O-L6); no reference to Host or Microsoft.Extensions.AI
  Frontmatter/                         SKILL.md reader and writer; diagnostics
  Directory/                           SkillDirectory: open, list, safe reads
  Model/                               SkillPackage, SkillMetadata, PackageEntry, SkillDiagnostic
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
    public static string Write(SkillMetadata metadata, string body);                                    // round-trips every preserved field, including ExtensionFields
}

public sealed class SkillDirectory
{
    public static SkillDirectory Open(string path, SkillOpenOptions? options = null);   // refuses reparse points on any component
    public SkillPackage Load();                                                       // metadata, body, diagnostics
    public ReadOnlyMemory<byte> ReadFile(string relativePath, long maxBytes);   // a file of any name in the package, as opaque bytes; maxBytes is required; see below
    public IReadOnlyList<PackageEntry> ListFiles();   // every entry of any name, including scripts/ and other folders; never filtered by folder
    public Stream OpenFile(string relativePath, long maxBytes);   // streaming, with the same path and reparse rules; never reads past maxBytes
    public IReadOnlyList<PackageEntry> ExportTo(string destination, bool overwrite);   // copies every entry as opaque bytes; refuses to overwrite unless overwrite is true; executes nothing
}

public sealed record SkillMetadata(string Name, string Description, string? License, string? Compatibility,
    IReadOnlyDictionary<string, string> Metadata, string? AllowedTools,
    IReadOnlyDictionary<string, string> ExtensionFields);   // unknown top-level keys and their scalar values, as read; a sequence or mapping value is a diagnostic, not a loss (decided 2026-11-10); empty for a standard file
public sealed record SkillPackage(SkillMetadata Metadata, string Body, IReadOnlyList<SkillDiagnostic> Diagnostics);   // no resource list: ListFiles is the one listing
public sealed record PackageEntry(string RelativePath, PackageEntryKind Kind, long Bytes);   // every file and folder under the root, sorted by RelativePath
public enum PackageEntryKind { File, Directory }
```

`Metadata` is a dictionary the caller can read in full. `AllowedTools` is a string the caller may show, never a permission.

**Exact values.** The library stores each value as the YAML parser returns it: a quoted scalar keeps its surrounding spaces, and nothing is trimmed for storage. The checks that the reference makes on a trimmed name or description run on a copy. Round-trip tests cover white space at both ends and inside values, and folded multi-line values.

**Package file reads (library side).**

- The path is relative, with forward slashes; it has no root, no drive letter, no `..` segment, and no colon in any component. On NTFS a colon in a file name addresses an alternate data stream (for example `notes.md:hidden`), so a colon is refused in any component, not only as a drive letter.
- Any file or folder in the package may be read: `references/`, `assets/`, `scripts/` and other folders alike. The library returns bytes; it does not interpret, parse or run them. Which files a deployment serves is the caller's decision. Orca refuses `scripts/` and binary assets (resources design D1 and D3), and that rule is Orca code, not library code.
- Every path component, and the final file, must not be a symbolic link, junction or other reparse point. Windows junctions and Unix symlinks are both refused. Checked in the .NET API documentation (`FileSystemInfo.LinkTarget`, 2026-11-10): "Symbolic links and junctions are the types of links this API can resolve"; for any other reparse point it returns null. So the check tests `FileAttributes.ReparsePoint` on each component, not `LinkTarget` alone. Still to verify by test: that .NET reports `ReparsePoint` for a Unix symlink, and the effect on folders synced by cloud clients (OneDrive placeholders are reparse points, and would be refused).
- After resolution, the full path must still lie under the skill root.
- A size cap is an argument of the call. Orca passes the resources design's limits (D2: 64 KiB per file); the library imposes no cap beyond the one the caller passes. Decoding bytes as text is the caller's job; Orca refuses invalid UTF-8 for resources (D1).
- Names that differ only in case must not both resolve to the same file on a case-insensitive file system. To test on Windows.
- The library does not audit. Orca audits its own tool that calls it (`skill_resource_served`, per the resources design).

**Concurrent modification.** The path rules check a path before it is opened, so another process could replace a component between the check and the open (a time-of-check to time-of-use race). Two options, decided as O-L8. The sketch above is written for (a), the simpler one; O-L8 recommends (b) if it passes verification. Option (a): the package directory is treated as unchanged while it is read. `Open` states that precondition, and `ListFiles`, `ReadFile`, `OpenFile` and `ExportTo` give a diagnostic when they detect a change (a size or write time that differs from the listing). Detection is not prevention, so this is a stated precondition, not a guarantee. Option (b) is a safe open: after opening, confirm that the opened file is the one the path check examined, on Windows by the volume serial number and file index of the handle, on Unix by device and inode. It is platform-specific and adds code, and its .NET mechanisms are to verify before it is chosen.

**Store (O-L4).** Recommended, changed after the external review: Orca keeps its store. Labels, immutable version folders, `.publish.lock` and the label rules are governance, not part of the standard, so they stay in Orca. The library reads and preserves standard directories and packages, and does not hold a store. The workbench uses the library to read and write packages. It keeps using Orca's store when it tests Orca's serving behaviour; its `ScenarioRunner` publishes to a temporary Orca store today (`ScenarioRunner.cs:144`), and that stays.

**Preserved fields (O-L3).** `skill.json` gains optional keys: `license`, `compatibility`, `metadata` (a string map) and `allowedTools` (a string). Orca reader ignores unknown keys today (`SkillStore.Parse` reads named keys only), so an old reader reads a new file, but the new keys are dropped. Recommended: remain on format 1, documenting the additive keys. The format number changes only for incompatible changes. Two effects to state with the change: the new keys are outside `ContentHash` until O-H2 adds them, so a report binding does not cover them; and they are inside the republish comparison (today the normalised `skill.json` text; see O-H6), so changing one at the same version is refused.

**Unicode names (O-L2).** The library accepts the reference's Unicode letters after NFKC. Orca's store identifiers stay ASCII (`SkillStore.IsId`, `^[a-z0-9]+(-[a-z0-9]+)*$`). A skill with a non-ASCII name is read by the library, and refused by Orca at save with the existing suggested name. Whether Orca identifiers should follow the standard is for the owner.

**What the library must not contain.** Orca types (`IHostModule`, `ModuleManifest`, `ToolDescriptor`); Microsoft.Extensions.AI; ModelContextProtocol; any code that runs a script.

**Dependencies.** YamlDotNet (Orca's central pin is 17.1.0). Nothing else. An architecture test enforces the list, as `OpenTail.Orca.Abstractions.Tests/ArchitectureTests.cs` does for modules.

## Gate before any code

The scaffold waits for O-L1 (licence, repository, package name), which the owner has decided for now (see O-L1), and for O-L6, which has a recommendation now (net10.0; see O-L6, to be confirmed by the owner against Stingray's `Directory.Build.props`). It also waits for O-L4 to be confirmed, as the gates table in `skills-plan.md` says: the library reads and preserves packages; Orca keeps the store and the governance. And it waits for O-L8 (concurrent modification), which the same table lists, because the choice shapes `Open` and the read methods. Step 0 below records that gate.

## Decisions for the owner

- **O-L1 Licence, repository and package name.** Decided by the owner, 2026-11-10: for now the library uses Orca's own licence, PolyForm Strict (`LICENSE-HOST.md`), so the owner can reuse it in other projects. The licence can be commercial, as the parent solution is. A more permissive licence can come later, since that move is the easy direction. Before any such move, confirm that the copyright of every copied line is the owner's alone, or that the other holders can relicense it (to verify for any contributor other than the owner). The external review's report that Stingray declares MIT is not checked here; it matters only if Stingray is to ship the library, which is a later question. The repository and package name remain to be chosen.
- **O-L2 Unicode names.** Recommended: the library accepts them; Orca identifiers stay ASCII for now.
- **O-L3 Additive fields.** Recommended: keep format 1 and document the keys.
- **O-L4 Store owner.** Recommended, changed after the external review: Orca. The store is governance (labels, immutable version folders, the lock, the label rules), not part of the standard; the library reads and preserves packages and holds no store.
- **O-L5 Identity owner.** Recommended: the library, after the hashing plan's decisions (O-H1 to O-H6).
- **O-L6 Target framework.** Recommended: net10.0. Orca (`OpenTail.Modules.Skills.csproj:3`) and the workbench (`Directory.Build.props:3`) use it, and the external review reports that Stingray's `Directory.Build.props` targets net10.0 too. That report is not checked from this repository; the owner confirms it before the gate closes. Choose an older target only if a consumer needs one.
- **O-L7 `SKILL.md` name.** The reference accepts exactly `SKILL.md` and `skill.md`, and tries `SKILL.md` first. Accept both in the library (recommended, as the reference does), with `SKILL.md` preferred? Orca keeps writing `SKILL.md` and reads only that name (`SkillStore.cs:53`).
- **O-L8 Concurrent modification.** Option (a) does not protect against a process that changes the package while it is read; it detects some changes, and a change made in the gap between two checks goes unnoticed. That is an acceptable threat model only if callers read packages that cannot be written during the read. Recommended, as the external review also prefers: (b), the safe open, for a general-purpose library that reads downloaded packages, provided its .NET mechanisms pass verification (not yet done); if they fail, (a) with the precondition stated. The owner decides.

## Verification still to do

- NativeAOT: whether YamlDotNet's use in `StandardSkill` survives a `PublishAot` build of a small consumer. Not verified; claimed nowhere until tested. What is known from the code: `StandardSkill` uses only `YamlDotNet.Core` and `YamlDotNet.Core.Events` (the event parser), not the reflection-based serializer, which narrows the question. If it fails, the frontmatter syntax needed is small, but YAML's traps (aliases, tags, anchors) are why the reader was built event-based. A replacement parser would need the same refusals.
- Windows junction handling in `ReadFile` (see above).
- The reference validator's behaviour on the corpus, run from a clean Python environment. The validator is a command-line tool (`skills-ref validate`); its output is the expected outcome for each corpus case.

## Migration (each step ships with its tests)

0. **Gate.** O-L1 (decided for now, see above) and O-L4 (the boundary above) are decided, O-L6 is confirmed, and O-L8 is chosen, and the gates table in `skills-plan.md` records them. No code before this.
1. **Extract with no behaviour change.** Move the standard parts of `Standard/` (`Parse` without `ignoreKeys`, which Orca's `StandardPublish.Prepare` applies; `ReadFrontmatter`; `SplitFrontmatter`; `Check` without the save-time rule; the diagnostics types) and their tests into the library. `Describe`, `Format`, the save-time ASCII rule (`SKILL-NAME-ASCII`, `SuggestSaveName`) and `StandardPublish` stay in Orca, calling the library (`StandardPublish.Prepare` calls `SkillStore.Parse`, Orca type). Orca and the workbench reference the library. What Orca and the workbench do does not change: values are still trimmed as today (exact values come in step 3), and Orca's tools return what they returned. The API does change in one place, so "unchanged" is not literally true of the tests: tests that call `StandardSkill.Parse` with `ignoreKeys` are retargeted at Orca's publish layer with the same assertions, and every other existing test passes as written. The count of retargeted tests is recorded in the step. Add the architecture test.
2. **Align the name rules with the reference.** NFKC on the name before the checks, and on both sides of the directory comparison. Add the corpus and its expected outcomes. Record each remaining divergence, with its reason, in the library's README. The save-time ASCII rule stays Orca rule.
3. **Preserve the fields.** Orca's store and the identity gain the optional keys (O-L3, O-H2); the library reads and writes them. The "not stored, so not served" warnings are removed. The "Exact values" rule starts here: until this step the reader trims every value, as today. A round-trip test reads a file, writes it, and reads it again, and every field is equal.
4. **Package file reads.** `ListFiles`, `ReadFile`, `OpenFile`, `ExportTo`, the safe-path rules and the concurrent-modification rule, with their tests. Orca's `skills.read_resource` tool, its audit and its serving rules (`scripts/` and binary assets refused) come later, from the resources design, and are Orca code.
5. **Packaging.** `Format` writes the preserved fields, so a published skill can be exported as a standard directory without losing its metadata.
6. **Publish as a package.** After O-L1 is decided.

## Tests

- **Corpus agreement.** Every corpus case gives the reference validator's outcome, except the divergences listed in step 2.
- **Round trip.** `license`, `compatibility`, `metadata` with arbitrary string keys, and `allowed-tools` are read, written and read again, unchanged, including white space at both ends and inside values, quoted and folded values.
- **Package file reads.** `../x`, an absolute path, `C:\x`, `references/../../x`, `notes.md:hidden` and `references/a.md::$DATA` (alternate data streams), a junction or symlink on any path component, `scripts/x` (read as opaque bytes, and unchanged on a round trip; Orca's refusal to serve it is tested in Orca), a file over a caller's cap, invalid UTF-8 (returned as bytes), and names that differ only in case.
- **`SKILL.md` names.** `SKILL.md` is preferred; `skill.md` is read when it is the only file (O-L7). Orca's store reads only `SKILL.md`.
- **Unknown keys.** Each unknown top-level key is an error, with its key and value returned. `ignoreKeys` is not part of the library's API.
- **Extension fields.** An unknown key's name and value are in `ExtensionFields`, and `Write` puts them back unchanged. A sequence or mapping value gives a diagnostic naming the key; it is neither stored as text nor dropped. Decided 2026-11-10: library v1 extension fields are scalar-only. The standard `metadata` map stays string-to-string and is a different thing; a recursive value type waits for a real client that needs one.
- **Listing and export.** `ListFiles` returns every file and folder, of any name, including `scripts/` and folders no other rule names. `ExportTo` copies every entry byte for byte and refuses to overwrite unless asked. A package with a file in an unnamed folder keeps that file through a round trip.
- **Bounded reads.** `ReadFile` and `OpenFile` refuse a file over `maxBytes`, and `OpenFile` never reads past it.
- **Concurrent change.** A file changed between listing and reading gives a diagnostic (option a), tested by changing a file during a read. If O-L8 chooses (b): a file replaced between the path check and the open is refused, on Windows and on Unix.
- **Trimming divergence.** Storage keeps the parsed value, and the reference's checks on a trimmed name or description run on a copy. Both behaviours are tested.
- **Diagnostics, not exceptions.** `Parse`, `Check` and `Load` return diagnostics for every bad input in the corpus. One test per diagnostic code.
- **Architecture.** The library references only YamlDotNet and the framework.
- **AOT smoke test** (after the verification above).

## Acceptance

- A .NET developer can open a skill directory from the corpus, read its metadata, retrieve a reference file, and see every diagnostic, with no OpenTail package installed.
- Orca and the workbench pass their existing tests, using the library.
- The corpus agreement holds, and the divergences are documented.
- No preserved field and no package file is dropped by a read, a write or a publish.

## Not built

**Built: the gate (step 0, provisionally) and step 1, on Orca, merged to `main` at `b2d7870`** (branch `feat/agentskills-library`, commit `3348c9c`; Orca suite passes on the merged state, 1154 tests, none skipped).

- **Gate, provisional decisions** (for the owner to confirm or reverse): O-L1 as decided (PolyForm Strict, Orca's licence); O-L4 Orca keeps the store (recommended); O-L6 net10.0 (recommended; the owner's confirmation of the consumers is not yet recorded); O-L8 option (b), the safe open, **chosen provisionally; partly verified on Windows on 2026-11-11** (scratch probes, not in the repository): (1) a junction is created without privilege, and .NET follows it when reading: a file read through `package\junc\secret.md` returned the content of a file outside the package; (2) the directory enumeration lists files behind a junction as part of the package; (3) the link's own attribute shows a reparse point, but the files beneath it do not, so checking only the last part of a path misses a junction in the middle; (4) `FILE_FLAG_OPEN_REPARSE_POINT` does not help with a junction in the middle of a path (the flag applies only to the last part); (5) `GetFinalPathNameByHandleW` on the opened handle reports the real target, so a check on the handle detects it; (6) a recursive delete of a folder holding a junction removes the target's contents, while a non-recursive delete of the junction alone leaves the target intact; (7) file and directory symbolic links could not be created without privilege, so they are **not tested**; (8) a colon in a file name writes an alternate data stream that enumeration lists as a file name, while reading the file ignores it. The design that follows from this: read content only through a handle whose final path is checked to lie inside the package before any byte is read; reject reparse points in every part of a path during walks; never recurse through a reparse point in any delete; reject colons in names. Symbolic links still need an elevated or developer-mode test. If the handle check cannot be made reliable, the fallback is option (a) with its precondition. O-L3 and O-L5 as recommended.
- **Step 1, extraction with no behaviour change**: `OpenTail.AgentSkills` (net10.0, YamlDotNet only) holds `StandardSkillFile`, `StandardDiagnostic`, `SplitText`, the frontmatter reader, the field rules and the directory rule (`AgentSkill`). An architecture test (`AgentSkillsLibraryTests`) checks that the library references no OpenTail assembly. Orca keeps the save-time name rule, `Describe`, `Format` and `SuggestSaveName` in `StandardSkill`, which delegates to the library and keeps its public surface, so no call site changed.
- **Deviations from step 1 as written, recorded**: the `ignoreKeys` parameter moved into the library, not left in Orca, because it is generic; nothing was retargeted (the call sites go through the facade); the library's tests were not moved into a library test project (they stay in Orca suite); and the ordering of the save-time name diagnostic changed (it now follows the library's diagnostics).

**Not built, and why:**
- **The workbench's reference to the library** (step 1 says the workbench references it): another repository, not changed here. Its code still reaches the standard through Orca's facade, which is unchanged, so it is not broken by this pass.
- **Step 2, built** (merged to Orca `main` at `88056d0`, branch `feat/agentskills-names`, commit `b00247b`): names are checked after NFKC, the length counts normalised code points, and the directory is compared after NFKC; Orca's save-time rule judges the same normalised name. The corpus is **our own** (18 cases, written from the rules recorded above); the reference's corpus was not fetched, so the step is not yet confirmed against it. Three cases fail on the previous library, as intended. The divergences and their reasons are in the library's README.
- **Step 3, built** (merged to Orca `main` at `b6b6234`, branch `feat/agentskills-preserve`, commit `bcffdd5`): `license`, `compatibility`, `allowed-tools` and `metadata` are stored in `skill.json` with the version (additive keys; format 1 unchanged, O-L3 as recommended), merged from the frontmatter, refused on a conflict, covered by the v2 identity (O-H2 as recommended), and kept exactly as written. The "not stored" warnings are gone. **Deviation**: `name` and `description` keep trimming (the description is compared with the summary's wording, and the name with the directory); the plan said exact values from this step, and the three text fields and the metadata are exact. The v1 identity and what Orca writes as its identity are unchanged (O-H5 still open).
- **Step 4, package file reads** (`ListFiles`, `ReadFile`, `OpenFile`, `ExportTo`, the safe-path rules and the concurrent-modification rule), and the verification of option (b): not built.
- **Steps 5 and 6** (packaging and publishing as a package; step 6 waits for the owner's publication decision, which O-L1 does not give): not built.
- **Resources** (`skill-resources.md`, work order step g): held. It depends on step 4 and on the owner decisions D1 to D6 in that plan.
- **No NativeAOT claim** is made until a `PublishAot` consumer builds and runs (the verification list above).
