namespace Ros.Domain.Work

open System
open Ros.Domain.Git
open Ros.Domain.Provenance

/// Post-hoc attribution reconciliation (issue #80).
///
/// Contemporaneous attribution is established while work happens: a work
/// item is active, and its completion event names the paths it changed.
/// Reconciliation is different in kind. It is evidence-backed *recovery*
/// performed after the fact, when meaningful changes were committed while no
/// work item was active. It never rewrites the original events and never
/// pretends the work was attributed from the beginning; it appends a
/// separate `work.attribution.reconciled` event whose paths are justified,
/// one by one, by the Git commits that changed them.
///
/// Three identities stay distinct and are never collapsed:
/// 1. the change author (Git's author/committer of each commit);
/// 2. the work item that receives attribution;
/// 3. the reconciliation actor (the process performing the recovery).
[<RequireQualifiedAccess>]
type GitEvidenceSelector =
    | Commit of reference: string
    | Range of baseReference: string * headReference: string

[<RequireQualifiedAccess>]
module GitEvidenceSelector =
    let text selector =
        match selector with
        | GitEvidenceSelector.Commit reference -> reference
        | GitEvidenceSelector.Range(baseReference, headReference) -> $"{baseReference}..{headReference}"

    /// A revision is passed to Git as an argument, so anything Git could read
    /// as an option, or that is not a single plain token, is refused before
    /// Git ever sees it.
    let private validReference (reference: string) =
        reference.Length > 0
        && not (reference.StartsWith "-")
        && not (reference |> Seq.exists (fun character -> Char.IsWhiteSpace character || Char.IsControl character))
        && not (reference.Contains "..")

    let tryParseCommit (value: string) : Result<GitEvidenceSelector, string> =
        if validReference value then
            Ok(GitEvidenceSelector.Commit value)
        elif value.Contains ".." then
            Error $"--commit '{value}' names a range; use --range BASE..HEAD"
        else
            Error $"--commit '{value}' is not a single Git revision"

    /// Only the two-dot `BASE..HEAD` form is accepted: the commits reachable
    /// from HEAD but not from BASE. The three-dot symmetric difference would
    /// also select commits on BASE's side, which never belong to this work.
    let tryParseRange (value: string) : Result<GitEvidenceSelector, string> =
        if value.Contains "..." then
            Error $"--range '{value}' uses a symmetric difference; only BASE..HEAD is accepted"
        else
            match value.Split("..") with
            | [| baseReference; headReference |] when validReference baseReference && validReference headReference ->
                Ok(GitEvidenceSelector.Range(baseReference, headReference))
            | _ -> Error $"--range '{value}' must be BASE..HEAD with two explicit revisions"

/// One selector as Git resolved it at reconciliation time.
type ResolvedEvidenceSelector =
    { Selector: GitEvidenceSelector
      BaseCommit: string option
      HeadCommit: string
      /// The commits the selector selects, oldest first.
      Commits: string list }

/// Everything Git established: the HEAD every selected commit was verified
/// against, how each selector resolved, and the commits themselves.
type GitReconciliationEvidence =
    { Head: string
      Selectors: ResolvedEvidenceSelector list
      Commits: GitCommit list }

/// What the repository knows about the requested work item.
[<RequireQualifiedAccess>]
type ReconciliationTarget =
    | Live of LiveWorkState
    | Backlog of status: string
    | Unknown

/// One `(commit, path)` pair an earlier reconciliation event attributed.
type ReconciliationClaim =
    { EventId: string
      WorkItemId: string
      Commit: string
      Path: string }

type ReconciliationRequest =
    { WorkItemId: string
      Target: ReconciliationTarget
      Reason: string
      /// When the reconciliation is recorded; it can never precede the
      /// commits it reconciles.
      OccurredAt: string
      Evidence: GitReconciliationEvidence
      /// Optional narrowing to named paths. It can only remove paths from
      /// what Git proves; a path Git does not prove is a rejection.
      PathRestriction: string list
      PathFilterConfig: PathFilterConfig
      /// Paths validation already treats as attributed.
      AttributedPaths: Set<string>
      ExistingClaims: ReconciliationClaim list }

[<RequireQualifiedAccess>]
type ReconciliationDisposition =
    | Reconciled
    | NotMeaningful
    | NotSelected
    | AlreadyAttributed
    | AlreadyReconciled of eventId: string
    | Conflict of workItemId: string * eventId: string

/// The verdict for one path one commit touched. For a rename, the source
/// path gets its own assessment whose `Path` differs from `Change.Path`.
type ReconciliationAssessment =
    { Commit: string
      Path: string
      Change: GitCommitChange
      Disposition: ReconciliationDisposition }

[<RequireQualifiedAccess>]
type ReconciliationRejection =
    | InvalidWorkItemId of string
    | UnknownWorkItem of string
    | AbandonedWorkItem of string
    | ReasonRequired
    | NoEvidence
    | InvalidSelector of message: string
    | ReferenceNotFound of reference: string * message: string
    | AmbiguousReference of reference: string * message: string
    | NotInCurrentHistory of reference: string * commit: string
    | RangeBaseNotAncestor of range: string
    | EmptyRange of range: string
    | MergeCommit of commit: string
    | PathNotInEvidence of path: string
    | ConflictingAttribution of path: string * commit: string * workItemId: string * eventId: string
    | OccurredBeforeEvidence of occurredAt: string * commit: string * committedAt: string
    | GitUnavailable of GitFailure

type ReconciliationPlan =
    { WorkItemId: string
      Reason: string
      Evidence: GitReconciliationEvidence
      Assessments: ReconciliationAssessment list
      /// The distinct paths the new event attributes, ordinally sorted.
      ReconciledPaths: string list
      /// Only the commits that justify a reconciled path, each narrowed to
      /// exactly the changes that justify one: the durable evidence.
      RecordedCommits: GitCommit list }

[<RequireQualifiedAccess>]
type ReconciliationDecision =
    | Reconcile of ReconciliationPlan
    | NothingToReconcile of GitReconciliationEvidence * ReconciliationAssessment list
    | Rejected of ReconciliationRejection list

[<RequireQualifiedAccess>]
module ReconciliationRejection =
    let message rejection =
        match rejection with
        | ReconciliationRejection.InvalidWorkItemId id -> $"'{id}' is not a valid work-item ID"
        | ReconciliationRejection.UnknownWorkItem id ->
            $"work item '{id}' does not exist in work context or the backlog; reconciliation attributes changes to an existing work item and never creates one"
        | ReconciliationRejection.AbandonedWorkItem id -> $"work item '{id}' was abandoned; abandoned work cannot receive attribution"
        | ReconciliationRejection.ReasonRequired -> "reconciliation requires --reason explaining why attribution was missing"
        | ReconciliationRejection.NoEvidence -> "reconciliation requires Git evidence: at least one --commit REV or --range BASE..HEAD"
        | ReconciliationRejection.InvalidSelector message -> message
        | ReconciliationRejection.ReferenceNotFound(reference, detail) -> $"Git revision '{reference}' does not resolve to a commit: {detail}"
        | ReconciliationRejection.AmbiguousReference(reference, detail) -> $"Git revision '{reference}' is ambiguous: {detail}"
        | ReconciliationRejection.NotInCurrentHistory(reference, commit) ->
            $"commit {commit} ('{reference}') is not in the history of HEAD; only changes this checkout actually contains can be reconciled"
        | ReconciliationRejection.RangeBaseNotAncestor range ->
            $"range '{range}': BASE is not an ancestor of HEAD, so the range would also select unrelated history"
        | ReconciliationRejection.EmptyRange range -> $"range '{range}' selects no commits"
        | ReconciliationRejection.MergeCommit commit ->
            $"commit {commit} is a merge; a merge's changes cannot be attributed to one line of work unambiguously. Reconcile the individual commits it merged with --commit"
        | ReconciliationRejection.PathNotInEvidence path ->
            $"--path '{path}' is not changed by any selected commit; only paths Git proves were changed can be reconciled"
        | ReconciliationRejection.ConflictingAttribution(path, commit, workItemId, eventId) ->
            $"'{path}' in commit {commit} is already reconciled to work item '{workItemId}' (event {eventId}); conflicting attribution is never overwritten"
        | ReconciliationRejection.OccurredBeforeEvidence(occurredAt, commit, committedAt) ->
            $"--occurred-at {occurredAt} precedes commit {commit} (committed {committedAt}); a reconciliation is recorded after the work it reconciles, at the real current time"
        | ReconciliationRejection.GitUnavailable failure -> $"{failure.Operation} unavailable: {failure.Message}"

[<RequireQualifiedAccess>]
module ReconciliationDisposition =
    let code disposition =
        match disposition with
        | ReconciliationDisposition.Reconciled -> "reconciled"
        | ReconciliationDisposition.NotMeaningful -> "not-meaningful"
        | ReconciliationDisposition.NotSelected -> "not-selected"
        | ReconciliationDisposition.AlreadyAttributed -> "already-attributed"
        | ReconciliationDisposition.AlreadyReconciled _ -> "already-reconciled"
        | ReconciliationDisposition.Conflict _ -> "conflict"

[<RequireQualifiedAccess>]
module WorkReconciliation =
    /// The attribution mode every reconciliation event records.
    let PostHoc = "post-hoc"

    let EventType = "work.attribution.reconciled"

    let normalizePath (path: string) =
        let forward = path.Replace('\\', '/')
        if forward.StartsWith "./" then forward.Substring 2 else forward

    /// Clock tolerance between the machine that committed and the one
    /// reconciling.
    let private skew = TimeSpan.FromMinutes 5.0

    let private instant (value: string) =
        match DateTimeOffset.TryParse(value, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) with
        | true, parsed -> Some parsed
        | _ -> None

    /// Commits whose committer date is later than `occurredAt`: a
    /// reconciliation dated before its own evidence would pass itself off
    /// as contemporaneous.
    let commitsAfter (occurredAt: string) (commits: GitCommit list) =
        match instant occurredAt with
        | None -> []
        | Some recorded ->
            commits
            |> List.filter (fun commit ->
                match instant commit.Committer.Date with
                | Some committed -> committed - skew > recorded
                | None -> false)

    let private ordinal (values: string list) =
        values |> List.distinct |> List.sortWith (fun left right -> String.CompareOrdinal(left, right))

    let private targetRejections (request: ReconciliationRequest) =
        if not (WorkItemId.isValid request.WorkItemId) then
            [ ReconciliationRejection.InvalidWorkItemId request.WorkItemId ]
        else
            match request.Target with
            | ReconciliationTarget.Unknown -> [ ReconciliationRejection.UnknownWorkItem request.WorkItemId ]
            | ReconciliationTarget.Backlog "abandoned" -> [ ReconciliationRejection.AbandonedWorkItem request.WorkItemId ]
            | _ -> []

    let private assess (request: ReconciliationRequest) (commit: GitCommit) (change: GitCommitChange) (path: string) =
        let claims =
            request.ExistingClaims |> List.filter (fun claim -> claim.Commit = commit.Sha && claim.Path = path)

        let disposition =
            if not request.PathRestriction.IsEmpty && not (List.contains path request.PathRestriction) then
                ReconciliationDisposition.NotSelected
            elif not (PathFilter.isMeaningful request.PathFilterConfig path) then
                ReconciliationDisposition.NotMeaningful
            else
                match claims |> List.tryFind (fun claim -> claim.WorkItemId <> request.WorkItemId), claims with
                | Some other, _ -> ReconciliationDisposition.Conflict(other.WorkItemId, other.EventId)
                | None, own :: _ -> ReconciliationDisposition.AlreadyReconciled own.EventId
                | None, [] when request.AttributedPaths.Contains path -> ReconciliationDisposition.AlreadyAttributed
                | None, [] -> ReconciliationDisposition.Reconciled

        { Commit = commit.Sha
          Path = path
          Change = change
          Disposition = disposition }

    /// Every `(commit, path)` verdict, in commit order then Git's own change
    /// order, each pair assessed once.
    let assessments (request: ReconciliationRequest) =
        request.Evidence.Commits
        |> List.collect (fun commit ->
            commit.Changes
            |> List.collect (fun change -> GitCommitChange.touchedPaths change |> List.map (assess request commit change)))
        |> List.distinctBy (fun assessment -> assessment.Commit, assessment.Path)

    let private recordedCommits (evidence: GitReconciliationEvidence) (reconciled: ReconciliationAssessment list) =
        evidence.Commits
        |> List.choose (fun commit ->
            let justifying =
                commit.Changes
                |> List.filter (fun change ->
                    reconciled
                    |> List.exists (fun assessment -> assessment.Commit = commit.Sha && assessment.Change = change))

            if justifying.IsEmpty then None else Some { commit with Changes = justifying })

    /// Decides a reconciliation from already-gathered Git evidence. Pure:
    /// every rejection is reported at once, and nothing is attributed unless
    /// every check passes.
    let decide (request: ReconciliationRequest) : ReconciliationDecision =
        let request =
            { request with PathRestriction = request.PathRestriction |> List.map normalizePath |> List.distinct }

        let evidencePaths =
            request.Evidence.Commits
            |> List.collect (fun commit -> commit.Changes |> List.collect GitCommitChange.touchedPaths)
            |> Set.ofList

        let preconditions =
            targetRejections request
            @ (if String.IsNullOrWhiteSpace request.Reason then [ ReconciliationRejection.ReasonRequired ] else [])
            @ (if request.Evidence.Commits.IsEmpty then [ ReconciliationRejection.NoEvidence ] else [])
            @ (commitsAfter request.OccurredAt request.Evidence.Commits
               |> List.map (fun commit -> ReconciliationRejection.OccurredBeforeEvidence(request.OccurredAt, commit.Sha, commit.Committer.Date)))
            @ (request.Evidence.Commits
               |> List.filter (fun commit -> commit.Parents.Length > 1)
               |> List.map (fun commit -> ReconciliationRejection.MergeCommit commit.Sha))
            @ (request.PathRestriction
               |> List.filter (evidencePaths.Contains >> not)
               |> List.map ReconciliationRejection.PathNotInEvidence)

        match preconditions with
        | _ :: _ -> ReconciliationDecision.Rejected preconditions
        | [] ->
            let assessed = assessments request

            let conflicts =
                assessed
                |> List.choose (fun assessment ->
                    match assessment.Disposition with
                    | ReconciliationDisposition.Conflict(workItemId, eventId) ->
                        Some(ReconciliationRejection.ConflictingAttribution(assessment.Path, assessment.Commit, workItemId, eventId))
                    | _ -> None)

            let reconciled =
                assessed |> List.filter (fun assessment -> assessment.Disposition = ReconciliationDisposition.Reconciled)

            match conflicts, reconciled with
            | _ :: _, _ -> ReconciliationDecision.Rejected conflicts
            | [], [] -> ReconciliationDecision.NothingToReconcile(request.Evidence, assessed)
            | [], _ ->
                ReconciliationDecision.Reconcile
                    { WorkItemId = request.WorkItemId
                      Reason = request.Reason.Trim()
                      Evidence = request.Evidence
                      Assessments = assessed
                      ReconciledPaths = reconciled |> List.map _.Path |> ordinal
                      RecordedCommits = recordedCommits request.Evidence reconciled }

/// A `work.attribution.reconciled` event as read back from the event log.
type ReconciliationRecord =
    { EventId: string
      WorkItemId: string
      OccurredAt: string
      Attribution: string
      Reason: string
      Paths: string list
      Evidence: GitReconciliationEvidence
      Actor: Actor option
      /// Whether the stored `eventId` is the content hash of the rest of
      /// the stored event, i.e. the event was not edited after it was written.
      IntegrityVerified: bool }

[<RequireQualifiedAccess>]
type ReconciliationEventRead =
    | Parsed of ReconciliationRecord
    | Malformed of line: int * message: string

type ReconciliationAssessmentResult =
    { Findings: WorkAttributionFinding list
      /// Records that passed every check; only these attribute paths.
      Valid: ReconciliationRecord list }

/// Validation of already-recorded reconciliation events. A reconciliation
/// event only attributes its paths when it is internally consistent: its
/// content hash matches, it names an existing, non-abandoned work item and
/// an actor, it is marked post-hoc, every path is justified by a recorded
/// non-merge commit change, and no other work item claims the same
/// `(commit, path)`. A hand-written or edited event therefore cannot make
/// validation pass; it makes validation fail.
[<RequireQualifiedAccess>]
module ReconciliationValidation =
    let EventLogPath = ".ros/events/events.jsonl"

    let Field = "work_reconciliation"

    let private finding message : WorkAttributionFinding =
        { Path = EventLogPath
          Field = Field
          Message = message }

    let private recordProblems (target: string -> ReconciliationTarget) (record: ReconciliationRecord) =
        let justified =
            record.Evidence.Commits
            |> List.collect (fun commit -> commit.Changes |> List.collect GitCommitChange.touchedPaths)
            |> Set.ofList

        [ if not record.IntegrityVerified then
              yield "its eventId does not match its content; reconciliation events must never be edited or written by hand"
          if record.Attribution <> WorkReconciliation.PostHoc then
              yield $"attribution must be '{WorkReconciliation.PostHoc}', not '{record.Attribution}'"
          if record.Actor.IsNone then
              yield "it does not record the reconciliation actor"
          if String.IsNullOrWhiteSpace record.Reason then
              yield "it does not record a reason"
          if record.Paths.IsEmpty then
              yield "it attributes no paths"
          match target record.WorkItemId with
          | ReconciliationTarget.Unknown -> yield $"work item '{record.WorkItemId}' does not exist"
          | ReconciliationTarget.Backlog "abandoned" -> yield $"work item '{record.WorkItemId}' was abandoned"
          | _ -> ()
          for commit in WorkReconciliation.commitsAfter record.OccurredAt record.Evidence.Commits do
              yield $"it is dated {record.OccurredAt}, before commit {commit.Sha} it reconciles was committed"
          for commit in record.Evidence.Commits |> List.filter (fun commit -> commit.Parents.Length > 1) do
              yield $"commit {commit.Sha} is a merge and cannot justify attribution"
          for path in record.Paths |> List.filter (justified.Contains >> not) do
              yield $"path '{path}' is not justified by any recorded commit change" ]
        |> List.map (fun problem -> $"reconciliation event {record.EventId} for '{record.WorkItemId}' is invalid: {problem}")

    let private claims (record: ReconciliationRecord) =
        record.Evidence.Commits
        |> List.collect (fun commit ->
            commit.Changes
            |> List.collect GitCommitChange.touchedPaths
            |> List.filter (fun path -> List.contains path record.Paths)
            |> List.map (fun path ->
                let claim: ReconciliationClaim =
                    { EventId = record.EventId
                      WorkItemId = record.WorkItemId
                      Commit = commit.Sha
                      Path = path }

                claim))

    /// The `(commit, path)` pairs every given record claims.
    let claimsOf (records: ReconciliationRecord list) = records |> List.collect claims

    let assess (target: string -> ReconciliationTarget) (reads: ReconciliationEventRead list) : ReconciliationAssessmentResult =
        let malformed =
            reads
            |> List.choose (function
                | ReconciliationEventRead.Malformed(line, message) ->
                    Some(finding $"reconciliation event on line {line} is malformed: {message}")
                | ReconciliationEventRead.Parsed _ -> None)

        let parsed =
            reads
            |> List.choose (function
                | ReconciliationEventRead.Parsed record -> Some record
                | ReconciliationEventRead.Malformed _ -> None)

        let individuallyChecked = parsed |> List.map (fun record -> record, recordProblems target record)

        let conflicting =
            claimsOf (individuallyChecked |> List.filter (snd >> List.isEmpty) |> List.map fst)
            |> List.groupBy (fun claim -> claim.Commit, claim.Path)
            |> List.filter (fun (_, group) -> (group |> List.map _.WorkItemId |> List.distinct).Length > 1)

        let conflictingEvents = conflicting |> List.collect (snd >> List.map _.EventId) |> Set.ofList

        let conflictFindings =
            conflicting
            |> List.map (fun ((commit, path), group) ->
                let owners =
                    group |> List.map (fun claim -> $"'{claim.WorkItemId}' (event {claim.EventId})") |> List.distinct |> String.concat ", "

                finding $"'{path}' in commit {commit} is reconciled to more than one work item: {owners}")

        { Findings = malformed @ (individuallyChecked |> List.collect snd |> List.map finding) @ conflictFindings
          Valid =
            individuallyChecked
            |> List.filter (fun (record, problems) -> problems.IsEmpty && not (conflictingEvents.Contains record.EventId))
            |> List.map fst }

/// A path's current content, as validation observes it.
[<RequireQualifiedAccess>]
type PathContentState =
    | Present of blob: string
    | Absent
    | Unreadable

/// Reconciled attribution is bound to content, not just to a path name.
/// Contemporaneous attribution names paths a work item changed while it was
/// active; reconciliation can only vouch for the specific changes Git
/// proves. A reconciled path therefore counts as attributed only while its
/// current content is one of the states the recorded evidence produced (a
/// recorded post-change blob, or absence after a deletion or as a rename's
/// source). Any later change to that path is new, unattributed work again,
/// so reconciling an old commit can never pre-authorize future edits.
[<RequireQualifiedAccess>]
module ReconciliationCoverage =
    let private statesOf (record: ReconciliationRecord) =
        record.Evidence.Commits
        |> List.collect (fun commit ->
            commit.Changes
            |> List.collect (fun change ->
                let destination =
                    match change.Kind, change.Blob with
                    | GitCommitChangeKind.Deleted, _
                    | _, None -> PathContentState.Absent
                    | _, Some blob -> PathContentState.Present blob

                let source =
                    match change.Kind, change.OriginalPath with
                    | GitCommitChangeKind.Renamed, Some original -> [ original, PathContentState.Absent ]
                    | _ -> []

                (change.Path, destination) :: source))
        |> List.filter (fun (path, _) -> List.contains path record.Paths)

    /// For every reconciled path, the content states the evidence vouches for.
    let acceptedStates (records: ReconciliationRecord list) : Map<string, PathContentState list> =
        records
        |> List.collect statesOf
        |> List.groupBy fst
        |> List.map (fun (path, states) -> path, states |> List.map snd |> List.distinct)
        |> Map.ofList

    /// The reconciled paths whose current content the evidence vouches for.
    let covered (accepted: Map<string, PathContentState list>) (current: Map<string, PathContentState>) : Set<string> =
        accepted
        |> Map.filter (fun path states ->
            match Map.tryFind path current with
            | Some state -> List.contains state states
            | None -> false)
        |> Map.keys
        |> Set.ofSeq
