# Writing a skill version

What a skill version needs to pass Orca, and what each refusal means. Use this when you write a version, whether through the workbench or by hand. The operating side (publishing, approving, rolling back) is in the [runbook](skills.md). The module's behaviour is in its [reference](../../OpenTail.Modules.Skills/README.md).

Written from `opentail-orca` at `dc957f7`, 2026-11-10.

## What a version is

A version is two texts, plus the arguments that propose it:

- **`skill.json`**: the metadata, as JSON. Orca stores it as sent.
- **`SKILL.md`**: the instructions the agent follows, as Markdown. Orca stores it as sent, unless it begins with frontmatter (below).

With them, `skills.publish` needs `skillId`, `version`, `reason`, `expectedFrom`, and optionally `label`, `report` and `ignoreFrontmatterKeys`.

## skill.json

Orca reads these fields:

| Field | Required | Rule |
|---|---|---|
| `id` | yes | Must equal `skillId`. Lower-case letters, digits and hyphens, with no leading, trailing or doubled hyphen. The standard's limit is 64 characters, and the standard checks enforce it at publish |
| `version` | yes | Must equal the `version` argument. A whole number from 1 to 999,999,999, with no leading zero |
| `summary` | yes | One sentence saying what the skill does. `skills.find` returns it, and the standard description is built from it |
| `appliesTo` | yes, in practice | The situations the skill applies to. A new version needs at least one non-blank entry |
| `tools` | no | The tool names the skill expects. Served as information. Orca does not check them |
| `policies` | no | A list of `{ "ref": "policies/payment-verification.md", "hash": "sha256:…" }`. `hash` is the normalised hash of the policy text the skill was tested against, or `null`. Only the file name of `ref` is used for the drift check |
| `owner` | no | The team that owns the skill |

`format` must be `1`. Orca refuses any other value, and a file with no `format`, both at publish and when a stored version is read. The workbench writes `"format": 1`.

Each field has the type the table gives it: text for `id`, `version`, `summary` and `owner`, a list of text for `appliesTo` and `tools`, and a list of objects for `policies`, each with a text `ref` and a text or `null` `hash`. A value of another type is refused, not converted: `"version": 1` is not `"version": "1"`.

Keep the file exactly as you want it served. Republishing an existing version with different `skill.json` text is refused when the request is made.

## SKILL.md: plain instructions

Write it as a procedure: what to look at, what to decide, which tool to use, and what never to do. Refer to policies by their `ref` so the agent knows which document to read. The workbench's `skills/` folder has three examples.

Two rules:

- **It must not be blank.** Orca refuses an empty text with `The instructions (SKILL.md) are empty.`
- **Start with a heading, not `---`.** A text that begins with a `---` line is read as frontmatter. See the next section.

## Frontmatter (optional)

`SKILL.md` may begin with a standard frontmatter block. Use it when you want the file to be valid Agent Skills form too:

```markdown
---
name: invoice-check
description: Check whether an unpaid invoice is genuine before anyone pays or chases it. Use when an invoice or payment demand arrives by email; or when a supplier asks about an unpaid invoice.
metadata:
  version: "3"
---

# Invoice check

Use this for invoices, payment demands, bank-detail changes and suppliers asking about unpaid invoices.
…
```

When there is a frontmatter block:

- **`name`** must equal the skill id.
- **`description`** must equal the text Orca builds from `summary` and `appliesTo`. The rule is `<summary>. Use when <entry>; or when <entry>.`, with the trailing full stop removed from the summary and from each entry. For a skill whose `summary` is "Check whether an unpaid invoice is genuine before anyone pays or chases it." and whose `appliesTo` holds "an invoice or payment demand arrives by email" and "a supplier asks about an unpaid invoice", the example's description is exactly that text. A different wording is refused, and the refusal shows the expected text. The workbench's catalog delivery lists this text to the agent before it loads the skill, so it is what the agent chooses from.
- **`metadata.version`**, if you give it, must equal the version.
- **Other top-level keys** are refused, unless they are listed in `ignoreFrontmatterKeys` (exact, case-sensitive names that must appear in the file). Standard fields cannot be ignored.
- **`license`, `compatibility`, `allowed-tools` and other metadata** are not stored and are not served. They come back as warnings in the change request. `allowed-tools` is never acted on: approval is Orca's policy, not the skill's.
- **Only the body** after the closing `---` is stored. Blank lines just after it are removed.

The description and the name are limited by the standard: the name is at most 64 characters, lower-case ASCII letters, digits and hyphens when saved, and the description is at most 1024 characters. Over the limit it is refused, never cut.

## Refusal and warning codes

Refusals come back in the form `[CODE] message`. Some messages add a suggested name. The workbench's package writer uses the same checks, so these codes can also appear when you export a skill.

**Refusals (the publish does not go ahead):**

| Code | Means | Fix |
|---|---|---|
| `SKILL-FRONTMATTER` | The frontmatter is malformed: not closed by a `---` line, not valid YAML (the message gives the line), empty, not a mapping, a duplicate key, an anchor, alias or tag, or a value that is not a single string. `metadata` must map strings to strings, with no nesting | Fix the YAML. The message names the key |
| `SKILL-UNKNOWN-KEY` | A top-level key is not one of `name`, `description`, `license`, `compatibility`, `metadata` or `allowed-tools` | Remove the key, or list it in `ignoreFrontmatterKeys` |
| `SKILL-IGNORE-STANDARD` | `ignoreFrontmatterKeys` lists a standard field | Remove it from the list. Only unknown keys can be ignored |
| `SKILL-IGNORE-ABSENT` | `ignoreFrontmatterKeys` lists a key the file does not have | Correct the spelling, or remove it from the list |
| `SKILL-NAME-MISSING` | The frontmatter has no `name` | Add `name`, equal to the skill id |
| `SKILL-NAME` | The name is over 64 characters, is not lower-case, starts or ends with a hyphen, has two hyphens in a row, or has characters other than letters, digits and hyphens | Correct the name |
| `SKILL-NAME-ASCII` | A saved name uses characters other than `a-z`, `0-9` and hyphens. The refusal suggests an ASCII name | Use the suggested name, or write the name in ASCII |
| `SKILL-NAME-DIRECTORY` | The name does not match the folder name. It is checked when reading folders, not at publish | Not reached by a publish |
| `SKILL-DESCRIPTION` | The summary is blank (`The summary is blank: say what the skill does.`), or the frontmatter description is empty | Write a summary, or a description |
| `SKILL-DESCRIPTION-LENGTH` | The description is over 1024 characters. The refusal gives the count | Shorten the `summary` or the `appliesTo` entries. Nothing is cut for you |
| `SKILL-APPLIES-TO` | `appliesTo` is empty, or an entry is blank | Give at least one situation, with no blank entries |
| `SKILL-COMPATIBILITY` | `compatibility` is present and empty, or over 500 characters | Correct or remove it |
| `SKILL-BODY` | The instructions are empty. Only the export path reports this code. A publish reports the empty text without a code | Write the instructions |
| `SKILL-FRONTMATTER-NAME` | The frontmatter `name` differs from the skill id | Make them equal. The refusal suggests a name if the one given is not ASCII |
| `SKILL-FRONTMATTER-DESCRIPTION` | The frontmatter `description` is not the text built from `summary` and `appliesTo`. The refusal shows the expected text | Copy the expected text |
| `SKILL-FRONTMATTER-VERSION` | `metadata.version` differs from the version | Make them equal, or remove `metadata.version` |

Two refusals have no code:

- `A new version needs both skillJson and instructions.` Send both.
- `<id> version <n> is already published with different content; use a new version number.` A version never changes once published.

**Warnings (the publish goes ahead, and the change request shows them):**

| Message | Means |
|---|---|
| `Unknown frontmatter key '<key>' is ignored, as the caller asked.` (code `SKILL-KEY-IGNORED`) | An unknown key you acknowledged was dropped |
| `'license' is not stored, so it is not served.` | The licence is not kept |
| `'compatibility' is not stored, so it is not served.` | Compatibility is not kept |
| `'allowed-tools' is not stored and is never acted on: approval is Orca's policy, not the skill's.` | The field is not kept, and it grants nothing |
| `metadata '<key>' is not stored, so it is not served.` | Other metadata is not kept. `version` is the exception: it is checked, not kept |

**Rollbacks** to a version already in the store are not checked again. Their files cannot change.

## Report binding and what your edits change

A scenario report says which content it describes, through a line in its first 40 lines:

```
skill-report: invoice-check@3 sha256:<64 hex digits>
```

The hash is the **content hash** of the version. It covers the id, version, `summary`, `appliesTo`, `tools`, the policy refs, `owner` and the instructions. Its normalisation ignores line endings, runs of spaces, trailing spaces and blank lines. So:

- **Reformatting** the instructions, or fixing whitespace, does not change the hash. A report still binds.
- **Changing the wording**, or any of the fields above, does change it. The report no longer binds, and the publish is refused if the report names this version.
- **Changing a policy's recorded `hash`** does not change the content hash, but it changes `skill.json`. A changed `skill.json` for a version that is already published is refused, so it needs a new version.

The workbench writes the binding line for you (`BindingLine`). If you write a report by hand, use exactly that form.

## Before you publish

- The id matches the folder name and `skillId`.
- Any changed content has a **new version number**.
- `summary` is one sentence. `appliesTo` has at least one situation.
- `SKILL.md` starts with a heading, unless you meant the frontmatter.
- The `policies` list records the hashes of the policy text you tested against.
- The report, if you attach one, carries a binding line for this id, this version and this content.
- `reason` says what changed and what the runs showed.
- `expectedFrom` is the version the label points at now, read just before you submit.
