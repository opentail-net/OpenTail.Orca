# OpenTail.AgentSkills

Reads and checks an Agent Skills `SKILL.md` against the standard (agentskills.io). It has no dependency on the OpenTail Host: it references the
platform and YamlDotNet only (an architecture test checks this). It does not save, describe or grant anything.

## What it does

- `AgentSkill.Parse(text, ignoreKeys)` reads a file's frontmatter and body. Problems come back as diagnostics, so a caller can list them all.
  Nothing is thrown for a bad file. Unknown top-level keys are refused unless the caller lists them as ignored.
- `AgentSkill.Check(file, directoryName)` applies the standard's rules to the fields. The name is checked after NFKC normalisation, its length
  counts normalised code points, and the directory name is compared after NFKC too. A caller's own rules for saving are not here: the Host adds
  its save-time rule (a saved name uses only a-z, 0-9 and hyphens).
- `AgentSkill.SplitFrontmatter(text)` separates the frontmatter from the body.

## Divergences from the reference validator

The reference is `skills-ref` (`validator.py`, github.com/agentskills/agentskills), read 2026-11-10. Each divergence below is deliberate or
open, with its reason. A corpus from the reference was not fetched: the tests in `AgentSkillNameCorpusTests` are our own cases, written from
the rules the reference applies.

1. **Letters and digits.** The reference uses Python's `isalnum()` after NFKC. The library uses `Rune.IsLetterOrDigit` (letter and decimal digit
   categories). They can differ for numeric characters that are neither letters nor decimal digits after NFKC. Open: to confirm with the
   reference's corpus.
2. **`compatibility` when empty.** The reference checks only that the value is a string and at most 500 characters. The library also refuses an
   empty or blank value, because the specification says 1 to 500 characters when given. Probable divergence, kept on purpose; to confirm.
3. **The save-time name rule** (a saved name uses only a-z, 0-9 and hyphens) is not in the standard. It is the Host's rule, applied on top of
   the library. The Host judges a saved name after NFKC as well, so a fullwidth name is judged on its normalised letters.
4. **File names.** The library reads text. Which file names are accepted (the reference tries `SKILL.md`, then `skill.md`) is a decision for the
   library's file-reading step (O-L7); it is not made here.
5. **NFKC before the checks** is the reference's rule, adopted in step 2 of the library plan. It changes what is accepted: a fullwidth or ligature
   name now matches a directory that its normalised form matches, and a name whose normalised length exceeds 64 is refused even if its raw length
   does not. Both follow the reference.

## Preserved fields

`license`, `compatibility`, `allowed-tools` and `metadata` are kept exactly as written (their values are not trimmed), so a value with trailing
spaces is the same value when it is read back. `name` and `description` are still trimmed: the description is compared with the one the summary
gives, and the name with the directory. The Host stores these fields in `skill.json` with the version, and its identity covers them.

## Not yet in the library

Reading and writing package files and packaging are later steps of the library plan. This README will record their divergences when they are built.
