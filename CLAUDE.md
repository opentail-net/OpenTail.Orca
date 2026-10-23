# Working rules for agents

Plans and designs live in `docs/design/` (index in `docs/design/README.md`); setup guides in `docs/guides/`; the cross-repo checklist is `docs/plan.md` in `opentail-connectivity`.

## Writing a plan or design

1. **Read the contracts first.** Before writing, open the interfaces and the existing implementations the plan touches, and list
   them in the plan under "Contracts this must satisfy" (what each method promises, what each caller relies on). A plan that
   proposes a new backend, store or provider must show how it meets every member of the interface it implements.
2. **Map existing behaviour completely.** When a plan reproduces something the code already does (a filter, a guard, a policy),
   go through the code branch by branch and give each branch a row in the plan. Never summarise from memory.
3. **Verify design-changing vendor facts before relying on them:** consistency model (are writes and deletes visible at once?),
   filter and query support, limits, licences, which SDK and API version. Check them in the vendor's documentation or source. Mark
   only minor details "to verify".
4. **One plan per system.** Do not cover several unrelated systems in one pass.
5. **Review before handing over.** Re-read the finished plan against the code for contradictions, missed conditions and claims
   about existing code that were not checked. State what is built and what is not consistently throughout.
6. **Claims about existing code** ("X has this bug") only after reading X.

These rules come from a plan (`opentail-connectivity/docs/design/redis-pinecone-mongodb.md`, 2026-10-23) that needed two external
review rounds for basics: an interface not read, half of an existing filter mapped, a vendor's asynchronous writes not checked.
