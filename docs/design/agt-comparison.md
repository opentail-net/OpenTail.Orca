# Comparison: Microsoft's Agent Governance Toolkit (.NET) and the Host

**Status:** findings recorded 2026-11-11. **Decision: defer adoption and conversion.** Nothing has been changed or converted. This
document only records what the two systems do, where they differ, and why we are waiting.

**Why this exists:** the owner asked whether the toolkit could replace or feed our governance layer, especially its human approval
chain. The answers were not stable: an early reading of the .NET docs page missed the approval code, and later reads corrected it.
This document is the reference, so the reasons can be checked later without redoing the reading.

## Read this first

- The toolkit's .NET library **does** have a human approval protocol: request, ordered stages, reviewers, a webhook transport with
  verified responses, an action digest, and a re-check right before execution. It is about 2,400 lines in `Approvals/`.
- The Host already has a human approval queue. It covers taint, labels, profiles, self-approval and audit, and that queue is the
  single source of truth for an action. Adopting the toolkit's chain would mean two approval systems for one action.
- The toolkit's approval store is an interface with only an in-memory reference implementation. Its kernel does not call the approval
  code at all; the application has to wire it in.
- The Host has one real gap that the toolkit closes: when an approved action runs, the Host does not compare the policy and arguments
  it was approved under. This should be fixed in the Host whatever we decide about the toolkit.
- Decision: do not adopt or convert now. Revisit on the triggers at the end.

## Sources and pins

### The toolkit

| | |
|---|---|
| Repository | <https://github.com/microsoft/agent-governance-toolkit> |
| .NET subfolder | <https://github.com/microsoft/agent-governance-toolkit/tree/main/agent-governance-dotnet> |
| Core package | `Microsoft.AgentGovernance` 5.0.0, MIT, targets `net8.0`, depends on YamlDotNet 18.1.0 ([NuGet](https://www.nuget.org/packages/Microsoft.AgentGovernance)) |
| Extension packages | `Microsoft.AgentGovernance.Extensions.ModelContextProtocol`, `Microsoft.AgentGovernance.Extensions.Microsoft.Agents` |
| Local copy | `C:\Git\split\examples\agent-governance-toolkit\agent-governance-dotnet\` |
| Pin | The local copy has **no git history of its own**: it sits inside the `C:\Git` repository, so that repository's commit is not the toolkit's. The pin is the SHA-256 of each file read (table below). |

The version is from `Directory.Build.props` (line 3). The upstream README says "public preview ... may have breaking changes before GA".

### The Host

| | |
|---|---|
| Repository | `C:\Git\split\opentail-host` (private, PolyForm Strict) |
| Branch and commit | `standard-skill-files`, commit `4b8a8b7`. Uncommitted changes at the time were `docs/design/README.md` and `docs/design/a2a.md` only; no code had changed. |

Line numbers below refer to that commit.

### File hashes of the toolkit files read

Re-check with `sha256sum` in the local copy. A changed hash means the copy differs from the one read here.

```
a336ea34905e966a82349fb1f06d6b202b5294891e8d56dfc13a9f3b722513c1  src/AgentGovernance/Approvals/ApprovalAudit.cs
a47c6dd6241e29ed77e239ce83f59fd36d4218aa432a5bf0af9bc79cd949216e  src/AgentGovernance/Approvals/ApprovalCoordinator.cs
7053b75c261dd3c69ab872568599c5d327552e19b7964cd0aafbe643287e3220  src/AgentGovernance/Approvals/ApprovalDigest.cs
cd9f3f8f53141c4a30e83968d8bef24c54a9ee67454cf153b2453e6501b29649  src/AgentGovernance/Approvals/ApprovalModels.cs
eda740852d3edae41cd22ea05449ee0f3c0b3111ba246da56a047e2de9325e5b  src/AgentGovernance/Approvals/ApprovalStore.cs
950935100fb41db04b36dcada3e7a2481ae56f690fc54d73ca30e17663bf0b65  src/AgentGovernance/Approvals/ApprovalTransport.cs
f1f0ce5e29bc19e04613724dc32289eff8fd3767eb565cf7828186bfedd81084  src/AgentGovernance/Approvals/WebhookApprover.cs
709c4419e3acdb75b00edbf9ac0b9471894ee79dc4c398933882e9705e6e936e  src/AgentGovernance/Policy/PolicyDecision.cs
4ebd82baf69cec02f30a84d3e127c83f0f3937a466385b754278177bcccd489b  src/AgentGovernance/Policy/PolicyEngine.cs
3f3a094ad58006f6ed412336a0e7f7ae4493dc9c143e243d96a81501ae278993  src/AgentGovernance/GovernanceKernel.cs
5e682776ea8bcb38c18a77dcb6675b260344818634d58437e79ca975f39f6f9e  src/AgentGovernance/Audit/AuditLogger.cs
35a906fb39d99a0e89175bffd2f2b39659c913a2c58102017dc6bf4ea33608ef  src/AgentGovernance/Trust/AgentIdentity.cs
c5f506dd155ceb18e688d469ce9e1a56870ab58f16236490d67c006849aed539  src/AgentGovernance/RateLimiting/RateLimiter.cs
a8bcce9b730d90c172f6731234019be494f2682994dbff20f567ccb3c6ac021d  src/AgentGovernance/Hypervisor/KillSwitch.cs
8383ec8a4b65f33966b9cf4bfb25a10cc1343c81bd136431f433eb0fa4dff704  src/AgentGovernance/Hypervisor/ExecutionRings.cs
e3b28689b3c44d982ba3a9b488951a534889d2fd41932baef49de90dc112377f  src/AgentGovernance/Security/PromptInjectionDetector.cs
d63dc09ce0dcb697f7b13776bb4c8cfc58e654c1e509636a73d2dba5b6bc1c74  src/AgentGovernance/Mcp/McpSecurityScanner.cs
6cb65243343c095c9e1fda89b72cde77a8214fe1cdee7daf7272288fcfdceeb2  src/AgentGovernance/Policy/CedarPolicyBackend.cs
a454f4f47c377dc85f165a5fe9ce584f26bdf22ef4f1cf7f77119b5b8d7572dc  src/AgentGovernance/Policy/OpaPolicyBackend.cs
0a4456ff5fbef3447b5f681c4514b63065b9501d8b3fff56d20d33ac71aaec9d  AgentGovernance.sln
e3c5d808ae7e2f3a18ce058394dcc7641814d83cd36a53e5e8e4267e132a0291  Directory.Build.props
fc0ead79ba8b667820c57aafa385f4f56a7bd249f67d38d2340885c55c2f91c8  README.md
c072a021823bf59c3cdc4f190473c33b071bf34e3efa08ff96acbe95d1c90aa0  AGENTS.md
```

## How each system handles one approval, in order

**The toolkit (as library calls the application makes):**

1. The application calls the kernel. The kernel returns a decision. A `require_approval` decision means "suspended, not allowed".
2. Nothing in the kernel opens an approval. The application builds an `ActionBinding` and calls `ApprovalCoordinator.OpenRequest`.
   The request is saved to the approval store, with `ExpiresAt` set from `RequestTtl`, and `FailClosedOnTimeout = true`.
3. Each stage is answered with `SubmitEntry`, or through `WebhookApprover`, whose responses are checked by a verifier the
   application supplies.
4. Before running the action, the application calls `ValidateForExecution`. It re-checks the digest, the policy version and the
   approval chain, and consumes a one-time approval.

**The Host (inside one component):**

1. `ActionQueue.SubmitAsync` runs every check in order (profile, decoys, identity level, taint, policy, data flow) and decides
   Execute, Queue or Deny.
2. A Queue decision stores a `Pending` action with its arguments hash, policy hash and expiry. Reviewers decide it through
   `ReviewAsync`, and the reviewer's identity and groups are recorded.
3. Expiry is handled by `ExpireDueAsync`.
4. When approved, `ActionExecutor` claims the action (`Approved` to `Executing`). The claim checks the status and the rate limit. It
   does not re-check the policy or the arguments. **This is the gap.**

## Differences, by area

| Area | Toolkit (.NET, local copy) | Host (4b8a8b7) | Difference |
|---|---|---|---|
| Decision outcomes | `allow`, `deny`, `warn`, `log`, `require_approval` (`Policy/PolicyDecision.cs` line 17; `PolicyAction`) | `Execute`, `Queue`, `Deny` (`OpenTail.Host.Abstractions/Policy/ToolPolicy.cs` lines 3-11) | Same idea. Our `Queue` is their `require_approval`. They also have `warn` and `log` as outcomes; we don't. |
| Decision point | No single point. `GovernanceKernel.EvaluateToolCall` (`GovernanceKernel.cs` line 246) returns a decision; the kernel has no approval references. Policy rules, context escalation and the MCP gateway (`Mcp/McpGateway.cs` lines 17, 155-157, `RequiresApproval`) produce a "needs approval" result, but nothing in `src/` outside `Approvals/` references `ApprovalCoordinator`, so nothing opens an approval. | `ActionQueue.SubmitAsync` (`OpenTail.Host/Runtime/ActionQueue.cs` line 23) is the only place a call is decided. | Ours is enforced where the action is stored. Theirs is a library the application must call correctly. |
| Policy language | YAML (YamlDotNet dependency), plus OPA and Cedar backends (`Policy/OpaPolicyBackend.cs`, `Policy/CedarPolicyBackend.cs`, `Policy/ExternalPolicyBackend.cs`). I read the file names, not the backends' behaviour. | JSON policy with conditional rules (`OpenTail.Host/Policy/Rules/Conditions.cs`). No OPA or Cedar. | They can use external policy engines; we can't. |
| Approval objects | `ApprovalRequest`, `ApprovalChainEntry`, `ApprovalResolution`, `ApprovalPolicyDecisionRecord` (`Approvals/ApprovalModels.cs` lines 194-476) | `ActionRecord` (`OpenTail.Host.Abstractions/Actions/ActionRecord.cs` line 81), `Reviews` (line 163), `Status` | Same concepts under different names and shapes. |
| Approval stages and approvers | Ordered `ApprovalStage` with `AllowedIdentities` and `AllowedRoles`; `ApproverKind.Human` (`ApprovalModels.cs` lines 38-49, 433-459) | `ReviewerGroup` (`ToolPolicy.cs` line 52), `RequiredApprovals` (line 55), `ReviewerKind` (lines 21-26) | Theirs supports ordered stages. Ours is one group and a count. |
| Approval store | `IApprovalStore` interface (`Approvals/ApprovalStore.cs` lines 7-38), with `InMemoryApprovalStore` as the only implementation in the copy | `IActionStore` (`Interfaces.cs` line 37), SQLite, compare-and-set transitions | Theirs is pluggable but has no durable implementation in the copy. Ours is durable. |
| External approvals | `IApprovalTransport` (`Approvals/ApprovalTransport.cs` line 10); `WebhookApprover` (`Approvals/WebhookApprover.cs` line 15) with a response verifier and request-time address checks | Outbound notifications only, signed (`OpenTail.Host.Notifications/Channels.cs` lines 42-43). No inbound approval webhook. | Theirs can take approvals from outside systems, with verified responses. Ours cannot. |
| Action binding | `ActionBinding` (`ApprovalModels.cs` line 101): agent, subject, operation, target (tool, schema version, resource) and parameters, with `Digest()` over the canonical JSON | `ArgumentsHash` (`ActionRecord.cs` line 90) and `PolicyHash` (line 123), stored separately. No single digest over the whole action. | Theirs binds one approval to the whole action. Ours binds the arguments and the policy separately. |
| Canonical JSON | `Approvals/ApprovalDigest.cs`: deterministic JSON with sorted keys. The file calls it "RFC 8785-style". Conformance is not verified; number formatting is the usual risk. | Not checked. | Needs test vectors before anyone says "RFC 8785". |
| Re-check before execution | `ValidateForExecutionCore` (`ApprovalCoordinator.cs`, from line 429): consumed, cancelled, expired, resolution outcome, action digest, policy version, chain id and version. The entry chain is checked first (lines 510-516), then the approval is consumed by `IApprovalStore.TryConsume` (line 517), which succeeds only once. | Claim step (`OpenTail.Host/Runtime/ActionExecutor.cs` line 385): status and rate limit only. `PolicyHash` is stored and never compared. | **Gap in ours.** |
| Expiry and timeout | `RequestTtl` 5 min and `StageTimeout` 5 min (`ApprovalCoordinator.cs` lines 70, 73); `FailClosedOnTimeout = true` set at open | `ExpiresAt` from `ExpireAfter` (`ActionQueue.cs` line 248), `ExpireDueAsync` (line 628) | Same effect. Theirs has explicit defaults. |
| Approval audit | `IApprovalAuditSink` (`Approvals/ApprovalAudit.cs` line 75); in-memory sink | `IAuditSink` (`Interfaces.cs` line 139); SQLite; chained with checkpoints (`OpenTail.Host/Storage/AuditChain.cs`) | Ours persists and has checkpoints. Theirs is in memory. |
| Audit chain | `AuditLogger` (`Audit/AuditLogger.cs`): an in-memory list, SHA-256 over `seq|timestamp|agentId|action|decision|previousHash` (lines 65, 234-237). Approval fields are not in the hash. | `AuditChain.cs`: SHA-256 chain, checkpoints (`AuditCheckpoint`), verification command (design: `audit-and-evidence.md`) | Their README says "tamper-proof". The code is an in-memory chain with no external anchoring. The ADR describes its approval chain as tamper evidence only inside a trusted store boundary. |
| Identity | `AgentIdentity` (`Trust/AgentIdentity.cs` lines 35, 308-323): native ECDSA P-256; HMAC paths (lines 698-730). `Jwk.cs` verifies Ed25519 (lines 53, 83). | `CallerIdentity` with namespaced subject (`OpenTail.Host.Abstractions/Identity/CallerIdentity.cs` line 16). API keys stored as SHA-256 hashes (`OpenTail.Host.Http/HostHttpOptions.cs` lines 176-192). JWT as an OAuth 2.1 resource server (`HostHttpOptions.cs` lines 14-46). | Different problems. Theirs is about agent identity documents. Ours is about who may call. |
| Profiles and least privilege | No equivalent identified in the files read. | Profiles bound to the credential (`OpenTail.Host/Policy/AgentProfiles.cs`) | Ours has no counterpart in the files read. |
| Untrusted content | `PromptInjectionDetector` (`Security/PromptInjectionDetector.cs`): pattern-based, sensitivity setting, custom patterns | Session taint on reads of untrusted content (`ToolDescriptor.cs` line 12; `SessionRegistry`), data labels and flow rules (`OpenTail.Host/Policy/DataLabels.cs`) | Theirs detects. Ours constrains what can happen after untrusted content arrives. |
| Rate limits | `RateLimiter` (`RateLimiting/RateLimiter.cs`): sliding window per key, lines 11-57 | `ToolPolicy.RateLimit` (`ToolPolicy.cs` line 48), `RateGate` (`OpenTail.Host/Runtime/RateGate.cs`), per-subject HTTP limit (`HostHttp.cs`) | Similar. |
| Kill switch and suspension | `KillSwitch` (`Hypervisor/KillSwitch.cs` lines 41-100): arm, disarm, `Kill(agentId, reason, detail)`, history, `OnKill` | `IdentityLevels` with a Suspended level and `SuspendAsync` (`OpenTail.Host/Runtime/IdentityLevels.cs` line 170); decoy tripwires (`ActionQueue.cs` lines 101-116) | Similar purpose. Ours is tied to identity and fires automatically. |
| Execution rings | `ExecutionRing` and `RingResourceLimits` (`Hypervisor/ExecutionRings.cs`) | None identified. | Theirs has resource tiers. |
| MCP | `McpGateway`, `McpSecurityScanner`, `McpResponseSanitizer`, `McpCredentialRedactor` (`Mcp/`). I read the file names. The README says the scanner detects tool poisoning and typosquatting; I did not read the scanner. | MCP is our server. A search for poisoning, typosquatting or tool-description scanning found nothing. | Matters only if we consume third-party MCP servers. |
| Packaging and maturity | MIT; `net8.0`; preview per README | PolyForm Strict; `net10.0`; private | Different licence and runtime. Theirs is preview. |
| Kernel and wiring | Library. Application wires approvals and execution. | One process; the queue, executor and store are wired by `OpenTailHostBuilder` (`OpenTail.Host/OpenTailHost.cs` lines 113-165). | Ours is wired by default. Theirs must be wired by the application. |

## Corrections to what was said earlier

- **"The .NET approval code is not visible."** Wrong. The earlier fetch could not show the `src/` listing. The local copy has `Approvals/`, about 2,400 lines in seven files.
- **"The .NET store is not defined."** Wrong. `IApprovalStore` is defined. It has only an in-memory reference implementation.
- **"The toolkit cannot express the approval path."** Wrong. It can. The error came from reading the .NET docs page, which does not cover approvals, and not the source.
- **"The kernel handles approval."** Not true. Nothing in the kernel references the approval types, and nothing in `src/` outside `Approvals/` calls `ApprovalCoordinator`.
- **The toolkit's re-check before execution.** I had only the README's description of it and had not checked it against the code. The code does implement it. `ValidateForExecutionCore` (`ApprovalCoordinator.cs` from line 429) checks the cancellation, expiry, resolution, action digest, policy version and chain id and version. It checks the entry chain (lines 510-516), then consumes the approval with `IApprovalStore.TryConsume` (line 517). The store's `TryConsume` (`ApprovalStore.cs` lines 163-179) succeeds only from `Allowed`, and marks the request `Consumed`.
- **The commit hash from the first look.** The hash came from the `C:\Git` repository, not from the toolkit.

## Decision

**Defer adoption and conversion.** Reasons:

1. **The valuable part is small.** The re-check before execution, the whole-action digest and the verified approval responses can be
   built in the Host with less work than adopting and wiring the toolkit. The re-check is the one that matters now.
2. **One approval system.** The Host's queue already carries taint, labels, profiles, self-approval and audit. Adopting the toolkit's
   chain would mean two systems for the same action, or rewriting the Host's queue around the toolkit's objects.
3. **Glue on both sides.** The toolkit's kernel does not drive approvals, so we would write the same wiring either way.
4. **Maturity.** Preview. The approval store has only an in-memory implementation in the copy. The digest's RFC 8785 conformance is
   unverified. The ADR (per the summary I read) says the reference implementation is Python and .NET parity was deferred, though the .NET code exists.
5. **Enforcement point.** It runs inside our process. It does not add a boundary we don't already have.

## Revisit when

- A customer requires the Microsoft approval protocol, or its conformance tests.
- The .NET approval code reaches a stable release with published conformance vectors.
- We need OPA or Cedar policy backends.
- We consume third-party MCP servers and need tool-description scanning.
- Our own re-check proves insufficient in practice.

## Candidates to port, if we build them ourselves (not decided)

- **Re-check before execution** (`ValidateForExecutionCore`): the policy version, the argument hash and the expiry, checked in the
  executor's claim. This is the gap; it should be fixed regardless.
- **A digest over the whole action** (`ActionBinding.Digest`): agent, subject, operation, target and arguments, with the policy version.
- **Verified approval responses** (`WebhookResponseVerifier`): needed if approvals ever come from outside the Host, for example from
  the A2A plan's review step.
- **Explicit fail-closed flag** (`FailClosedOnTimeout`): our expiry already denies; the flag would make that visible in the record.

## Not verified

- Most of `ApprovalCoordinator.cs` (911 lines). Only the request, execution and consumption paths were read.
- Whether the digest matches RFC 8785 on any test vector.
- The toolkit's own tests (`tests/AgentGovernance.Tests/`, including `ApprovalProtocolTests.cs`): not read and not run.
- The Python implementation and its parity.
- The McpSecurityScanner's behaviour.
- The Host's `ArgumentsHash` computation.
- Whether the Host has any execution-time check outside the claim step.

## How to re-check

1. In the local copy: `sha256sum` the files listed above and compare with the table.
2. Read `src/AgentGovernance/Approvals/ApprovalCoordinator.cs` from `ValidateForExecutionCore` to the end.
3. In the Host: `grep -rn "PolicyHash" OpenTail.Host` and confirm no comparison exists in the executor.
4. Compare the two approval flows above with the current code.
