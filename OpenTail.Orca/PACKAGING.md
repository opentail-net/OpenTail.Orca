# Packages and how they assemble

## Twenty-one packages, one version

Nineteen Host and module packages and two profile packages, built here. The modules also use six Connectivity libraries
(`OpenTail.Connectivity.*`), which come as packages from their own repository, `opentail-connectivity`, with their own version. The
dependency graph is shallow:

```
Host.Abstractions   (only Microsoft.Extensions.AI.Abstractions)     <- the module contract, frozen at version 1
   ▲        ▲        ▲
   │        │        └── Host.Notifications   (+ SQLite)   signed webhooks, Slack, reminders, durable outbox
   │        └── Modules.Csv / OpenApi / GitHubIssues / ServiceNow / Social   (Abstractions only; OpenApi also uses Microsoft.OpenApi.Readers)
   │        └── Modules.Files                              (Abstractions + Connectivity.Files, for PDF and Office text)
   │        └── Modules.Sql                                (Abstractions + Microsoft.Data.Sqlite; other drivers come from the deployment)
   │        └── Modules.Email / Calendar                   (Abstractions + Connectivity.Files)
   │        └── Modules.Web                                (Abstractions + Connectivity.Web, which uses Connectivity.Files)
   │        └── providers: Modules.Email.Imap (+ Connectivity.Inbox.Imap, MailKit), Modules.Calendar.Ics (+ Connectivity.Calendar.Ics),
   │                       Modules.Microsoft365 (+ Connectivity.Inbox.Graph, Microsoft Graph SDK),
   │                       Modules.Google (+ Connectivity.Inbox.Gmail, Google API client); each references Modules.Email / .Calendar
   │        └── profiles: Profiles.YouTube, Profiles.X, Profiles.Jira, Profiles.Confluence (no code: a profile and its spec as resources; reference Modules.OpenApi)
   │
Host  (queue, policy and rules, sessions and taint, MCP server, SQLite, modules.json loader, credentials, guarded HTTP)
   ▲
   └── Host.Admin  (approval page + API, stdio DeploymentRunner, policy and module commands)
          ▲
          └── Host.Http  (Streamable HTTP, JWT / API-key sign-in, metadata, limits;  also references Host.Notifications)
```

* Modules don't know about the Host. An architecture test enforces that a module may reference only `Host.Abstractions`, the small
  `OpenTail.Connectivity.*` libraries and, for provider packages, the provider-neutral `Modules.Email` and `Modules.Calendar`.
  Nothing here references `OpenTail.Core`.
* The Host doesn't know about any module. It finds them through `IHostModule` (added in code) or `IHostModuleFactory` (named in
  `modules.json`, discovered in the packages the deployment references through its deps.json).
* A stdio-only deployment references `Host.Admin` (or just `Host`) and never pulls in HTTP, JWT or notifications.
* All twenty-four share one version. The Connectivity packages are pinned in `Directory.Packages.props`; moving to a new Connectivity
  release is a one-line change there.

## How the packages come back together

A deployment is a small console app:

1. Reference `OpenTail.Orca.Http` (team, signed-in) or `OpenTail.Orca.Admin` (your machine, stdio). Either brings in `Host` and
   `Host.Abstractions`.
2. Reference the module packages you want. Referencing is the trust decision; nothing is loaded from a folder.
3. `Main` is one line, `return await HttpDeploymentRunner.RunAsync(args, "my-host");`, with the modules enabled in `modules.json`
   ([MODULES.md](MODULES.md)), or names modules in code with `RunAsync(args, "my-host", b => b.AddModule(...))`. Both can be mixed.

The operator then supplies a policy file, a SQLite path, reviewer tokens, the modules file and (for HTTP) an HTTP config file; see
[README.md](README.md) and [OPERATIONS.md](OPERATIONS.md). A module that isn't referenced isn't shipped; one that is referenced but not
enabled stays off; a tool the policy doesn't mention is denied. Anyone can write a module against `Host.Abstractions` alone.

## The module contract (frozen at version 1)

The contract is what a module author compiles against: `IHostModule`, `IToolRegistry`, `ToolRegistration`, `ToolDescriptor`,
`ToolRisk`, `IPreviewableTool`, `PreviewResult`, `IHostModuleFactory`, `ModuleContext`, `ModuleManifest`, `CredentialRequirement`,
`CredentialKind`, `Credential`, `ICredentialProvider`, `IModuleHttpClientFactory`, `ToolTransientException`, `HostContract` (including the
`AIFunctionArguments.Context` keys), the two assembly attributes, and the helpers `RootedPath` and `NetworkRules`.

* Every module assembly says which contract it was built against with `[assembly: HostContractVersion(HostContract.Version)]`. The constant
  is compiled into the module, so the Host sees the real build version. A Host refuses a module outside `HostContract.MinSupportedVersion`
  to `HostContract.Version` and names both.
* **The promise for version 1:** members are only added, never removed or changed, and an added member has a default, so every module
  built for contract 1 keeps working with every later Host that supports contract 1. Anything else is contract 2, and the Host keeps
  loading contract-1 modules for as long as `MinSupportedVersion` is 1.
* `ContractFreezeTests` compares the public surface of these types with `OpenTail.Orca.Abstractions.Tests/contract-v1.txt`. A change fails
  the build's tests, says what was removed and what was added, and is only accepted deliberately (`OPENTAIL_UPDATE_CONTRACT=1`).
* The manifest has its own format number (`ModuleManifest.Format`); a Host refuses a newer format than it reads.

The contract was frozen after the first real module (GitHub Issues) was written against it, which changed it once: factories receive the
module's config when describing it, so a manifest can follow a configured base URL (GitHub Enterprise).

Added since the freeze, as the promise allows (a new member with a default, so every contract-1 module still works):

* `IHostModuleFactory.Describe(id, config, baseDirectory)` (2026-10-16), defaulting to `Describe(id, config)`. The Host calls it so a
  factory whose config names a file can read it while describing: an integration profile decides the manifest's hosts and credential.
* `ToolDescriptor.AuditResult` (2026-10-17), default null: after a successful run the Host writes an audit entry from a named text
  field of the result (the skills module's `skill_served`, with `invoice-check@3`).
* `ModuleManifest.ServerInstructions` (2026-10-17), default null: a sentence the Host adds to its MCP server instructions while the
  module has an advertised tool (the skills module points the agent at `skills.find`).
* `ToolDescriptor.Administers` (2026-10-18), default false: an administration tool (`skills.publish`), never granted by a wildcard or
  to a caller without an agent profile, and held to a strict review policy at load ([POLICY.md](POLICY.md#administration-tools)).
* `ToolDescriptor.Counts` (2026-10-18), default null: where the result says how many records it returned, which entity ids it touched
  and which arguments hold recipients, for the Host's usage rollups (`ResultCounts`, a new type).
* `ToolFailureException` and `ToolFailureKind` (2026-10-18), new types: a tool says what kind of failure it hit (transient,
  environment, contract, auth, policy, unknown outcome, bug), for the Host's retry and inspection rules
  ([failures design](../docs/design/failures-and-inspection.md)). Modules that never throw it behave as before; an unclassified
  exception is a bug.
* `ToolDescriptor.Labels` (2026-10-18), default empty: kinds of sensitive data the tool returns (`finance`, `personal`, ...), for
  the data-flow check ([POLICY.md](POLICY.md#data-flow)).

## Verified

`scripts/check-host-deployments.ps1` (run it before every release) does this end to end:

* publishes the four samples and fails if anything from the heavy OpenTail stack (Core, Semantic Kernel, Roslyn, ...) ships, if a
  sample ships a module it did not name, or if a required assembly is missing;
* packs all twenty-four packages, and fails if they do not carry one version;
* restores a project **outside the repo**, using only the packed folder, the Connectivity feed and nuget.org, builds it against `Host.Http`, `Host.Notifications`
  and every module, and **runs** it (`--new-api-key`), proving the dependency lists are complete and the packed assemblies load;
* builds a second outside project whose `Main` names **no module**, references `Host.Http` and twelve module packages from the packed feed,
  and checks that `--adopt-policy github` and `--list-modules` find every module (files, github-issues, servicenow, social, csv, web,
  sql, email-imap, calendar-ics, calendar-graph, calendar-google, and the YouTube profile from its package) from `modules.json` alone, and
  that the program runs as a module worker.

Last result (2026-10-17): every sample and the server pass (EmailOnly 42 assemblies / 60 MB, FilesOpenApi 44 / 59.8 MB, TeamHttp 55 /
61 MB, Server 106 / 110.3 MB); twenty-three packages, one version; both outside projects build and run. FilesOpenApi grew because the
Files module now brings `Connectivity.Files` (and its text extractor) to read PDF and Office files.

## Versioning

The Host packages share one version, from `host-v*` tags (`Directory.Build.props`: `MinVerTagPrefix=host-v`, minimum `0.1`, applied to
every project named `OpenTail.Orca*`, `OpenTail.Modules.*` and `OpenTail.Profiles.*`).

| Git state | Version of every package |
|---|---|
| no `host-v*` tag in history | `0.0.0-alpha.0.<commits>` |
| tag `host-v0.0.1` on the commit | `0.0.1` |
| N commits after that tag | `0.1.1-alpha.0.N` |

To release, tag and push the tag:

```
git tag -a host-v0.0.2 -m "Host 0.0.2"
git push origin host-v0.0.2
```

CI then builds, tests, runs the deployment check and the container smoke test, and only if all pass publishes the packages to GitHub
Packages and the image to `ghcr.io/opentail-net/opentail-orca:<version>` (plus `latest` for a release without a pre-release suffix).
Both are private.

The package version and the contract version are separate: packages stay 0.x for now, while the module contract is already frozen at 1.

## Licence

Every package built here carries `LICENSE-HOST.md` (PolyForm Strict 1.0.0) and sets `requireLicenseAcceptance`, set once in
`Directory.Build.props`. The Connectivity packages are under the same licence.

## Before this is usable by someone else

- **A public feed.** Packages and image are published privately (GitHub Packages, GHCR), which needs a token even to read. Going
  public means choosing nuget.org or another public feed for the Host packages and the Connectivity packages together, a publisher
  name, and whether to sign them.
- **`Host.Admin` carries Kestrel** even for a stdio-only deployment that only wants the loopback approval page. If that matters, the
  approval endpoints (`AdminEndpoints`) and the stdio `DeploymentRunner` could be split.
- **Single-file and trimmed publishing** have not been tried with configured modules. Trimming can remove a factory nobody references in
  code; the Host then says the type is missing.
