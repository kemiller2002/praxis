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

Preview changes no history files. Apply reads only Git's regular-file stages
1, 2 and 3 for these two exact paths in a pending merge, requiring each stage
to match the actual single merge base, HEAD and incoming commit. A modified
index cannot substitute an invented history. It preserves both
independent work-item records, unknown fields, execution/checkpoint provenance
and original event lines. Shared events occur once. Base events cannot be
removed, rewritten or reordered, including across successive merges. A
deterministic topological merge preserves each branch's relative event order;
contradictory ordering cycles refuse. Context `updatedAt` is the later
existing UTC observation; lifecycle states are never chosen by timestamp.

Divergent updates to the same work-item/event identity, duplicate/missing IDs,
item deletion, changed repository identity, unsupported context versions,
oversized blobs, linked paths and incomplete conflict stages refuse without
writing history. Other source conflicts still need ordinary review. The tool
does not stage, commit, fetch, push, approve completion or merge the PR.

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
