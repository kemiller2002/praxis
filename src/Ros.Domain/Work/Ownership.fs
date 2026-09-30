namespace Ros.Domain.Work

/// One commit between two points of history: its own changes (against its
/// only parent), or none for a merge, whose changes belong to the lines of
/// history it joins.
type CommitChange =
    { Commit: string
      IsMerge: bool
      Paths: string list }

/// What a work item's recorded evidence claims: a commit its durable
/// checkpoint (or Git-evidenced reconciliation) contains, and the meaningful
/// paths that evidence attributed to the item.
type CheckpointClaim =
    { WorkItemId: string
      Commit: string
      Paths: string list }

/// Which changes after a point in history belong to a work item
/// (PRAXIS-CONT-12). On a shared branch, a raw difference between two
/// commits also contains other items' merged work. A commit belongs to other
/// items, not this one, when every meaningful path it changed is already
/// claimed by some other item whose recorded evidence (a durable checkpoint,
/// or a Git-evidenced reconciliation) contains the commit. Evidence is
/// Praxis's recorded claims and Git ancestry only: nothing is inferred from
/// authors, messages or timing.
[<RequireQualifiedAccess>]
module CommitOwnership =
    /// Whether other items' recorded claims already own this commit.
    /// `covering` names the other items whose claims contain it.
    let isForeign (filter: PathFilterConfig) (claimedBy: string -> Set<string>) (covering: string list) (change: CommitChange) =
        let meaningful = PathFilter.meaningfulPaths filter change.Paths |> Set.ofList
        let owned = covering |> List.map claimedBy |> Set.unionMany

        change.IsMerge || (not meaningful.IsEmpty && not covering.IsEmpty && Set.isSubset meaningful owned)

    /// The raw paths this item's own commits changed: every non-merge commit
    /// that other items' claims do not own. Callers apply the meaningful-path
    /// filter and baseline exactly as they do to a raw difference.
    let ownPaths (filter: PathFilterConfig) (claims: CheckpointClaim list) (covering: string -> string list) (changes: CommitChange list) =
        let claimed =
            claims
            |> List.groupBy (fun claim -> claim.WorkItemId)
            |> List.map (fun (workItemId, entries) -> workItemId, entries |> List.collect (fun claim -> claim.Paths) |> Set.ofList)
            |> Map.ofList

        let claimedBy workItemId = claimed.TryFind workItemId |> Option.defaultValue Set.empty

        changes
        |> List.filter (fun change -> not (isForeign filter claimedBy (covering change.Commit) change))
        |> List.collect (fun change -> change.Paths)
        |> List.distinct
        |> List.sortWith (fun left right -> System.String.CompareOrdinal(left, right))
