namespace Ros.Application.Git

open Ros.Domain.Git

type GitRepository =
    { ObserveStatus: unit -> GitStatusObservation }

/// A ref name (e.g. `$ROS_BASE_REF`) in; the committed-range comparison
/// outcome out. `None` input means the ref was never configured.
type GitBaseComparison =
    { Compare: string option -> GitBaseComparisonOutcome }

[<RequireQualifiedAccess>]
module GitOperations =
    let observe repository = repository.ObserveStatus()

    let compareBase (comparison: GitBaseComparison) ref = comparison.Compare ref

/// Read-only access to committed history, used to verify reconciliation
/// evidence. Every operation reports failure explicitly; none guesses.
type GitHistory =
    { /// `HEAD` as a full commit id.
      Head: unit -> GitRefResolution
      /// A user-supplied revision as a full commit id.
      ResolveCommit: string -> GitRefResolution
      /// Whether the first commit is an ancestor of (or equal to) the second.
      IsAncestor: string -> string -> Result<bool, GitFailure>
      /// Commits reachable from the second but not the first, oldest first.
      ListRange: string -> string -> Result<string list, GitFailure>
      /// A commit's identity and its changes against its single parent.
      ReadCommit: string -> Result<GitCommit, GitFailure> }
