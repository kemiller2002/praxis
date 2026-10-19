namespace Praxis.Application.Work

open System
open System.Text
open Praxis.Domain.Work

/// `praxis work validate-run` (PRAXIS-REL-005): one validation run for the
/// current holder of a claim.
type ValidationRunRequest =
    { WorkItemId: string
      Fence: ClaimFence
      Validation: string
      EvidenceId: string
      Command: string list
      Timeout: TimeSpan }

/// What running the validation command produced. `output` is stdout
/// followed by stderr.
[<RequireQualifiedAccess>]
type CommandRun =
    | Exited of exitCode: int * output: string
    | TimedOut of after: TimeSpan
    | NotStarted of reason: string

/// Where a result was durably written, and the SHA-256 of the exact result
/// document bytes (the digest the I1 event records).
type RecordedResult =
    { ResultPath: string
      OutputPath: string
      ResultDigest: string }

type ValidationRunPorts =
    { Claims: WorkClaimPorts
      /// The commit Praxis observes as checked out (Git HEAD).
      ObserveHead: unit -> Result<string, string>
      /// Tracked paths outside `.ros/` and `.praxis/` with uncommitted changes.
      DirtyPaths: unit -> Result<string list, string>
      RunCommand: string list -> TimeSpan -> CommandRun
      /// Writes the output and the result document.
      Record: ValidationResult -> string -> Result<RecordedResult, string> }

[<RequireQualifiedAccess>]
type ValidationRunOutcome =
    | Observed of result: ValidationResult * recorded: RecordedResult
    | Refused of finding: CompletionFinding * current: WorkClaim option
    | Failed of reason: string

[<RequireQualifiedAccess>]
module ValidationRuns =
    let private refused code detail current =
        ValidationRunOutcome.Refused({ Code = code; Detail = detail }, current)

    let private observed (result: ValidationResult) (recorded: RecordedResult) : ClaimEvent =
        { WorkItemId = result.WorkItemId
          OccurredAt = result.ObservedAt
          Payload =
            ClaimEventPayload.ValidationObserved(
                result.Claimant,
                result.ClaimToken,
                result.Generation,
                result.Validation,
                result.EvidenceId,
                result.Revision,
                result.Status,
                result.ObservedAt,
                recorded.ResultDigest
            ) }

    let private execute (ports: ValidationRunPorts) (request: ValidationRunRequest) (head: string) =
        let startedAt = ports.Claims.Now()
        let run = ports.RunCommand request.Command request.Timeout
        let observedAt = ports.Claims.Now()

        let status, exitCode, output =
            match run with
            | CommandRun.Exited(0, output) -> CheckStatus.Passed, Some 0, output
            | CommandRun.Exited(code, output) -> CheckStatus.Failed, Some code, output
            | CommandRun.TimedOut after -> CheckStatus.Unavailable, None, $"timed out after {after.TotalSeconds:F0}s\n"
            | CommandRun.NotStarted reason -> CheckStatus.Unavailable, None, $"did not start: {reason}\n"

        let result =
            { ValidationResult.WorkItemId = request.WorkItemId
              Claimant = request.Fence.Claimant
              ClaimToken = request.Fence.ClaimToken
              Generation = request.Fence.Generation
              Validation = request.Validation
              EvidenceId = request.EvidenceId
              Revision = head
              ObservedAt = observedAt
              StartedAt = startedAt
              Status = status
              ExitCode = exitCode
              Command = request.Command
              OutputSha256 = CompletionGate.sha256 (UTF8Encoding(false).GetBytes output) }

        match ports.Record result output with
        | Error message -> ValidationRunOutcome.Failed $"the validation ran but its result could not be written: {message}"
        | Ok recorded ->
            match ports.Claims.Emit(observed result recorded) with
            | Ok() -> ValidationRunOutcome.Observed(result, recorded)
            | Error message ->
                ValidationRunOutcome.Failed
                    $"the result is written to {recorded.ResultPath} but its {ValidationEvidence.ObservedEventType} event was not appended ({message}); it is not evidence until recorded, so run the validation again"

    /// Fence-checks the caller, requires a clean worktree at an observed
    /// HEAD, runs the command with a timeout, writes the result and records
    /// its digest in a `work.validation.observed` event.
    let run (ports: ValidationRunPorts) (request: ValidationRunRequest) =
        if not (ValidationEvidence.isSafeName request.Validation && ValidationEvidence.isSafeName request.EvidenceId) then
            refused "PRX.CLAIM.INVALID_REQUEST" "--validation and --evidence-id must be 1-128 letters, digits, '.', '-' or '_'" None
        elif List.isEmpty request.Command then
            refused "PRX.CLAIM.INVALID_REQUEST" "a command to run is required after '--'" None
        else
            match ports.Claims.Store.Read request.WorkItemId with
            | Error message -> ValidationRunOutcome.Failed message
            | Ok current ->
                match WorkClaim.fence (ports.Claims.Now()) request.Fence current with
                | Error conflict -> refused (ClaimConflict.code conflict) (ClaimConflict.detail conflict) current
                | Ok _ ->
                    match ports.ObserveHead(), ports.DirtyPaths() with
                    | Error message, _
                    | _, Error message -> ValidationRunOutcome.Failed message
                    | Ok _, Ok((_ :: _) as dirty) ->
                        let shown = dirty |> List.truncate 10 |> String.concat ", "

                        refused
                            ValidationEvidence.DirtyWorktreeCode
                            $"tracked files differ from HEAD ({shown}); commit or discard them so the result names the revision it ran at"
                            current
                    | Ok head, Ok [] -> execute ports request head
