# skills.publish: questions to resolve

**Status:** decided by the owner on 2026-10-18: all three recommendations below are accepted, and publishing needs one approval for `staging` and two for `production`. Built on 2026-10-18: `expectedFrom` (required, checked at queue and at execution), the report binding line (`skill-report: <id>@<version> <content hash>`, refused when it names other content, labelled "not bound" when absent), and no reviewer edits. Moved here from `opentail-connectivity` on 2026-10-18; the work plan there keeps a short entry that links here. **The code they concern is in
[`opentail-orca`](https://github.com/opentail-net/OpenTail.Orca)**, `OpenTail.Modules.Skills` and the action queue, read at
`230cdd0`; line numbers will drift. Raised in review of the [skills design](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/design/skills-and-scenarios.md) as built. Who may
call `skills.publish` at all is a separate design: [roles and permissions](roles-and-permissions.md).

| # | Question | Risk today | Recommendation |
|---|---|---|---|
| 1 | Can the reviewer trust the report in the change request? | A forged or mismatched report looks exactly like a real one | Bind the report to the content and label it; signing is not needed once only the workbench may call `skills.publish` |
| 2 | What does an approval apply to, if the label moved after review? | Approving an old request can roll production back without anyone intending it | The publish checks the label still points where the reviewer saw it |
| 3 | What if a reviewer edits the arguments when approving? | What runs no longer matches the diff the reviewer read | No edits on `skills.publish`: request changes instead |

## 1. Can the reviewer trust the report?

**Today.** `skills.publish` takes a `report` argument: free Markdown from whoever calls the tool. The preview puts it into the
change request as it is (`OpenTail.Modules.Skills/SkillsModule.cs:166`), next to the caller's `reason`, and the reviewer reads
both first. Orca does not know where the report came from or which version it describes.

**Why it matters.** The before-and-after numbers are what the reviewer decides on. A report showing "100% / 100%, held-out up"
reads the same whether the workbench produced it or someone typed it. It can also be wrong honestly: the report of version 4
attached to a proposal of version 5. Orca's v6 plan sets the principle for AI reviewers: the reviewer sees the action, built
by Orca, never the requester's case for it. A self-reported report is the requester's case.

**Options.**

| Option | What it gives | What it costs |
|---|---|---|
| A. Label it | The preview says "supplied by the requester, not checked by Orca" | Nothing; but a confident report still persuades |
| B. Bind it to the content | The report states the skill id, version and a hash of its `SKILL.md`, `skill.json` and policy hashes; Orca checks these match the proposed version and refuses the proposal if not | Small; catches mismatches and honest mistakes, not forgery |
| C. Sign it | The workbench signs the report and its bindings with a key Orca knows; the preview shows "produced by workbench *name* for exactly this content", or refuses | Key management, one key per workbench; proves origin, not correctness: a compromised workbench can sign anything |
| D. Orca fetches or computes it | Orca gets the report from the workbench by run id, or runs the comparison itself | A new trust link from Orca to the workbench, or evaluation inside Orca, which the skills design keeps out of it |

**Recommendation (revised 2026-10-17):** B, with A for anything B cannot check. Add `reportHash` and the bindings to the
report's header and verify them in the preview, so the change request says plainly which numbers match the proposed content.
C is not needed: under the [roles design](roles-and-permissions.md) only a profile that names `skills.publish` can
call it, which in practice is the workbench's key, so the authenticated caller already proves where the report came from. A
signature would add something only if the report passed through untrusted hands on the way, and it does not. Revisit C if a
report ever travels separately from the call (attached later, or relayed by another service).

**To decide:**
- [ ] B and A, without signing, as revised?
- [ ] Is a report required for every publish, or can a person publish without one (an urgent rollback, say), clearly labelled?

## 2. What does an approval apply to?

**Today.** The preview is computed once, when the action is queued (`OpenTail.Orca/Runtime/ActionQueue.cs:90`), and stored. It
records, for example, "move production from version 3 to version 4" (`SkillsModule.cs:148`). When the action is approved, the
publish moves the label to the requested version without looking at where it points now (`SkillsModule.cs:115`,
`SkillStore.cs:105`). A publish can wait up to seven days (`ExpireAfter` in the suggested policy).

**Why it matters.** Proposal A (v3 to v4) is queued. Proposal B (v4 to v5) is queued later and approved first. Then someone
approves A, which still says "v3 to v4": production goes back to v4. Nobody meant to roll back, and the change request they
approved described something else.

**Options.**

| Option | What it gives | What it costs |
|---|---|---|
| A. Compare and set | `skills.publish` takes an `expectedFrom` argument (the version the label points at now; the workbench fills it in). The preview refuses if it is already wrong; the publish refuses at execution if the label moved: "the label moved since this was reviewed; resubmit" | Small, inside the Skills module; the caller sends one more argument |
| B. Supersede | When a publish for a skill and label is executed, other pending publishes for the same skill and label are marked stale and need resubmitting | A queue feature in Orca; useful for any tool where only the latest proposal makes sense |
| C. Refresh the preview at approval | Orca recomputes a tool's preview when someone opens it to decide, and shows what changed since it was queued | A general Orca feature; it also helps question 3 and any tool whose preview depends on current state |

**Recommendation:** A now, because it is small and closes the hole. B or C later, as general Orca features.

**To decide:**
- [ ] A now, as proposed?
- [ ] Is a deliberate rollback still expressed the same way (publish an earlier version with `expectedFrom` set to the current
  one)? Proposed: yes, so a rollback is as explicit as an update.
- [ ] B or C, or both, as Orca features, and when?

## 3. What if a reviewer edits the arguments?

**Today.** A reviewer can approve with changes, editing the arguments (on the review page and the Teams card). Orca checks
edited arguments against Deny rules (`ActionQueue.cs:226`), but the stored preview is not rebuilt. For `skills.publish`, an edit
to `instructions`, `version` or `label` would run without the diff the reviewer saw matching it, and a signed report (question
1) would no longer cover the content.

**Why it matters.** The change request is the record of what was approved. If what runs can differ from it, the approval proves
less than it seems to, and the audit trail shows an approval of something nobody reviewed as a whole.

**Options.**

| Option | What it gives | What it costs |
|---|---|---|
| A. No edits on this tool | A per-tool policy setting (for example `AllowEdits: false`) Orca enforces; the reviewer can approve, reject or request changes, and the proposer resubmits a new version | A small Orca policy feature; the default for administration tools in the [roles design](roles-and-permissions.md) |
| B. Rebuild the preview after an edit | The edited arguments get a fresh preview, and a second reviewer (or the same one) confirms it | More Orca work, and a second look for every edit |

**Recommendation:** A. A skill change should always be a new, complete version with its own report, which is what "request
changes" already produces. The same setting suits other administration tools.

**To decide:**
- [ ] `AllowEdits: false` as Orca policy setting, and the default for `skills.publish`?
- [ ] Should the same apply to every tool whose preview carries a diff or a signed attachment?
