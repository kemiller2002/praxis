# Registry maintenance and organizational transparency obligations

Status: **proposed** (not approved)
Work item: WI-0116 (2026-11-26; first proposed as WI-0110 in closed PR #305 and renumbered after an ID collision with a different WI-0110 on main). Proposed by echelon-registry work item GOV-TRN-001
(2026-11-25). Approval is required before implementation.
Related: echelon-registry `spec/repository-manifest.md` (`echelon.repository/v1`),
`portfolio/governance/`; organization policies POL-ORG-TRN-001 (transparency), STD-ORG-CLS-001
(classification), STD-ORG-PUB-001 (disclosure); this repository's
`requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md` requirements 17–20.

## Objective

Keep the Echelon Registry's record of what exists, and what Echelon is pursuing, current as a side
effect of normal repository work, without making any repository depend on the Registry being
available, and without asking agents or people to maintain the same fact in several places.

## Boundaries

- Praxis owns the **obligation** (when an agent must evaluate a registry update) and the **local
  checks**. The Registry owns the records and the central validator (`tools/portfolio`). The Knowledge
  Store owns representation and retrieval. Organization administration owns policy text.
- Nothing in these requirements adds a run-time dependency. Requirement 18 of
  PRAXIS-DUAL-ENTRY-RECONCILIATION (Praxis fully operational without the Registry) takes precedence.

## Requirements

### Consult before proposing

- **RM-01** Before proposing a new repository, system, product or major initiative, an agent MUST consult
  the Registry portfolio (`portfolio/repositories.json`, `portfolio/initiatives.json`,
  `portfolio/analysis/responsibility-and-capability-matrix.md`) or a Knowledge Store projection of it, and
  record in the proposal which existing systems were considered and why each does or does not fit.
  When the Registry is unreachable the agent MUST say so in the proposal; unreachability MUST NOT
  block the work.
- **RM-02** A proposal for a new system MUST state the capability it would provide, the existing owner
  of that capability per the capability ownership matrix (or "unowned"), and the non-responsibilities
  of the new system.

### Evaluate registry impact on these triggers

- **RM-03** When a change does any of the following, the agent MUST evaluate whether a registry update
  is required and record the outcome (`required`, `not-required` with a one-line reason, or
  `deferred` with a work item) in the completion evidence:
  1. creates a repository;
  2. renames a repository;
  3. introduces a system;
  4. changes a system's responsibilities or non-responsibilities;
  5. adds a capability;
  6. removes a capability;
  7. changes a dependency between Echelon systems;
  8. introduces a strategic initiative;
  9. completes an initiative;
  10. abandons an initiative;
  11. changes a product's direction;
  12. publishes a major release;
  13. makes a material architectural decision (a `DF-` record);
  14. changes information classification of a record or repository;
  15. identifies duplicated functionality.
- **RM-04** Triggers 2–7, 11 and 15 also require an update to the repository's own
  `.echelon/repository.json` in the same change when the repository has adopted it.
- **RM-05** Trigger 12 (release) MUST be accompanied by the Registry release record for that release in
  the same release workflow, or by a work item in the Registry when the release workflow cannot write
  to it. (Addresses drift DRF-02.)

### Single source for each fact

- **RM-06** Facts listed as *locally authoritative* in `spec/repository-manifest.md` §2 MUST be written
  only in `.echelon/repository.json`. Central portfolio entries for adopting repositories are generated
  from it; agents MUST NOT hand-edit generated central entries.
- **RM-07** Capability claims MUST appear only in `echelon.system/v2` `provides`; a repository manifest,
  README or charter MUST NOT be cited as evidence that a capability is provided.
- **RM-08** `praxis upgrade` SHOULD remove or rewrite the legacy `rosVersion` field when it disagrees with
  `.echelon/ros.json` `installedVersion`, and `praxis validate` SHOULD report the disagreement.
  (Addresses DRF-01.)
- **RM-09** Unfilled installation templates (charter, handoff, current state) MUST be distinguishable
  from authored content by machine, for example a front-matter `template: true` marker that Praxis
  removes when the file is first authored. Agents MUST treat template content as "not documented".
  (Addresses DRF-05.)

### Classification and disclosure

- **RM-10** New structured records (registry entries, initiative records, evidence records, due
  diligence answers) MUST carry `classification` (STD-ORG-CLS-001).
- **RM-11** In a repository whose visibility is public, `praxis validate` SHOULD fail when a record
  declares `classification` other than `public`, and MUST NOT print the record's content in the
  diagnostic.
- **RM-12** Agents MUST NOT name a private repository in a public repository unless a disclosure
  decision for that identity exists (STD-ORG-PUB-001 PUB-SG-11).
- **RM-13** Agents MUST NOT write credentials, personal data or unremediated vulnerability details into
  any repository, issue, pull request, commit message or CI log. If an agent discovers such material in
  a public surface it MUST stop, report it to the owner through a non-public channel, and record only
  an opaque reference (STD-ORG-PUB-001 §7).
- **RM-14** Agents MUST NOT change repository visibility.

### Availability and enforcement

- **RM-15** The **local** check (the repository's own manifest is valid against
  `echelon.repository/v1`, and RM-03 evidence is present when a trigger applies) MAY be blocking. Any
  check that needs the Registry or the network MUST be advisory and MUST report `unavailable` rather
  than failure when the Registry cannot be reached.
- **RM-16** Central reconciliation (manifest ↔ central entry, systems ↔ repositories, stale entries) runs
  in the Registry (`tools/portfolio validate`), not in each repository's validation.
- **RM-17** Praxis SHOULD offer `praxis registry-impact` (name provisional) that lists which RM-03
  triggers a change appears to hit, from the changed paths and records, as an aid; the agent's
  evaluation remains the evidence.

## Acceptance criteria

1. A change that adds a `DF-` decision record and no RM-03 evaluation fails validation in a repository
   that opted in; the same change with `not-required` and a reason passes.
2. With the network disabled, `praxis validate` in an opted-in repository completes and reports the
   central reconciliation as `unavailable`.
3. A public repository containing a record with `classification: internal` fails RM-11 without
   printing the record's text.
4. `praxis validate` reports a `rosVersion`/`installedVersion` disagreement (RM-08).

## Rollout

Opt-in per repository (`ros.json` `workProtocol.registryMaintenance: "advisory" | "enforced"`), advisory by
default, pilot in echelon-registry, praxis and one product repository. Measure: stale registry entries
(metric M-08) and time to onboard an agent (M-19) before and after.

## Known conflict

Requirement 1 of PRAXIS-DUAL-ENTRY-RECONCILIATION (work item ID equals branch name) cannot be met by
cross-repository sessions whose branch names are assigned by the hosting platform. This proposal was
authored on such a branch; reconciliation is required when a work item is assigned.
