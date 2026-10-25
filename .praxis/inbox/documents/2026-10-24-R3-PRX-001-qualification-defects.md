# R3-PRX-001: Defects observed while qualifying Praxis 3.20.0 (PRX-D1..D4)

Priority: P1 (D1), P2 (D2–D4)
Owner system: Praxis
Target repositories: praxis
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Evidence: kemiller2002/indy-init-rehearsal-003 `research/rehearsal-003-engineering-hardening.md` §7 (OBSERVED in an
isolated scratch repository with a local bare origin). That document says these requirements were "drafted, not
committed, because this session has no Praxis write scope". This intake records them.

## Defects and requirements (proposed)

- **PRX-D1 (P1).**
  - **Defect.** An `--occurred-at` value truncated to whole seconds can predate the execution's millisecond start.
    From then on `validate` fails permanently. Present in 3.16.0 and 3.20.0. Workaround: millisecond timestamps.
  - **Requirement.** Praxis refuses an `--occurred-at` earlier than the execution start at the moment of recording,
    with a structured refusal, instead of storing it. Alternatively it compares at the precision supplied. Existing
    records that already fail `validate` this way get a documented reconcile path.
- **PRX-D2 (P2).**
  - **Defect.** The `architecture check` success message ignores which flags are enabled.
  - **Requirement.** The success output lists exactly the checks that ran. A check that is not enabled is reported as
    not run, never implied as passed.
- **PRX-D3 (P1).**
  - **Defect.** `work finish` verifies and attests, but leaves the work item `active`, so a separate legacy
    completion is still needed.
  - **Requirement.** A verified `finish` completes the item, or the documentation and exit status state plainly that
    it does not. Replay remains idempotent.
- **PRX-D4 (P2).**
  - **Defect.** Ingested usage rows have no timestamps, so dated prices cannot be applied.
  - **Requirement.** Ingested usage rows carry the provider event timestamp. When the source has none, they are marked
    unavailable (never zero).

## Acceptance criteria

- **D1:** a regression test where `--occurred-at` precedes the start by under one second is refused at record time,
  and `validate` stays green.
- **D2:** a test with one flag enabled prints only that check.
- **D3:** after `finish` exits 0, `work show` reports the item completed. A second `finish` replays with exit 0 and no
  duplicate record.
- **D4:** a fixture transcript with timestamps produces dated rows that are priced by the dated price table. Without
  timestamps the rows are marked unavailable.

## Relevance

Rehearsals pin Praxis 3.16.0 (D1 applies). The OD-9 verified-completion pilot depends on `finish` (D3).
