namespace Ros.Domain.Git

[<RequireQualifiedAccess>]
type GitDelta =
    | Unmodified
    | Added
    | Modified
    | Deleted
    | Renamed
    | Copied
    | TypeChanged
    | Unmerged
    | Unknown of char

[<RequireQualifiedAccess>]
type GitChangeStatus =
    | Tracked of index: GitDelta * workTree: GitDelta
    | Untracked
    | Ignored

type GitChange =
    { Status: GitChangeStatus
      Path: string
      OriginalPath: string option }

[<RequireQualifiedAccess>]
type GitUnavailableReason =
    | ToolUnavailable
    | NotRepository
    | CommandFailed
    | MalformedOutput

type GitFailure =
    { Operation: string
      Reason: GitUnavailableReason
      Message: string
      ExitCode: int option }

[<RequireQualifiedAccess>]
type GitStatusObservation =
    | Clean
    | Changed of GitChange list
    | Unavailable of GitFailure

/// Mirrors production's optional `ROS_BASE_REF` committed-range comparison
/// in `gitPaths` (`tools/ros_cli.mjs`): a missing/unresolvable ref is a
/// silent no-op (a CI base ref can be absent in nested fixture repositories),
/// while a resolvable ref whose diff itself fails is a hard failure, not a
/// silently skipped one.
[<RequireQualifiedAccess>]
type GitBaseComparisonOutcome =
    | NotConfigured
    | RefUnavailable
    | Committed of paths: string list
    | Unavailable of GitFailure

[<RequireQualifiedAccess>]
module GitStatus =
    let private deltaCode delta =
        match delta with
        | GitDelta.Unmodified -> ' '
        | GitDelta.Added -> 'A'
        | GitDelta.Modified -> 'M'
        | GitDelta.Deleted -> 'D'
        | GitDelta.Renamed -> 'R'
        | GitDelta.Copied -> 'C'
        | GitDelta.TypeChanged -> 'T'
        | GitDelta.Unmerged -> 'U'
        | GitDelta.Unknown value -> value

    let code status =
        match status with
        | GitChangeStatus.Tracked(index, workTree) ->
            System.String([| deltaCode index; deltaCode workTree |])
        | GitChangeStatus.Untracked -> "??"
        | GitChangeStatus.Ignored -> "!!"
