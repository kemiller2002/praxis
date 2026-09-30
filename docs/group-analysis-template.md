# Group analysis: GROUP-ID

<!--
Template for PRX-GRP-040 and PRX-GRP-045 (requirements/PLANNING-WORK-GROUPS.md).
Write sections 1-4 and commit them before changing any code for the grouped
execution. Complete section 5 before completing any member. Delete these
comments. Every claim cites a file and line, a command and its output, or a
work item.
-->

- Group: `GROUP-ID` (`./praxis plan explain-group GROUP-ID` or `./praxis work group show GROUP-ID`)
- Members: `ITEM-1`, `ITEM-2`, ...
- Execution repository: ...
- Base commit: ...

## 1. Members

One row per member, taken from its own description (`./praxis work show ID`).
Acceptance criteria are listed here and verified per member in section 5.

| Member | Obligation | Acceptance criteria | Depends on |
|---|---|---|---|
| ITEM-1 | ... | 1. ... 2. ... | - |

## 2. Reuse inventory

Search the codebase for what the members touch before designing anything. Do
not rely on memory. List every existing parser, domain rule, type, store,
command pipeline, rendering convention and test helper that is relevant.

| Existing element | Location | What it does | Reuse, extend, or not reused (why) |
|---|---|---|---|
| ... | `path:line` | ... | ... |

For every new abstraction proposed in section 3, name the existing one you
considered and why it does not fit. "None exists" is a claim, so state the
search that established it (for example `grep -rn "parseX" src`).

## 3. Group-level design

- Common architecture and shared invariants:
- Conflicting requirements, and how each is resolved:
- The one design that serves several members (store, validation rule, output contract, error style):
- Compatibility constraints and migration implications:
- Common tests:
- Risks of solving each member independently:
- Shared group infrastructure, and the member it is attributed to or its own work item (PRX-GRP-043):

## 4. Order

Implementation order and why (dependencies, what each member unlocks). Name
the milestones at which a group checkpoint is recorded (PRX-GRP-044).

## 5. Verification pass (before completing any member)

Exercise each criterion. Do not infer a criterion from the shared design.
Status is one of `met`, `partially met`, `not met` or `unknown`. A member with
any criterion not met is not completed: it stays active, it is blocked, or its
gap is captured as its own work item.

| Member | Criterion | Status | Evidence (test, command and result, or `path:line`) |
|---|---|---|---|
| ITEM-1 | 1. ... | met | `dotnet ... ` -> ... |
