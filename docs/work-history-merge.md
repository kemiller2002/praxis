# Native work-history reconciliation

During an ordinary Git merge, independent native work records can conflict at
the end of the shared JSON array and append-only event log. Use an **exclusive,
idle checkout** with no other Praxis writers:

```sh
./praxis work history merge
./praxis work history merge --apply
git add .ros/context/current.json .ros/events/events.jsonl
git add .ros/work/queue.json .ros/work/queue.md   # when the queue participated
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

## Backlog queue

Two branches that each `praxis add` a backlog row conflict in
`.ros/work/queue.json` and its generated `.ros/work/queue.md`. Never resolve
that by taking one side's queue: that silently drops the other branch's row.
The same command merges the queue whenever it is unmerged, in one of two
shapes (anything else, including a missing stage or a link, refuses):

- `queue.json` has stages 1, 2 and 3, bound to the same base, HEAD and
  MERGE_HEAD commits as the history stages. Rows are keyed by `id` and merged
  three ways: a row only one side changed takes that change; an identical
  addition on both sides is kept once; a row both sides changed differently
  refuses and names the ID. `schemaVersion` and `repository` must agree, and
  `nextSeq` is the largest of the three, never lowered. Backlog rows have no
  native deletion (abandoned rows stay), so a missing base row refuses, except
  when that side's own new `work reidentify` row names it in
  `reidentifiedFrom` and the other side left it unchanged. Rows keep base
  order, then HEAD's additions, then MERGE_HEAD's, as the append-only writer
  does, and `queue.json` is written with the backlog writer's own serializer.
- `queue.json` merged cleanly (stage 0) but `queue.md` conflicted. The merged
  `queue.json` is kept byte for byte (the working file must equal its index
  entry) and only `queue.md` is rebuilt.

`queue.md` is never merged as text: it is regenerated from the merged queue
and the merged live context, exactly as every native backlog write renders
it. The preview reports row counts per side and the merged `nextSeq`.

Two branches that each generated the next free ID for *different* rows produce
the same ID with different content; that refuses rather than renumbering a
row. If a queue conflict arises without a history conflict, this command does
not apply (it still requires both history paths to be conflicted).

## Refusals and recovery

Divergent updates to the same work-item/event identity, duplicate/missing IDs,
item deletion, changed repository identity, unsupported context versions,
oversized blobs, linked paths and incomplete conflict stages refuse without
writing history. Other source conflicts still need ordinary review. The tool
does not stage, commit, fetch, push, approve completion or merge the PR.

Apply reobserves HEAD, MERGE_HEAD, exact index stages and target contents, then
uses the existing two-file native recovery journal for history and, after it
has been applied, the existing `backlog-state` journal for `queue.json` and
`queue.md`. Both journals are checked before anything is written. Retrying
`--apply` recomputes identical write sets, so a crash between or inside the two
transactions is safe to re-run: an already-applied history is accepted, and a
pending journal resumes only when its write set matches this exact merge; an
unrelated journal refuses. Keep the index stages until apply/recovery
finishes, then review and stage the result. Rebuild generated registries from
canonical source records rather than choosing a conflicted registry branch.

This command reconciles already-recorded branch histories; it creates no work
transition or fabricated historical event. Native protocol commands remain
the authority for subsequent state changes. The command's cooperative lock
serializes other history-merge invocations, not every native lifecycle writer.
The exclusive-checkout requirement is necessary while WI-0087 remains open;
this is not a concurrent live-controller state merger.
