# Review of the example projects: what is worth mining

Status: **review, not a decision.** Written 2026-11-13. It feeds the reviewer-gate plan (`docs/design/reviewer-gate.md`); nothing here changes
that plan yet. Sources are local copies of seven projects supplied under an examples folder, cited below as `examples/<project>/...`
relative to that folder. Rag.NET was consulted only. Credits for each project are in [`THIRD-PARTY-ACKNOWLEDGEMENTS.md`](../../THIRD-PARTY-ACKNOWLEDGEMENTS.md).

**Method and limits.** I read each project's README, its documents on approval, policy, audit and execution, and the code paths named
below. I did not read every file (the Microsoft toolkit alone has about 5,000), did not run any project, and did not test the points marked
"not reproduced". Ideas are paraphrased. No code, configuration or text is copied.

## 1. Summary

- Five projects are worth mining for the gate: the Microsoft toolkit (one ADR and one spec), Signet, actionproxy, PortcullisMCP and cordon
  (selectively). Darvoza is worth mining for hardening, not the gate.
- The most useful single source is the toolkit's **ADR-0030**, an accepted, action-bound approval protocol. It corrects three things in our
  plan: timeouts must never approve, an approval must be consumed by the execution it authorises, and an LLM's output is advisory.
- Reading our own code against these sources found one gap that is not in the plan yet: **a retry of an interrupted action appears
  unguarded** (F11, section 3; read from code, not reproduced).
- Two of our plan's ideas conflict with an accepted design in the toolkit: an AI in the approver seat, and an undo window that runs by itself.
  Both need an explicit owner decision (section 4).

## 2. Per project

### 2.1 Microsoft agent-governance-toolkit (MIT; has a NOTICE file)

**What it is:** a large governance suite for agents. Two parts matter here.

**Ideas**
- **ADR-0030, action-bound approval protocol** (`examples/agent-governance-toolkit/docs/adr/0030-action-bound-approval-protocol.md`).
  - §1: `require_approval` is a suspended decision. It is neither allow nor deny, and nothing runs until a terminal approval exists.
  - §2: before an approval is requested, the exact request is turned into a binding with an action digest.
  - §4: chain semantics. A deny ends the chain. Duplicates are idempotent. Conflicting votes are rejected and audited. Expiry, transport failure
    or an invalid identity resolve to deny or expired, never allow.
  - §5: the approver's identity must be authenticated. A name in the response body is not evidence.
  - §6: immediately before execution, check that the resolution is terminal allow, not expired or consumed, and that the action digest, policy
    version and chain version all match. Consume the approval in the same transaction that starts execution, so two workers cannot reuse it.
  - §8: an LLM may produce advisory output only. The rejected alternative "let an LLM judge approve directly" is recorded with its reasons.
  - The context also records a bug: a callback's timeout was measured after the callback returned, so a slow callback was never interrupted.
    And a timeout that auto-approves is "unsuitable for a governance boundary".
- **MCP security gateway spec** (`docs/specs/MCP-SECURITY-GATEWAY-1.0.md`).
  - §4.2: a fixed evaluation order: deny-list, then allow-list, then approval for sensitive tools, then rate limit, then allow.
  - §5: response scanning, with a block, sanitise or log policy, aimed at prompt injection in tool results.
  - §16 and the glossary: schema drift detection and a per-tool fingerprint, to catch a tool whose description changes silently after approval.
- **Agent SRE** (`agent-governance-python/agent-sre/README.md`): hierarchical budgets, auto-throttle and a kill switch.

**Where it lands:** ADR-0030 is the core of the gate's contract. It settles the open questions in the plan's section 4 (F4, F8, the approval
versus execution split). The fingerprint and response scanning are new channel defences for MCP.

**What we already have:** the execution-time recheck of the policy hash and binding digest (`OpenTail.Host/Runtime/ActionExecutor.cs:400-443`),
which covers the recheck part of §6. Whether the approval and the execution start share one transaction is not verified. Expiry fails closed (`ActionQueue.ExpireDueAsync`). Duplicate-vote handling exists in
`ReviewAuthority.Tally`.

**Verdict:** mine. Strongest source, and it is accepted design, not an experiment.

### 2.2 Signet (MIT)

**What it is:** a provider-neutral human-approval gateway for MCP, in Python. Approval is for consequential writes.

**Ideas**
- **Never retry an unknown outcome blindly.** A crash or timeout after possible dispatch becomes `outcome_unknown`. The caller must not
  resubmit. Signet does a bounded read-only reconciliation, and redispatches at most once, and only when a stable provider idempotency key
  exists and the first attempt is proven not to have had an effect
  (`examples/Signet/docs/mcp-approval-tools.md`, around lines 112-130; `docs/security-model.md`, lines 24-25).
- **A durable fence before network I/O.** Dispatch is recorded before anything leaves, so a later crash is known to be ambiguous
  (`docs/security-model.md`).
- **An approver can approve but not change policy or credentials.** The MCP approval path cannot deny, edit, retry, manage credentials or
  approve a policy change (`examples/Signet/README.md`, Guarantees).
- **Step-up proof for approval.** A fresh TOTP code or WebAuthn assertion is bound to one action, request, version and payload hash, and is
  consumed in the same transaction (`README.md`, Guarantees).
- **Honest scope.** The security model says what the controls do not stop: a process running as the same user, and any path that bypasses
  the gateway (`README.md`, and `docs/security-model.md`).

**Where it lands:** the unknown-outcome rule closes a gap in our code (F11). The approver split supports the approver identity in plan §5.2.
The step-up proof is an option for irreversible actions when an AI is approving.

**Verdict:** mine.

### 2.3 actionproxy (Apache-2.0)

**What it is:** approval gates for AI agent tool calls, in TypeScript, with a local demo and a documented decision model.

**Ideas**
- **Separate the approval from the execution authority.** An approval is a human decision. Execution gets its own object: one-use, with a default
  lifetime of 60 seconds. It is consumed before the provider is called, and a second use is an audited replay
  (`examples/actionproxy/docs/execution-authorization-v1.md`, lines 19-28, 92, 115). This is the cleanest way to design "yes, with undo".
- **Separate the authorization deadline from the SLA reminder.** The 24-hour authorization lifetime is a hard limit. The four-hour SLA only
  prompts reminders and never expires or authorises anything (`docs/approval-authorization-v1.md`, lines 44-45).
- **Approvals bound to a nonce and a hash.** Terminal states consume the nonce. A storage compare-and-set checks status, nonce, hash, expiry,
  binding and quorum in one transaction (`docs/approval-authorization-v1.md`, lines 62, 88). This is also the fix for our budget race (F8).
- **Stable reason codes and a decision projection.** Outcomes are allow, deny or require approval, with machine-readable reason codes and a
  policy digest. Human text is for display only (`docs/decision-model-v1.md`).
- **Taint by content source, per rule** (`apps/server/src/policy/content-influence.ts`, `evaluateContentInfluence`). A rule lists the source
  integrities it allows. Content outside that list tightens the decision, and unverified scope is treated as unknown. Restrictions only
  tighten (`docs/CONTENT_INFLUENCE_PERFORMANCE.md` describes the same path).
- **Threat table** (`docs/THREAT_MODEL.md`): concurrent approvers racing to finalise one action, and external runners that execute a different
  payload than the one approved. Both are controlled by storage-level conditional transitions.
- **Delivery failure never removes a pending approval** (`docs/APPROVAL_CHANNELS.md`). Channel messages are presentation only.

**Where it lands:** the execution authority gives plan §5.3 a bounded window that is revalidated when it closes. Stable reason codes help
the AI verdicts in §5.3 to be audited. Source-based taint is an option for the taint decision (D-2).

**Verdict:** mine.

### 2.4 PortcullisMCP (Apache-2.0)

**What it is:** an MCP gateway in Go with a policy decision point (PDP), and an "escalation" service that asks a human.

**Ideas**
- **A signed, narrow, expiring escalation token.** When the PDP needs escalation, it returns the exact arguments. A human approves those
  arguments on a separate page. The approval is a signed token bound to those arguments. The PDP re-checks the token on retry, and the token
  cannot be used for a different argument set. The token can be revoked (`examples/PortcullisMCP/README.md`, "Explain 'escalation'").
- **Decision logs for both steps.** The escalate decision and the allow decision are both logged, with the signed token, so an auditor can
  match the approval to the arguments.
- **Agent self-approval, named as a threat.** The approval page sits behind a single sign-on proxy, so the agent cannot complete the human
  challenge (`examples/PortcullisMCP/docs/guard-sso-proxy.md`, lines 3-12).

**Where it lands:** the signed token matters only when an external runner executes an action, which we do not do today. The self-approval
threat applies directly to plan §5.2: an AI approver's identity must not be one the agent controls.

**Verdict:** mine, selectively. The token is for later. The self-approval threat is for now.

### 2.5 cordon (MIT)

**What it is:** a security gateway for MCP tool calls, in TypeScript.

**Ideas**
- **Deny on timeout, but keep the request replayable.** An unanswered approval is denied when it times out. Its full context is kept, and it
  can be replayed if someone approves it late (`examples/cordon/README.md`, around lines 134-137 and 198-204). Our expiry marks an action
  Expired. Our `--requeue` command can revive an ended item, but nothing does this automatically.
- **Call-graph rules.** A rule can require approval for one tool after another, such as a send after a read of sensitive data. Rules only
  add severity (`README.md`, around lines 278-310). Our rules see only the arguments of the call in question.
- **A known-tool surface.** A tool that appears in an upstream update is blocked until someone promotes it (`README.md`, around lines 258-274).
- **Fail-closed SQL classification.** Unparseable SQL is blocked (`README.md`, around line 257).

**Caveat:** its read-only check uses tool-name prefixes (`README.md`, around lines 219-222). That is a heuristic and should not be copied.

**Verdict:** mine, selectively: call-graph rules and the replayable timeout.

### 2.6 Darvoza (MIT)

**What it is:** a .NET 10 gateway that puts per-role tool policy and an audit trail in front of any MCP server.

**Ideas (hardening, not the gate)**
- **Audit at the outermost layer, covering denials** (`examples/darvoza/docs/adr/ADR-0003-audit-trail-and-decision-context.md`). Every call is recorded, allowed or denied.
- **Secrets never travel in arguments.** The upstream receives a curated environment plus only the variables named. Gateway secrets are refused
  by name at startup. The resolved argument list is written to the log once, so a secret there would leak (`docs/adr/ADR-0004-...md`, and the
  comments in `policy.example.yaml`).
- **The policy file is a trust boundary.** Whoever can edit it can allow anything, so it must be writable only by its owner
  (`policy.example.yaml`, the comments; ADR-0004).

**What we already have:** the hash chain and `--verify-audit` (`OpenTail.Host/OPERATIONS.md`), and policy versions with hashes. We have not
checked whether our audit covers requests denied before they reach the queue. That check is open.

**Verdict:** mine for hardening, after the gate work.

### 2.7 mcp-gateway (MIT)

**What it is:** a multi-tenant MCP gateway in Python, with per-identity process isolation and pluggable approvers.

**Ideas**
- **Per-identity isolation.** Each identity gets its own worker and secret (`examples/mcp-gateway/README.md`). That is a credential-scoping idea
  for later, not for the gate.
- **A three-state rule per tool with defaults and overrides:** auto-approve, push-approve or reject (`architecture.md`, around lines 35-37 and
  183-187). We already have per-tool modes.

**Anti-pattern:** the zero-infrastructure approver is `auto`, which approves everything (`README.md`, the pluggable-adapters table, and
`policy.example.minimal.yaml`). That should never be a default.

**Other observations:** the approval acknowledgement is bound to a backend-stored token, not to the topic name (`architecture.md`, security
table). Pending approvals live in a transient store and are lost on restart (`architecture.md`, around line 240). Our queue is durable, so
we are ahead there.

**Verdict:** ideas only. The `auto` approver is a warning, not a pattern.

### 2.8 Rag.NET (MIT)

**What it is:** a .NET library for retrieval-augmented generation. It has no approval or gate features.

**Consulted only:** model output that comes from outside the corpus, such as a described image, is treated as untrusted text and sanitised by
default (`examples/Rag.NET/docs/guide/ingestion.md`, around line 214). Retrieval chooses sources by provenance, not by score
(`docs/guide/graphrag.md`, around line 421).

**Verdict:** skip for the gate. The untrusted-output principle matches our taint rule, but it adds nothing new.

## 3. Where our own code is already ahead, or behind

| Area | Ours | Examples | Gap |
|---|---|---|---|
| Recheck before run | Policy hash and binding digest at execution (`ActionExecutor.cs:400-443`) | ADR-0030 §6; actionproxy | Not verified: whether approval and execution start share one transaction |
| Crash recovery | Interrupted runs become Failed (retryable); only idempotent-by-key tools are retried automatically (`ActionExecutor.cs:197-236`) | Signet: reconcile before any redispatch | **Retry path has no check** (see F11) |
| Durable queue | Yes | mcp-gateway loses pending approvals on restart | We are ahead |
| Audit integrity | Hash chain, `--verify-audit` | actionproxy, Darvoza | Both have it; no gap |
| Approval identity | Authenticated tokens and `ReviewAuthority` | ADR-0030 §5; PortcullisMCP | Gap: an AI approver would authenticate as `admin:` today (F1) |
| Undo window | Delayed execution exists for Execute rules | actionproxy: a bounded, revalidated execution window | Gap for queued approvals (F4) |
| Source-based taint | Session-wide, time-windowed | actionproxy: per rule, per source | Choice, not a gap (D-2) |

**F11 (new).** `ActionQueue.cs:497-501` is the Retry path. It moves a Failed action to Approved and executes it again. It does not check the
error kind (`interrupted`) or whether the tool is idempotent by key. The recovery path (`ActionExecutor.cs:197-236`) does check both before
it retries automatically. So a manual or AI Retry of an interrupted, non-idempotent action can run it twice. I read the path but did not
reproduce it, and I have not checked whether `ExecuteAsync` guards it.

## 4. Where the examples disagree with our plan

1. **An AI in the approver seat.** ADR-0030 §8 says an LLM's output cannot satisfy a human approval stage and cannot produce allow or deny.
   Its rejected alternatives say directly that letting an LLM judge approve "does not constitute human oversight". Signet and PortcullisMCP
   both keep human approval behind a channel the agent cannot reach. Our plan (§5.2, D-6 and D-7) puts an AI there. That is a decision to make
   knowingly, especially if the audit trail is later presented as human oversight. ADR-0030 also notes this bears on EU AI Act Articles 12 and 14.
2. **An undo window that runs by itself.** Our delayed run (`ActionQueue.RunDueDelayedAsync`) executes the action when the window ends, unless
   someone stops it. That is a timeout approval, and ADR-0030 calls that unsuitable for a governance boundary. For reversible actions it is
   reasonable. For partner replies it is a risk to accept explicitly, or to replace with a bounded execution authority that revalidates at
   the end (actionproxy's model).
3. **The Retry path** (F11), described above.

## 5. Proposed changes to the plan (v2)

| Idea | Source | Plan section | Proposed change |
|---|---|---|---|
| Never redispatch an unknown outcome without reconciliation | Signet; ADR-0030 | New F11; §5.9 | Add a rule: a Retry of an interrupted action is refused unless the tool is idempotent by key or a reconciliation proves no effect. Test it first. |
| Consume the approval in the execution transaction | ADR-0030 §6; actionproxy | §5.8 (F8) | Make the approval consume and the execution start one transaction. This fixes the budget race as well. |
| Short-lived execution authority | actionproxy | §5.3 (yes, with undo) | Model the undo window as an execution authority that expires and is revalidated when it closes. |
| Approver cannot change policy or credentials | Signet; PortcullisMCP | §5.2 | An AI credential may approve or refuse only. It cannot edit policy, credentials or budgets. |
| Approver is not agent-controlled | PortcullisMCP; Signet | §5.2 | Record who can reach the approval route. An AI approver counts only if the agent cannot reach that route. |
| Source-based taint, per rule | actionproxy | §5.4 (D-2) | Add as option D: an allow list of content sources per rule, with unknown when scope is unverified. Decide with the owner. |
| Sequence rules | cordon | §5.10 (new) | Add rules that look at recent calls in the session, such as a send after a read of sensitive data. |
| Deny on timeout, keep it replayable | cordon | §5.3 | Keep expired approvals replayable for a set period, with a human decision to revive. |
| Stable reason codes | actionproxy | §5.3 | Every verdict, human or AI, carries a reason code, so the audit can be queried. |
| Response scanning and tool fingerprints | toolkit MCP spec | new §5.11 (MCP channel) | Scan tool results before they reach the agent, and fingerprint tools to detect silent changes. Mainly defence for MCP. |
| Audit at the outermost layer, including denials | Darvoza | hardening backlog | Check whether requests denied before the queue are audited. |
| Secrets not in arguments | Darvoza | hardening backlog | Check credentials passed to modules. |

## 6. Not adopted

- Rag.NET: no gate ideas.
- mcp-gateway's `auto` approver as a default: an anti-pattern.
- cordon's name-prefix read-only check: too weak.
- The toolkit's other components (Agent Mesh, Agent Lightning, the policy engine's Rust core): outside the gate's scope. They can be reviewed separately if needed.

## 7. Decisions for the owner

- **Accept or reject the AI approver** in light of section 4, item 1.
- **Undo window:** keep the run-by-itself behaviour for reversible actions only, or replace it with a bounded execution authority everywhere (section 4, item 2).
- **Taint option D** (source-based): include as an option, or leave it out.
- **Hardening backlog:** Darvoza's two checks, when to schedule them.

## 8. Next step

Fold section 5 into `reviewer-gate.md` as v2, after the owner's decisions in section 7. That is a separate, reviewable change to the plan. Nothing in
this review is committed.
