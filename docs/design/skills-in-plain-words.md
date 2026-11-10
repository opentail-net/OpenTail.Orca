# Skills in plain words

What the idea is, what is built, what is only planned, and the order of work. Written 2026-11-10.

## The short version

A company writes its business procedures as "skills". An AI agent reads the right one before it acts. OpenTail makes sure the agent only does allowed things, that every change to a procedure is approved, and that you can prove which version was used. The Workbench is where procedures are tested before they go live. The plans are about making that solid and portable.

## What a skill is

A skill is a folder with a plain-language instruction file (`SKILL.md`) and sometimes reference files. For example: "how to check an unpaid invoice before anyone pays it". The agent reads the relevant one when a situation matches. Agent Skills is an open standard for this folder format, so the same skill should work with different AI tools.

## What OpenTail adds

- **The Host** is the gatekeeper. It holds approved skills, has people approve each new version, keeps old versions, can roll back, and records which skill version was used for each action. Skills guide the agent but never grant new powers; policy still decides what the agent can do.
- **The Workbench** is the test bench. You run the agent against realistic email scenarios and check that it did the right thing and nothing forbidden. You can compare two versions. Some scenarios are kept from tuning, so they catch overfitting.

## What is built

- The standard skill format in the Host. It is merged into `main` up to commit `6f1a59b`. The newest Host work is on `standard-skill-files` and is not in `main` yet.
- In the Workbench, the agent can load skills on demand instead of getting all of them up front. There is a no-skill baseline, so you can see whether a skill helps. There are near-miss tests, where the agent should not act.

## What is only planned

- **A standalone library** that reads and writes skill folders without OpenTail, so other apps could use them. It is blocked on a licence question: the Host code is restrictively licensed.
- **Hash improvements.** A hash is a fingerprint of a skill's text. The plan makes it stricter, so indentation counts, and keeps the old fingerprints valid so existing evidence still checks out.
- **Store fixes.** One real bug: a damaged label file can make the Host overwrite good data when it publishes. That is the first thing to fix.
- **Measuring outcomes.** Counting how often people approve or reject actions after a skill version is served, to spot a bad version. The hard part is crediting each decision to the right version.
- **Reference files** that a skill can pull in only when it needs them.

## The order of work

1. Fix the label-overwrite bug.
2. Lock in the current hash values with tests.
3. Make the Workbench score whether the agent loads the right skill.
4. Check whether there are enough real decisions to measure anything.
5. Only then decide on the library and reference files.

---

The detail is in the Host repository, branch `skills-plan`, folder `docs/design`. Start with `skills-plan.md`. Each plan lists its open decisions.
