# Native work-history reconciliation

During an ordinary Git merge, independent native work records can conflict at
the end of the shared JSON array and append-only event log. Use an **exclusive,
idle checkout** with no other Praxis writers:

```sh
./praxis work history merge
./praxis work history merge --apply
git add .ros/context/current.json .ros/events/events.jsonl
./praxis registry build
./praxis validate
```

Preview changes no history files. History comes only from actual HEAD, incoming
commit and one to 32 real merge-base commits. Conflicted index stages 2 and 3
must match their real branch commits; stage 1 must match the real ancestor when
there is exactly one. A synthetic stage-1 history is never parsed or used as
native data. Either native path may already have auto-merged: its working bytes
must equal the staged version (allowing Git's CRLF/LF checkout conversion) or
the exact generated native write, and its
data must match the authoritative commit-derived plan. At least one path must
still have complete regular-file conflict stages. A modified index cannot
substitute invented history. The planner preserves both
independent work-item records, unknown fields, execution/checkpoint provenance
and original event lines. Shared events occur once. Base events cannot be
removed, rewritten or reordered, but may have independent events inserted
between them by a prior branch integration. Shared events anchor the merge:
both branches must agree on their relative order. Independent events between
anchors retain each branch's order, with the local branch first where neither
branch establishes a cross-branch order. This tie is deterministic bookkeeping,
not a claim about chronological or causal order. Context `updatedAt` is the later
existing UTC observation; lifecycle states are never chosen by timestamp.

Divergent updates to the same work-item/event identity, contradictory order of
shared events, duplicate/missing IDs,
item deletion, changed repository identity, unsupported context versions,
oversized blobs, linked paths and incomplete conflict stages refuse without
writing history. Other source conflicts still need ordinary review. The tool
does not stage, commit, fetch, push, approve completion or merge the PR.

Opposite previously serialized orders can form a cycle even when every event
is present and unchanged. This tool still refuses that case. Adopting a new
canonical order needs an explicit, recorded owner decision and a separately
reviewed native reconciliation path; choosing a branch or editing event lines
by hand is not an approved repair.

Apply reobserves HEAD, MERGE_HEAD, exact index stages and target contents, then
uses the existing two-file native recovery journal. Retrying `--apply` resumes
an interrupted journal only when its rendered write set matches this exact
merge; an unrelated journal refuses. Keep the index stages until apply/recovery
finishes, then review and stage the result. Rebuild generated registries from
canonical source records rather than choosing a conflicted registry branch.

This command reconciles already-recorded branch histories; it creates no work
transition or fabricated historical event. Native protocol commands remain
the authority for subsequent state changes. The command's cooperative lock
serializes other history-merge invocations, not every native lifecycle writer.
The exclusive-checkout requirement is necessary while WI-0087 remains open;
this is not a concurrent live-controller state merger.
