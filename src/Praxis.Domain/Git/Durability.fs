namespace Praxis.Domain.Git

open System.Text.RegularExpressions

/// A full, lowercase, hexadecimal Git object id (SHA-1 or SHA-256). A short
/// or symbolic revision can never be one, so a value of this type always
/// names exactly one commit.
type CommitId =
    private
    | CommitId of string

    member this.Value =
        let (CommitId value) = this
        value

    override this.ToString() = this.Value

[<RequireQualifiedAccess>]
module CommitId =
    let private pattern = Regex(@"^(?:[0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant)

    let tryParse (value: string) =
        if isNull value then None
        elif pattern.IsMatch value then Some(CommitId value)
        else None

    let value (commit: CommitId) = commit.Value

    let short (commit: CommitId) = commit.Value.Substring(0, 12)

/// A configured Git remote. `Url` is never a credential carrier: the
/// infrastructure strips user information before constructing one, and an
/// unknown URL stays `None`.
type RemoteIdentity = { Name: string; Url: string option }

/// What `HEAD` is, as Git reports it.
[<RequireQualifiedAccess>]
type HeadState =
    /// HEAD is a branch that points at a commit.
    | OnBranch of branch: string * commit: CommitId
    /// HEAD names a commit directly; no branch will carry new commits.
    | Detached of commit: CommitId
    /// A branch with no commits yet (a fresh repository).
    | Unborn of branch: string option

/// The branch configuration `git rev-parse @{upstream}` would use.
[<RequireQualifiedAccess>]
type UpstreamState =
    | Tracking of remote: RemoteIdentity * remoteBranch: string
    /// The branch has no configured upstream.
    | NoUpstream
    /// The branch's upstream names a remote that is not configured.
    | RemoteNotConfigured of remoteName: string * remoteBranch: string

/// The head of a branch on a remote, read from the remote itself
/// (`git ls-remote`), never from a possibly stale remote-tracking ref.
[<RequireQualifiedAccess>]
type RemoteBranchObservation =
    | At of CommitId
    | Missing
    /// The remote could not be contacted (network, authentication, a
    /// missing repository). Never success.
    | Unreachable of GitFailure
    /// Git itself failed or answered in a form that could not be read.
    | Unavailable of GitFailure

/// How a local commit relates to another commit.
[<RequireQualifiedAccess>]
type CommitRelation =
    | Same
    /// The first commit has this many commits the second lacks, and not the reverse.
    | Ahead of count: int
    /// The second commit has this many commits the first lacks, and not the reverse.
    | Behind of count: int
    | Diverged of ahead: int * behind: int

[<RequireQualifiedAccess>]
type CommitRelationObservation =
    | Related of CommitRelation
    /// The commit is not present locally, so the relation cannot be known
    /// without fetching. Praxis never fetches on the executor's behalf.
    | ObjectMissing of CommitId
    | Unavailable of GitFailure

/// A generic Git read that either produced a typed value or failed with a
/// typed failure.
[<RequireQualifiedAccess>]
type GitRead<'value> =
    | Observed of 'value
    | Unavailable of GitFailure

[<RequireQualifiedAccess>]
module CommitRelation =
    /// The relation implied by `git rev-list --left-right --count A...B`.
    let ofCounts (ahead: int) (behind: int) =
        match ahead, behind with
        | 0, 0 -> CommitRelation.Same
        | ahead, 0 -> CommitRelation.Ahead ahead
        | 0, behind -> CommitRelation.Behind behind
        | ahead, behind -> CommitRelation.Diverged(ahead, behind)

[<RequireQualifiedAccess>]
module GitChangePaths =
    /// Every path a status entry touches: both sides of a rename or copy.
    let touchedPaths (change: GitChange) =
        match change.OriginalPath with
        | Some original -> [ change.Path; original ]
        | None -> [ change.Path ]
