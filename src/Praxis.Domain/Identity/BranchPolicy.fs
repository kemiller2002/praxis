namespace Praxis.Domain.Identity

// requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md item 1 (DER-01): every
// work item executes on a branch named for its work-item ID, and
// "validation rejects meaningful work on a branch other than its work-item
// ID". The envelope path already refuses a claimed mismatch
// (`EnvelopeReconciliation.WorkItemBranchMismatch`); this is the same rule
// for native work, gated by `workProtocol.branchPolicy` so repositories
// that do not adopt it are unaffected. Pure.

[<RequireQualifiedAccess>]
type BranchPolicy =
    /// No branch rule (the default).
    | Unrestricted
    /// Meaningful work runs on a branch whose name is a work-item ID.
    | WorkItemId

[<RequireQualifiedAccess>]
module BranchPolicy =
    let code policy =
        match policy with
        | BranchPolicy.Unrestricted -> "none"
        | BranchPolicy.WorkItemId -> "work-item-id"

    let tryParse (value: string) =
        match value with
        | "none" -> Some BranchPolicy.Unrestricted
        | "work-item-id" -> Some BranchPolicy.WorkItemId
        | _ -> None

    /// The branch-policy finding, if any. `branch` is `None` when the branch
    /// cannot be determined (a detached HEAD outside pull-request CI);
    /// `workItems` are the IDs the work context records.
    let check (policy: BranchPolicy) (branch: string option) (meaningfulPaths: string list) (workItems: string list) : string option =
        match policy, meaningfulPaths with
        | BranchPolicy.Unrestricted, _
        | _, [] -> None
        | BranchPolicy.WorkItemId, paths ->
            let count = paths.Length

            match branch with
            | None ->
                Some
                    $"{count} meaningful change(s) exist but the branch cannot be determined (detached HEAD); workProtocol.branchPolicy 'work-item-id' requires work on a branch named for its work item (DER-01)"
            | Some name when List.contains name workItems -> None
            | Some name ->
                Some
                    $"{count} meaningful change(s) are on branch '{name}', which is not the ID of a work item in the work context; workProtocol.branchPolicy 'work-item-id' requires each work item to run on a branch named for its ID (DER-01)"
