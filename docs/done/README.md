# Done plans

Plans whose work is built. Each file keeps its design, its reviews and its implementation log. The status of the Host, and what is still
open, is in [`OpenTail.Host/STATUS.md`](../../OpenTail.Host/STATUS.md).

| File | What it was | State at archive |
|---|---|---|
| [`MCP-HOST-PLAN.md`](MCP-HOST-PLAN.md) | v1: Host core, queue, SQLite, first modules, sample deployments | Phases 0 to 8 built; review pass done |
| [`MCP-HOST-V2-PLAN.md`](MCP-HOST-V2-PLAN.md) | v2: team use (HTTP and sign-in, policy rules, notifications, resumable work, approval controls, docs) | Its phases V1 to V7 built; acceptance 2026-10-16 |
| [`MCP-HOST-V3-PLAN.md`](MCP-HOST-V3-PLAN.md) | v3: modules as bricks | W1 to W5, W5b, W7 and W8 built; W6 (publishing) and live tests open |
| [`MCP-HOST-V4-PLAN.md`](MCP-HOST-V4-PLAN.md) | v4: integrations as data (profiles) | P1 to P4b built; ServiceNow kept as code by decision |
| [`MCP-HOST-V7-PLAN.md`](MCP-HOST-V7-PLAN.md) | v7: administration and telemetry on the review page | A1 to A4 built 2026-10-19; browser walkthrough open |
| [`snowflake-s1-s2.md`](snowflake-s1-s2.md) | Snowflake provider, steps S1 and S2 | Built 2026-10-23; S3 to S5 open ([design](../design/snowflake.md)) |

Still active, in `docs/`: `MCP-HOST-V5-PLAN.md` and `MCP-HOST-V6-PLAN.md`.

## Commit hashes in these logs

The logs cite commits from the repository's earlier history. The Host was developed inside the outer `OpenTail` repository, on branch
`host/modular-mcp`, before the split of 2026-10-20. None of those hashes are in `opentail-host`, and `opentail-host` does not contain the
earlier history.

Where to look, from spot checks (I did not check every hash):

- v1 to v5 hashes: the outer repository, `C:\Git\OpenTail`, branch `host/modular-mcp`. Checked there: `81b8a54`, `552dfd0`, `9bb652b`,
  `4bd34a6`, `a9e436c`, `9dae9b6`, `f927911`, `377ffa9`, `69b1031`, `16bb496`, `3266100`, `560619c`.
- v7 hashes (`4fb53ee`, `78e55f9`, `e56ceca`, `cf2e074`, `5aa5ab5`): the backup bundle `C:\Git\split-backup-2026-10-20\opentail-host.bundle`.
  Checked there: all five.
- The Snowflake commit `4625a04` is in `opentail-host` itself.

Rows that say "(this commit)" or "and the next commit" have no hash.

## Status lines

The status line at the top of each archived plan was written when the plan was a draft, and it was not updated when the work landed. Each
archived plan now has a banner below its title that points to `STATUS.md`. The implementation log at the end of each plan is the record.

Test counts in the logs are snapshots, true when each entry was written.
