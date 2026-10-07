# ROS Architecture

The repository separates:

- **Framework:** stable operating instructions
- **Missions:** bounded work assignments
- **Context:** compact current understanding
- **Research:** canonical scientific records
- **Registries:** machine-readable indexes
- **Templates:** reusable artifact structures
- **Input documents:** unprocessed incoming material
- **Generated:** derived outputs
- **Tools:** deterministic automation and validation
- **Archive:** deprecated or superseded noncanonical material
- **Telemetry:** versioned normalized metric definitions plus segmented, provider-neutral execution records

Canonical artifacts must remain usable independently of any specific model,
agent vendor, or chat history.

## Execution-observability boundary

ROS owns execution identity, capability state, normalization, provenance, Git/clock derivation, validation, and the raw-field preservation boundary. Provider adapters translate runtime output at the edge; they do not define core semantics. Runtime providers own the truth of their usage streams. Work items remain external-system truth, while multiple execution records link to one item. Central publication, access control, retention, and cross-repository reconciliation remain outside this local capability.

## Work-system boundary

The external project-management store owns work items, priorities, assignments, and portfolio state. ROS owns protocol versions, legal semantic transitions, validation, repository integration, and the provider-neutral adapter contract. Each repository owns implementation and evidence. Local context and events preserve attributable intent without becoming a project-management datastore. See `docs/work-protocol.md` and `DF-ROS-2026-A006`.
