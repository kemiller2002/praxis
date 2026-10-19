namespace Praxis.Domain.Work

open System

[<RequireQualifiedAccess>]
type CheckStatus =
    | Passed
    | Failed
    | Unavailable

[<RequireQualifiedAccess>]
module CheckStatus =
    let code status =
        match status with
        | CheckStatus.Passed -> "passed"
        | CheckStatus.Failed -> "failed"
        | CheckStatus.Unavailable -> "unavailable"

    let tryParse text =
        [ CheckStatus.Passed; CheckStatus.Failed; CheckStatus.Unavailable ] |> List.tryFind (fun status -> code status = text)

type CompletionFinding = { Code: string; Detail: string }

/// `praxis.validation-result/1` (PRAXIS-REL-005): one check that Praxis ran
/// itself (`work validate-run`) for the holder of a claim, at an observed
/// clean revision. Completion evidence must be such a result, recorded by a
/// `work.validation.observed` event; file existence alone proves nothing.
type ValidationResult =
    { WorkItemId: string
      Claimant: string
      ClaimToken: string
      Generation: int64
      Validation: string
      EvidenceId: string
      Revision: string
      ObservedAt: DateTimeOffset
      StartedAt: DateTimeOffset
      Status: CheckStatus
      /// `None` when the command timed out or never started.
      ExitCode: int option
      Command: string list
      OutputSha256: string }

/// A `work.validation.observed` event read back from the event log.
type RecordedValidation =
    { WorkItemId: string
      ClaimToken: string
      Generation: int64
      ResultDigest: string }

/// Pure binding checks between a cited validation result and the completion
/// request that cites it. Every refusal is a stable `PRX.COMPLETION.*` code.
[<RequireQualifiedAccess>]
module ValidationEvidence =
    let SchemaVersion = "praxis.validation-result/1"
    let ObservedEventType = "work.validation.observed"

    let UnrecognizedCode = "PRX.COMPLETION.EVIDENCE_UNRECOGNIZED"
    let UnrecordedCode = "PRX.COMPLETION.EVIDENCE_UNRECORDED"
    let ForeignClaimCode = "PRX.COMPLETION.EVIDENCE_FOREIGN_CLAIM"
    let RevisionMismatchCode = "PRX.COMPLETION.EVIDENCE_REVISION_MISMATCH"
    let NotPassedCode = "PRX.COMPLETION.EVIDENCE_NOT_PASSED"
    let StaleCode = "PRX.COMPLETION.EVIDENCE_STALE"
    let ValidationMismatchCode = "PRX.COMPLETION.EVIDENCE_VALIDATION_MISMATCH"
    let DirtyWorktreeCode = "PRX.VALIDATION.DIRTY_WORKTREE"

    /// Evidence and validation names become path segments; keep them plain.
    let isSafeName (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 128
        && value <> "."
        && value <> ".."
        && value |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = '.')

    let private stamp (value: DateTimeOffset) =
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Globalization.CultureInfo.InvariantCulture)

    let private finding code detail = { Code = code; Detail = detail }

    /// Every refusal for evidence `evidenceId` (digest `digest`, parsed as
    /// `result`) cited by `citedBy` validation names, against the current
    /// claim `holder`, the presented fence and the verification revision.
    let check
        (workItemId: string)
        (fence: ClaimFence)
        (holder: ClaimHolder option)
        (verificationRevision: string)
        (recorded: Result<RecordedValidation list, string>)
        (evidenceId: string)
        (citedBy: string list)
        (digest: string)
        (result: ValidationResult)
        =
        [ if result.WorkItemId <> workItemId || result.ClaimToken <> fence.ClaimToken || result.Generation <> fence.Generation then
              yield
                  finding
                      ForeignClaimCode
                      $"evidence '{evidenceId}' was observed for {result.WorkItemId} generation {result.Generation}, not this claim ({workItemId} generation {fence.Generation} with the presented token); run 'praxis work validate-run' under the current claim"

          match recorded with
          | Error reason -> yield finding UnrecordedCode $"evidence '{evidenceId}': the event log cannot be read to confirm the result ({reason})"
          | Ok events ->
              let matches (event: RecordedValidation) =
                  event.WorkItemId = workItemId
                  && event.ClaimToken = fence.ClaimToken
                  && event.Generation = fence.Generation
                  && event.ResultDigest = digest

              if not (events |> List.exists matches) then
                  yield
                      finding
                          UnrecordedCode
                          $"evidence '{evidenceId}' ({digest}) is not recorded by a {ObservedEventType} event for this claim; only results produced by 'praxis work validate-run' are evidence"

          if result.Revision <> verificationRevision then
              yield
                  finding
                      RevisionMismatchCode
                      $"evidence '{evidenceId}' ran at {result.Revision}, not the verification revision {verificationRevision}; re-run the validation at that revision"

          if result.Status <> CheckStatus.Passed then
              yield finding NotPassedCode $"evidence '{evidenceId}' records a {CheckStatus.code result.Status} run of '{result.Validation}' (exit {result.ExitCode})"

          match holder with
          | Some holder when result.ObservedAt >= holder.AcquiredAt && result.ObservedAt < holder.LeaseExpiresAt -> ()
          | Some holder ->
              yield
                  finding
                      StaleCode
                      $"evidence '{evidenceId}' was observed at {stamp result.ObservedAt}, outside the claim's lease window [{stamp holder.AcquiredAt}, {stamp holder.LeaseExpiresAt})"
          | None -> yield finding StaleCode $"evidence '{evidenceId}' cannot be placed in a lease window: the claim has no holder"

          if result.EvidenceId <> evidenceId then
              yield
                  finding
                      ValidationMismatchCode
                      $"evidence '{evidenceId}' is a result recorded for evidence '{result.EvidenceId}'"

          for name in citedBy |> List.distinct do
              if name <> result.Validation then
                  yield
                      finding
                          ValidationMismatchCode
                          $"validation '{name}' cites evidence '{evidenceId}', which is a result of validation '{result.Validation}'" ]

/// INVARIANT (PRAXIS-REL-005): passing completion evidence can only come
/// from `ValidatedEvidence.establish`. The constructor is private, so no
/// other code — in this assembly or any other — can build one; the only
/// way to obtain a value is a `praxis.validation-result/1` that passed every
/// binding check above (recorded digest, same item/token/generation,
/// verification revision, lease window, passed status, validation name).
/// `CompletionGate.packet` and `CompletionGate.authority` accept only these
/// values, and the Dokimos authority's evidence observations *are* these
/// values, so a `Passed` observation built from a raw file, a failed or
/// unavailable result, or an unrecorded document does not type-check.
/// Failed and unavailable results stay representable for reporting as a
/// plain `ValidationResult` (inside `EvidenceObservation.Observed`), which
/// the builders do not accept.
[<Sealed>]
type ValidatedEvidence private (id: string, digest: string, result: ValidationResult) =
    /// The evidence ID it is cited as.
    member _.Id = id
    /// SHA-256 of the exact result document bytes Praxis read.
    member _.Digest = digest
    member _.Result = result
    member _.Revision = result.Revision
    /// Always `Passed`: `establish` refuses anything else.
    member _.Status = result.Status
    member _.ObservedAt = result.ObservedAt
    member _.Validation = result.Validation
    /// Praxis never asserts reuse across claims.
    member _.Reusable = false

    /// The only constructor. The error side carries the stable
    /// `PRX.COMPLETION.EVIDENCE_*` refusal codes.
    static member establish
        (workItemId: string)
        (fence: ClaimFence)
        (holder: ClaimHolder option)
        (verificationRevision: string)
        (recorded: Result<RecordedValidation list, string>)
        (evidenceId: string)
        (citedBy: string list)
        (digest: string)
        (result: ValidationResult)
        : Result<ValidatedEvidence, CompletionFinding list> =
        match ValidationEvidence.check workItemId fence holder verificationRevision recorded evidenceId citedBy digest result with
        | [] when result.Status = CheckStatus.Passed -> Ok(ValidatedEvidence(evidenceId, digest, result))
        | [] -> Error [ { Code = ValidationEvidence.NotPassedCode; Detail = $"evidence '{evidenceId}' did not pass" } ]
        | findings -> Error findings
