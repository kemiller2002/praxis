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
