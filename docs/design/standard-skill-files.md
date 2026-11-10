# Design: standard skill files

**Status:** phases 1 and 2 built 2026-11-09, committed in `opentail-host` (`dc957f7`) and `opentail-workbench` (`e83e469`). Decisions 1, 2, 5 and 6 are the owner's, confirmed 2026-11-09. Decisions 3, 4 and 7 were taken provisionally by Claude on the same day, for the owner to review. Written from reading `opentail-host` at `6081d5b`, `opentail-workbench` at `6d3e220`, the [Agent Skills specification](https://agentskills.io/specification) (fetched 2026-11-09), and the reference validator `skills-ref` (its `validator.py` and `parser.py`, fetched 2026-11-09; its test fixtures are not yet read). The implementation record is at the end.

**Where it fits:** the Host's skills module (`OpenTail.Modules.Skills`), with the workbench's package writer as the one consumer outside it. It adds no package, no repository, no catalog, no loader and no runtime. Skills stay where they are: the Host's store, its labels, its approval and its `skills.get`.

## The problem

The Host keeps each version as `skill.json` plus a `SKILL.md` holding plain instructions, with no frontmatter. The workbench already writes standard `SKILL.md` files for native clients (`NativePackage.cs`), but nothing checks them against the standard. So:

- A version can be published whose standard form is invalid, for example a summary too long for the 1024-character description limit. The problem shows up only when someone packages it.
- The export logic has no test against the specification.
- Native clients get `name` and `description` from a string generator, not from a checked rule.

The fix is one reader, validator and formatter for the standard form, used at publish and at export. How skills are stored, labelled, approved and served does not change.

## Scope

**In:**

1. A reader, validator and formatter for the standard `SKILL.md` form, in `OpenTail.Modules.Skills/Standard/`.
2. A publish-time check that a proposed version's standard form is valid, including a frontmatter block a caller puts at the top of the instructions (see "Frontmatter in a publish request").
3. The workbench's package writer uses the formatter.

**Out:**

- Loading and unloading. `skills.get` already serves a version. Unloading is the agent's decision, not the Host's.
- Discovery from folders, and resource files (`references/`, `assets/`). Neither is needed while every skill is one file.
- Script execution. Never.
- `allowed-tools` being acted on. See decision 3.
- Changing how skills are authored, unless the owner decides to re-bake them (phase 4).
- A separate package or repository.

## Contracts this must satisfy

**The standard** (agentskills.io, 2026-11-09):

- `name`: 1 to 64 characters; lowercase letters, digits and hyphens; no leading or trailing hyphen; no `--`; equal to the skill directory's name.
- `description`: 1 to 1024 characters, non-empty; says what the skill does and when to use it.
- `compatibility`: optional, 1 to 500 characters.
- `license`: optional string.
- `metadata`: optional map from string keys to string values.
- `allowed-tools`: optional, space-separated, "pre-approved tools the skill may use" (experimental).
- The body after the frontmatter is free Markdown. `scripts/`, `references/` and `assets/` are optional.

**The reference validator** (`skills-ref`, source as fetched):

- The name check is `c.isalnum()` over each character, so it accepts non-ASCII letters. Uppercase fails through `name != name.lower()`. The spec's prose says "unicode lowercase alphanumeric"; its examples say `a-z`.
- Lengths are Python `len()`, which counts code points, not UTF-16 units.
- `validate` rejects frontmatter keys outside `name`, `description`, `license`, `allowed-tools`, `metadata` and `compatibility`. `read_properties` silently drops them.
- Metadata values are not type-checked.
- The frontmatter is split with `content.split("---", 2)`, which is not line-anchored. There is no BOM handling. The body is stripped. The YAML is read with `strictyaml`.

**Where this design departs from the reference, by decision:**

| Point | Reference | This design |
|---|---|---|
| Names on read | Any Unicode letter or digit | The same |
| Names on save | Any Unicode letter or digit | ASCII only, with a suggested ASCII name in the refusal (decision 1) |
| Unknown top-level keys | Error in `validate`, dropped by `read_properties` | Error, which the caller can acknowledge by name (decision 2) |
| BOM | Fails | Removed on read |
| `---` delimiters | Split anywhere | Must be on lines of their own |
| `metadata` values | Not checked | Must be strings, as the spec says |

**The Host store** (`OpenTail.Modules.Skills`):

- `SkillStore.Parse(metadataJson, instructions, id, version)` requires `id`, `version`, a non-empty `summary` and non-blank instructions. `appliesTo`, `tools`, `policies` and `owner` are optional. Its `instructions` is the whole `SKILL.md` text.
- `SkillStore.IdPattern` is `^[a-z0-9]+(-[a-z0-9]+)*$`, with no length cap. `VersionPattern` is `^[1-9][0-9]{0,8}$`. A Host id is therefore always ASCII already.
- `SkillStore.Publish`: a version's files never change; a label moves only if `expectedFrom` still matches.
- `SkillText.ContentHash` covers id, version, summary, appliesTo, tools, policy references, owner and instructions. Report bindings (`skill-report: <id>@<version> <hash>`) are checked against it at publish, and the workbench computes the same line (`Skill.BindingLine`).
- `SkillsModule.PublishPreview` validates a request before it is queued, and refuses with `ArgumentException`.

**The workbench writer** (`src/OpenTail.Workbench/Formats/NativePackage.cs`):

- `NativePackage.Write(skill, outFolder, checkIn)` writes `<outFolder>/<id>/SKILL.md`. Frontmatter: `name: <id>` and `description: <JSON string>`. Then the instructions, trimmed, with a check-in paragraph when `checkIn` is set.
- `NativePackage.Description` joined the summary and `appliesTo` (see the table below). It has been replaced by the Host's `StandardSkill.Describe`, with the same wording.
- The test `A_native_package_has_front_matter_a_check_in_and_the_instructions` (`tests/OpenTail.Workbench.Tests/DeliveryTests.cs`) asserts the start of the output, the description wording and the check-in text.

**Consumers:** native clients read `name` and `description` to decide whether a skill applies. An agent following the check-in calls `skills.get`.

## Existing behaviour, branch by branch

`NativePackage.Description` today, and what it becomes:

| Branch | Today | Under this design |
|---|---|---|
| Summary ends with `.` | The period is trimmed, then one is added | Same |
| `appliesTo` has one item | `Use when X.` | Same |
| `appliesTo` has several items | `Use when X; or when Y.` | Same |
| `appliesTo` is empty | `Use when .`. `Parse` allows an empty list, so this can happen | Refused at publish (new versions) and at export (any version, because `Describe` refuses it). A description that says "when to use" with nothing to say is a defect |
| Text of 1001 to 1024 characters | Cut to 997 characters and `...` appended, although the standard allows 1024 | Passed through unchanged |
| Text over 1024 characters | Cut to 997 characters and `...` appended. The cut can split a UTF-16 surrogate pair | Refused at publish and at export, with a diagnostic. Silent truncation is not allowed (decision 5) |
| `checkIn` true or false | Check-in paragraph added or not | Same |
| Folder | `<outFolder>/<id>` | Same. It is what makes the directory-name rule hold |

The three existing skills all have `appliesTo` entries (2, 3 and 3), and their summaries are 88, 113 and 118 characters, so none of them hits a refused branch.

## Design

### Components (`OpenTail.Modules.Skills/Standard/`)

- **`StandardSkillFile`** (record): the parsed form. Fields: `Name`, `Description`, `Compatibility`, `License`, `Metadata` (string to string), `AllowedTools`, `Body`, `Diagnostics`.
- **`StandardSkill`** (static class) holds the rules:
  - **`Parse(text, ignoreKeys)`** returns a `StandardSkillFile` with its diagnostics, rather than throwing on the first error, so a refusal can list them all. A leading BOM is removed. The `---` delimiters must be on lines of their own. The YAML is read with YamlDotNet's event parser, not object deserialisation: anchors, aliases, tags and multiple documents are errors; duplicate keys are errors; every value must be a string, except that `metadata` must be a mapping of strings. Unknown top-level keys are errors (see "Unknown frontmatter keys").
  - **`Check(file, directoryName, saving)`** applies the field rules. `saving: false` is the rule for reading: any Unicode letter or digit, lowercase, no leading, trailing or doubled hyphen, at most 64 code points. `saving: true` also requires `a-z`, `0-9` and hyphens only, and gives a `Suggestion` (see "Suggested names"). `directoryName` null skips the directory rule; otherwise the name must equal the directory name after NFKC normalisation of the directory name, as the reference does. `description` is 1 to 1024 code points and not blank. `compatibility`, if present, is 1 to 500 code points.
  - **`Describe(summary, appliesTo)`** returns the description text and the refusal, if any. It refuses a blank summary, an empty `appliesTo` or a blank entry in it, and any text over 1024 code points.
  - **`Format(id, version, summary, appliesTo, instructions, bodyPrefix)`** returns a `StandardFormat`: the text, or the errors that refused it. `name` is the id, `description` comes from `Describe`, `metadata` holds `version` (decision 6), there is no `allowed-tools`, and the body is the instructions trimmed. `bodyPrefix` goes between the frontmatter and the body, for the workbench's check-in. The text is read back and checked before it is returned.
  - **`SuggestSaveName(name)`** returns a suggested ASCII name, or none.
- **`StandardPublish.Prepare(id, version, skillJson, instructions, isNewVersion, ignoreKeys)`** (`StandardPublish.cs`) decides what a publish request's instructions become. It throws `ArgumentException` with every refusal, each tagged with its code, or returns the stored `SkillVersion` and the warnings for the change request.

YAML library: YamlDotNet 17.0.1, the version the workbench already pins, now also pinned in the Host's `Directory.Packages.props`. The Host had no YAML library before. A hand-written reader for the subset the standard needs was rejected: the plan's own rule is not to write a homemade YAML parser.

### Name rules: reading and saving

| Rule | Reading (any file) | Saving (publish, export) |
|---|---|---|
| Characters | Any Unicode letter or digit, lowercase | ASCII `a-z`, `0-9` and `-` only (decision 1) |
| No leading, trailing or doubled hyphen | Yes | Yes |
| At most 64 code points | Yes | Yes |
| Equals the directory name (NFKC on the directory name) | Yes | Yes |

Because a Host id is already ASCII (`IdPattern`), the ASCII rule can only bite on a name that arrives inside a publish request's frontmatter, or in a file read from elsewhere. That is why the frontmatter rules below matter.

### Suggested names

`SuggestSaveName` does the following, in order:

1. Replaces letters with no decomposition by fixed pairs, after lowercasing: `ß`→`ss`, `æ`→`ae`, `œ`→`oe`, `ø`→`o`, `đ`→`d`, `ł`→`l`.
2. Decomposes (NFKD) and drops combining marks.
3. Lowercases with the invariant culture.
4. Replaces each run of characters outside `a-z` and `0-9` with one hyphen.
5. Trims hyphens from both ends, cuts to 64 code points, and trims hyphens again.
6. Returns the result only if it is non-empty. Otherwise it returns none, and the refusal asks the caller to choose a name. A result made this way always passes the saving checks.

Examples, now tests: `Café Review` → `cafe-review`; `Überweisung-prüfen` → `uberweisung-prufen`; `Straße` → `strasse`; `--Invoices--` → `invoices`; `日本語` → none.

The suggestion is returned to the caller and never applied. The caller chooses the id, and if the folder is named after the old name, the folder must be renamed too.

### Unknown frontmatter keys

`Parse` reports each unknown top-level key as an error, `SKILL-UNKNOWN-KEY`, with the key name. The caller may acknowledge keys by name (decision 2):

- `skills.publish` takes an optional `ignoreFrontmatterKeys`: a list of exact, case-sensitive key names.
- Every listed key must appear in the file. A listed key that is absent is refused, so a typo cannot pass silently. A listed standard field (`name`, `license` and so on) is refused too: only unknown keys can be ignored.
- An acknowledged key is dropped and reported as a warning in the change request, so the reviewer sees it.
- There is no "ignore all" and no pattern. The action's arguments are already audited.
- Export has no such option. It generates its own frontmatter and never reads an incoming one.

### Frontmatter in a publish request

A publish request's `instructions` may begin with a frontmatter block (provisional, decision 7). When it does:

- The block is read with `Parse` (with `ignoreFrontmatterKeys`) and checked with `Check(..., saving: true)`.
- `name` must equal `skillId`. Otherwise the request is refused, with the suggested name if the frontmatter name is non-ASCII.
- `description` must equal the text `Describe` produces from `summary` and `appliesTo`. Otherwise the request is refused, and the derived text is shown. One source for the description: the store's `summary` and `appliesTo` decide it.
- `metadata.version`, if present, must equal the version.
- `license`, `compatibility`, `allowed-tools` and any other metadata are not stored. Each is reported as a warning in the change request.
- The stored instructions are the text after the closing delimiter, with leading blank lines removed.

A request without frontmatter behaves as today, and its instructions are stored exactly as sent.

### Where the checks run

1. **Publish preview** (`PublishPreview.PreviewAsync`). When a request supplies `skillJson` and `instructions` for a new version, `StandardPublish.Prepare` runs the frontmatter rules when frontmatter is present, and otherwise runs `StandardSkill.Format` on the instructions. It refuses with the diagnostics and suggestions. A rollback to an existing version is not re-checked: its files cannot change, and it may predate this check.
2. **Execution** (`SkillsModule.Publish`). It runs `StandardPublish.Prepare` again, so the stored instructions are the ones the reviewer saw, and passes the result to the store.
3. **Export** (`NativePackage.Write`). It calls `StandardSkill.Format` before writing, which checks the text it writes, and throws on an invalid skill.
4. **Store** (`SkillStore.Parse`). Unchanged. Old versions keep loading.

### What is stored

For a new version without frontmatter, the instructions are stored as sent, as today. For a new version with frontmatter, the instructions are stored as the body only. The standard form is generated when needed and never stored. Existing versions are not rewritten, so the content hash of every existing version stays the same, and no migration is needed.

### What must not change

- The `ContentHash` inputs. No field is added or removed.
- The stored files of existing versions.
- The `skills.get` response, in phase 1. A field can be added later, additively (phase 3).
- `SkillStore.IdPattern`. The 64-character limit lives in the standard rules, so the store's own contract is untouched. An id over 64 characters can still be stored, but it cannot be exported. None exists in the repository (see verification).

## Decisions

**Confirmed by the owner, 2026-11-09:**

1. **Names: ASCII only on save.** Reading accepts any Unicode letter or digit, as the reference does. A refusal to save returns a suggested ASCII name to the caller.
2. **Unknown top-level keys are errors.** The caller can acknowledge specific keys by name (`ignoreFrontmatterKeys`), and they are then reported as warnings. The mechanism is in "Unknown frontmatter keys".
5. **An over-length description is refused, not truncated.**
6. **`metadata.version` carries the Host's version**, as the spec's own example does.

**Provisional: taken by Claude on 2026-11-09 for the owner to review, not yet confirmed:**

3. **`allowed-tools` is never generated.** The standard says it pre-approves tools. Our `tools` list says what a skill uses; approval is policy's job, and skills never change policy. Generating `allowed-tools` from `tools` would tell a client to pre-approve `mail.send`. Implemented: `Format` never writes it, and an incoming one is a warning on publish, never acted on.
4. **An empty `appliesTo` is refused at publish**, for new versions, and at export. The three existing skills are unaffected. Implemented in `Describe`, so it applies to both.
7. **Frontmatter in a publish request is accepted**, with the rules in "Frontmatter in a publish request". The owner did not address it directly. The main alternative is to refuse frontmatter in publish requests until phase 4. That would leave decisions 1 and 2 with no input until then, because Host ids are already ASCII.

## Phases

**Phase 1: the standard reader, validator and formatter.** Built 2026-11-09: `StandardSkill` (`Parse`, `Check`, `SuggestSaveName`, `Describe`, `Format`) and `NativePackage` calling `Format`. No change to the store.

Acceptance: `SKILL.md` for each of the three existing skills is the same as before, except for the new `metadata` block and the line endings, which changed (see the implementation record). Checked by writing the packages before and after and comparing them.

**Phase 2: publish check.** Built 2026-11-09: `StandardPublish.Prepare` at preview and at execution, `ignoreFrontmatterKeys` on `skills.publish`, and the `frontmatterWarnings` field in the change request.

**Phase 3 (optional, owner's call):** `skills.get` adds `name` and `description` fields. Additive. Not built.

**Phase 4 (only if the owner decides to re-bake the skills):** authoring moves to the standard form. Not designed here. What would have to be revisited:

- `Instructions` must mean the body, with the frontmatter stripped before hashing, so the hash does not depend on how the frontmatter is written. Phase 2 already stores the body for frontmatter requests.
- `name` must equal the id, and the folder rule must hold in the store.
- `description` is either authored or derived from `summary` and `appliesTo`. If both are kept, a drift check is needed.
- Existing versions are not rewritten.

## Test plan

Implemented in `OpenTail.Host.Tests/StandardSkillTests.cs` and `StandardPublishTests.cs` (45 cases with the theories, all passing in the scratch run). The list below is the intent; the implementation record says which items are not tests.

**Reader:** BOM; CRLF; quoted and folded descriptions; a colon and a `#` inside quotes; duplicate keys; unknown keys, reported as errors with the key name; a non-string or nested metadata value; missing or unclosed delimiters; anchors, aliases, sequences and tags; a non-ASCII name read without error and refused on save with a suggestion; a description of 1024 emoji, which is 2048 UTF-16 units, accepted.

**Suggestions:** the examples under "Suggested names"; a 70-character name cut to 64 with no trailing hyphen; the suggestion passes the saving checks.

**Validator:** each name rule on read and on save; lengths 64 and 65; directory match; description 1024 and 1025 code points; compatibility 500 and 501 code points.

**Acknowledged keys:** a listed key that is present is dropped and reported as a warning; a listed key that is absent is refused; a listed standard field is refused; an unlisted unknown key is still an error.

**Formatter:** `Parse(Format(x))` returns `x`. A summary containing quotes, colons, `#`, non-ASCII text and a newline.

**Export:** the three existing skills, compared before and after (not a unit test; see the implementation record).

**Publish:** the refusals for an empty `appliesTo`, an over-length description, a frontmatter name that differs from the id (with its suggestion), a frontmatter description that differs from the derived one, and a `metadata.version` that differs; a rollback is not re-checked; a frontmatter request stores the body only, and its content hash equals that of the same body published without frontmatter; the preview reports the warnings, and execution stores only the body.

**Cross-check:** run `skills-ref validate` on the exported folders, if Python and `skills-ref` are available. Not yet checked.

## Verification status

- **Checked:** the spec, from its page. The reference validator's `validator.py` and `parser.py`, from GitHub on 2026-11-09, including its use of Python `len()`. The Host and workbench code at the commits above. The three workbench skills' `skill.json`.
- **Not checked:** the reference repository's test fixtures. Whether `skills-ref` runs here. Whether any production store has ids over 64 characters or skills with empty `appliesTo`; the production store is not in the repository. Whether `strictyaml` and YamlDotNet agree on the same inputs.
- **Not run here:** the Host's own test project and the workbench's test project (see the implementation record).
- **Could differ:** the reference validator's behaviour at a later commit.

## Implementation record (2026-11-09)

**Built, uncommitted:**

- `opentail-host/OpenTail.Modules.Skills/Standard/StandardSkill.cs` and `StandardPublish.cs`.
- `opentail-host/OpenTail.Modules.Skills/SkillsModule.cs`: the publish preview and execution go through `StandardPublish.Prepare`; `skills.publish` takes `ignoreFrontmatterKeys`; the change request carries `frontmatterWarnings`.
- `opentail-host/OpenTail.Modules.Skills/OpenTail.Modules.Skills.csproj` and `Directory.Packages.props`: YamlDotNet 17.0.1.
- `opentail-host/OpenTail.Host.Tests/StandardSkillTests.cs` and `StandardPublishTests.cs`.
- `opentail-workbench/src/OpenTail.Workbench/Formats/NativePackage.cs`: writes through `StandardSkill.Format`, and refuses an invalid skill with `InvalidDataException` before creating any folder.
- `opentail-workbench/tests/OpenTail.Workbench.Tests/DeliveryTests.cs`: one assertion compares the instructions with line endings normalised.

**Verified here:**

- The Skills module builds with warnings as errors, and with no warnings.
- 45 tests pass. They run from a scratch project outside the repository that links the repository's test files and global usings unchanged. The publish tests drive the real `SkillsModule` through a fake tool registry. The scratch project exists because `OpenTail.Host.Tests` cannot be restored on this machine.
- The three workbench packages, before and after. Ignoring line endings, the only difference is the `metadata` block (`version: "1"`).
- A skill with an empty `appliesTo` is refused at export, and no file is written.

**Changed on purpose:** line endings. The old writer put CRLF on the frontmatter and check-in lines and bare LF on the body, so each file mixed the two. The new writer uses the platform's line ending throughout. Every exported file's bytes change, though the text does not. The workbench test that compared the instructions with the output would have failed on Windows, so it now normalises line endings first.

**Second pass, same day, from scratch projects outside the repository.** The repository's own test projects need the private GitHub feed, which this machine does not have (the feed token is not set, and the connectivity packages are not in the local cache). So the Skills-related code was compiled from the repository's files, unchanged, against the Host core and the Skills module:

- **Host:** the Skills-related test files of `OpenTail.Host.Tests` (the 12 `SkillsModuleTests`, the 2 `SkillOutcomesTests`, and the 45 new cases) compile with warnings as errors and pass: 59 of 59. The rest of that project has not been run.
- **Workbench:** all 51 tests of `OpenTail.Workbench.Tests` compile with warnings as errors. 29 pass, including `NativePackageTests`. The other 22 fail during setup, all with one error: the sandboxed Host needs an `email-imap` module type, which comes from a GitHub-only package. That is before any skill code runs. I did not run them against the code from before this change, so the claim that they are environmental rests on that error message.
- **CLI:** `workbench package` builds and runs. Its output is byte-identical to the harness's output, and the only text change from before is the `metadata` block in the three skills.

**Still to run on the owner's machine:** the full `OpenTail.Host.Tests` and `OpenTail.Workbench.Tests` projects, with the feed token, to cover the 22 workbench tests and the rest of the Host suite.

**Not tests:** a `---` inside the body; an empty body on publish (`SkillStore.Parse` already refuses it); export as a unit test (a one-off before-and-after comparison was done instead).

**Update 2026-11-10.** The YamlDotNet pin was 17.0.1, and restore against the GitHub feed failed: `OpenTail.Connectivity.Files` needs 17.1.0 or later through `Transformations.Text` (NU1109). The pin is 17.1.0 in `538e77c`. With the feed token, `OpenTail.Host.Tests` restores and passes: 1037 tests. The module reference's first two open items were fixed in `1464f70`: the publish preview now compares `skill.json` for a republished version as well as the instructions, and `skills.find` names a label whose files are missing in a `warnings` list. The workbench's `Directory.Packages.props` still pins 17.0.1. This was not checked, and it may need the same change.

## Known limits

- An instructions text that starts with a Markdown rule (`---`) and has a second `---` line later is read as frontmatter. It is then refused or mis-split. Starting the instructions with a heading avoids it. Not solved.
- The YAML is YamlDotNet's event parser. Aliases, anchors and tags are refused. The reference uses `strictyaml`, and the two may disagree on edge inputs. Unverified.
- `SuggestSaveName` covers a fixed list of letters with no decomposition. Other scripts, such as Chinese or Arabic, get no suggestion.
- Description text is checked for presence and length only, not content.

## Not in this design

Loading and unloading, discovery from folders, resource files, script execution, MCP skills, a public package, a separate repository, and any change to labels, approval, audit or outcomes.
