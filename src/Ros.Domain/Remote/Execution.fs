namespace Ros.Domain.Remote

open System
open System.Globalization

/// How an accepted `praxis.remote` request is carried out
/// (PRAXIS-REMOTE-03, `DF-ROS-2026-A041` sections 3-6). Everything here is
/// pure: it plans, classifies, and judges, and leaves every effect to the
/// executor boundary.

/// What executing an accepted request means.
[<RequireQualifiedAccess>]
type ExecutionPlan =
    /// Answered by the remote boundary itself, without running a command.
    | Describe
    | RequestStatus of requestId: string
    /// Run the *same* command implementation as the local CLI, with this
    /// argument list. Every value in it was validated as untrusted input
    /// (`RequestValidation`) and is passed as a discrete argument; no shell
    /// ever sees it.
    | Command of arguments: string list
    /// Run each constituent in order, stopping at the first that does not
    /// succeed (PRAXIS-REMOTE-08).
    | Batch of constituents: Request list

[<RequireQualifiedAccess>]
module ExecutionPlan =
    let private repeated (flag: string) (values: string list) =
        values |> List.collect (fun value -> [ flag; value ])

    let private optionalFlag (flag: string) (value: string option) =
        value |> Option.map (fun text -> [ flag; text ]) |> Option.defaultValue []

    /// Maps a validated request to the local CLI command it is equivalent
    /// to. `occurredAt` is the executor's own clock: a requester can assert
    /// `requestedAt`, but never the time a transition happened.
    let forRequest (occurredAt: string) (request: Request) : ExecutionPlan =
        match request.Arguments with
        | Arguments.NoArguments ->
            match request.Operation with
            | Operation.Describe -> ExecutionPlan.Describe
            | Operation.Status -> ExecutionPlan.Command [ "status"; "--json" ]
            | Operation.ProvenanceIdentity -> ExecutionPlan.Command [ "provenance"; "identity"; "--json" ]
            | _ -> ExecutionPlan.Command [ "validate"; "--json" ]
        | Arguments.WorkContext id -> ExecutionPlan.Command [ "work"; "context"; id ]
        | Arguments.RequestStatus id -> ExecutionPlan.RequestStatus id
        | Arguments.WorkStart start ->
            ExecutionPlan.Command(
                [ "work"; "start" ]
                @ repeated "--id" start.WorkItemIds
                @ [ "--occurred-at"; occurredAt ]
                @ optionalFlag "--type" start.Type
                @ repeated "--classification" start.Classifications
            )
        | Arguments.WorkResume ids ->
            ExecutionPlan.Command([ "work"; "resume" ] @ repeated "--id" ids @ [ "--occurred-at"; occurredAt ])
        | Arguments.WorkBlock(ids, reason) ->
            ExecutionPlan.Command([ "work"; "block" ] @ repeated "--id" ids @ [ "--occurred-at"; occurredAt; "--reason"; reason ])
        | Arguments.WorkComplete complete ->
            ExecutionPlan.Command(
                [ "work"; "complete" ]
                @ repeated "--id" complete.WorkItemIds
                @ [ "--occurred-at"; occurredAt ]
                @ repeated "--evidence" (complete.Evidence |> List.map (fun evidence -> $"{evidence.Type}={evidence.Path}"))
                @ optionalFlag "--conclusion" complete.Conclusion
            )
        | Arguments.TelemetryRecord record ->
            // The target is the requester's own execution when it named one,
            // otherwise the work item; the command resolves either.
            let target =
                request.ExecutionId |> Option.orElse record.WorkItemId |> Option.toList

            ExecutionPlan.Command(
                [ "telemetry"; "record" ]
                @ target
                @ [ "--metric"; record.Metric; "--value"; record.Value.ToString(CultureInfo.InvariantCulture) ]
                @ optionalFlag "--unit" record.Unit
                @ optionalFlag "--currency" record.Currency
                @ optionalFlag "--quality" record.Quality
                @ optionalFlag "--confidence" record.Confidence
                @ optionalFlag "--scope" record.Scope
                // Unlabelled remote telemetry is what the requester reported.
                @ [ "--source-type"; record.SourceType |> Option.defaultValue "agent-report" ]
                @ optionalFlag "--source-name" record.SourceName
                @ optionalFlag "--mechanism" record.Mechanism
                @ optionalFlag "--pricing-source" record.PricingSource
                @ optionalFlag "--pricing-version" record.PricingVersion
                @ optionalFlag "--collected-at" record.CollectedAt
                @ optionalFlag "--step" record.Step
            )
        | Arguments.Batch _ -> ExecutionPlan.Batch(Batch.constituents request)
        | Arguments.Step(stepId, name, reason) ->
            let transition =
                match request.Operation with
                | Operation.StepStart -> "start"
                | Operation.StepFail -> "fail"
                | _ -> "complete"

            ExecutionPlan.Command(
                [ "telemetry"; "step"; transition ]
                @ Option.toList request.ExecutionId
                @ [ "--step"; stepId; "--occurred-at"; occurredAt ]
                @ optionalFlag "--name" name
                @ optionalFlag "--reason" reason
            )
        | Arguments.WorkReconcile reconcile ->
            ExecutionPlan.Command(
                [ "work"; "reconcile"; "--id"; reconcile.WorkItemId; "--occurred-at"; occurredAt; "--reason"; reconcile.Reason; "--json" ]
                @ repeated "--commit" reconcile.Commits
                @ repeated "--range" reconcile.Ranges
                @ repeated "--path" reconcile.Paths
            )

/// Authority available to a request: what the transport grants (the
/// adapter's job, from trusted workflow configuration) intersected with
/// what the repository has opted into. Remote mutation is opt-in: a
/// repository that says nothing allows remote reads only.
[<RequireQualifiedAccess>]
module RemotePolicy =
    let defaultRepositoryCapabilities = set [ Capability.Read ]

    let effectiveGrants (repository: Set<Capability>) (transport: Set<Capability>) = Set.intersect repository transport

/// Which paths a remote request may persist, and where its journal lives.
/// A remote executor never commits arbitrary working-tree changes: only
/// Praxis-owned state under `.ros/` may be persisted, and the adapter
/// commits exactly the paths reported here.
[<RequireQualifiedAccess>]
module RemotePersistence =
    [<Literal>]
    let JournalDirectory = ".ros/remote/requests"

    let private transient = [ ".ros/locks/"; ".ros/transactions/" ]

    let isPraxisOwned (path: string) =
        path.StartsWith(".ros/", StringComparison.Ordinal)
        && not (path.Contains "..")
        && not (transient |> List.exists (fun prefix -> path.StartsWith(prefix, StringComparison.Ordinal)))

    let isTransient (path: string) =
        transient |> List.exists (fun prefix -> path.StartsWith(prefix, StringComparison.Ordinal))

    /// `:` is legal in a request ID but not in every file system; `~` is
    /// not legal in a request ID, so the mapping cannot collide.
    let journalPath (requestId: string) =
        $"{JournalDirectory}/{requestId.Replace(':', '~')}.json"

    /// Splits the paths a command changed into those that may be persisted
    /// and those that must not; transient bookkeeping is dropped.
    let partition (changed: string list) =
        let relevant = changed |> List.filter (isTransient >> not) |> List.distinct |> List.sort
        relevant |> List.partition isPraxisOwned

/// Maps a finished command to the protocol's outcome vocabulary. The CLI's
/// own contract is `0` success, `1` refusal or failure, `2` bad arguments;
/// a remote request's arguments were built by `ExecutionPlan` from
/// validated values, so `2` is an executor defect, not a caller error.
[<RequireQualifiedAccess>]
module CommandOutcome =
    let classify (operation: Operation) (exitCode: int) : FailureCode option =
        match operation, exitCode with
        | _, 0 -> None
        | Operation.Validate, 1 -> Some FailureCode.ValidationFailed
        | _, 1 -> Some FailureCode.DomainRejected
        | _ -> Some FailureCode.Internal

/// A validation finding reduced to what identifies it.
type ValidationFinding =
    { Severity: string
      Path: string
      Field: string
      Message: string }

/// A remote mutation may not make the repository less valid than it was.
/// Findings present before the request are not the request's doing (and
/// may be exactly what, say, a reconciliation is fixing); any finding that
/// appears only afterwards means the mutation is refused and undone.
[<RequireQualifiedAccess>]
module ValidationRegression =
    let introduced (before: ValidationFinding list) (after: ValidationFinding list) =
        let existing = set before
        after |> List.filter (fun finding -> finding.Severity = "error" && not (existing.Contains finding)) |> List.distinct

/// Whether a batch is journalled as a whole (PRAXIS-REMOTE-08). A batch
/// that kept nothing leaves no record. One stopped by a failure the same
/// request may retry is not journalled either: its accepted constituents
/// are journalled individually, so resending it replays them and re-runs
/// the stopped constituent rather than replaying the failure forever.
[<RequireQualifiedAccess>]
module BatchJournal =
    let shouldRecord (keptAnything: bool) (stoppedBy: FailureCode option) =
        keptAnything
        && (match stoppedBy with
            | Some code -> FailureCode.retry code <> RetryAdvice.SameRequest
            | None -> true)
