# OpenTail Host

An MCP server that exposes only the modules you enable and decides, per tool call, whether it **runs now**, **waits for a person**
(on a review page or in Microsoft Teams) or is **denied**. It runs on one machine over stdio, or as a signed-in server for a team,
including as a container.

Start with [`OpenTail.Host/README.md`](OpenTail.Host/README.md). For running it at a public address, see
[`OpenTail.Host/DEPLOY.md`](OpenTail.Host/DEPLOY.md); for Teams, [`OpenTail.Host/TEAMS-SETUP.md`](OpenTail.Host/TEAMS-SETUP.md).

| Folder | What |
|---|---|
| `OpenTail.Host.Abstractions` | The module contract, frozen at version 1 |
| `OpenTail.Host`, `.Admin`, `.Http`, `.Notifications` | The queue and policy, the review page, HTTP with sign-in and the Teams bot, notifications |
| `OpenTail.Modules.*` | Modules: files, CSV, SQL, web, email, calendar, GitHub Issues, ServiceNow, social, OpenAPI, Microsoft 365, Google |
| `OpenTail.Profiles.*`, `profiles/` | Integration profiles (YouTube, X): an OpenAPI document plus one JSON file, no code |
| `OpenTail.Host.Server` | The ready-to-run Host with every module compiled in; the container image |
| `OpenTail.Host.Sample.*` | Small example deployments |
| `docs/done/MCP-HOST-*.md`, `docs/MCP-HOST-V5-PLAN.md`, `docs/MCP-HOST-V6-PLAN.md` | The Host plans: completed ones are archived in `docs/done/`, the two still active are in `docs/`; status by version is in `OpenTail.Host/STATUS.md` |

## Build and test

The Connectivity libraries the modules use come from
[`opentail-connectivity`](https://github.com/opentail-net/opentail-connectivity) as packages on GitHub Packages, which needs a token
even to read. Set `GITHUB_PACKAGES_TOKEN` to a token with `read:packages` (a classic personal access token, or `gh auth token` after
`gh auth refresh -s read:packages`), then:

```
dotnet build OpenTail.Host.slnx
dotnet test --solution OpenTail.Host.slnx
pwsh scripts/check-host-deployments.ps1
```

Releases: tag `host-v<version>`; see [`OpenTail.Host/PACKAGING.md`](OpenTail.Host/PACKAGING.md#versioning).

## Licence

PolyForm Strict 1.0.0, see [LICENSE-HOST.md](LICENSE-HOST.md), for everything in this repository: noncommercial use only;
commercial use needs a commercial licence. The Connectivity libraries are under the same licence.

Repository history was restarted on 2026-10-20 and versions reset to 0.0.1. Commit hashes and version numbers in older
documents refer to the earlier history.
