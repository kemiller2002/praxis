namespace Praxis.Infrastructure.Work

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// F4: the admitted-contract cache (`<store>/cache/ordo/<digest-hex>.json`,
/// I8). An entry is the canonical `ecir/1` form of a contract Praxis
/// admitted, named by its Ordo digest. A hit — the host authority pins a
/// digest the cache holds — skips reading and admitting the source contract
/// file. The entry is content-addressed: it is admitted again from its
/// canonical form under the same pin, so a tampered entry can only fail
/// (stale digest), never widen what binds; REL-GATE-002 readiness is always
/// recomputed from it.
[<RequireQualifiedAccess>]
module OrdoContractCache =
    let directory (root: string) = Path.Combine(TransientStore.cache root, "ordo")

    let entry (root: string) (digest: string) =
        Path.Combine(directory root, (if digest.StartsWith "sha256:" then digest.Substring 7 else digest) + ".json")

    /// `hit` or `miss`, for the `timings` report.
    let bind (root: string) (contract: string option) (expectedDigest: string option) (input: CompletionAuthorityInput) =
        let pin =
            expectedDigest
            |> Option.orElse (Some input.OrdoContractDigest |> Option.filter (String.IsNullOrWhiteSpace >> not))
            |> Option.filter CompletionGate.isSha256Digest

        let cached = pin |> Option.map (entry root) |> Option.filter File.Exists

        match cached with
        | Some path ->
            match OrdoBoundAuthority.bind pin (File.ReadAllText path) input with
            | Ok bound -> Ok(bound, "hit")
            | Error findings -> Error findings
        | None ->
            FileWorkClaims.bindOrdoContract root contract expectedDigest input
            |> Result.map (fun bound ->
                let view = Praxis.Domain.Ordo.OrdoContractAdmission.readyScopeView (BoundCompletionAuthority.scope bound)
                let file = contract |> Option.map (fun path -> if Path.IsPathRooted path then path else Path.Combine(root, path))

                try
                    match file |> Option.map (File.ReadAllText >> Praxis.Domain.Ordo.OrdoContractAdmission.canonicalForm) with
                    | Some(Ok canonical) when not (File.Exists(entry root view.Contract.Digest)) ->
                        TransientStore.publish (entry root view.Contract.Digest) (UTF8Encoding(false).GetBytes canonical)
                    | _ -> ()
                with _ ->
                    ()

                bound, "miss")

/// `praxis.validation-result/1` documents in the I8 store, as F4 candidates.
[<RequireQualifiedAccess>]
module FileValidationCandidates =
    let read (root: string) (workItemId: string) : Result<EvidenceCandidate list, string> =
        try
            let folder = Path.Combine(TransientStore.validations root, workItemId)

            if not (Directory.Exists folder) then
                Ok []
            else
                Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
                |> Array.choose (fun path ->
                    match ValidationResultJson.observe (File.ReadAllBytes path) with
                    | EvidenceObservation.Observed(digest, result) when result.WorkItemId = workItemId ->
                        Some
                            { Id = result.EvidenceId
                              Path = path
                              Digest = digest
                              Validation = result.Validation
                              Status = result.Status
                              Commit = result.Revision
                              Generation = result.Generation
                              ObservedAt = result.ObservedAt
                              Tiebreak = path }
                    | _ -> None)
                |> Array.toList
                |> Ok
        with error ->
            Error error.Message

/// I8 step 3-6: the canonical tracked completion event and the
/// metadata-only commit whose parent is the verified commit.
[<RequireQualifiedAccess>]
module CompletionMetadataCommit =
    let EventsPath = ".ros/events/events.jsonl"

    let private git (root: string) (arguments: string list) =
        match ClaimProcess.run "git" root [ "GIT_TERMINAL_PROMPT", "0" ] None FileWorkClaims.gitTimeout arguments with
        | Ok result when result.ExitCode = 0 -> Ok(result.Stdout.Trim())
        | Ok result -> Error $"""git {arguments |> List.truncate 2 |> String.concat " "} failed: {(result.Stderr + result.Stdout).Trim()}"""
        | Error _ -> Error "git is unavailable"

    /// The tracked `work.completion.verified` event. It carries the verified
    /// commit, the evidence (receipt) digests and the packet digest, and
    /// never the claim token: it is committed to history.
    let eventNode (root: string) (actor: Actor option) (execution: string option) (claim: WorkClaim) (completion: VerifiedCompletion) =
        let holder = claim.Holder.Value
        let repository = FileWorkConfigRepository.readRepositoryId root
        let protocolVersion = FileWorkConfigRepository.readProtocolVersion root

        let event =
            { WorkItemId = claim.WorkItemId
              OccurredAt = completion.CompletedAt
              Payload = ClaimEventPayload.CompletionVerified(holder.Claimant, holder.ClaimToken, claim.Generation, completion) }

        let node = WorkClaimJson.eventNode repository protocolVersion execution actor event
        node.Remove "eventId" |> ignore
        node.Remove "claimToken" |> ignore
        node["verifiedCommit"] <- JsonValue.Create completion.Commit
        let digests = JsonArray()
        completion.EvidenceDigests |> List.iter (fun digest -> digests.Add(JsonValue.Create digest))
        node["receiptDigests"] <- digests
        node["eventId"] <- JsonValue.Create(Praxis.Infrastructure.Json.CanonicalJson.sha256HexPrefix 24 (Praxis.Infrastructure.Json.CanonicalJson.serializeCompact node))
        node

    let private outside (paths: string list) =
        paths |> List.filter (fun path -> not (path.StartsWith(".ros/", StringComparison.Ordinal)))

    /// Idempotent: an event already in the tracked log is not published again
    /// (the result is the last commit that touched the log).
    let publish (root: string) (actor: Actor option) (execution: string option) (claim: WorkClaim) (completion: VerifiedCompletion) : Result<string, string> =
        let node = eventNode root actor execution claim completion
        let eventId = node["eventId"].GetValue<string>()

        if FileWorkClaimEvents.trackedContains root eventId then
            git root [ "log"; "-n"; "1"; "--format=%H"; "--"; EventsPath ]
        else
            let path = Path.Combine(root, EventsPath)
            let before = if File.Exists path then Some(File.ReadAllBytes path) else None

            let restore (headBefore: string) =
                git root [ "reset"; "-q"; headBefore ] |> ignore

                match before with
                | Some bytes -> File.WriteAllBytes(path, bytes)
                | None -> if File.Exists path then File.Delete path

            let identity =
                match git root [ "config"; "user.email" ] with
                | Ok email when email <> "" -> []
                | _ -> [ "-c"; "user.name=Praxis completion"; "-c"; "user.email=completion@praxis.invalid" ]

            match git root [ "rev-parse"; "--verify"; "HEAD^{commit}" ], FileWorkVerification.worktreeStatus root () with
            | Error message, _
            | _, Error message -> Error message
            | Ok head, _ when head <> completion.Commit -> Error $"HEAD is {head}, not the verified commit {completion.Commit}"
            | Ok _, Ok((_ :: _) as dirty) -> Error $"""the worktree is not clean ({dirty |> List.truncate 5 |> String.concat ", "})"""
            | Ok head, Ok [] ->
                let message = $"praxis: verified completion of {claim.WorkItemId} at {completion.Commit.Substring(0, 12)}"

                let committed =
                    FileWorkClaimEvents.appendTracked root node
                    |> Result.bind (fun () -> git root [ "add"; "--"; EventsPath ])
                    |> Result.bind (fun _ -> git root (identity @ [ "commit"; "--no-verify"; "--no-gpg-sign"; "-q"; "-m"; message; "--only"; "--"; EventsPath ]))
                    |> Result.bind (fun _ -> git root [ "rev-parse"; "HEAD" ])
                    |> Result.bind (fun metadata ->
                        git root [ "rev-parse"; "HEAD^" ]
                        |> Result.bind (fun parent ->
                            git root [ "diff"; "--name-only"; head; metadata ]
                            |> Result.bind (fun changed ->
                                let paths = changed.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

                                let source = outside paths

                                if parent <> head then
                                    Error $"the metadata commit's parent is {parent}, not {head}"
                                elif not source.IsEmpty then
                                    Error $"""the metadata commit touched source paths {source |> String.concat ", "}"""
                                else
                                    match FileWorkVerification.worktreeStatus root () with
                                    | Ok [] -> Ok metadata
                                    | Ok dirty -> Error $"""the worktree is not clean after the metadata commit ({dirty |> List.truncate 5 |> String.concat ", "})"""
                                    | Error message -> Error message)))

                match committed with
                | Ok metadata -> Ok metadata
                | Error message ->
                    (try restore head with _ -> ())
                    Error message

/// `praxis work completion-identity --id ID` (I8 identity check).
type CompletionIdentityReport =
    { WorkItemId: string
      VerifiedCommit: string
      Head: string
      ChangedPaths: string list
      SourcePaths: string list
      Clean: bool }

[<RequireQualifiedAccess>]
module CompletionIdentity =
    let ViolatedCode = "PRX.COMPLETION.IDENTITY_VIOLATED"
    let NotCompletedCode = "PRX.COMPLETION.NOT_COMPLETED"

    let check (root: string) (choice: ClaimStoreChoice) (workItemId: string) : Result<CompletionIdentityReport, string * string> =
        let git arguments =
            match ClaimProcess.run "git" root [] None FileWorkClaims.gitTimeout arguments with
            | Ok result when result.ExitCode = 0 -> Ok(result.Stdout.Trim())
            | Ok result -> Error("PRX.COMPLETION.IDENTITY_UNAVAILABLE", result.Stderr.Trim())
            | Error _ -> Error("PRX.COMPLETION.IDENTITY_UNAVAILABLE", "git is unavailable")

        match (FileWorkClaims.store root choice).Read workItemId with
        | Error message -> Error("PRX.COMPLETION.IDENTITY_UNAVAILABLE", message)
        | Ok(Some { Completion = Some completion }) ->
            git [ "rev-parse"; "--verify"; "HEAD^{commit}" ]
            |> Result.bind (fun head ->
                git [ "diff"; "--name-only"; $"{completion.Commit}..HEAD" ]
                |> Result.bind (fun changed ->
                    let paths = changed.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

                    FileWorkVerification.worktreeStatus root ()
                    |> Result.mapError (fun message -> "PRX.COMPLETION.IDENTITY_UNAVAILABLE", message)
                    |> Result.map (fun status ->
                        { WorkItemId = workItemId
                          VerifiedCommit = completion.Commit
                          Head = head
                          ChangedPaths = paths
                          SourcePaths = paths |> List.filter (fun path -> not (path.StartsWith(".ros/", StringComparison.Ordinal)))
                          Clean = status.IsEmpty })))
        | Ok _ -> Error(NotCompletedCode, $"'{workItemId}' has no verified completion")

/// The arguments `complete-verified` was given (CLI-parsed, not derived).
type CompletionArguments =
    { WorkItemId: string
      Token: string
      AuthorityPath: string option
      ContractPath: string option
      ExpectedOrdoDigest: string option
      Explicit: ExplicitCompletion
      ExplicitEvidence: EvidenceSubmission list
      Timeout: TimeSpan
      Execution: string option }

type DerivedCompletionReport =
    { Result: DerivedCompletionResult
      Timings: (string * int64) list
      AdmissionCache: string option
      /// When the report was produced (for the claim's effective status).
      ReportedAt: DateTimeOffset }

/// F4 composition of `praxis work complete-verified`.
[<RequireQualifiedAccess>]
module FileDerivedCompletion =
    let AuthorityVariable = "PRAXIS_COMPLETION_AUTHORITY"
    let ContractVariable = "PRAXIS_ORDO_CONTRACT"

    let private environment name = Environment.GetEnvironmentVariable(name: string) |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private full (root: string) (path: string) = Path.GetFullPath(if Path.IsPathRooted path then path else Path.Combine(root, path))

    /// The explicit flag, the environment, or both when they agree.
    let private choose (root: string) flag (explicit: string option) (variable: string) =
        match explicit, environment variable with
        | Some given, Some derived when full root given <> full root derived ->
            Error { Code = DerivedCompletion.MismatchCode; Detail = $"{flag} '{given}' differs from {variable} '{derived}'" }
        | Some given, _ -> Ok(Some given)
        | None, derived -> Ok derived

    /// What Praxis observes of an explicitly cited evidence file.
    let private observeExplicit (root: string) (submission: EvidenceSubmission) =
        match FileWorkClaims.observeEvidence root submission with
        | EvidenceObservation.Observed(digest, result) ->
            Some
                { Id = submission.Id; Path = submission.Path; Digest = digest; Validation = result.Validation; Status = result.Status
                  Commit = result.Revision; Generation = result.Generation; ObservedAt = result.ObservedAt; Tiebreak = submission.Path }
        | EvidenceObservation.Enveloped(digest, envelope) ->
            VerificationReceipt.parsePayload envelope.Payload
            |> Result.toOption
            |> Option.map (fun payload ->
                { Id = submission.Id; Path = submission.Path; Digest = digest; Validation = payload.Validation; Status = payload.Status
                  Commit = payload.Commit; Generation = payload.FencingGeneration; ObservedAt = payload.ObservedAt; Tiebreak = payload.ReceiptId })
        | _ -> None

    /// `Error(Choice1Of2 message)`: invalid arguments (exit 2).
    let run
        (root: string)
        (choice: ClaimStoreChoice)
        (actor: Actor option)
        (verifier: CompletionVerifier option)
        (arguments: CompletionArguments)
        : Result<DerivedCompletionReport, string> =
        let clock = Stopwatch.StartNew()
        let timings = Collections.Generic.List<string * int64>()
        let mutable last = 0L

        let mark name =
            let now = clock.ElapsedMilliseconds
            timings.Add(name, now - last)
            last <- now

        let report result cache =
            Ok { Result = result; Timings = List.ofSeq timings; AdmissionCache = cache; ReportedAt = DateTimeOffset.UtcNow }
        let rejected findings = { Outcome = CompletionOutcome.Rejected(findings, None); VerifiedCommit = None; MetadataCommit = None }

        match choose root "--authority" arguments.AuthorityPath AuthorityVariable, choose root "--ordo-contract" arguments.ContractPath ContractVariable with
        | Error finding, _
        | _, Error finding -> report (rejected [ finding ]) None
        | Ok None, _ -> Error $"--authority FILE (or {AuthorityVariable}) is required"
        | Ok(Some authorityPath), Ok contract ->
            match FileWorkClaims.readAuthorityInput root authorityPath with
            | Error message -> Error message
            | Ok input ->
                match OrdoContractCache.bind root contract arguments.ExpectedOrdoDigest input with
                | Error findings ->
                    mark "admission"
                    report (rejected findings) None
                | Ok(bound, cache) ->
                    mark "admission"
                    let attested = BoundCompletionAuthority.isAttested bound
                    let basePorts = FileWorkClaims.completionPorts root choice actor arguments.Execution arguments.Timeout

                    let ports =
                        { Completion = { basePorts with Verifier = verifier |> Option.defaultValue basePorts.Verifier }
                          WorktreeStatus = FileWorkVerification.worktreeStatus root
                          Candidates = if attested then FileWorkVerification.candidates root else FileValidationCandidates.read root
                          Publish = CompletionMetadataCommit.publish root actor arguments.Execution
                          Mark = mark }

                    let explicit =
                        { arguments.Explicit with Evidence = arguments.ExplicitEvidence |> List.map (fun submission -> submission, observeExplicit root submission) }

                    report (DerivedCompletion.complete ports bound arguments.WorkItemId arguments.Token explicit) (Some cache)
