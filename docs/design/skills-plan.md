# Skills plan

**Date:** 2026-11-10. **Responds to:** the directional review of the skills product (the Host's governance, the Workbench's evaluation, Agent Skills compatibility).
**Scope:** one plan for the skills product. Each system keeps its own design: the Host's [standard skill files](standard-skill-files.md) and [resources](skill-resources.md), and the Workbench's evaluation (`opentail-workbench/README.md`).

## What the product is for

Two separate jobs, as the review says:

1. **The skill**: a portable, versioned instruction set that tells an agent when a task applies, what information to use, how to decide, how to verify its work, and when to stop or escalate.
2. **The Host**: governed publishing, approval, versioning, discovery and serving of those instructions.

A skill's instructions never grant authority. Policy and tools decide what the agent can do.

## Success criteria

| Criterion | What success looks like | Where it is measured today |
|---|---|---|
| Discoverability | Relevant skills activate; irrelevant ones do not | Partly: selection scenarios check outcomes, not activation. Step 2 adds an activation measure. |
| Usefulness | Results improve against a baseline without the skill | Not yet: the Workbench compares skill versions, not skill against no skill. Step 4 adds the baseline mode. |
| Safety | Forbidden actions do not increase | Yes: `must_not` checks on every scenario |
| Portability | The same skill packages for compatible clients | Partly: `SKILL.md` packages work; resources do not (step 3) |
| Governance | Versions are tested, reviewed, auditable and reversible | Yes: approval, labels, immutable versions, audit, rollback |

## Steps

| # | Step | State | Where |
|---|---|---|---|
| 1 | Merge both branches | **Done.** Host PR #2 merged into `main` (`6779f6b`). The Workbench's packaging branch was already merged into its `main` (PR #1, `ce65cb3`). | Host `main`; Workbench `main` |
| 1a | Make the Workbench restore on `main` | **Done** on branch `progressive-disclosure`: YamlDotNet pinned to 17.1.0, as the Host needs. Its test suite (51 tests) passes. | Workbench `Directory.Packages.props` |
| 2 | Progressive disclosure in the Workbench | **Built 2026-11-10, not committed.** A `catalog` delivery: names and standard descriptions in the prompt; `activate_skill` loads a skill's instructions, and each call is recorded. `native` stays as the upper bound and the default. | Workbench `AgentRunner.cs`, branch `progressive-disclosure` |
| 3 | Design: skill resources (`references/`, `assets/`) | **Design written, no code.** Six decisions for the owner (D1 to D6). | `docs/design/skill-resources.md` |
| 4 | Evidence without paid model runs | **Built 2026-11-10, not committed.** Three held-out near-miss scenarios (`policy-edge`, one per skill) and a `none` baseline delivery (no skill; every call is outside skills). Real-model runs are not started; see step 5. | Workbench |
| 5 | Real-model runs and client evidence | **Not started.** Needs a model choice, a budget and an API key from the owner. | — |

### Step 2 detail

Before step 2, read from the code on 2026-11-10: `AgentRunner` had two deliveries. `Native` passes every skill's full instructions into the system prompt. `Tools` gives the model the Host's `skills.find` and `skills.get`, so it discovers and loads skills itself. `Tools` already separates discovery from activation, but it is not how a native client loads skills, and nothing measures which skill was activated.

Built: a third delivery, `Catalog`. The system prompt lists each skill's name and standard description (from `StandardSkill.Describe`, the same text a native package carries), and nothing else. A harness tool, `activate_skill(id)`, returns the skill's `SKILL.md` body, standing in for the client's file read. Activations are recorded in the transcript. The harness tool is exempt from the outside-skills flag, through `ScenarioRunner.CatalogTools` and not `BuiltIns`, so `ToolChecks` still checks only the Host's own tools. `Native` stays, labelled as an upper bound.

Default delivery stays `Native` for now. Changing it would change what existing batches mean, and batches are not keyed by delivery yet. Switching the default is a follow-up once they are.

### Step 3 detail

See [skill-resources.md](skill-resources.md). Its decisions are the owner's: D1 text only, D2 limits, D3 refuse `scripts/`, D4 what the change request shows, D5 larger resources deferred, D6 policy for the read tool.

### Step 4 detail

- **Near-miss held-out scenarios.** Built: one per skill, `held_out: true`, `kind: policy-edge`, each a message that looks like the skill's domain but should not trigger its action. Their expectations are `must_not` only, on the action the skill would take, so they hold whichever delivery is used. Each rests on a policy line: `triage-marketing-mentions-invoices` (the routing table gives marketing no ticket and no reply); `invoice-copy-request-from-customer` (the payment checks cover invoices passed for payment, not a customer's copy request); `reply-journalist-asks-opening-hours` (the skill covers a customer's enquiry; rule 3 of the routing table forbids a reply outside `northwind.example`). Each near miss has only `must_not` expectations, so a run that does nothing passes it: the signal is in how often a run acts where it should not.
- **No-skill baseline.** Built as the `None` delivery: the agent gets the task and the tools, but no skill, so every call counts as outside skills. A batch run this way is compared with a skill batch on the same scenarios and model, using the existing comparison, which warns when the deliveries differ.
- **Real-model baseline: not run.** The harness can run it with `--model` and `WORKBENCH_API_KEY`. That spends money, and the owner has not chosen a model or a budget.

## External review of 2026-11-10

An external review of the Host's skills module and of the direction was checked against the code. The confirmed findings each have a plan:

- **Store and discovery.** Damaged labels and versions can hide the whole store. A failed publish leaves debris. The backup is not one tested procedure. Plan: [skill-store-integrity.md](skill-store-integrity.md).
- **Hashing.** Indentation is not part of the identity, so an indentation-only edit is accepted at the same version. Plan: [skill-content-hashing.md](skill-content-hashing.md).
- **Outcomes.** A decision counts for every version served earlier in its session. Plan: [skill-outcome-attribution.md](skill-outcome-attribution.md).
- **Standard.** License, compatibility and metadata are not stored. The standard code lives inside the Host module, and the module references the Host's abstractions. Plan: [agent-skills-library.md](agent-skills-library.md).

Three points in the review were corrected by checking. The spec's Unicode name wording is ambiguous, but the reference validator accepts Unicode letters, and so does our reader (without the reference's NFKC step; the library plan lists the differences); only the save-time rule refuses them. A backup procedure exists in `docs/guides/skills.md`, though it is not one tested procedure. The admin command already prints sample sizes and intervals.

Nothing from this section is built. Each plan lists the owner's decisions it waits for. None of it is committed.

## Deferred

- Preserving the standard frontmatter's `license` and `compatibility` in the store. Needs a stored-format decision, and it is additive, so it can follow step 3.
- `skills.find` returning the standard description, and near-miss tests for it.
- Attributing outcomes to a task instead of to a session. Until then, `SkillOutcomes` is an indication to investigate, never a reason to roll back automatically.
- Switching the default delivery to `Catalog` (see step 2).

## Not building

A second permission system inside skills (policy already controls tools and approval). A script runner inside skills (a separate execution and security problem). A workflow engine dressed up as skills (instructions guide decisions; they do not guarantee execution). A large automatic tuning system (the tuner and human approval are enough).

## Decisions for the owner

- **D1–D6:** the resource decisions in [skill-resources.md](skill-resources.md).
- **Real-model runs:** which model or models, what budget, and whether an API key is available for the Workbench.
- **Uncommitted work in the Host:** the review fixes from the last round are still uncommitted on branch `standard-skill-files`. They are not in `main`. Committing them needs your go-ahead.
- **Default delivery:** when it switches to `Catalog` (see step 2).
- **Uncommitted Workbench work:** steps 1a, 2 and 4 are on `progressive-disclosure`, uncommitted. Committing it needs your go-ahead. The three near-miss scenarios are new files.

## Verification

- Host: `OpenTail.Host.Tests` passes at `6f1a59b` (1053 tests), the tip that `main` merged in `6779f6b`. The uncommitted work in the Host's working tree brings the count to 1072, and it passed in full on 2026-11-10.
- Workbench: `OpenTail.Workbench.Tests` passes on `progressive-disclosure`: 57 tests (51 before steps 2 and 4; six added). The whole solution builds with no warnings or errors.
- Design documents: checked against the Agent Skills specification on 2026-11-10 and against the code listed in each document's contracts section.

## Consistency check

- The review's claims about the Workbench were checked: the native prompt does carry full instructions; the scenario count was 31 (10, 10 and 11 per skill), not 30, and is 34 after step 4 (one held-out near miss added per skill); the 73-run figure is in the Workbench README; the packager writes no resource folders; the outcome attribution counts every skill served earlier in the session.
- The review said the packaging depends on an unmerged Host branch. That was true at the time of the review. It is now merged.
