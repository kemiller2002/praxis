namespace Ros.Domain.Git

/// One side of a commit's identity as Git itself records it. Reconciliation
/// preserves these verbatim: they name who produced the change, which is a
/// different fact from who later attributed it to a work item.
type GitPerson =
    { Name: string
      Email: string
      Date: string }

/// How one commit changed one path, as `git diff-tree --raw` reports it
/// against the commit's single parent (or the empty tree for a root commit).
[<RequireQualifiedAccess>]
type GitCommitChangeKind =
    | Added
    | Modified
    | Deleted
    | Renamed
    | Copied
    | TypeChanged

type GitCommitChange =
    { Kind: GitCommitChangeKind
      Path: string
      /// The source path of a rename or copy.
      OriginalPath: string option
      /// The post-change blob id; `None` for a deletion.
      Blob: string option }

type GitCommit =
    { Sha: string
      Parents: string list
      Author: GitPerson
      Committer: GitPerson
      Subject: string
      Changes: GitCommitChange list }

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
module GitUnavailableReason =
    /// The raw reason code production's `commandFailure`/parse-failure
    /// helpers assign (`tools/ros_git.mjs`), used verbatim in messages that
    /// must match production's, such as the work-attribution "cannot verify"
    /// finding.
    let code reason =
        match reason with
        | GitUnavailableReason.ToolUnavailable -> "tool-unavailable"
        | GitUnavailableReason.NotRepository -> "not-repository"
        | GitUnavailableReason.CommandFailed -> "command-failed"
        | GitUnavailableReason.MalformedOutput -> "malformed-output"

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

/// A user-supplied revision resolved against the repository. `Ambiguous`
/// is distinct from `NotFound` so a caller can fail closed on a short SHA
/// or ref name Git could read more than one way.
[<RequireQualifiedAccess>]
type GitRefResolution =
    | Resolved of sha: string
    | NotFound of message: string
    | Ambiguous of message: string
    | Unavailable of GitFailure

[<RequireQualifiedAccess>]
module GitCommitChangeKind =
    let code kind =
        match kind with
        | GitCommitChangeKind.Added -> "added"
        | GitCommitChangeKind.Modified -> "modified"
        | GitCommitChangeKind.Deleted -> "deleted"
        | GitCommitChangeKind.Renamed -> "renamed"
        | GitCommitChangeKind.Copied -> "copied"
        | GitCommitChangeKind.TypeChanged -> "type-changed"

    let tryParse code =
        match code with
        | "added" -> Some GitCommitChangeKind.Added
        | "modified" -> Some GitCommitChangeKind.Modified
        | "deleted" -> Some GitCommitChangeKind.Deleted
        | "renamed" -> Some GitCommitChangeKind.Renamed
        | "copied" -> Some GitCommitChangeKind.Copied
        | "type-changed" -> Some GitCommitChangeKind.TypeChanged
        | _ -> None

[<RequireQualifiedAccess>]
module GitCommitChange =
    /// Every path whose content this change alters: both sides of a rename
    /// (the source disappears, the destination appears), but only the
    /// destination of a copy (its source is left untouched).
    let touchedPaths (change: GitCommitChange) =
        match change.Kind, change.OriginalPath with
        | GitCommitChangeKind.Renamed, Some original -> [ change.Path; original ]
        | _ -> [ change.Path ]

/// The object id Git gives content it stores: the hash of a
/// `<type> <length>\0` header followed by the bytes, in the repository's
/// object format.
[<RequireQualifiedAccess>]
module GitObjectId =
    type Format =
        | Sha1
        | Sha256

    let private hash format (bytes: byte array) =
        match format with
        | Sha1 -> System.Security.Cryptography.SHA1.HashData bytes
        | Sha256 -> System.Security.Cryptography.SHA256.HashData bytes

    /// The blob id of `content`, as `git hash-object --no-filters` reports it.
    let blob (format: Format) (content: byte array) =
        let header = System.Text.Encoding.ASCII.GetBytes $"blob {content.Length}\000"
        Array.append header content |> hash format |> System.Convert.ToHexString |> fun hex -> hex.ToLowerInvariant()
