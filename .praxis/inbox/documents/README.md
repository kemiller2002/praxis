# Praxis input documents

Drop unprocessed source material here. A local Praxis runtime is not required to submit an input.

Agents MUST treat files here as source material, not canonical requirements. Preserve source provenance, derive the appropriate requirements/decisions/evidence/open questions, and leave the source unprocessed until those derived changes have been durably reconciled.

When working a tracked item, use a branch whose name exactly equals the work-item ID.

With a Praxis runtime, use `praxis inbox claim|derive|complete|release|reject|recover` (see docs/fallback-reconciliation.md, "Input documents"). Without one, a claimed input is visible under `.praxis/processing/<claim-id>/claim.json`; do not process an input someone else has claimed there.
