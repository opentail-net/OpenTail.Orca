# Skills in plain words

What the idea is, what is built, what is only planned, and the order of work. Written 2026-11-10.

## The short version

A company writes its business procedures as "skills". An AI agent reads the right one before it acts. OpenTail governs the actions an agent performs through its own tools, requires each change to a procedure to go through the configured approval process, and records which version was served to the agent. The Workbench is where procedures are tested before they go live. The plans are about making that solid and portable.

## What a skill is

A skill is a folder with a plain-language instruction file (`SKILL.md`) and sometimes reference files. For example: "how to check an unpaid invoice before anyone pays it". The agent reads the relevant one when a situation matches. Agent Skills is an open standard for this folder format, so the same skill should work with different AI tools.

## What OpenTail adds

- **Orca** is the gatekeeper. It holds approved skills, sends each new version through the configured approval process, keeps old versions, can roll back, and records each time a skill version is served to an agent. It does not record which skill shaped a given action; that is the hard part of "Measuring outcomes" below. Skills guide the agent but never grant new powers; policy still decides what the agent can do.
- **The Workbench** is the test bench. You run the agent against realistic email scenarios and check that it did the right thing and nothing forbidden. You can compare two versions. Some scenarios are kept from tuning, so they catch overfitting.

## What is built

- The standard skill format in Orca. It is merged into `main` up to commit `6f1a59b`. The newest Orca work is on `standard-skill-files` and is not in `main` yet.
- In the Workbench, the agent can load skills on demand instead of getting all of them up front. There is a no-skill baseline, so a skill can be compared with no skill. There are near-miss tests, where the agent should not act. This is committed on the Workbench branch `progressive-disclosure`, not merged into its `main`. No run against a real model has been made yet, so there is no evidence so far that a skill helps.

## What is only planned

- **A standalone library** that reads and writes skill folders without OpenTail, so other apps could use them. For now it uses Orca's own licence, the proprietary commercial one (PolyForm Strict until 2026-11-13), so the owner can reuse it in other projects. A more permissive licence can come later.
- **Hash improvements.** A hash is a fingerprint of a skill's text. The plan makes it stricter, so indentation counts, and keeps the old fingerprints valid so existing evidence still checks out.
- **Store fixes.** One data-loss risk, found by reading the code and not yet reproduced by a test: a damaged label file can make Orca overwrite good data when it publishes. That is the first thing to fix. The rest: one damaged skill must not hide the others, a failed publish must leave no debris, and the backup must be one tested procedure.
- **Measuring outcomes.** Counting how often people approve or reject actions after a skill version is served, to spot a bad version. The hard part is crediting each decision to the right version.
- **Reference files** that a skill can pull in only when it needs them.

## The order of work

1. Fix the label-overwrite bug.
2. Lock in the current hash values with tests.
3. Make the Workbench score whether the agent loads the right skill.
4. Check whether there are enough real decisions to measure anything.
5. Only then decide on the outcome measurement, the library and reference files.

None of the five steps has started. Earlier work (the catalog delivery, the no-skill baseline and the near-miss scenarios) is built and committed on a Workbench branch, but it is not one of these steps. Each waits for the owner's decisions, except step 2, which needs none. Runs against a real model also wait for the owner (a model, a budget and a key).

---

The detail is in Orca repository, branch `skills-plan`, folder `docs/design`. Start with `skills-plan.md`. Each plan lists its open decisions.
