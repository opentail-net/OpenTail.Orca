# Skills: setup, publishing and operations

For whoever runs a Host with the skills module, and for the people who approve changes to skills. What the module does, and what it promises, is in [OpenTail.Modules.Skills/README.md](../../OpenTail.Modules.Skills/README.md). How to write a skill version is in [writing a skill version](writing-skill-versions.md).

Written from `opentail-host` at `dc957f7`, 2026-11-10. Commands use `my-host` as the deployment's name.

## Enable the module

1. **Reference the package** `OpenTail.Modules.Skills` in the deployment project. Referencing is the trust decision: only referenced code can be enabled.
2. **Add it to `modules.json`** with the id `skills`. The settings are in the [module reference](../../OpenTail.Modules.Skills/README.md#configuration).
3. **Look before you run:**
   `my-host --policy policy.json --modules modules.json --list-modules`
   It shows the module's tools, their risks, and what the policy does with each.
4. **Adopt the suggested policy:**
   `my-host --policy policy.json --modules modules.json --adopt-policy skills`
   It writes `policy.d/skills.json`. The options are in [OPERATIONS.md](../../OpenTail.Host/OPERATIONS.md).
5. **Put the approvers in the `skill-approvers` group.** The Host docs show how groups are set only for Teams reviewers (`Teams.Reviewers`, in [TEAMS-SETUP.md](../../OpenTail.Host/TEAMS-SETUP.md)). For the approval page, the docs do not say how groups are assigned. Confirm that before the first approval (see [open questions](#open-questions)).
6. **Give each caller a profile.** Profiles are defined under `Agents` in the policy file. An API key names its profile in the HTTP config. Read [agent profiles](../../OpenTail.Host/POLICY.md#agent-profiles) first. A sketch:

   ```json
   "Agents": {
     "invoice-assistant": { "Tools": [ "mail.read", "erp.query", "skills.find", "skills.get" ] },
     "skill-submitter":   { "Tools": [ "skills.find", "skills.get", "skills.publish" ] }
   }
   ```

   The agents that do the work get `skills.find` and `skills.get`. Only the people or tools that propose changes get `skills.publish`. `*` and `skills.*` never include `skills.publish`, so a profile must name it. The Host refuses to start if a profile names `skills.publish` next to any other tool above Read, unless the Host runs in sandbox or shadow mode.

## Put the first version live

The store has no versions at the start, and only an approved publish writes to it. So the first version goes through approval like any other.

1. **Prepare the content:** `skill.json` and `SKILL.md`, as the [authoring guide](writing-skill-versions.md) describes.
2. **Publish to staging first.** Submit `skills.publish` with `version` set to `1`, `expectedFrom` set to `none`, `label` set to `staging`, and the `skillJson`, `instructions` and `reason`. One approval is enough for staging.
3. **Check it on a staging Host.** A Host with `"Label": "staging"` serves it.
4. **Promote to production.** Submit `skills.publish` with the same `version`, `label` set to `production`, `expectedFrom` set to `none`, and no content. The version already exists, so only the label moves. Production needs two different approvers.

## Publish a new version

1. **Edit the skill** in `opentail-workbench`, under `skills/<id>/`. Give it a new version number. Never reuse a number, because a version never changes once published.
2. **Run the scenarios** with `workbench run --model <id> --skill <id>`. Then compare the batch with the one for the live version: `workbench compare --base <batch> --candidate <batch> --report <file>`. The comparison report carries the binding line for the candidate's content.
3. **Check the policies.** `workbench policies` shows, for each skill and policy, whether the skill was tested against the current text, was never recorded, was tested against text that has since CHANGED, or is missing. After the scenarios pass, `workbench policies --record` writes the current hashes into `skill.json`. That changes the file, so the result is a new version.
4. **Find the live version.** `skills.find` shows the version at the Host's label. That is the `expectedFrom` value. For the other label, read `labels.json` in the store.
5. **Prepare the arguments:**
   `workbench propose --skill <id> --reason "<what changed; what the runs showed>" --expected-from <version> [--label staging] [--report <file>]`
   It prints the `skills.publish` arguments as JSON. It submits nothing. It stops if the report describes other content, because the Host would refuse it as well. The workbench holds no Host credentials.
6. **Submit** the arguments with `skills.publish`, as a person whose key's profile names it. The change is queued.
7. **Review.** Read the reason first, then the diff, then the report check, which says whether the report is **Bound** or **Not bound**, then the policies that changed. Approve, reject, or request changes. Edits to the arguments are not possible. A request for changes means a new version number.

## Roll back

1. **Choose the version to return to.** It must still be in the store. Versions are kept forever.
2. **Submit** `skills.publish` with `version` set to that number, `expectedFrom` set to the current version, and a `reason` that says why. Send no content.
3. **Approve it** like any other publish. The change request says `(rollback)`, and the diff shows the lines that come back.

`--skill-outcomes` can suggest a rollback when a newer version is turned down noticeably more often than the one before it. It only suggests. A rollback is always a publish through approval.

## Check what is live and how it is doing

- **`skills.find`** with no argument lists every skill with its version at the Host's label. If a label points at a version whose files are missing, it is left out of the list and named in `warnings`.
- **`labels.json`** in the store shows both labels for every skill.
- **`--skill-outcomes [--since 30d] [--min-cases 20] [--worse-by 0.15]`** shows, for each version, how people decided the actions that followed it, with a 95% interval, and any rollback suggestion.
- **`--export-review-outcomes`** exports rejected and sent-back actions for the workbench. Redaction is best effort. Check each line before it leaves the deployment.
- **`--export-shadow-cases`** exports shadow runs. **It is not redacted.** Keep it inside the deployment.

## Staging and serve-only deployments

- **Staging.** A second Host with `"Label": "staging"` in its own `modules.json`. It serves the staging label.
- **Serve-only.** `"AllowPublish": false` leaves out `skills.publish`. Such a Host can read a store another Host writes. Only one Host may publish to a folder, so the serving Host should only read it.

## Policy drift

When `skills.get` returns a `policyWarnings` entry, the skill was tested against an older version of a policy. The agent is told to follow the current policy and to say that the skill may be out of date.

To clear the warning:
1. Run the scenarios against the new policy text.
2. Record the new hashes with `workbench policies --record`.
3. Publish a new version that carries them. Changing the hashes changes the skill's files, so it needs a new version number.

## Backups and recovery

- **Back up the store folder with the database.** The store is a folder on disk. It is not in the SQLite file, and the database backup in [OPERATIONS.md](../../OpenTail.Host/OPERATIONS.md#what-to-back-up-and-how-to-upgrade) does not cover it. Copy it when no approval is running.
- **Nothing in the store is deleted.** The module keeps every version. The folder only grows.
- **To restore**, copy the folder back. Then check `labels.json` and `skills.find` against the publishes you know were approved.

## When a publish is refused

A refusal is returned at once as `invalid_arguments`, and nothing is queued. A failure at approval is shown on the action. Messages are quoted from the code.

| Message (starts with) | When | Cause | Fix |
|---|---|---|---|
| `expectedFrom is required: the version the 'production' label points at now` | Request | `expectedFrom` is missing | Add it. Read the live version with `skills.find`. Use `none` if the label is empty. |
| `The 'production' label of <id> is at 3, not 2: someone published since.` | Request | The label moved after you read it | Read the live version again and propose again, with the new `expectedFrom`. Check the change still makes sense. |
| `The 'production' label of <id> moved since this was reviewed` | Approval (the action fails) | The label moved while the request waited | Nothing was published. Resubmit. This is what stops an old approval from rolling production back. |
| `<id> version 2 is already published with different content; use a new version number.` | Request | The instructions or `skill.json` changed under an existing number | Use a new version number. Versions never change. |
| `The report describes other content than <id> version 3 as proposed` | Request | The report's binding line names another version or another content hash | Run the compare for this version and attach that report. |
| `<id> has no published version 4; send skillJson and instructions to add it.` | Request | A rollback to a version that was never published | Send the content, or pick a version that exists. |
| `A new version needs both skillJson and instructions.` | Request | Only one of the two is present | Send both. |
| `Label must be one of production, staging.` | Request | Any other label | Use one of the two. |
| `A reason is required: the reviewer reads it first.` | Request | The reason is blank | Say what changed and what the runs showed. |
| `'<value>' is not a skill id.` or `'<value>' is not a version number.` | Request | Bad id or version | Ids are lower-case letters, digits and hyphens. A version is a whole number from 1 with no leading zero. |
| `[SKILL-…] …` | Request | A standard-form rule failed | Look up the code in the [authoring guide](writing-skill-versions.md#refusal-and-warning-codes). |
| `Not bound: the report names no content for this skill.` | Change request (shown, not refused) | The report has no binding line | The reviewer sees it as unchecked. Attach a comparison report to bind it. |
| `No published skill '<id>' (label production).` | `skills.get` | No version at the label | Check the label. Publish, or promote. |
| `The store lists <id>@<v> but its files are missing.` | `skills.get`; `skills.find` names it in `warnings` | `labels.json` points at a version folder that is gone | Restore the store folder from backup. |
| `The skill store is busy: another publish holds it, or its folder cannot be locked.` | Request | Another Host is publishing to the same store right now, or the folder cannot be locked | Wait a moment and resubmit. Nothing was published. |
| `skill.json must say "format": 1.` | Request, or a `warnings` entry from `skills.find` | The version's `skill.json` is in another format, or has none | Correct it in a new version. Stored versions cannot be edited: see [before upgrading](#before-upgrading). |
| The Host refuses to start, naming `ReviewerGroup`, `SelfApproval Forbid` or `AllowEdits false` | Startup | The policy for `skills.publish` is looser than the suggested one | Adopt the suggested policy: `--adopt-policy skills`. |
| The Host refuses to start, naming a profile with `skills.publish` | Startup | A profile names `skills.publish` next to another tool above Read | Split the profile. |
| `Config.Store is required.` | Startup | No `Store` in the config | Add it. |
| `Config.Label must be one of production, staging.` | Startup | Any other label | Correct it. |
| `Config.PolicyFolder <path> does not exist.` | Startup | The path is wrong. Relative paths are relative to `modules.json` | Correct the path, or remove the setting. |

## Before upgrading

Every stored version must say `"format": 1`. A stored version that does not is refused when it is read: `skills.find` names it in `warnings`, and `skills.get` refuses it. Check the store before upgrading:

```
grep -L '"format": 1' <store>/*/*/skill.json
```

Each file the command lists would stop being served. Republish that content as a new version with `"format": 1` and move the label to it. Do not edit a stored version: versions never change, and the Host does not rewrite them.

## Open questions

- **Approver groups on the approval page.** The Host docs show groups for Teams reviewers only. Confirm how the approval page assigns `skill-approvers`.
- **Network shares.** A publish holds the store's lock file, and a second publishing Host is refused while it is held. File locks may not be enforced on a network share, so keep the store on a local disk, or run one publishing Host per store there. Limit 1 in the [module reference](../../OpenTail.Modules.Skills/README.md#known-limits-and-gaps) explains it.
- **Standard-form decisions 3, 4 and 7**, which are provisional until the owner reviews them ([standard skill files](../design/standard-skill-files.md)).
