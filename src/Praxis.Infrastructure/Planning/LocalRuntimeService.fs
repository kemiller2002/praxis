namespace Praxis.Infrastructure.Planning

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// Malformed input exits 2; a refused operation exits 3.
[<RequireQualifiedAccess>]
type LocalCommandFailure = Invalid of string | Refused of string

type LocalGrantDraftRequest =
    { KeyId: string; Approver: string; GroupId: string; CohortId: string; Source: string
      Manifest: string; Blueprint: string; Members: Map<string, string>; Decisions: string list
      /// CONSTRUCT-008: an `ordo.construction-decisions/1` file whose digest
      /// the grant binds. Omitted for grants without construction decisions.
      DecisionRecordsPath: string option
      ValidMinutes: int; DraftPath: string; PayloadPath: string }

/// File, clock and process effects behind `praxis local`. The CLI parses
/// arguments and prints; every operation here returns the lines to print.
[<RequireQualifiedAccess>]
module LocalRuntimeService =
    let private invalid result = result |> Result.mapError LocalCommandFailure.Invalid
    let private refused result = result |> Result.mapError LocalCommandFailure.Refused

    let private readText path = try Ok(File.ReadAllText(Path.GetFullPath path)) with error -> Error(LocalCommandFailure.Invalid error.Message)
    let private readBytes path = try Ok(File.ReadAllBytes(Path.GetFullPath path)) with error -> Error(LocalCommandFailure.Invalid error.Message)

    /// Outputs are new files only; nothing existing is overwritten.
    let private writeNew (path: string) (bytes: byte array) =
        try
            use output = new FileStream(Path.GetFullPath path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            output.Write bytes
            output.Flush true
            Ok()
        with error -> Error(LocalCommandFailure.Refused("cannot create " + path + ": " + error.Message))

    let private utf8 (text: string) = UTF8Encoding(false).GetBytes text

    let private readPacket path =
        readText path |> Result.bind (fun text -> LocalAgentHandoffJson.readPacket text |> Result.mapError (fun error -> LocalCommandFailure.Invalid(string error)))

    /// The host configuration must name the repository being operated on.
    let configuration root (path: string) =
        LocalHostConfiguration.load (Path.GetFullPath path) |> refused |> Result.bind (fun loaded ->
            let normalize (value: string) = Path.TrimEndingDirectorySeparator(Path.GetFullPath value)
            if normalize loaded.RepositoryRoot <> normalize root then Error(LocalCommandFailure.Refused "host configuration names a different repository root")
            else Ok loaded)

    /// The same quality-readiness gate as `work complete`; the durable
    /// checkpoint guard is supplied by the caller.
    let nativePolicy root (checkpointGuard: string list -> Result<unit, string>) : LocalRuntimeNativePolicy =
        let evaluate ids evidence = FileCompletionReadiness.evaluate root ids evidence (FileGroupGates.facet root evidence)
        { QualityGate = fun id evidence ->
            match evaluate [ id ] evidence with
            | CompletionGateOutcome.NotApplicable
            | CompletionGateOutcome.Ready _ -> Ok()
            | CompletionGateOutcome.PolicyInvalid reason -> Error("workProtocol.qualityEvidence is invalid: " + reason)
            | CompletionGateOutcome.Refused items ->
                Error(items |> List.collect (fun item -> CompletionReadiness.blockingReasons item |> List.map (fun reason -> item.WorkItemId + ": " + reason)) |> String.concat "; ")
          Readiness = fun plan ->
            plan.ItemPlans |> List.fold (fun acc item ->
                acc |> Result.bind (fun (records: Map<string, JsonObject>) ->
                    match evaluate [ item.Item.Id ] item.Item.Evidence with
                    | CompletionGateOutcome.NotApplicable -> Ok records
                    | CompletionGateOutcome.Ready _ as outcome ->
                        Ok(FileCompletionReadiness.extensions outcome |> Map.fold (fun state key value -> state.Add(key, value)) records)
                    | _ -> Error("completion readiness changed for " + item.Item.Id))) (Ok Map.empty)
          CheckpointGuard = checkpointGuard }

    let stageName = function LocalRunStage.Dispatched -> "dispatched" | LocalRunStage.Resumed -> "resumed"

    let reportNode (outcome: LocalRunReport) =
        let node = JsonObject()
        node["dispatchId"] <- JsonValue.Create outcome.DispatchId
        node["stage"] <- JsonValue.Create(stageName outcome.Stage)
        node["targetRef"] <- JsonValue.Create outcome.TargetRef
        node["targetCommit"] <- JsonValue.Create outcome.TargetCommit
        node["completedMembers"] <- JsonArray(outcome.CompletedMembers |> List.map (fun id -> JsonValue.Create id :> JsonNode) |> List.toArray)
        outcome.WorkerWorktree |> Option.iter (fun path -> node["workerWorktree"] <- JsonValue.Create path)
        node

    /// Dispatches run in order and stop at the first refusal; `completed`
    /// observes each finished dispatch as it completes.
    let run root (actor: Actor) hostConfig (dispatches: (string * string) list) target checkpointGuard (completed: LocalRunReport -> unit) =
        configuration root hostConfig |> Result.bind (fun loaded ->
            let policy = nativePolicy root checkpointGuard
            dispatches |> List.fold (fun acc (packetPath, delegationPath) ->
                acc |> Result.bind (fun reports ->
                    readPacket packetPath |> Result.bind (fun packet ->
                        let request = { Packet = packet; DelegationPath = Path.GetFullPath delegationPath; TargetRef = target }
                        FileLocalRuntime.run loaded policy actor request CancellationToken.None
                        |> Result.mapError (fun error -> LocalCommandFailure.Refused(sprintf "dispatch %s stopped: %s" packet.DispatchId error))
                        |> Result.map (fun outcome -> completed outcome; reports @ [ outcome ])))) (Ok []))

    let status root hostConfig dispatchId =
        configuration root hostConfig |> Result.bind (fun loaded ->
            FileLocalDispatchJournal.create loaded.RepositoryRoot loaded.JournalRoot |> refused |> Result.bind (fun journal ->
                match journal.Load loaded.RepositoryIdentity dispatchId with
                | Error error -> Error(LocalCommandFailure.Refused("no dispatch journal: " + error))
                | Ok saved ->
                    FileLocalIntegrationJournal.create loaded.RepositoryRoot loaded.IntegrationRoot |> refused |> Result.map (fun integration ->
                        [ sprintf "dispatch %s: %A" dispatchId (LocalDispatchJournal.phase saved)
                          match integration.Load loaded.RepositoryIdentity dispatchId with
                          | Ok(Some recorded) ->
                              sprintf "integration %s: %s -> %s (%s)" recorded.Plan.TargetRef recorded.Plan.BeforeCommit recorded.Plan.CandidateCommit
                                  (match recorded.AppliedAt with Some at -> "applied " + at.ToString("O") | None -> "prepared, not applied")
                          | Ok None -> "integration: not prepared"
                          | Error error -> sprintf "integration: unreadable (%s)" error ])))

    /// Issue time is the current UTC second; the window is `validMinutes`.
    let propose root hostConfig (proposal: LocalPacketProposal) validMinutes out =
        configuration root hostConfig |> Result.bind (fun loaded ->
            let now = DateTimeOffset.UtcNow
            let issued = DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero)
            LocalOperator.propose loaded { proposal with IssuedAt = issued; ExpiresAt = issued.AddMinutes(float validMinutes) } |> refused
            |> Result.bind (fun packet ->
                writeNew out (utf8 (LocalAgentHandoffJson.renderPacket packet)) |> Result.map (fun () ->
                    LocalOperator.review packet @ [ sprintf "packet written to %s; it has no authority until the operator delegates it" out ])))

    let pin root hostConfig validatorId =
        configuration root hostConfig |> Result.bind (fun loaded ->
            FileLocalRuntime.validatorPin loaded validatorId |> refused |> Result.map (fun digest -> [ digest ]))

    let initialize root hostConfig publicKeyPath keyId approver revision qualified =
        configuration root hostConfig |> Result.bind (fun loaded ->
            readText publicKeyPath |> Result.bind (fun pem ->
                LocalOperator.initialize loaded revision keyId approver pem qualified |> refused |> Result.map (fun () ->
                    [ sprintf "host policy %s initialized at %s with signer %s (%s); qualified release declared: %b" revision loaded.PolicyRoot keyId approver qualified ])))

    let grantDraft root hostConfig (request: LocalGrantDraftRequest) =
        configuration root hostConfig |> Result.bind (fun loaded ->
            let issued = DateTimeOffset.UtcNow.AddSeconds(-1.)
            let decisionRecords =
                match request.DecisionRecordsPath with
                | None -> Ok None
                | Some path ->
                    Praxis.Infrastructure.Construction.ConstructionDecisions.readFile path
                    |> Result.bind (fun (text, digest) ->
                        Praxis.Infrastructure.Construction.ConstructionDecisions.envelope text |> Result.bind (fun envelope ->
                            if envelope.CohortId <> request.CohortId then Error("construction decisions are for cohort " + envelope.CohortId + ", not " + request.CohortId)
                            else Ok(Some digest)))
                    |> invalid
            decisionRecords |> Result.bind (fun decisionsDigest ->
                LocalOperator.grantDraft loaded request.KeyId request.Approver request.GroupId request.CohortId request.Source request.Manifest request.Blueprint
                    request.Members request.Decisions decisionsDigest issued (issued.AddMinutes(float (min request.ValidMinutes 1440))) CancellationToken.None
                |> refused |> Result.bind (fun draft ->
                    writeNew request.DraftPath (utf8 (LocalOperator.renderDraft draft))
                    |> Result.bind (fun () -> writeNew request.PayloadPath (LocalOperator.grantPayload draft))
                    |> Result.map (fun () ->
                        [ sprintf "grant draft %s and signing payload %s written; sign the payload bytes with the operator key (ECDSA P-256, SHA-256)" request.DraftPath request.PayloadPath ]))))

    let grant root hostConfig draftPath signaturePath revision =
        configuration root hostConfig |> Result.bind (fun loaded ->
            readText draftPath |> Result.bind (LocalOperator.readDraft >> invalid) |> Result.bind (fun draft ->
                readBytes signaturePath |> Result.bind (fun signature ->
                    LocalOperator.applyGrant loaded draft signature revision DateTimeOffset.UtcNow |> refused |> Result.map (fun () ->
                        [ sprintf "group %s granted; host policy is now %s" draft.Approval.Scope.GroupId revision ]
                        @ (draft.Approval.Scope.ConstructionDecisionsDigest |> Option.map (sprintf "construction decisions %s are bound by the signed grant") |> Option.toList)))))

    let revoke root hostConfig keyId revision =
        configuration root hostConfig |> Result.bind (fun loaded ->
            LocalOperator.revoke loaded keyId revision DateTimeOffset.UtcNow |> refused |> Result.map (fun () ->
                [ sprintf "signer %s revoked; host policy is now %s" keyId revision ]))

    let review root hostConfig packetPath keyId approver payloadPath =
        configuration root hostConfig |> Result.bind (fun _ ->
            readPacket packetPath |> Result.bind (fun packet ->
                writeNew payloadPath (LocalOperator.delegationPayload keyId approver packet) |> Result.map (fun () ->
                    LocalOperator.review packet @ [ sprintf "review the assignment above; signing %s delegates exactly this packet" payloadPath ])))

    let delegation root hostConfig packetPath keyId approver signaturePath out =
        configuration root hostConfig |> Result.bind (fun loaded ->
            readPacket packetPath |> Result.bind (fun packet ->
                readBytes signaturePath |> Result.bind (fun signature ->
                    LocalOperator.assembleDelegation loaded keyId approver packet signature DateTimeOffset.UtcNow |> refused
                    |> Result.bind (fun envelope -> writeNew out (utf8 envelope))
                    |> Result.map (fun () -> [ sprintf "delegation for %s verified against the protected policy and written to %s" packet.DispatchId out ]))))

    /// The worker side: packet on stdin, the current directory is the checkout.
    let bridge git backend =
        let settings = { Git = git; Backend = backend; Worktree = Directory.GetCurrentDirectory() }
        LocalWorkerBridge.run settings (Console.In.ReadToEnd()) |> refused |> Result.map LocalAgentHandoffJson.renderResult
