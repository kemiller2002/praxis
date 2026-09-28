namespace Ros.Domain.Remote

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Ros.Domain.Provenance

/// The `praxis.remote` request/response protocol (`DF-ROS-2026-A041`,
/// `docs/remote-protocol.md`, `RQ-ROS-2026-A021`): a typed, versioned,
/// transport-independent envelope through which an agent without a local
/// Praxis runtime asks a trusted executor to run an explicit Praxis
/// operation. Nothing here knows about GitHub or any other transport, and
/// nothing here executes anything: this module decides *whether* a request
/// may run, *whether* it is a replay, and *how* a refusal is classified.
/// Execution stays with the same command implementations the local CLI
/// uses, so remote requests never meet a second, weaker rule set.

/// Authority an operation requires. The executor derives the granted set
/// from trusted configuration, never from the request document itself.
[<RequireQualifiedAccess>]
type Capability =
    | Read
    | Mutate
    | Complete
    | Reconcile
    | Admin

[<RequireQualifiedAccess>]
module Capability =
    let code capability =
        match capability with
        | Capability.Read -> "read"
        | Capability.Mutate -> "mutate"
        | Capability.Complete -> "complete"
        | Capability.Reconcile -> "reconcile"
        | Capability.Admin -> "admin"

    let all = [ Capability.Read; Capability.Mutate; Capability.Complete; Capability.Reconcile; Capability.Admin ]

    let tryParse (value: string) = all |> List.tryFind (fun capability -> code capability = value)

/// The allow-listed operation catalog. There is deliberately no operation
/// that accepts a command string: every operation is an explicit Praxis
/// capability with typed arguments.
[<RequireQualifiedAccess>]
type Operation =
    | Describe
    | Status
    | Validate
    | WorkContext
    | ProvenanceIdentity
    | RequestStatus
    | WorkStart
    | WorkResume
    | WorkBlock
    | TelemetryRecord
    | WorkComplete
    | WorkReconcile
    | StepStart
    | StepComplete
    | StepFail

[<RequireQualifiedAccess>]
module Operation =
    let all =
        [ Operation.Describe
          Operation.Status
          Operation.Validate
          Operation.WorkContext
          Operation.ProvenanceIdentity
          Operation.RequestStatus
          Operation.WorkStart
          Operation.WorkResume
          Operation.WorkBlock
          Operation.TelemetryRecord
          Operation.WorkComplete
          Operation.WorkReconcile
          Operation.StepStart
          Operation.StepComplete
          Operation.StepFail ]

    let code operation =
        match operation with
        | Operation.Describe -> "praxis.describe"
        | Operation.Status -> "status"
        | Operation.Validate -> "validate"
        | Operation.WorkContext -> "work.context"
        | Operation.ProvenanceIdentity -> "provenance.identity"
        | Operation.RequestStatus -> "request.status"
        | Operation.WorkStart -> "work.start"
        | Operation.WorkResume -> "work.resume"
        | Operation.WorkBlock -> "work.block"
        | Operation.TelemetryRecord -> "telemetry.record"
        | Operation.WorkComplete -> "work.complete"
        | Operation.WorkReconcile -> "work.reconcile"
        | Operation.StepStart -> "step.start"
        | Operation.StepComplete -> "step.complete"
        | Operation.StepFail -> "step.fail"

    let tryParse (value: string) = all |> List.tryFind (fun operation -> code operation = value)

    let capability operation =
        match operation with
        | Operation.Describe
        | Operation.Status
        | Operation.Validate
        | Operation.WorkContext
        | Operation.ProvenanceIdentity
        | Operation.RequestStatus -> Capability.Read
        | Operation.WorkStart
        | Operation.WorkResume
        | Operation.WorkBlock
        | Operation.TelemetryRecord
        | Operation.StepStart
        | Operation.StepComplete
        | Operation.StepFail -> Capability.Mutate
        | Operation.WorkComplete -> Capability.Complete
        | Operation.WorkReconcile -> Capability.Reconcile

    /// Mutating operations change repository state, so they must be bound
    /// to an expected commit and journalled for idempotent replay.
    let isMutating operation = capability operation <> Capability.Read

    /// The argument fields each operation accepts, as (required, optional).
    /// The JSON contract rejects any other field, and discovery publishes
    /// exactly this catalog, so the two can never disagree.
    let arguments operation : string list * string list =
        match operation with
        | Operation.Describe
        | Operation.Status
        | Operation.Validate
        | Operation.ProvenanceIdentity -> [], []
        | Operation.WorkContext -> [ "workItemId" ], []
        | Operation.RequestStatus -> [ "requestId" ], []
        | Operation.WorkStart -> [ "workItemIds" ], [ "type"; "classifications" ]
        | Operation.WorkResume -> [ "workItemIds" ], []
        | Operation.WorkBlock -> [ "workItemIds"; "reason" ], []
        | Operation.WorkComplete -> [ "workItemIds" ], [ "evidence"; "conclusion" ]
        | Operation.TelemetryRecord ->
            [ "metric"; "value" ],
            [ "workItemId"; "unit"; "currency"; "quality"; "confidence"; "scope"; "sourceType"; "sourceName"; "mechanism"; "pricingSource"; "pricingVersion"; "collectedAt"; "step" ]
        | Operation.WorkReconcile -> [ "workItemId"; "reason" ], [ "commits"; "ranges"; "paths" ]
        | Operation.StepStart -> [ "stepId" ], [ "name" ]
        | Operation.StepComplete
        | Operation.StepFail -> [ "stepId" ], [ "reason" ]

    /// The protocol minor version that introduced the operation. A request
    /// may only use operations its own declared version knows about.
    let introducedIn (operation: Operation) =
        match operation with
        | Operation.StepStart
        | Operation.StepComplete
        | Operation.StepFail -> 1
        | _ -> 0

    /// Step operations act on the requester's own execution, which the
    /// request must name.
    let requiresExecution operation =
        match operation with
        | Operation.StepStart
        | Operation.StepComplete
        | Operation.StepFail -> true
        | _ -> false

type ProtocolVersion = { Major: int; Minor: int }

[<RequireQualifiedAccess>]
module ProtocolVersion =
    [<Literal>]
    let Protocol = "praxis.remote"

    let current = { Major = 1; Minor = 1 }

    let code version = $"{version.Major}.{version.Minor}"

    let private pattern = Regex(@"^(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})\z", RegexOptions.CultureInvariant)

    let tryParse (value: string) =
        match pattern.Match value with
        | matched when matched.Success ->
            Some
                { Major = int matched.Groups[1].Value
                  Minor = int matched.Groups[2].Value }
        | _ -> None

    /// Same major version and a minor version the executor already knows:
    /// a newer minor may carry semantics this executor cannot honour, so it
    /// is refused rather than half-executed.
    let isSupportedBy (executor: ProtocolVersion) (requested: ProtocolVersion) =
        requested.Major = executor.Major && requested.Minor <= executor.Minor

/// The actor the *request* asserts. It is self-reported by the requester,
/// never inferred from the host that executes the request.
type RequestActor =
    { Actor: Actor
      SessionId: string option }

type RepositoryBinding =
    { Ref: string option
      ExpectedSha: string option }

type EvidenceArgument = { Type: string; Path: string }

type WorkStartArguments =
    { WorkItemIds: string list
      Type: string option
      Classifications: string list }

type WorkCompleteArguments =
    { WorkItemIds: string list
      Evidence: EvidenceArgument list
      Conclusion: string option }

type TelemetryRecordArguments =
    { WorkItemId: string option
      Metric: string
      Value: decimal
      Unit: string option
      Currency: string option
      Quality: string option
      Confidence: string option
      Scope: string option
      SourceType: string option
      SourceName: string option
      Mechanism: string option
      PricingSource: string option
      PricingVersion: string option
      CollectedAt: string option
      Step: string option }

type WorkReconcileArguments =
    { WorkItemId: string
      Reason: string
      Commits: string list
      Ranges: string list
      Paths: string list }

/// Operation-specific arguments. Each case belongs to exactly one
/// operation (`Arguments.matches`), so a request can never pair an
/// operation with another operation's arguments.
[<RequireQualifiedAccess>]
type Arguments =
    | NoArguments
    | WorkContext of workItemId: string
    | RequestStatus of requestId: string
    | WorkStart of WorkStartArguments
    | WorkResume of workItemIds: string list
    | WorkBlock of workItemIds: string list * reason: string
    | WorkComplete of WorkCompleteArguments
    | TelemetryRecord of TelemetryRecordArguments
    | WorkReconcile of WorkReconcileArguments
    | Step of stepId: string * name: string option * reason: string option

[<RequireQualifiedAccess>]
module Arguments =
    let matches (operation: Operation) (arguments: Arguments) =
        match operation, arguments with
        | (Operation.Describe | Operation.Status | Operation.Validate | Operation.ProvenanceIdentity), Arguments.NoArguments
        | Operation.WorkContext, Arguments.WorkContext _
        | Operation.RequestStatus, Arguments.RequestStatus _
        | Operation.WorkStart, Arguments.WorkStart _
        | Operation.WorkResume, Arguments.WorkResume _
        | Operation.WorkBlock, Arguments.WorkBlock _
        | Operation.WorkComplete, Arguments.WorkComplete _
        | Operation.TelemetryRecord, Arguments.TelemetryRecord _
        | Operation.WorkReconcile, Arguments.WorkReconcile _
        | (Operation.StepStart | Operation.StepComplete | Operation.StepFail), Arguments.Step _ -> true
        | _ -> false

type Request =
    { ProtocolVersion: ProtocolVersion
      RequestId: string
      Operation: Operation
      Repository: RepositoryBinding
      Actor: RequestActor option
      ExecutionId: string option
      Arguments: Arguments
      RequestedAt: DateTimeOffset option }

/// A field-level problem. Messages never echo a rejected value, so a
/// refused secret cannot leak through diagnostics.
type Problem = { Field: string; Message: string }

/// Which side can decide a failure. Praxis-decided failures are known not
/// to have changed state; executor/transport failures may have, which is
/// exactly why they are kept apart.
[<RequireQualifiedAccess>]
type DecidedBy =
    | Praxis
    | Executor

[<RequireQualifiedAccess>]
type Outcome =
    | Succeeded
    | Rejected
    | Failed
    | Unknown

[<RequireQualifiedAccess>]
module Outcome =
    let code outcome =
        match outcome with
        | Outcome.Succeeded -> "succeeded"
        | Outcome.Rejected -> "rejected"
        | Outcome.Failed -> "failed"
        | Outcome.Unknown -> "unknown"

/// Retry guidance that never encourages a blind retry: `SameRequest` is
/// safe only because mutations are idempotent per request ID;
/// `AfterRefresh` means re-read repository state and form a new request.
[<RequireQualifiedAccess>]
type RetryAdvice =
    | Never
    | SameRequest
    | AfterRefresh

[<RequireQualifiedAccess>]
module RetryAdvice =
    let code advice =
        match advice with
        | RetryAdvice.Never -> "never"
        | RetryAdvice.SameRequest -> "same-request"
        | RetryAdvice.AfterRefresh -> "after-refresh"

[<RequireQualifiedAccess>]
type FailureCode =
    | InvalidRequest
    | SecretDetected
    | UnsupportedProtocol
    | UnsupportedOperation
    | Unauthorized
    | IdempotencyConflict
    | StaleRef
    | DomainRejected
    | ValidationFailed
    | ConcurrencyConflict
    | BootstrapFailed
    | RepositoryWriteFailed
    | TransportFailed
    | RateLimited
    | Timeout
    | Cancelled
    | Internal

[<RequireQualifiedAccess>]
module FailureCode =
    let all =
        [ FailureCode.InvalidRequest
          FailureCode.SecretDetected
          FailureCode.UnsupportedProtocol
          FailureCode.UnsupportedOperation
          FailureCode.Unauthorized
          FailureCode.IdempotencyConflict
          FailureCode.StaleRef
          FailureCode.DomainRejected
          FailureCode.ValidationFailed
          FailureCode.ConcurrencyConflict
          FailureCode.BootstrapFailed
          FailureCode.RepositoryWriteFailed
          FailureCode.TransportFailed
          FailureCode.RateLimited
          FailureCode.Timeout
          FailureCode.Cancelled
          FailureCode.Internal ]

    let code failure =
        match failure with
        | FailureCode.InvalidRequest -> "invalid-request"
        | FailureCode.SecretDetected -> "secret-detected"
        | FailureCode.UnsupportedProtocol -> "unsupported-protocol"
        | FailureCode.UnsupportedOperation -> "unsupported-operation"
        | FailureCode.Unauthorized -> "unauthorized"
        | FailureCode.IdempotencyConflict -> "idempotency-conflict"
        | FailureCode.StaleRef -> "stale-ref"
        | FailureCode.DomainRejected -> "domain-rejected"
        | FailureCode.ValidationFailed -> "validation-failed"
        | FailureCode.ConcurrencyConflict -> "concurrency-conflict"
        | FailureCode.BootstrapFailed -> "bootstrap-failed"
        | FailureCode.RepositoryWriteFailed -> "repository-write-failed"
        | FailureCode.TransportFailed -> "transport-failed"
        | FailureCode.RateLimited -> "rate-limited"
        | FailureCode.Timeout -> "timeout"
        | FailureCode.Cancelled -> "cancelled"
        | FailureCode.Internal -> "internal"

    let tryParse (value: string) = all |> List.tryFind (fun failure -> code failure = value)

    let decidedBy failure =
        match failure with
        | FailureCode.InvalidRequest
        | FailureCode.SecretDetected
        | FailureCode.UnsupportedProtocol
        | FailureCode.UnsupportedOperation
        | FailureCode.Unauthorized
        | FailureCode.IdempotencyConflict
        | FailureCode.StaleRef
        | FailureCode.DomainRejected
        | FailureCode.ValidationFailed
        | FailureCode.Internal -> DecidedBy.Praxis
        | FailureCode.ConcurrencyConflict
        | FailureCode.BootstrapFailed
        | FailureCode.RepositoryWriteFailed
        | FailureCode.TransportFailed
        | FailureCode.RateLimited
        | FailureCode.Timeout
        | FailureCode.Cancelled -> DecidedBy.Executor

    /// `Rejected`: Praxis refused before any effect. `Failed`: the attempt
    /// ended and is known to have persisted nothing. `Unknown`: an effect
    /// may have been persisted and has not been confirmed either way; it is
    /// never promoted to success or failure (`DF-ROS-2026-A007`).
    let outcome failure =
        match failure with
        | FailureCode.InvalidRequest
        | FailureCode.SecretDetected
        | FailureCode.UnsupportedProtocol
        | FailureCode.UnsupportedOperation
        | FailureCode.Unauthorized
        | FailureCode.IdempotencyConflict
        | FailureCode.StaleRef
        | FailureCode.DomainRejected -> Outcome.Rejected
        | FailureCode.ValidationFailed
        | FailureCode.ConcurrencyConflict
        | FailureCode.BootstrapFailed
        | FailureCode.Internal -> Outcome.Failed
        | FailureCode.RepositoryWriteFailed
        | FailureCode.TransportFailed
        | FailureCode.RateLimited
        | FailureCode.Timeout
        | FailureCode.Cancelled -> Outcome.Unknown

    let retry failure =
        match failure with
        | FailureCode.InvalidRequest
        | FailureCode.SecretDetected
        | FailureCode.UnsupportedProtocol
        | FailureCode.UnsupportedOperation
        | FailureCode.Unauthorized
        | FailureCode.IdempotencyConflict
        | FailureCode.DomainRejected
        | FailureCode.ValidationFailed -> RetryAdvice.Never
        | FailureCode.StaleRef
        | FailureCode.ConcurrencyConflict -> RetryAdvice.AfterRefresh
        | FailureCode.BootstrapFailed
        | FailureCode.RepositoryWriteFailed
        | FailureCode.TransportFailed
        | FailureCode.RateLimited
        | FailureCode.Timeout
        | FailureCode.Cancelled
        | FailureCode.Internal -> RetryAdvice.SameRequest

type RemoteFailure =
    { Code: FailureCode
      Message: string
      Problems: Problem list }

[<RequireQualifiedAccess>]
module RemoteFailure =
    let create code message problems =
        { Code = code
          Message = message
          Problems = problems }

/// Recognisable credential material. A request carrying any is refused
/// outright (`secret-detected`) and the value is never echoed, so a secret
/// pasted into a reason or path cannot reach events, logs, or artifacts.
[<RequireQualifiedAccess>]
module SecretMaterial =
    let private patterns =
        [ @"gh[pousr]_[A-Za-z0-9]{30,}"
          @"github_pat_[A-Za-z0-9_]{20,}"
          @"sk-[A-Za-z0-9_-]{20,}"
          @"AKIA[0-9A-Z]{16}"
          @"AIza[0-9A-Za-z_-]{35}"
          @"xox[abprs]-[A-Za-z0-9-]{10,}"
          @"-----BEGIN [A-Z ]*PRIVATE KEY-----"
          @"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}" ]
        |> List.map (fun pattern -> Regex(pattern, RegexOptions.CultureInvariant))

    let looksLikeSecret (value: string) =
        patterns |> List.exists (fun pattern -> pattern.IsMatch value)

/// Value rules for untrusted request content. The protocol validates shape
/// and safety only; the *meaning* of a value (is this transition legal, is
/// this metric well-formed) stays with the command that would run, so it is
/// never re-implemented here.
[<RequireQualifiedAccess>]
module RequestValidation =
    let private regex pattern = Regex(pattern, RegexOptions.CultureInvariant)

    let private requestIdPattern = regex @"^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}\z"
    let private workItemIdPattern = regex @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z"
    let private executionIdPattern = regex @"^EXE-[A-Za-z0-9._-]{1,120}\z"
    let private refPattern = regex @"^refs/heads/[A-Za-z0-9._/-]{1,240}\z"
    let private shaPattern = regex @"^(?:[0-9a-f]{40}|[0-9a-f]{64})\z"
    let private revisionPattern = regex @"^[0-9a-f]{7,64}\z"
    let private tokenPattern = regex @"^[A-Za-z0-9][A-Za-z0-9._:/@+-]{0,127}\z"
    let private currencyPattern = regex @"^[A-Z]{3}\z"
    let private controlCharacter = regex @"[\u0000-\u0008\u000B-\u001F\u007F]"

    [<Literal>]
    let MaxTextLength = 2000

    [<Literal>]
    let MaxPathLength = 512

    [<Literal>]
    let MaxWorkItems = 20

    [<Literal>]
    let MaxListLength = 100

    /// Source types that denote Praxis's *own* observation. A requester can
    /// only assert telemetry; it can never claim the executor observed it.
    let executorObservedSourceTypes = set [ "ros-git"; "ros-clock"; "environment" ]

    let private problem field message = { Field = field; Message = message }

    let private checkPattern (pattern: Regex) description field (value: string) =
        if pattern.IsMatch value then [] else [ problem field $"must be {description}" ]

    let isRequestId (value: string) = requestIdPattern.IsMatch value

    let requestId = checkPattern requestIdPattern "8-128 characters of letters, digits, '.', '_', ':' or '-', starting with a letter or digit"
    let workItemId = checkPattern workItemIdPattern "a work-item ID (letters, digits, '.', '_' or '-', starting with a letter or digit)"
    let executionId = checkPattern executionIdPattern "an execution ID of the form EXE-..."
    let token = checkPattern tokenPattern "a short identifier (letters, digits and . _ : / @ + -, not starting with '-')"
    let commitSha = checkPattern shaPattern "a full lowercase hexadecimal commit SHA"
    let revision = checkPattern revisionPattern "a lowercase hexadecimal commit SHA (7-64 characters); symbolic revisions are not accepted remotely"

    let gitRef field (value: string) =
        [ yield! checkPattern refPattern "a branch ref of the form refs/heads/NAME" field value
          if value.Contains ".." || value.Contains "//" || value.EndsWith "/" || value.EndsWith ".lock" then
              yield problem field "must be a well-formed branch ref" ]

    let text field (value: string) =
        [ if value.Trim().Length = 0 then yield problem field "must not be empty"
          if value.Length > MaxTextLength then yield problem field $"must be at most {MaxTextLength} characters"
          if controlCharacter.IsMatch value then yield problem field "must not contain control characters"
          if value.StartsWith "-" then yield problem field "must not start with '-'" ]

    let relativePath field (value: string) =
        let segments = value.Split '/'

        [ if value.Length = 0 || value.Length > MaxPathLength then
              yield problem field $"must be 1-{MaxPathLength} characters"
          if value.StartsWith "/" || value.Contains "\\" || value.Contains ":" then
              yield problem field "must be a repository-relative path with '/' separators"
          if segments |> Array.exists (fun segment -> segment = ".." || segment = "") then
              yield problem field "must not contain '..' or empty segments"
          if value.StartsWith "-" then yield problem field "must not start with '-'"
          if controlCharacter.IsMatch value then yield problem field "must not contain control characters" ]

    let currency = checkPattern currencyPattern "a three-letter uppercase currency code"

    let range field (value: string) =
        match value.Split("..", StringSplitOptions.None) with
        | [| baseRevision; headRevision |] -> revision $"{field}.base" baseRevision @ revision $"{field}.head" headRevision
        | _ -> [ problem field "must be a two-dot range BASE..HEAD of commit SHAs" ]

    let private boundedList field maximum (values: 'a list) =
        if values.Length > maximum then [ problem field $"must contain at most {maximum} entries" ] else []

    let private nonEmptyList field (values: 'a list) =
        if values.IsEmpty then [ problem field "must contain at least one entry" ] else []

    let private each field (check: string -> string -> Problem list) (values: string list) =
        values |> List.mapi (fun index value -> check $"{field}[{index}]" value) |> List.concat

    let private optional field check (value: string option) =
        value |> Option.map (check field) |> Option.defaultValue []

    let private workItemIds field (values: string list) =
        nonEmptyList field values @ boundedList field MaxWorkItems values @ each field workItemId values

    let private actorValue field (value: string) =
        if value = Actor.UnknownValue then [] else token field value

    let actor (requestActor: RequestActor) =
        let value = requestActor.Actor

        [ if ActorKind.code value.Kind |> Seq.exists Char.IsControl then
              yield problem "actor.kind" "must not contain control characters"
          yield! actorValue "actor.id" value.Id
          yield!
              [ "actor.provider", value.Provider; "actor.model", value.Model; "actor.runtime", value.Runtime ]
              |> List.collect (fun (field, text) -> optional field actorValue text)
          yield! optional "actor.sessionId" actorValue requestActor.SessionId
          yield!
              Actor.problems value
              |> List.map (fun (field, message) -> problem $"actor.{field}" message) ]

    let arguments (value: Arguments) =
        match value with
        | Arguments.NoArguments -> []
        | Arguments.WorkContext id -> workItemId "arguments.workItemId" id
        | Arguments.RequestStatus id -> requestId "arguments.requestId" id
        | Arguments.WorkStart start ->
            workItemIds "arguments.workItemIds" start.WorkItemIds
            @ optional "arguments.type" token start.Type
            @ boundedList "arguments.classifications" MaxListLength start.Classifications
            @ each "arguments.classifications" token start.Classifications
        | Arguments.WorkResume ids -> workItemIds "arguments.workItemIds" ids
        | Arguments.WorkBlock(ids, reason) -> workItemIds "arguments.workItemIds" ids @ text "arguments.reason" reason
        | Arguments.WorkComplete complete ->
            workItemIds "arguments.workItemIds" complete.WorkItemIds
            @ boundedList "arguments.evidence" MaxListLength complete.Evidence
            @ (complete.Evidence
               |> List.mapi (fun index evidence ->
                   token $"arguments.evidence[{index}].type" evidence.Type
                   @ relativePath $"arguments.evidence[{index}].path" evidence.Path)
               |> List.concat)
            @ optional "arguments.conclusion" text complete.Conclusion
        | Arguments.TelemetryRecord record ->
            [ yield! optional "arguments.workItemId" workItemId record.WorkItemId
              yield! token "arguments.metric" record.Metric
              if record.Value < 0m then yield problem "arguments.value" "must not be negative"
              yield!
                  [ "arguments.unit", record.Unit
                    "arguments.quality", record.Quality
                    "arguments.confidence", record.Confidence
                    "arguments.scope", record.Scope
                    "arguments.sourceType", record.SourceType
                    "arguments.sourceName", record.SourceName
                    "arguments.mechanism", record.Mechanism
                    "arguments.pricingSource", record.PricingSource
                    "arguments.pricingVersion", record.PricingVersion
                    "arguments.collectedAt", record.CollectedAt
                    "arguments.step", record.Step ]
                  |> List.collect (fun (field, value) -> optional field token value)
              yield! optional "arguments.currency" currency record.Currency
              match record.SourceType with
              | Some sourceType when executorObservedSourceTypes.Contains sourceType ->
                  yield problem "arguments.sourceType" "names a source only the executor can observe; a request can only assert telemetry"
              | _ -> () ]
        | Arguments.Step(stepId, name, reason) ->
            token "arguments.stepId" stepId @ optional "arguments.name" text name @ optional "arguments.reason" text reason
        | Arguments.WorkReconcile reconcile ->
            [ yield! workItemId "arguments.workItemId" reconcile.WorkItemId
              yield! text "arguments.reason" reconcile.Reason
              if reconcile.Commits.IsEmpty && reconcile.Ranges.IsEmpty then
                  yield problem "arguments" "work.reconcile needs at least one commit or range"
              yield! boundedList "arguments.commits" MaxListLength reconcile.Commits
              yield! each "arguments.commits" revision reconcile.Commits
              yield! boundedList "arguments.ranges" MaxListLength reconcile.Ranges
              yield! each "arguments.ranges" range reconcile.Ranges
              yield! boundedList "arguments.paths" MaxListLength reconcile.Paths
              yield! each "arguments.paths" relativePath reconcile.Paths ]

    /// Every string the request carries, with the field it came from, for
    /// the secret-material scan.
    let private strings (request: Request) : (string * string) list =
        let fromActor =
            request.Actor
            |> Option.map (fun requestActor ->
                [ "actor.id", Some requestActor.Actor.Id
                  "actor.provider", requestActor.Actor.Provider
                  "actor.model", requestActor.Actor.Model
                  "actor.runtime", requestActor.Actor.Runtime
                  "actor.sessionId", requestActor.SessionId ])
            |> Option.defaultValue []

        let fromArguments =
            match request.Arguments with
            | Arguments.NoArguments -> []
            | Arguments.WorkContext id -> [ "arguments.workItemId", Some id ]
            | Arguments.RequestStatus id -> [ "arguments.requestId", Some id ]
            | Arguments.WorkStart start ->
                ("arguments.type", start.Type)
                :: (start.WorkItemIds @ start.Classifications |> List.map (fun value -> "arguments", Some value))
            | Arguments.WorkResume ids -> ids |> List.map (fun value -> "arguments.workItemIds", Some value)
            | Arguments.WorkBlock(ids, reason) ->
                ("arguments.reason", Some reason) :: (ids |> List.map (fun value -> "arguments.workItemIds", Some value))
            | Arguments.WorkComplete complete ->
                ("arguments.conclusion", complete.Conclusion)
                :: (complete.Evidence
                    |> List.collect (fun evidence -> [ "arguments.evidence", Some evidence.Type; "arguments.evidence", Some evidence.Path ]))
            | Arguments.TelemetryRecord record ->
                [ "arguments.metric", Some record.Metric
                  "arguments.unit", record.Unit
                  "arguments.sourceName", record.SourceName
                  "arguments.mechanism", record.Mechanism
                  "arguments.pricingSource", record.PricingSource
                  "arguments.pricingVersion", record.PricingVersion
                  "arguments.step", record.Step ]
            | Arguments.Step(stepId, name, reason) ->
                [ "arguments.stepId", Some stepId; "arguments.name", name; "arguments.reason", reason ]
            | Arguments.WorkReconcile reconcile ->
                ("arguments.reason", Some reconcile.Reason)
                :: (reconcile.Paths |> List.map (fun value -> "arguments.paths", Some value))

        [ "requestId", Some request.RequestId
          "repository.ref", request.Repository.Ref
          "execution.id", request.ExecutionId ]
        @ fromActor
        @ fromArguments
        |> List.choose (fun (field, value) -> value |> Option.map (fun text -> field, text))

    let secretFields (request: Request) =
        strings request
        |> List.filter (snd >> SecretMaterial.looksLikeSecret)
        |> List.map fst
        |> List.distinct

    /// Every value problem in the request, in field order.
    let problems (request: Request) : Problem list =
        [ yield! requestId "requestId" request.RequestId
          yield! optional "repository.ref" gitRef request.Repository.Ref
          yield! optional "repository.expectedSha" commitSha request.Repository.ExpectedSha
          yield! optional "execution.id" executionId request.ExecutionId
          yield! request.Actor |> Option.map actor |> Option.defaultValue []
          if Operation.requiresExecution request.Operation && request.ExecutionId.IsNone then
              yield problem "execution.id" $"is required by '{Operation.code request.Operation}'"
          if not (Arguments.matches request.Operation request.Arguments) then
              yield problem "arguments" $"do not match operation '{Operation.code request.Operation}'"
          yield! arguments request.Arguments ]

/// The semantic identity of a request. A retry must reproduce it exactly;
/// the same request ID with a different fingerprint is a different intent
/// and fails closed. `expectedSha`, `requestedAt`, and `x-` extensions are
/// deliberately excluded: a caller whose earlier attempt succeeded but
/// whose result was lost will see the branch moved *by its own commit* and
/// must still be able to recover that outcome by retrying.
[<RequireQualifiedAccess>]
module RequestFingerprint =
    let private field (name: string) (value: string) =
        $"{name.Length}:{name}={value.Length}:{value};"

    let private optionalField name (value: string option) =
        match value with
        | Some text -> field name ("+" + text)
        | None -> field name "-"

    let private listField name (values: string list) =
        field $"{name}#" (string values.Length) + (values |> List.mapi (fun index value -> field $"{name}[{index}]" value) |> String.concat "")

    let private argumentsEncoding (arguments: Arguments) =
        match arguments with
        | Arguments.NoArguments -> field "arguments" "none"
        | Arguments.WorkContext id -> field "workItemId" id
        | Arguments.RequestStatus id -> field "requestId" id
        | Arguments.WorkStart start ->
            listField "workItemIds" start.WorkItemIds
            + optionalField "type" start.Type
            + listField "classifications" start.Classifications
        | Arguments.WorkResume ids -> listField "workItemIds" ids
        | Arguments.WorkBlock(ids, reason) -> listField "workItemIds" ids + field "reason" reason
        | Arguments.WorkComplete complete ->
            listField "workItemIds" complete.WorkItemIds
            + listField "evidence" (complete.Evidence |> List.map (fun evidence -> field "type" evidence.Type + field "path" evidence.Path))
            + optionalField "conclusion" complete.Conclusion
        | Arguments.TelemetryRecord record ->
            optionalField "workItemId" record.WorkItemId
            + field "metric" record.Metric
            + field "value" (record.Value.ToString(Globalization.CultureInfo.InvariantCulture))
            + ([ "unit", record.Unit
                 "currency", record.Currency
                 "quality", record.Quality
                 "confidence", record.Confidence
                 "scope", record.Scope
                 "sourceType", record.SourceType
                 "sourceName", record.SourceName
                 "mechanism", record.Mechanism
                 "pricingSource", record.PricingSource
                 "pricingVersion", record.PricingVersion
                 "collectedAt", record.CollectedAt
                 "step", record.Step ]
               |> List.map (fun (name, value) -> optionalField name value)
               |> String.concat "")
        | Arguments.Step(stepId, name, reason) ->
            field "stepId" stepId + optionalField "name" name + optionalField "reason" reason
        | Arguments.WorkReconcile reconcile ->
            field "workItemId" reconcile.WorkItemId
            + field "reason" reconcile.Reason
            + listField "commits" reconcile.Commits
            + listField "ranges" reconcile.Ranges
            + listField "paths" reconcile.Paths

    let private actorEncoding (requestActor: RequestActor option) =
        match requestActor with
        | None -> field "actor" "none"
        | Some value ->
            field "actor.kind" (ActorKind.code value.Actor.Kind)
            + field "actor.id" value.Actor.Id
            + optionalField "actor.provider" value.Actor.Provider
            + optionalField "actor.model" value.Actor.Model
            + optionalField "actor.runtime" value.Actor.Runtime
            + optionalField "actor.sessionId" value.SessionId

    /// The canonical, unambiguous (length-prefixed) encoding the
    /// fingerprint hashes. Exposed so tests and auditors can inspect it.
    let canonical (request: Request) =
        field "protocol" ProtocolVersion.Protocol
        + field "major" (string request.ProtocolVersion.Major)
        + field "operation" (Operation.code request.Operation)
        + optionalField "ref" request.Repository.Ref
        + actorEncoding request.Actor
        + optionalField "execution" request.ExecutionId
        + argumentsEncoding request.Arguments

    let compute (request: Request) =
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical request))
        "sha256:" + Convert.ToHexString(digest).ToLowerInvariant()

/// Facts the executor supplies from its own trusted environment. None of
/// these may come from the request document: holding a request confers no
/// authority, and a request cannot tell the executor what commit it is on.
type TrustedContext =
    { Principal: string
      Grants: Set<Capability>
      ObservedRef: string option
      ObservedSha: string option }

/// What the request journal already holds for this request ID.
[<RequireQualifiedAccess>]
type JournalLookup =
    | NotRecorded
    | Recorded of fingerprint: string

[<RequireQualifiedAccess>]
type Decision =
    | Reject of RemoteFailure
    | Replay
    | Execute

/// The protocol's pure decision (`DF-ROS-2026-A041` section 4, steps 4-8;
/// steps 1-3 happen while parsing the document). Order matters and is part
/// of the contract:
///
/// 1. value validation and secret material (nothing is executed or
///    journalled for a malformed request);
/// 2. a mutation must name its ref and expected SHA;
/// 3. authorization precedes replay, so an unauthorized caller cannot read
///    another request's recorded outcome by guessing its ID;
/// 4. replay precedes the stale-ref check, so a caller whose success was
///    lost in transit recovers it even though its own commit moved the ref;
/// 5. only a new, authorized, current mutation executes.
[<RequireQualifiedAccess>]
module RequestDecision =
    let private reject code message problems =
        Decision.Reject(RemoteFailure.create code message problems)

    let decide (context: TrustedContext) (journal: JournalLookup) (request: Request) : Decision =
        let mutating = Operation.isMutating request.Operation
        let capability = Operation.capability request.Operation

        match RequestValidation.secretFields request, RequestValidation.problems request with
        | (_ :: _) as fields, _ ->
            reject
                FailureCode.SecretDetected
                "the request contains what looks like credential material; it was refused and not recorded"
                (fields |> List.map (fun field -> { Field = field; Message = "looks like credential material" }))
        | [], (_ :: _ as problems) -> reject FailureCode.InvalidRequest "the request is invalid" problems
        | [], [] ->
            match mutating, request.Repository.Ref, request.Repository.ExpectedSha with
            | true, None, _
            | true, _, None ->
                reject
                    FailureCode.InvalidRequest
                    "a mutating request must name repository.ref and repository.expectedSha"
                    [ { Field = "repository"; Message = "ref and expectedSha are required for a mutating operation" } ]
            | _ when not (context.Grants.Contains capability) ->
                reject
                    FailureCode.Unauthorized
                    $"the caller is not granted the '{Capability.code capability}' capability required by '{Operation.code request.Operation}'"
                    []
            | _ ->
                match mutating, journal with
                | true, JournalLookup.Recorded fingerprint when fingerprint = RequestFingerprint.compute request -> Decision.Replay
                | true, JournalLookup.Recorded _ ->
                    reject
                        FailureCode.IdempotencyConflict
                        "this request ID was already used for a different request; use a new request ID for a new intent"
                        []
                | true, JournalLookup.NotRecorded when context.ObservedRef <> request.Repository.Ref ->
                    reject
                        FailureCode.StaleRef
                        "the executor is not on the ref the request names"
                        [ { Field = "repository.ref"; Message = "does not match the executor's checked-out ref" } ]
                | true, JournalLookup.NotRecorded when context.ObservedSha <> request.Repository.ExpectedSha ->
                    reject
                        FailureCode.StaleRef
                        "the ref has moved since the request was formed; re-read the repository state and form a new request"
                        [ { Field = "repository.expectedSha"; Message = "does not match the observed commit" } ]
                | _ -> Decision.Execute

/// What the executor itself observed about where and how a remote request
/// ran (`DF-ROS-2026-A041` section 7). These are facts about the *runner*,
/// kept apart from the *actor* the request asserts, so that a CI runner
/// executing an agent's request can never become the apparent author of
/// that agent's work. `Kind` names the executor family (for example
/// `github-actions`); nothing in the Domain interprets it.
type ExecutorFacts =
    { Kind: string
      RunId: string option
      RunAttempt: string option
      WorkflowRef: string option
      Repository: string option
      Host: string option
      Principal: string option
      PraxisVersion: string }

/// The structured response. `Result` is the executing command's own
/// machine-readable output, carried verbatim.
type Response =
    { ProtocolVersion: ProtocolVersion
      RequestId: string option
      Operation: string option
      Outcome: Outcome
      Failure: RemoteFailure option
      Replayed: bool
      Repository: RepositoryBinding
      ObservedSha: string option
      PraxisVersion: string
      Executor: ExecutorFacts option
      Persistence: string list
      Result: string option }

[<RequireQualifiedAccess>]
module Response =
    let rejected (version: ProtocolVersion) praxisVersion requestId operation (repository: RepositoryBinding) observedSha (failure: RemoteFailure) =
        { ProtocolVersion = version
          RequestId = requestId
          Operation = operation
          Outcome = FailureCode.outcome failure.Code
          Failure = Some failure
          Replayed = false
          Repository = repository
          ObservedSha = observedSha
          PraxisVersion = praxisVersion
          Executor = None
          Persistence = []
          Result = None }

    let succeeded praxisVersion (request: Request) observedSha result =
        { ProtocolVersion = request.ProtocolVersion
          RequestId = Some request.RequestId
          Operation = Some(Operation.code request.Operation)
          Outcome = Outcome.Succeeded
          Failure = None
          Replayed = false
          Repository = request.Repository
          ObservedSha = observedSha
          PraxisVersion = praxisVersion
          Executor = None
          Persistence = []
          Result = result }

    /// A replay returns the recorded response unchanged except for the
    /// flag that tells the caller it did not execute again.
    let replayed (recorded: Response) = { recorded with Replayed = true }
