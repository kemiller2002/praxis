---
id: DF-ROS-2026-A002
title: Artifact identity and acceptance boundary
status: accepted
version: 1.0.0
created: 2026-07-24
updated: 2026-07-24
author_agent: codex
supersedes: []
superseded_by: []
related_documents: [DF-ROS-2026-A001]
tags: [architecture, identity, lifecycle]
---

# Context

Sequential identifiers collide on parallel branches, and the scaffold does not
define when records become immutable.

# Decision

New IDs use a readable prefix, area, year, and random hexadecimal token.
Legacy four-digit sequences remain valid. Content becomes immutable at
`accepted`; REP `canonical` is a compatibility alias. Substantive correction
uses reciprocal supersession.

# Alternatives

- Central sequence allocator: rejected because Git branches lack a reliable
  shared lock.
- Timestamps alone: rejected because concurrency and readability remain weak.
- Immutable drafts: rejected because it creates needless replacement records.

# Consequences

Validation detects collisions and broken relationships. Automatic detection of
accepted-content mutation requires a trusted Git baseline and is deferred from
the initial vertical slice.

# Reversibility and validation

The token format can expand later while retaining existing IDs. Fixtures test
duplicates, broken references, and valid legacy and token IDs.
