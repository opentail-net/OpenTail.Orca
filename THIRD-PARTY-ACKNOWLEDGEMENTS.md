# Third-party acknowledgements

This file credits the open-source projects whose published designs informed the OpenTail Host's design documents. It is kept current
whenever a design document cites a third-party project.

## What is credited here

**Ideas only.** The projects below informed design documents in `docs/design/`. No code, configuration or documentation text from them is
copied into this repository. Where a design document describes an idea from one of them, the description is our own paraphrase, with a
citation to the project's file that states the idea.

If any code, configuration or text is copied later, the copy must be recorded here before it is merged. Record the source, the licence,
the copyright notice and any NOTICE file. The MIT licence requires its copyright and permission notice to be kept with the copy. The
Apache-2.0 licence requires its licence text to be kept, and any NOTICE file to be kept too.

## Projects

### Microsoft agent-governance-toolkit

- Source: https://github.com/microsoft/agent-governance-toolkit
- Licence: MIT (`LICENSE`); the project also ships a `NOTICE` file, which lists its own third-party components.
- Copyright, as stated in its licence: Copyright (c) Microsoft Corporation.
- Used for: `docs/design/examples-review.md` and `docs/design/reviewer-gate.md`. The action-bound approval protocol (ADR-0030), its
  execution-time revalidation, its position on LLM output, and the MCP security gateway specification (evaluation order, response scanning,
  tool fingerprints).

### Signet

- Source: https://github.com/bee-san/Signet
- Licence: MIT (`LICENSE`).
- Copyright, as stated in its licence: Copyright (c) 2026 Signet contributors.
- Used for: `docs/design/examples-review.md`. The rule that an unknown outcome is never retried blindly, the approver that cannot change
  policy, and the step-up proof for approval.

### actionproxy

- Source: https://github.com/ActionProxy/actionproxy
- Licence: Apache License, Version 2.0 (`LICENSE`).
- Copyright holder: not stated in the `LICENSE` file or the README checked.
- Used for: `docs/design/examples-review.md`. The separate, short-lived execution authority, the approval bound to a nonce and hash, the
  separation of the SLA reminder from the authorisation deadline, source-based taint, and stable reason codes.

### PortcullisMCP

- Source: https://github.com/paclabsnet/PortcullisMCP
- Licence: Apache License, Version 2.0 (`LICENSE`).
- Copyright holder: not stated in the `LICENSE` file or the README checked.
- Used for: `docs/design/examples-review.md`. The signed, narrow, expiring escalation token, and the naming of agent self-approval as a
  threat.

### cordon

- Source: https://github.com/marras0914/cordon
- Licence: MIT (`LICENSE`).
- Copyright, as stated in its licence: Copyright (c) 2026 Cordon Contributors.
- Used for: `docs/design/examples-review.md`. Call-graph (sequence) rules, and a timed-out approval that stays replayable.

### Darvoza

- Source: https://github.com/anorboev/darvoza
- Licence: MIT (`LICENSE`).
- Copyright, as stated in its licence: Copyright (c) 2026 Mansur Anorboev.
- Used for: `docs/design/examples-review.md`, as a hardening backlog: audit at the outermost layer, secrets kept out of arguments, and the
  policy file as a trust boundary.

### mcp-gateway

- Source: https://github.com/nick-pape/mcp-gateway
- Licence: MIT (`LICENSE`).
- Copyright, as stated in its licence: Copyright (c) 2026 Nick Pape.
- Used for: `docs/design/examples-review.md`, as a warning against an approver that approves everything by default, and as a comparison
  point for durable queues.

### Rag.NET

- Source: https://github.com/MarcelRoozekrans/Rag.NET
- Licence: MIT (`LICENSE`).
- Copyright, as stated in its licence: Copyright (c) 2026 Marcel Roozekrans.
- Used for: consulted only, in `docs/design/examples-review.md`. No idea from it was adopted.
