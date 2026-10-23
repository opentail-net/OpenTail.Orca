# Host designs

Designs for Host work that is planned but not built, newest first. The cross-repo checklist is the
[work plan](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/plan.md) in `opentail-connectivity`
(sections 12 to 16); the versioned Host plans are `MCP-HOST-V*-PLAN.md` at the root.

| Design | Plan section | State |
|---|---|---|
| [Snowflake as a read-only Sql target](snowflake.md): warehouse checks, a daily budget per identity, the real-account suite ([guide](../guides/snowflake.md), [done](../done/snowflake-s1-s2.md)) | 4 | S1, S2 built 2026-10-23; S3 to S5 open |
| [Audit and evidence](audit-and-evidence.md): decision traces, policy versions, long-lived decision records, an export auditors can verify, tamper protection | 18 | Proposed 2026-10-18 |
| [Failures, retries and inspection](failures-and-inspection.md): failure kinds, retry limits, paused routes, dead items to inspection, redrive through approval | 17 | Proposed 2026-10-18 |
| [Queued actions as MCP Tasks](mcp-tasks.md): the standard task handle for queued and delayed actions; SDK 2.x first | 17 | Proposed 2026-10-18 |
| [Slack as a front door for work](slack-front-door.md): handing work to agents from Slack | 17 | Deferred 2026-10-18 |
| [Work items and continuations](work-and-continuations.md): an inbox for agents, parking and resuming after a wait | 17 | Proposed 2026-10-18 |
| [Roles and permissions](roles-and-permissions.md): agent profiles bound to the credential, the `Administers` flag | 12 | Steps 1 to 3 built 2026-10-18 |
| [skills.publish questions](skills-publish-questions.md) | 12 | Built 2026-10-18 |
| [Adaptive limits and intrusion detection](adaptive-limits.md) | 13 | Groundwork built 2026-10-18; enforcement waits |
| [Where data goes](data-flow.md): labels and flow rules | 14 | Decided 2026-10-18; waits |
| [Execute with an undo window](undo-window.md) | 15 | Decided 2026-10-18; waits |
| [Three smaller extensions](more-extensions.md): temporary access, audit hash chain, watching the reviewers | 16 | Decided 2026-10-18; waits |

**Build order (owner, 2026-10-18):** agent profiles, then the metrics groundwork; both built on 2026-10-18. The other designs wait; most build on those two.

**The checks a call passes,** as the designs add to them (built ones in bold): **authentication**, **profile** (is the tool in the
caller's profile?), **policy mode and rules**, **taint**, flow rules (labels), adaptive level, delay; then **execution and
audit**. Every check can only tighten, so the strictest answer wins whatever order they run in; a profile refusal is final.
