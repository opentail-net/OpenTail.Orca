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
| Discoverability | Relevant skills activate; irrelevant ones do not | Partly: selection scenarios check outcomes, not activation. A scenario can check activation today only by naming `tool: activate_skill`, which works for the catalog delivery alone. Step 2a adds a form that works for every delivery, and a separate activation rate. |
| Usefulness | Results improve against a baseline without the skill | Not measured yet: the no-skill baseline is built (step 4, on `progressive-disclosure`), but no real-model batch has compared a skill with it. |
| Safety | Forbidden actions do not increase | Yes: `must_not` checks on every scenario |
| Portability | The same skill packages for compatible clients | Partly: `SKILL.md` packages work; resources do not (step 3) |
| Governance | Versions are tested, reviewed, auditable and reversible | Yes: approval, labels, immutable versions, audit, rollback |

## Steps

| # | Step | State | Where |
|---|---|---|---|
| 1 | Merge both branches | **Done.** Host PR #2 merged `standard-skill-files` into `main` (`6779f6b`), up to `6f1a59b`; `4b8a8b7` is not in `main`. The Workbench's packaging branch was already merged into its `main` (PR #1, `ce65cb3`). | Host `main`; Workbench `main` |
| 1a | Make the Workbench restore on `main` | **Done** on branch `progressive-disclosure`: YamlDotNet pinned to 17.1.0, as the Host needs. Its test suite passes (57 tests, with steps 2 and 4). | Workbench `Directory.Packages.props` |
| 2 | Progressive disclosure in the Workbench | **Built 2026-11-10; committed (`b762d50`) and pushed.** A `catalog` delivery: names and standard descriptions in the prompt; `activate_skill` loads a skill's instructions, and each call is recorded. `native` stays as the upper bound and the default. | Workbench `AgentRunner.cs`, branch `progressive-disclosure` |
| 2a | Activation evaluation in the Workbench | **Planned, not started.** Activation expectations for the catalog and tools deliveries, scored and compared like actions (see the step 2a detail). | Workbench `Checks/`, `Runs/Comparison.cs`, the scenario format |
| 3 | Design: skill resources (`references/`, `assets/`) | **Design written, no code.** Six decisions for the owner (D1 to D6). | `docs/design/skill-resources.md` |
| 4 | Evidence without paid model runs | **Built 2026-11-10; committed (`b762d50`) and pushed.** Three held-out near-miss scenarios (`policy-edge`, one per skill) and a `none` baseline delivery (no skill; every call is outside skills). Real-model runs are not started; see step 5. | Workbench |
| 5 | Real-model runs and client evidence | **Not started.** Needs a model choice, a budget and an API key from the owner. | — |

### Step 2 detail

Before step 2, read from the code on 2026-11-10: `AgentRunner` had two deliveries. `Native` passes every skill's full instructions into the system prompt. `Tools` gives the model the Host's `skills.find` and `skills.get`, so it discovers and loads skills itself. `Tools` already separates discovery from activation, but it is not how a native client loads skills, and nothing measures which skill was activated.

Built: a third delivery, `Catalog`. The system prompt lists each skill's name and standard description (from `StandardSkill.Describe`, the same text a native package carries), and nothing else. A harness tool, `activate_skill(id)`, returns the skill's `SKILL.md` body, standing in for the client's file read. Activations are recorded in the transcript. The harness tool is exempt from the outside-skills flag, through `ScenarioRunner.CatalogTools` and not `BuiltIns`, so `ToolChecks` still checks only the Host's own tools. `Native` stays, labelled as an upper bound.

Default delivery stays `Native` for now. Changing it would change what existing batches mean, and batches are not keyed by delivery yet. Switching the default is a follow-up once they are.

### Step 2a detail

The second review's point: an agent that activates an irrelevant skill, reads it, and takes no action still passes a near-miss scenario, because the near miss checks only actions. The gap is narrower than the review said. A scenario can already name `tool: activate_skill` with an `id` argument in `must` or `must_not`: the checker matches any call by name and arguments (`Checks/Checker.cs:10-24`). And `compare` already lists activation calls as call shifts (`Runs/Comparison.cs`, `Shifts`). What is missing: a form that works for every delivery (tools activation is `skills.get`), a separate activation rate per scenario with an interval, and a near-miss expectation that does not depend on one delivery's tool name.

Planned: `activates: [ids]` (the listed skills must be activated during the run) and `mustNotActivate: [ids]` in a scenario's `expect`, checked the way actions are, given their own rate, and included in batch comparisons per scenario and delivery. Near-miss scenarios gain `mustNotActivate` for their skill. Catalog activation is an `activate_skill` call; tools activation is a `skills.get` call. Native and none have no activation step, so their result is not applicable, not a pass.

**Success.** An activation counts only when the load succeeded and returned the intended skill. A catalog activation is an `activate_skill` call whose result names the skill's id and version (`served`). A tools activation is a `skills.get` call that returned the intended id and version without an error. A failed or refused call is an attempt, not an activation. The record keeps the skill and version loaded. Activation results are reported separately from action pass rates, as recommended.

Detailed design, with contracts, branch tables and tests: [workbench-activation-evaluation.md](workbench-activation-evaluation.md). The text here is the summary.

Open question, for the owner: whether activation expectations count toward a scenario's pass, or are reported beside it. Recommended: beside it at first, so existing pass rates do not move.

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

### Second review, of the committed plans

A second review asked for six changes. All six are in the plans:

1. **Independence.** The library does not hold the Host's store (labels, version folders, the lock, the label rules). It reads and preserves every file of a package as opaque bytes; the Host decides what it serves (`scripts/` and binary assets are refused by the Host). `ignoreKeys` moves to the Host's publish layer.
2. **Resource approval** shows the content of each new resource and a readable diff of each changed one (D4, changed).
3. **Hashing and resources agree.** Resources are hashed over exact bytes, never with `Normalise`. The v2 encoding is specified at byte level (normalise each value, take its length, hash the raw bytes). O-H2 names `allowed-tools` as well as `license`, `compatibility` and `metadata`.
4. **Activation** is evaluated: step 2a.
5. **Store guarantee.** A failed publish never moves a label; a handled failure leaves nothing behind; a crash may leave an unlabelled version, which is never served and is reported. The backup procedure quiesces the Host, or states the weaker guarantee (O-S5).
6. **Attribution window and decisions.** Decisions are selected by review time (O-A2). Human, system and AI decisions are separated (O-A3).

The review also asked that the merge of the two branches' documents be reconciled rather than copied from one side. That is done: `standard-skill-files` was merged into `skills-plan` (`c4174a0`), with the three shared documents read on both sides.

Nothing from this section is built. Each plan lists the owner's decisions it waits for.

## Order of work

Agreed after the review of the plans, 2026-11-10. The plans were ordered by design; this order is by value and risk. Each step names the decision it waits on, and nothing later starts before the step before it is done or deliberately set aside.

1. **Stop the publish that overwrites damaged labels (S9).** Store plan, design item 2, and the tests named there. This is a data-loss risk on the Host today, so it is taken out of the store plan and done on its own, without waiting for the rest of that plan (cleanup, store check, backup). Waits on O-S2 (recommended: refuse the publish, as design item 2 says) and on O-S6 (the repair procedure must exist before publishes start refusing damage; recommended: by hand, with the Host stopped).
2. **v1 golden hash tests.** Hashing plan, work order step 1. Records the current identities before anything changes. Needs no decision.
3. **Activation scoring in the Workbench (step 2a).** Makes the discoverability criterion measured. Designed in [workbench-activation-evaluation.md](workbench-activation-evaluation.md). Waits on the question of whether activation counts toward a pass (recommended: reported beside it).
4. **Measure real decision counts.** Count clean, ambiguous and unattributed decisions on a real store, without changing attribution. The result decides whether option A is built, and whether its thresholds can be reached at all. Needs the owner's store; no store is available in this repository.
5. **Decide, not before:** attribution option A (only if step 4 shows enough data, and after O-A2 and O-A3); the library (its licence is decided for now, so it waits only for its place in this order); resources (deferred until a real skill needs a reference file).

Everything else in the plans waits behind these steps.

## Gates before implementation

From the second review. A gate says what must be decided or written before a piece of work starts. This plan does not authorise any start; the owner does.

| Work | Gate | Status |
|---|---|---|
| v1 golden tests (hashing) | none | can start; needs no decision |
| Store corruption and failure tests | the publish guarantee (`skill-store-integrity.md`, "The publish guarantee") | the guarantee is written; the tests can be written now. The changes they drive wait on decisions: refusing publishes on O-S2 and O-S6 (how damage is repaired once publishes refuse it); the backup on O-S4 and O-S5; policy-reference resolution on O-S1; deploying the stricter reading on an existing store on O-S3 |
| v2 hashing | the byte-level algorithm (`skill-content-hashing.md`, design item 3) and O-H1 to O-H6 | the algorithm is written (terminators, presence markers and sorted metadata added in review); decisions open, and the store must keep an absent owner apart from an empty one first, and the v2 field list must be final before the first v2 identity is written (design item 4) |
| Outcome attribution, option A | O-A2 (window by review time, and the flag name) and O-A3 (whose decisions count) | recommendations written; decisions open. Also needed first, and not decisions: the measurement of order-of-work step 4; a subject on the two `ActionQueue` reviews that carry none; the check that a review moves the action's update time (all in `skill-outcome-attribution.md`, "Not built") |
| Library scaffold | O-L1 (licence: decided for now, the Host's own PolyForm Strict; a permissive licence can come later, after a check of copyright), O-L6 (net10.0 recommended; the owner confirms it against Stingray), O-L4 (the Host keeps the store) and O-L8 (concurrent modification) | recommendations written; decisions open |
| Resource storage and serving | D4 (show the content) and the exact-bytes digest (`skill-resources.md`) | D4 changed to show the content; owner to confirm. D1 to D6 are all open. Resources enter a version's identity only under v2 hashing, so storing them also waits on the v2 hashing gate above |
| Activation evaluation (step 2a) | the question of whether activation counts toward a pass (recommended: beside the pass rate), and its design document | designed; the decision is open |

## Deferred

- Preserving the standard frontmatter's `license`, `compatibility`, `metadata` and `allowed-tools` in the store (O-L3 and O-H2 in the plans). Needs the stored-format decision; it is additive, so it can follow step 3.
- `skills.find` returning the standard description, and near-miss tests for it.
- Attributing outcomes to a task instead of to a session. Until then, `SkillOutcomes` is an indication to investigate, never a reason to roll back automatically.
- Switching the default delivery to `Catalog` (see step 2).

## Not building

A second permission system inside skills (policy already controls tools and approval). A script runner inside skills (a separate execution and security problem). A workflow engine dressed up as skills (instructions guide decisions; they do not guarantee execution). A large automatic tuning system (the tuner and human approval are enough).

## Decisions for the owner

- **D1–D6:** the resource decisions in [skill-resources.md](skill-resources.md).
- **Real-model runs:** which model or models, what budget, and whether an API key is available for the Workbench.
- **Host work on `standard-skill-files`:** committed (`4b8a8b7`) and pushed. It is not in `main`; merging it needs your go-ahead.
- **`metadata.version` on new standard submissions:** the design says it is optional, "if present, must equal the version" (`standard-skill-files.md:156`). The Agent Skills specification makes the whole `metadata` field optional. Recommended: keep the rule. Host-generated packages carry `metadata.version`, imported frontmatter may omit it, and a supplied value must match the Host's version. An earlier review asked for it to be required; a later review withdrew that. Owner decides; the code and tests follow the decision.
- **Agreed 2026-11-10:** undo-window stops and expiries leave the human rate and are counted separately (attribution plan, Decided); a read-only measurement report comes before any attribution build (attribution plan, Decided); activation has its own design, `workbench-activation-evaluation.md`; the preview has its own branch table (store plan); a partial approval counts once, at the final approval (attribution plan, Decided); library v1 extension fields are scalar-only, with a diagnostic for complex values (library plan).
- **Agreed 2026-11-10, second round:** the measurement report shows the calendar time to 20 clean decisions per version, from the observed rate, with the period and the stability assumption stated; undo-window stops are reported as a rate with a defined denominator, and the existing `delayed` audit entry gets a structured scheduled-time field before any rate is published (late stops are reported separately, and O-A4 decides whether `StopAsync` refuses them); the partial-approval path is tested against the real store; the documentation stays lean, with one activation design and the preview table in the store plan.
- **Plan decisions:** each plan lists its own: O-S1 to O-S6, O-H1 to O-H6, O-A1 to O-A4, O-L1 to O-L8; D1 to D6 in the resources design; and the activation question in step 2a.
- **Default delivery:** when it switches to `Catalog` (see step 2).
- **Workbench work on `progressive-disclosure`:** steps 1a, 2 and 4 are committed (`b762d50`) and pushed. Merging into `main` needs your go-ahead.

## Verification

Repository state: Host `skills-plan` at `ebcfebd` when this revision was written (pushed; 14 commits ahead of `origin/main`, which is `6779f6b`, counted with `git rev-list`; any later commit moves the head, so check with `git log`); Host `standard-skill-files` at `4b8a8b7` (pushed, not in `main`); Host `main` at `6779f6b`; Workbench `progressive-disclosure` at `b762d50` (pushed); Workbench `main` at `ce65cb3`. The plans are on `skills-plan`, not `main`.

- Host: `OpenTail.Host.Tests` passed at `4b8a8b7` (1072 tests, none failed or skipped), run locally on 2026-11-10 before that commit. No `.cs` file has changed on this branch since `4b8a8b7` (`git diff --name-only 4b8a8b7 HEAD -- '*.cs'` is empty), so that result covers the branch's code. The later commits change documents only. No check runs are recorded on GitHub for these commits. `main` is at `6779f6b`.
- Workbench: `OpenTail.Workbench.Tests` passes on `progressive-disclosure`: 57 tests (51 before steps 2 and 4; six added). The whole solution builds with no warnings or errors.
- Design documents: checked against the Agent Skills specification on 2026-11-10 and against the code listed in each document's contracts section.

## Consistency check

- The review's claims about the Workbench were checked: the native prompt does carry full instructions; the scenario count was 31 (10, 10 and 11 per skill), not 30, and is 34 after step 4 (one held-out near miss added per skill); the 73-run figure is in the Workbench README; the packager writes no resource folders; the outcome attribution counts every skill served earlier in the session.
- The review said the packaging depends on an unmerged Host branch. That was true at the time of the review. It is now merged.
