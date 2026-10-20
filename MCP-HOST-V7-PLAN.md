# OpenTail Host v7: administration and telemetry on the review page (plan)

Status: A1-A4 built 2026-10-19 (local commits, not pushed). Tested with the admin API and the page script syntax-checked; not walked through in a real browser. Follows v6 ([`MCP-HOST-V6-PLAN.md`](MCP-HOST-V6-PLAN.md)).

## 1. Why

Operators administer the Host today by reading config files, stderr and command-line reports (`--policy-versions`,
`--access-report`, `--usage`). Metrics and traces leave the process only if someone runs an OpenTelemetry collector. A pilot
customer needs to see, in one place they can already sign in to, what each agent may do, who is mapped to which agent, what the
Host is doing and how it decided, and a way to change an agent's profile that is safer than editing a file by hand.

## 2. Decisions (taken with the owner on 2026-10-19)

* **Where:** the existing review page (`OpenTail.Host.Admin`), not a separate app and not a Teams-only surface. It already has
  company sign-in, the `opentail.review` and `opentail.admin` roles, a strict CSP and no cookies, and it ships inside the Host.
* **Teams:** nothing in this plan works only in Teams. A Teams tab can show the same page later (it needs a `frame-ancestors`
  exception for Teams and Teams single sign-on; see 6).
* **Read first, changes second.** Changes are proposals: validated, replayed against stored actions, approved by a second admin.
* **The config files stay authoritative.** The Host never rewrites its own policy (in a container `/config` is read-only). An
  approved proposal is downloaded and deployed the way config is deployed today; when the Host starts with it, the proposal is
  marked applied and audited.

## 3. What a viewer sees, by role

| View | Reviewer | Admin |
|---|---|---|
| Actions (today's page) | yes | yes |
| Modules (today) | yes | yes |
| Agents: profiles, what each may call and how (Execute/Queue/Deny), requestable tools, delegation | no | yes |
| People: person to identities (`teams:`, `key:`, ...) | no | yes |
| Keys: name, hash prefix, profile (never the key) | no | yes |
| Policy versions, with a diff between any two | no | yes |
| Health and startup warnings | no | yes |
| Telemetry: metrics since start, recent traces as a waterfall, linked to actions | no | yes |
| Audit: the newest entries of the whole trail | no | yes |
| Usage: per identity, tool and day (the `usage_daily` rollups) | no | yes |
| Policy proposals: draft, replay, approve, reject, download | no | yes (approve: a different admin) |

Every admin endpoint answers 403 to a reviewer. Nothing new shows argument values or results; telemetry carries only the tags it
already carries (tool, module, mode, risk, taint, status) plus the action id.

## 4. Phases

### A1. Administration views, read-only

* `GET /api/admin/overview`: policy hash and when it was loaded, agent profiles resolved against the loaded catalog (each tool's
  mode), `People`, keys from the access snapshot, health report, startup warnings.
* `GET /api/admin/policy-versions` and `GET /api/admin/policy-versions/diff?from=&to=`: versions, and a section-by-section diff of
  the canonical JSON (agents, people, tools, rules), the same comparison that produces `profile_changed` and friends.
* `GET /api/admin/usage?days=`: the usage rollups.
* The page: an **Admin** button, shown only when the API says the viewer is an admin (`GET /api/me`).
* Tests: role gating, content, no key material.

### A2. Telemetry and traces on the page

* `HostTelemetryRecorder` in `OpenTail.Host`: a `MeterListener` that keeps totals per instrument and tag set (counters, up-down
  counters, histograms as count, sum, min, max and fixed buckets), and an `ActivityListener` that keeps the last 500 finished
  spans of the `OpenTail.Host` source in a ring buffer. Bounded memory (at most 2,000 tag sets; older spans drop). Off with
  `"Telemetry": { "Recorder": false }` in the HTTP config; on by default when the page is served.
* More spans where there were none: `action.review`, `maintenance.sweep`, `notification.deliver`, and the action id
  (`opentail.action_id`) on the existing ones so a span links to its action.
* `GET /api/admin/telemetry/metrics`, `GET /api/admin/telemetry/traces?limit=`, `GET /api/admin/audit?limit=`.
* The page: a **Telemetry** view (metric table, the queue gauge, recent traces grouped by trace with a waterfall, a click opens the
  action) and an **Audit** view.
* Tests: the recorder aggregates and bounds; endpoints gated.

### A3. Policy proposals

* Schema 12: `policy_proposals` (id, created, proposer, base hash, candidate canonical JSON, candidate hash, note, replay summary,
  status `proposed|approved|rejected|withdrawn|applied`, decided by, decided at).
* `POST /api/admin/proposals` with a full candidate policy: parsed and validated exactly as at startup (against the loaded
  catalog), replayed against stored actions (`PolicyReplay`, in-process), stored, audited `policy_proposed`. A proposal whose base
  is not the running policy is marked stale.
* `POST /api/admin/proposals/{id}/approve|reject|withdraw`: approve needs an admin who is not the proposer
  (`policy_proposal_approved`); reject needs a reason. `GET /api/admin/proposals/{id}/policy.json` downloads the candidate.
* At startup, a loaded policy whose hash matches an approved proposal marks it applied (`policy_proposal_applied`).
* The page: a **Proposals** view; from an agent profile, **Propose a change** edits that profile's tools, requestable tools and
  delegation in a form (or the whole policy as JSON), shows the replay ("these 14 actions would have run without review") and the
  diff before submitting.
* Tests: validation, replay, two-person rule, stale base, applied on start, audit entries.

### A4. Documentation

OPERATIONS.md (the admin views, telemetry recorder, proposals), SOP.md (changing an agent's access is now: propose, second admin
approves, deploy, check applied), POLICY.md pointer, plan log below.

## 5. What this does not do

* No hot reload: a policy change still takes effect on restart. Hot reload touches every component that holds the resolver; it is
  its own piece of work.
* No editing of keys or secrets on the page: keys live in the HTTP config and environment.
* The recorder is not a metrics backend: it holds what happened since the process started. Use OpenTelemetry export for history.

## 6. Later

* Teams tab: allow `frame-ancestors` for `teams.microsoft.com` and friends when configured, and Teams SSO for the page.
* Proposals for module configuration (`modules.json`) and notification routes.
* Hot reload of an approved policy.

## 7. Implementation log

| Phase | Commit | What |
|---|---|---|
| A1 | 4fb53ee | `/api/me`, `/api/admin/overview` (profiles resolved per tool incl. denied ones, people, keys, health, warnings), policy versions and `PolicyDiff`, usage; Admin button and five tabs on the page. `AdminViewsTests` (4). Host.Tests 893 pass. |
| A2 | 78e55f9, e56ceca | `HostTelemetryRecorder` (MeterListener totals per tag set, histogram buckets, last 500 spans; bounded), spans `action.review`, `maintenance.sweep`, `notification.deliver`, `opentail.action_id` on spans; `/api/admin/telemetry/metrics`, `/traces` (grouped by trace), `/audit` (`IAuditReader.ReadRecent`); Telemetry (waterfall) and Audit tabs; `RecordTelemetry` switch. `TelemetryRecorderTests` (4) + audit tail test. Host.Tests 898 pass (twice). The first commit had a test that failed under parallel load; the second fixes it. |
| A3 | cf2e074 | `PolicyProposals` (propose: parse, whole policy only, `OpenTailHostBuilder.CheckPolicy` extracted from `Build`, in-process `PolicyReplay`; approve by a different person, not stale; reject with reason; withdraw by proposer; `MarkAppliedAsync` on the first sweep), schema 12 `policy_proposals`, `/api/admin/policy/current`, `/proposals` (list, get with diff, post, approve/reject/withdraw, `policy.json` download); Proposals tab and *Propose a change* on Agents. `PolicyProposalTests` (7). Host.Tests 905 pass. |
| A4 | 5aa5ab5 | OPERATIONS.md (admin tabs and API, telemetry on the page, audit events), SOP.md (C1 on the page with two admins; O2 step 0), POLICY.md pointer. |
