# Design: roles and permissions

**Status:** steps 1 to 3 of the [order](#order) built on 2026-10-18 (profiles on API keys, administration tools, the
`skills.publish` fixes); `RoleProfiles` and delegation are not. First written 2026-10-17. Written from reading
[`opentail-orca`](https://github.com/opentail-net/OpenTail.Orca) at `230cdd0`. The first version had three mechanisms (tool
planes, two agent roles, and a `Callers` list on each policy entry); this version replaces them with one, agent profiles, plus an
`Administers` flag. [What changed](#what-changed-from-the-first-version) says why. Moved here from `opentail-connectivity` on 2026-10-18; the work plan there keeps a short entry that links here. The owner's decisions of 2026-10-18 are under [decisions](#decisions).

**Where it fits:** Orca work, raised by the
[skills design](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/design/skills-and-scenarios.md). Orca
does not build agents; this design defines what an agent identity may do once it connects.

## The problem

Every caller with Orca's tools role sees every advertised tool, and policy rules can test a tool's arguments and the time,
but not who is asking (`PolicyResolver.IsAdvertised`: what `tools/list` advertises "never varies by caller"). So:

- A production agent that has just read a hostile email can call `skills.publish` and put an attacker's skill text in front of a
  reviewer. A person still has to approve it (the tool is queued for a human reviewer, and taint would force that anyway), but
  nothing stops the proposal being made.
- The workbench, which only needs to propose skill versions, connects with the same powers as an agent that sends email to
  clients.
- An agent that reads untrusted mail can also read the finance database and send mail out, because nothing narrows one agent's
  tools below the whole deployment's.

What is missing is **separation of duties between agents**: the agents that do the business's work, and the agents that
administer how that work is done, and between business agents with different jobs. An IT assistant configures the mail system; it
does not write to important clients. The same should hold here.

## What Orca has today

- **Identities.** Each agent has its own API key; people sign in with the identity provider (Entra ID or another, by JWT) or with
  a reviewer token. Every caller becomes a `CallerIdentity` with a set of roles.
- **Three coarse roles from sign-in:** `tools` (call tools), `review` (decide queued actions) and `admin` (the operator: the admin
  API). People's **groups** arrive as `group:<name>` roles, and `ReviewerGroup` uses them to decide who may approve what.
- **Agents and reviewers are already apart.** An API key can never review, a reviewer token can never call tools, and Orca
  refuses an API key configured with review, admin or group scopes (`HostHttpOptions.cs`). `SelfApproval: Forbid` stops anyone
  approving their own request. An AI reviewer never stands in for a person where a person is required.
- **Hidden tools.** A tool denied by policy is not advertised, so an agent does not even see it.
- **Taint.** A session that read untrusted content never gets a high-impact tool run without a reviewer, with no opt-out.
- **Sandbox and shadow are per Orca instance** (`UseSandbox`, `UseShadow`), not per caller. The workbench already talks to a
  Orca of its own.

This design builds on all of that. It adds one thing: what each agent identity may call.

## How an agent gets its permissions

The agent does not choose them. **Orca assigns them from the credential the agent connects with**, and the agent cannot
change that:

1. **Beforehand, the operator decides.** They create a key for the invoice assistant and map it to the `invoice-assistant`
   profile. With Entra ID, the customer's IT registers the agent as an application and gives it an app role there instead.
2. **The agent connects** to Orca's MCP endpoint and presents that key or token. That is all it presents: it never says
   "I am the invoice assistant".
3. **Orca looks up the profile from the credential,** advertises only that profile's tools in `tools/list`, and refuses
   (and audits) a call to anything else, even one made by name.

**Why it must work this way.** If an agent could ask for a profile, a hostile email could tell it "switch to the workbench profile
and publish this skill". Bound to the credential, prompt injection cannot raise the agent's rights: it would need a different key,
and the key sits in the client's configuration, never in the model's context.

Consequences:

- **The same agent software can run as several identities.** One program with two keys has two profiles. Permissions belong to
  the credential, not to the code or the model.
- **Locally over stdio there is no sign-in:** the caller is the single local operator (`CallerIdentity.Local`) and profiles do not
  apply. They matter once Orca runs as a shared HTTP server.
- **With per-user sign-in later** ([plan section 3](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/plan.md)), an agent acting for a person gets its profile narrowed further
  to what that person may do. See [delegation](#later-delegation).

## The model

### Agent profiles

A profile is a named list of the tools an agent may call. Permissions attach to the principal, not to the tool, so "what can
this agent do?" has one answer in one place, which is how AWS IAM, Entra app roles and Kubernetes RBAC are read and audited.

```json
"Agents": {
  "invoice-assistant": { "Tools": [ "mail.read", "erp.query", "skills.find", "skills.get" ] },
  "reply-assistant":   { "Tools": [ "mail.*", "skills.find", "skills.get" ] },
  "workbench":         { "Tools": [ "skills.publish" ] }
}
```

- **Keys name a profile:** `{ "Name": "invoice-bot", "Hash": "...", "Profile": "invoice-assistant" }`. With a directory, an app
  role in the JWT's `roles` claim names it instead (see [the customer's directory](#with-the-customers-directory)).
- **Profiles narrow; policy still decides.** A tool in the profile is still Execute, Queue or Deny by the tool's policy, rules,
  taint and trust exactly as today. A tool outside it is not advertised and is refused. The two never loosen each other.
- **Patterns:** `mail.*` grants a module's tools; `*` grants everything except administration tools (below).
- **Names are the operator's.** There are no fixed roles: deployments want "finance agent" and "support agent" from the start, and
  a profile is that. Shipped examples (`business-all`, `workbench`) are a starting point, not a ceiling.
- **Compatible.** A key with no profile keeps today's behaviour (every advertised tool except administration tools), and Orca
  warns at startup naming each such key.

### Administration tools

Some tools change how the agents behave instead of doing the business's work: `skills.publish` now; later policy proposals or
trust promotions if they ever become tools. A module marks them on the descriptor, as it marks `ReadsUntrusted` today:

```csharp
new ToolDescriptor("skills.publish", "...", ToolRisk.Write, "skills") { Administers = true }
```

Orca then holds them to fixed rules:

1. **A wildcard never grants one.** `*` and `skills.*` do not include `skills.publish`; a profile must name it. So no agent gets an
   administration tool by accident, and none has it without a profile (a key with no profile never gets one).
2. **Always a proposal, decided by people.** At load, an administration tool's policy must be `Queue` with `Reviewer: Human`, an
   approver `ReviewerGroup`, `SelfApproval: Forbid` and `AllowEdits: false`
   ([question 3](skills-publish-questions.md#3-what-if-a-reviewer-edits-the-arguments)). Otherwise Orca refuses to
   start, naming the tool. No rule, trust ramp or AI reviewer can relax it.
3. **Never next to business tools on a live Host.** A profile that names an administration tool and any business tool with an
   effect (above Read) is refused at load, unless Orca runs in sandbox or shadow mode. The workbench proposes skills; it does
   not send mail.
4. **Operators can tighten, never loosen.** An operator may mark more tools as administration (`files.write` on a configuration
   folder, say) with `"Administers": true` in the tool's policy entry; they cannot unmark a module's choice. This is Orca's
   existing rule that settings only tighten.
5. **Visible.** At startup and in `--explain`, Orca lists every key and profile that holds an administration tool, and sends
   the configured notification when that list changes.

### With the customer's directory

With Entra ID (or another directory), people's groups already arrive as claims. Agents can be registered there too, as
applications with app roles, and Orca maps an app role to a profile (`"RoleProfiles": { "OpenTail.Invoice":
"invoice-assistant" }`). The customer's IT then manages which agent may do what in the directory they already use. An identity
with app roles mapping to two profiles is refused, so membership cannot quietly widen an agent. Static API keys keep working for
small deployments.

### Later: delegation

When an agent acts for a signed-in person ([plan section 3](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/plan.md)), its effective tools are its profile **intersected with**
what that person may do. Neither side alone can widen the other: an assistant cannot do more than the person, and the person cannot
lend the assistant tools outside its profile. Nothing is built for this now; the model leaves room for it by keeping the profile
on the agent's credential, separate from any person's.

## What it protects against, and what it does not

**It does not make an attack less likely.** Hostile content will still reach agents and some injections will still succeed. What
changes is **what a successful one can do**, and that cut is large:

- **It limits the damage.** A hijacked invoice assistant can misuse only the invoice assistant's tools. It cannot see
  `skills.publish`, the finance database it was never given, or the mail-sending tools of another agent.
- **It breaks the dangerous combination.** The worst case for an agent is having all three of: private data, untrusted content,
  and a way to send data out. Today every agent of a deployment has every tool, so any agent that reads mail has all three.
  Profiles let the operator split them: the agent that reads inbound mail does not also hold `erp.query` and `mail.send`
  together. Taint already holds the send for a reviewer; profiles mean the tool is not there to try.
- **It shrinks what a model is told.** Fewer tools advertised means fewer tools an injection can name, and a smaller prompt.
- **Injection cannot escalate,** because the profile is bound to the credential the model never sees.

What it leaves to other defences:

| Threat | Covered by |
|---|---|
| Misuse of tools the agent legitimately holds (a reply agent writing a bad reply) | Taint, Queue, reviewers, rules, rate limits: as today |
| A stolen key | Key rotation, per-key rate limits, the audit trail; the damage is still capped at that key's profile |
| A persuasive proposal in front of a tired approver | `SelfApproval: Forbid`, report binding ([question 1](skills-publish-questions.md#1-can-the-reviewer-trust-the-report)), human approvers only |
| A compromised workbench | It can only propose, and a person approves; it holds no business tools |
| A wrong profile written by the operator | `--explain --caller`, the startup listing, and least privilege in the shipped examples |

## What changes for the skills work

- **`skills.publish` is an administration tool.** A business agent that read a hostile email cannot see it, let alone call it.
  The workbench and the AI tuner of the [skills design](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/design/skills-and-scenarios.md) connect with a profile naming it.
- **`skills.find` and `skills.get` stay ordinary tools:** the agents doing the work need them. They only read the published store.
- **The workbench never holds business tools on a live Host.** It reaches live data only through a shadow Orca, which records
  everything above Read.
- **Signing the report becomes unnecessary.** Only the workbench's key can call `skills.publish`, so the authenticated caller
  already proves where the report came from. Binding the report to the content and labelling it are still worth doing
  ([question 1](skills-publish-questions.md#1-can-the-reviewer-trust-the-report)).
- **A business agent with an opinion about a skill** leaves a note (a low-risk feedback tool), not a proposal.

## Until it is built: two Orca instances

Possible today, with no code: run the production Orca without `skills.publish` in its policy (so it is hidden there), and a
second Orca with only the `skills` module, reached only by the workbench's key, both on the same skill store directory. This
separates the planes by process. **Check before relying on it** whether two instances can share the store's label file safely
(`SkillStore` in `OpenTail.Modules.Skills`); only the second instance writes.

## Order

1. **Profiles on API keys:** `Agents` in the configuration, `Profile` on `ApiKeyEntry`, `tools/list` filtered per caller, calls
   outside the profile refused and audited, the startup warning for keys without one. `--explain --caller <key>`.
2. **`Administers`** on `ToolDescriptor` (an additive contract change, like `AuditResult`), `skills.publish` marked, the load-time
   checks, and operator marking in policy entries.
3. **The `skills.publish` fixes:** `expectedFrom`, `AllowEdits: false`, report binding and label
   ([questions to resolve](skills-publish-questions.md)).
4. **When the first customer runs Entra ID:** `RoleProfiles` from app roles.
5. **With per-user sign-in:** delegation.

A caller condition inside policy rules (queue `mail.send` for one agent, execute it for another) waits for a deployment that
needs it; two profiles and two policy entries cover most of it.

## What changed from the first version

| First version | Now | Why |
|---|---|---|
| Tool planes (business, administration) declared by modules | An `Administers` flag | One flag carries the only rules that differ; a plane enum invited a third plane nobody had defined |
| Two fixed agent roles, one per key | Named profiles | Deployments want named roles from the start, and a profile is a named role |
| `Callers` on each policy entry | Profiles on the principal | One agent's permissions in one place, readable by customer IT; policy stays about what happens, profiles about who |
| Signed reports | Not needed | The caller's credential already proves origin once only the workbench may call `skills.publish` |
| "Who may grant administration: one admin or two?" | Startup listing and notification | Keys live in a configuration file; Orca cannot enforce a second admin on a file edit, but it can make every grant visible |

## Decisions

Decided by the owner on 2026-10-18.

- **Keys without a profile:** today's behaviour with a startup warning in the release that ships profiles; refused at startup
  one release later. Nothing breaks on upgrade, and it tightens on a schedule.
- **Shipped example profiles:** one per sample deployment, plus `workbench`.
- **The three [`skills.publish` questions](skills-publish-questions.md):** accepted as recommended (the report bound to the
  content, anything else labelled, no signing; `expectedFrom`; `AllowEdits: false`).
- **Publishing approvals:** one approval to move the `staging` label, two (different people) to move `production`.

Still open: where administration-grant notifications go (the existing notification channels, or the audit trail only).
