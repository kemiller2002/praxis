# Praxis shared application foundation requirements

Status: **Required**

These requirements apply to Praxis/ROS runtime and UI surfaces when the named capability is applicable.

## Aegis

1. Praxis's native .NET/F# CLI, infrastructure, repository, filesystem, process, package, network, Git, and other external operational boundaries MUST use Aegis for unexpected operational failure.
2. Expected Praxis outcomes such as invalid work transitions, verification failures, stale installation state, user-owned-file conflicts, unsupported commands, and policy refusals MUST remain typed Praxis/Ordo outcomes and MUST NOT be converted into Aegis faults.
3. The Node compatibility/bootstrap/server layers MUST NOT invent a competing durable fault taxonomy. Until a native Aegis adapter exists for those JavaScript-only edges, they MUST return explicit non-success diagnostics and preserve enough context for the native boundary to classify the fault without exposing secrets.
4. Retry/recovery MUST respect idempotency and unknown-effect state, especially around repository mutation, package installation, Git operations, and agent/provider invocation.
5. Aegis context MUST redact credentials, tokens, private repository data, and sensitive work content.
6. Native Aegis dependencies MUST be pinned to released versions and tested with replaceable sinks.

## Forma

1. Praxis web and hub interfaces MUST consume a pinned Forma release.
2. Existing Forma patterns/components/tokens MUST be used before Praxis-local presentation equivalents.
3. Forma CSS or component markup MUST NOT be copied/forked into Praxis merely for convenience.
4. Native HTML remains the semantic authority; Forma owns presentation; Praxis owns work state, legal transitions, permissions, evidence, and repository meaning.
5. Mobile, keyboard, focus, accessibility, non-color-state, and responsive behavior inherit Forma's shared contracts.
6. Operational-fault presentation in interactive UI SHOULD use Forma's standard fault/error presentation components.

## Folio

1. Folio is conditional until Praxis exposes printable/PDF/paginated work reports, execution summaries, telemetry reports, evidence packets, audit reports, or similar document surfaces.
2. Once such a surface exists it MUST consume a pinned Folio release and use existing Folio primitives before local print implementations.
3. Forma remains responsible for interactive controls around document preview; Folio owns printable document intent; Praxis owns the document's work/evidence meaning.

## Dependency discipline and completion

Shared dependencies MUST use released versions or immutable artifacts. A missing shared capability MUST be recorded in the owning shared repository rather than silently reimplemented in Praxis.

A feature using an applicable shared capability is complete only when evidence shows the dependency is actually used and the relevant Aegis boundary, Forma browser/accessibility, or Folio print/PDF behavior is tested.
