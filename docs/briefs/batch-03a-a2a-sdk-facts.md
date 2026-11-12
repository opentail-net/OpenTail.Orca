# Batch 03a: A2A SDK facts (read only; one file out, no repo code or package changes)

From the planning session (git-fa). Batch 02 is accepted and committed. Your decisions are confirmed: A3 fixed in the guide; the
`teams_refresh_failed` audit event stays; `waiting` uses the Reject check.

The planner will design the A2A thin slice (checklist C.0) from verified facts. Find them. Do NOT add packages to
`Directory.Packages.props` or any project in the repo. Work in a scratch folder outside the repo (for example under `%TEMP%`), with a
throwaway project that references `A2A` and `A2A.AspNetCore` version `1.0.0-preview4` (if that exact version does not restore, report
the versions that exist and use the nearest 1.0 preview; say which). Establish facts by compiling and running small probes and by
reading the package's XML docs / decompiled signatures and the a2a-dotnet repository source for that version. Mark every fact as
RUN (you executed it), SOURCE (read in the package or repo source, with file) or DOCS (documentation only).

Write `docs/briefs/a2a-sdk-facts.md`, at most 120 lines:

1. Packages: exact versions restored, target frameworks, licences, their transitive dependencies and whether any conflicts with a
   version pinned in this repo's `Directory.Packages.props` (compare, do not change).
2. The server seam: the exact interface a host implements to serve A2A without the SDK's own task store (`IA2ARequestHandler` or
   whatever it is really called): every member with its signature. What `MapA2A` (or its real name) takes and returns; whether its
   result accepts `RequireAuthorization(...)`; how the handler gets the `HttpContext`/caller identity, if at all.
3. `SendMessage`: the request and response types; where `returnImmediately` lives and its default; what the SDK itself does for
   blocking vs immediate (does the SDK wait, or does it leave that to the handler?); how `messageId`, `contextId`, `taskId`, parts
   (text, data, file), `metadata` and `extensions` appear on the types.
4. `GetTask`, `CancelTask`, `ListTasks`: request/response types; pagination fields; the error types and JSON-RPC codes for task not
   found, not cancelable, version not supported, unsupported operation.
5. `A2A-Version` header: RUN a minimal mapped endpoint with a trivial handler and record the actual HTTP status and JSON-RPC error
   for each of: header missing, empty, `0.3`, `1.0`, `2.0`. Is the check in the SDK or left to the host? Can the host override it?
6. `A2A-Extensions` header and `extensions`/`metadata` on a message: what the SDK parses, validates or passes through.
7. The agent card: the types; how the SDK serves `/.well-known/agent-card.json` and the extended card; whether serving can be
   switched off or put behind authorization; whether `skills` may be empty (RUN: serialise and serve one).
8. Content types accepted on the JSON-RPC endpoint (RUN: `application/json`, `application/a2a+json`, none).
9. The client: the class a test would use to call a mapped endpoint in-process (WebApplicationFactory or TestServer), with a
   10-line working example you actually ran (send a message with returnImmediately=true, then get the task).
10. Anything surprising, unstable or marked experimental/obsolete in the API surface you touched.

Rules: facts only, no design recommendations. If something cannot be established, say "not established" and why. Delete nothing in
the repo; leave the scratch project where it is and give its path. No commit or push. Report to `git-fa` in at most 15 lines when
the file is written.
