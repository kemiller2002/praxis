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

Canonical artifacts must remain usable independently of any specific model,
agent vendor, or chat history.

## Work-system boundary

The external project-management store owns work items, priorities, assignments, and portfolio state. ROS owns protocol versions, legal semantic transitions, validation, repository integration, and the provider-neutral adapter contract. Each repository owns implementation and evidence. Local context and events preserve attributable intent without becoming a project-management datastore. See `docs/work-protocol.md` and `DF-ROS-2026-A006`.
