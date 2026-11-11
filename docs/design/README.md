# Host designs

Designs for Host work that is planned but not built, newest first. The cross-repo checklist is the
[work plan](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/plan.md) in `opentail-connectivity`
(sections 12 to 16); the versioned Host plans are `MCP-HOST-V*-PLAN.md` at the root.

| Design | Plan section | State |
|---|---|---|
| [Checklist: governance naming, re-check, and A2A](governance-checklist.md): what is done, what is open, and what waits for the owner | none | Living list; updated 2026-11-11 |
| [Plan: align public approval names with Microsoft's vocabulary now, freeze them later](governance-naming-plan.md): binding digest, re-check at claim, export; freeze rule | none | Step 1 (approval audit event names) done locally 2026-11-11, uncommitted; the rest proposed |
| [Comparison with Microsoft's agent-governance toolkit (.NET)](agt-comparison.md): where the two differ, file hashes and locations, and why adoption and conversion are deferred | none | Findings recorded 2026-11-11; decision: defer |
| [A2A as a second front door](a2a.md): agent-to-agent tasks in and out, curated skills, replies as reviewed exits; phases 0 to 4 | none yet (owner to place) | Proposed 2026-11-11; nothing built |
| [Snowflake as a read-only Sql target](snowflake.md): warehouse checks, a daily budget per identity, the real-account suite ([guide](../guides/snowflake.md), [done](../done/snowflake-s1-s2.md)) | 4 | S1, S2 built 2026-10-23; S3 to S5 open |
| [Audit and evidence](audit-and-evidence.md): decision traces, policy versions, long-lived decision records, an export auditors can verify, tamper protection | 18 | Proposed 2026-10-18 |
| [Failures, retries and inspection](failures-and-inspection.md): failure kinds, retry limits, paused routes, dead items to inspection, redrive through approval | 17 | Proposed 2026-10-18 |
| [Queued actions as MCP Tasks](mcp-tasks.md): the standard task handle for queued and delayed actions; SDK 2.x first | 17 | Proposed 2026-10-18 |
| [Slack as a front door for work](slack-front-door.md): handing work to agents from Slack | 17 | Deferred 2026-10-18 |
| [Work items and continuations](work-and-continuations.md): an inbox for agents, parking and resuming after a wait | 17 | Proposed 2026-10-18 |
| [Roles and permissions](roles-and-permissions.md): agent profiles bound to the credential, the `Administers` flag | 12 | Steps 1 to 3 built 2026-10-18 |
| [skills.publish questions](skills-publish-questions.md) | 12 | Built 2026-10-18 |
| [Skills plan](skills-plan.md): the directional review's steps for the skills product, with the owner's decisions | — | Steps 1, 1a, 2 and 4 built 2026-11-10 and committed (Workbench `b762d50`, Host `4b8a8b7`); step 2a planned; step 3 design proposed; step 5 waits for the owner; the plans wait on the gates in skills-plan.md |
| [Skill resources](skill-resources.md): `references/` and `assets/` in a skill version, bound by hash, read through a read-only tool | — | Proposed 2026-11-10; digests over exact bytes; D4 changed to show content; decisions D1 to D6 for the owner |
| [Skill store integrity](skill-store-integrity.md): damaged labels and versions fail one at a time; a failed publish never moves a label, and a handled failure leaves nothing behind; one tested backup procedure | — | Planned 2026-11-10 from an external review; the publish guarantee is defined; decisions O-S1 to O-S6 |
| [Skill content hashing](skill-content-hashing.md): a versioned, unambiguous identity that keeps indentation; v1 identities stay valid | — | Planned 2026-11-10; decisions O-H1 to O-H6 |
| [Skill outcome attribution](skill-outcome-attribution.md): a decision counts only for the skill version that could have shaped it | — | Planned 2026-11-10; decisions O-A1 to O-A4 (the window is by review time) |
| [Agent Skills library](agent-skills-library.md): an independent .NET reader for skill directories, with preserved fields and safe resource reads; the Host becomes one consumer | — | Planned 2026-11-10; the Host keeps the store; decisions O-L1 to O-L8; the licence is decided for now |
| [Workbench activation evaluation](workbench-activation-evaluation.md): how a skill load is recognised for each delivery, and how activation expectations are scored and compared | — | Designed 2026-11-10; decisions O-W1 to O-W4; the question of whether activation counts toward a pass is open |
| [Skills in plain words](skills-in-plain-words.md): a plain-language overview of the skills plan; the plans govern where they differ | — | Written 2026-11-10 |
| [Standard skill files](standard-skill-files.md): the Agent Skills form of a skill version, checked at publish and at export; no new package | — | Phases 1 and 2 built 2026-11-09, committed (`dc957f7`, `e83e469`); decisions 3, 4 and 7 provisional |
| [Adaptive limits and intrusion detection](adaptive-limits.md) | 13 | Groundwork built 2026-10-18; enforcement waits |
| [Where data goes](data-flow.md): labels and flow rules | 14 | Decided 2026-10-18; waits |
| [Execute with an undo window](undo-window.md) | 15 | Decided 2026-10-18; waits |
| [Three smaller extensions](more-extensions.md): temporary access, audit hash chain, watching the reviewers | 16 | Decided 2026-10-18; waits |

**Build order (owner, 2026-10-18):** agent profiles, then the metrics groundwork; both built on 2026-10-18. The other designs wait; most build on those two.

**The checks a call passes,** as the designs add to them (built ones in bold): **authentication**, **profile** (is the tool in the
caller's profile?), **policy mode and rules**, **taint**, flow rules (labels), adaptive level, delay; then **execution and
audit**. Every check can only tighten, so the strictest answer wins whatever order they run in; a profile refusal is final.
