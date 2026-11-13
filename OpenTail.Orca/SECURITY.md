# Security model of the OpenTail Orca

Orca puts a human (or a smarter AI) between a language model and the things it could do. This page says what that protects against,
how, and, just as important, what it does **not** protect against. If you are deciding whether to deploy it, read the last section first.

## What we assume

* The **model is untrusted input**. Anything it sends can be shaped by content it read (a prompt injection in an email, a web page, a
  file, an API response, a database row). So can its choice of tool and its arguments.
* **Operators** (who write the policy file, run the process and hold the secrets) are trusted. **Reviewers** are trusted to read what they
  approve.
* The machine Orca runs on, and the OS account it runs as, are trusted. Orca is *guarded execution*, not a sandbox.

## Controls, by what they stop

| Threat | Control | Where |
|---|---|---|
| The model calls a tool nobody enabled | Deny by default: a tool with no policy entry (that is not a plain read) is denied and is not even listed to the model | policy |
| A hijacked agent reaches tools outside its job | Agent profiles: each API key names a profile, and the caller sees and may call only that profile's tools. The profile comes from the credential, so no prompt can widen it; calls outside it are refused and audited | policy, queue |
| A hijacked agent keeps trying | Identity levels (`Limits`): a call outside its profile suspends the identity, a rejected action elevates it (everything above Read queued); report only by default, enforced when set; only a person clears, a suspended key only after rotation | queue |
| A prompt injection makes the model send, delete or spend | Taint: once a caller read untrusted content, high-impact tools are held for a reviewer, whatever the policy says. Taint is set when the call is *submitted*, follows the caller across sessions for `TaintWindow` (so `start_session` cannot launder it), survives restarts, and is also set by reading an untrusted *result* through a status call | queue, sessions |
| A rule is bypassed with a crafted argument | Three-valued rules: unreadable or missing input can only move a call toward *more* caution. An auto-execute rule must account for every argument (a model-added `bcc` does not ride it). Exact domain matching; linear-time regex; no rule can switch taint off | rules |
| An agent approves its own request | Credentials are separate: an API key can never review; a reviewer token can never call tools; the reviewer recorded is the one the server authenticated, never anything in the request body. Optional `SelfApproval: Forbid` and several approvers | HTTP layer, queue |
| An AI reviewer is made to look like a person | The runner stamps `ai:<name>`; an AI never decides a human-only action, never satisfies a group, never counts towards several approvals | queue |
| One user reads or acts on another's actions | Every caller-facing read and write is scoped to the caller; someone else's id answers like a missing one. Sessions are owner-bound. Idempotency keys are per requester | queue, built-in tools |
| An approval is applied twice, or two approvers race | Every transition is a compare-and-set in a `BEGIN IMMEDIATE` transaction; the "n-th approval" decision is made inside it; the pending cap and budgets are counted in the insert transaction | SQLite store |
| A crash re-runs a side effect | Approved-but-unstarted actions run once; an interrupted action is retried automatically only if its tool is idempotent by key; otherwise it waits for a reviewer | executor |
| DNS rebinding, CSRF, cross-origin calls | Orca and Origin allow-lists; bearer tokens (no cookies); CSP with a nonce on the approval page; no framing | HTTP layer |
| Stolen or guessed credentials | API keys: 32 random bytes, stored only as SHA-256, constant-time comparison. JWT: audience, issuer, expiry, signature checked; the audience must be this resource and tokens are never forwarded | HTTP layer |
| Someone with database access edits the audit trail | Each audit entry stores the previous entry's hash and its own (schema 5); `--verify-audit` names the first entry changed, removed or reordered, and checkpoints from `--audit-checkpoint` kept elsewhere catch entries removed from the end. Entries from before schema 5 are reported as unverifiable | SQLite store, CLI |
| Open by mistake | The server refuses to start beyond loopback without sign-in, in plain http beyond loopback without `TrustProxy`, or when built without HTTP sign-in | startup checks |
| Flooding | Body size limit, per-subject rate limit, per-requester pending cap, bounded long polls | HTTP layer, queue |
| Leaks through notifications | Messages carry facts (tool, risk, requester, status, a link), never argument values or results; an opt-in preview is escaped and capped; review links need sign-in; webhooks are signed; no redirects, https only | notifications |
| Leaks through logs and audit | The audit trail never holds argument values; error text is scrubbed of secrets, and every secret Orca handed to a module is removed by exact value; configuration holds environment-variable *names*, not secrets; finished actions can be deleted after `RetentionDays` | audit, redaction |
| A module is enabled that nobody checked | Only packages the deployment references can be enabled; nothing is loaded from a folder. A configured module's Read tools are denied until named in the policy; its tools must carry its own prefix; a module built for another contract is refused | module loader |
| A module's suggested policy widens access | Adoption accepts only `Tools` entries for that module's own registered tools, refuses global keys and other modules' tools, refuses `Execute` on anything but Read without `--allow-execute`, and writes a separate fragment that may only hold that module's tools | policy adoption |
| A module or an injected URL reaches somewhere else, or a token goes to the wrong host | Module HTTP clients reach only the operator's `Network.Hosts` (which can narrow the manifest, never widen it), https unless an origin is listed, public addresses unless allowed, redirects re-checked; a credential is attached only for its bound host and dropped on a redirect away from it; each module sees only credentials mapped to it | guarded HTTP, credentials |
| Issue or ticket text written by strangers drives a write | Modules mark such reads `ReadsUntrusted` and such writes `ExternalEffect`, so taint holds the write for a reviewer even when a rule would run it | modules, queue |

## What it does not do

Be honest with yourself about these before relying on it.

1. **Reviewers are the real control.** A reviewer who approves without reading defeats everything above. Orca shows the real
   arguments, a preview of the effect and whether the session was tainted, and rules and budgets reduce how many decisions people must
   make, but nothing can make people careful.
2. **It is not a sandbox.** Tools run with Orca process's OS identity. A tool that can read your whole disk can. Use a dedicated
   account, least-privilege credentials (a read-only database login for the SQL module), and the modules' own roots and allow-lists.
3. **Best-effort guards are best-effort.** The SQL statement guard, error redaction, and regex-based rules reduce risk; the database
   login, the allow-lists and the review step are the controls.
4. **Taint is conservative, not omniscient.** It knows a caller read content through Orca tool. It cannot know what a user pasted into
   the chat, or what the model remembers from another system.
5. **An auto-execute rule is a promise you make.** Orca will run anything that matches it without a person. Review rules with
   `--explain` and `--replay-policy`, prefer anchored regexes for paths, and keep budgets small.
6. **One node.** State is a SQLite file. There is no clustering or failover, and no multi-tenant isolation: one deployment is one
   tenant.
7. **Notifications and sign-in providers are only as safe as their owners.** A leaked Slack webhook URL lets someone post fake
   messages (they cannot approve anything: links need sign-in); a compromised identity provider can mint tokens.
8. **Not independently audited.** The design was reviewed by a second model and tested adversarially by the authors; it has not had a
   third-party security review, penetration test, or a long soak under load.
9. **A module is code you run.** By default a module runs inside Orca process with its OS identity: the host allow-list, the scoped
   credentials and the prefix rules are then guard rails against mistakes and injected input, and the module's own code can open sockets,
   read files and ignore its manifest. `"Isolation": { "Mode": "Process" }` runs it in a worker process that inherits none of Orca's
   environment, never receives an HTTP credential (Orca carries out its HTTP) and is stopped when it hangs or misbehaves. That becomes
   containment only when the operator also runs the worker under a separate account and without network (`Isolation.Command`); see
   [MODULES.md](MODULES.md#running-a-module-in-its-own-process). Modules that open their own connections (IMAP, SQL, the Graph SDK) need
   network in the worker. Install modules from people you do not know only isolated that way.
10. **Taint holds high-impact tools only.** A `Write` tool with an `Execute` policy runs for a tainted caller. `--lint` points these out.

## Reporting and hardening checklist

* Run with a dedicated, unprivileged account. Put the SQLite file somewhere only that account can read; back it up.
* Terminate TLS in front (or give Kestrel a certificate) and set `PublicBaseUrl`; set `TrustProxy` only when nothing but the proxy can
  reach the port.
* Give each agent its own API key (`--new-api-key <name>`) with the narrowest agent profile that does its job; revoke by removing the
  hash from the config. Give reviewer tokens only the
  roles they need.
* Keep `TaintWindow` at its default or longer. Do not set it to `0` unless each conversation really is isolated from the others.
* Read [POLICY.md](POLICY.md) before writing an `Execute` rule. Run `--lint` after every policy or module change.
* Give each module the narrowest token its tools need (a fine-grained GitHub token for the listed repositories only), and list only the
  hosts it needs under `Network.Hosts`.
* Watch `opentail.host.pending_actions` and `time_to_decision` (OpenTelemetry). A growing queue means people stopped looking, which is
  exactly when an injected request gets approved.
