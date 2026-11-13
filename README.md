# OpenTail Orca

An MCP server that exposes only the modules you enable and decides, per tool call, whether it **runs now**, **waits for a person**
(on a review page or in Microsoft Teams) or is **denied**. It runs on one machine over stdio, or as a signed-in server for a team,
including as a container.

Start with [`OpenTail.Orca/README.md`](OpenTail.Orca/README.md). For running it at a public address, see
[`OpenTail.Orca/DEPLOY.md`](OpenTail.Orca/DEPLOY.md); for Teams, [`OpenTail.Orca/TEAMS-SETUP.md`](OpenTail.Orca/TEAMS-SETUP.md).

| Folder | What |
|---|---|
| `OpenTail.Orca.Abstractions` | The module contract, frozen at version 1 |
| `OpenTail.Orca`, `.Admin`, `.Http`, `.Notifications` | The queue and policy, the review page, HTTP with sign-in and the Teams bot, notifications |
| `OpenTail.Modules.*` | Modules: files, CSV, SQL, web, email, calendar, GitHub Issues, ServiceNow, social, OpenAPI, Microsoft 365, Google |
| `OpenTail.Profiles.*`, `profiles/` | Integration profiles (YouTube, X): an OpenAPI document plus one JSON file, no code |
| `OpenTail.Orca.Server` | The ready-to-run Orca with every module compiled in; the container image |
| `OpenTail.Orca.Sample.*` | Small example deployments |
| `docs/done/MCP-HOST-*.md`, `docs/MCP-HOST-V5-PLAN.md`, `docs/design/reviewer-gate.md` | Orca plans: completed ones are archived in `docs/done/`; V5 is still active in `docs/`; the AI-reviewer plan is `docs/design/reviewer-gate.md`, which supersedes V6 (`docs/MCP-HOST-V6-PLAN.md` is a pointer). Status by version is in `OpenTail.Orca/STATUS.md` |

## Build and test

The Connectivity libraries the modules use come from
[`opentail-connectivity`](https://github.com/opentail-net/opentail-connectivity) as packages on GitHub Packages, which needs a token
even to read. Set `GITHUB_PACKAGES_TOKEN` to a token with `read:packages` (a classic personal access token, or `gh auth token` after
`gh auth refresh -s read:packages`), then:

```
dotnet build OpenTail.Orca.slnx
dotnet test --solution OpenTail.Orca.slnx
pwsh scripts/check-host-deployments.ps1
```

Releases: tag `host-v<version>`; see [`OpenTail.Orca/PACKAGING.md`](OpenTail.Orca/PACKAGING.md#versioning).

## Licence

Proprietary and commercial. Use, copying, modification and distribution need a written commercial licence from OpenTail; see [LICENSE.md](LICENSE.md).

Repository history was restarted on 2026-10-20 and versions reset to 0.0.1. Commit hashes and version numbers in older
documents refer to the earlier history.
