namespace Praxis.Cli

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Execution
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// `praxis local ...`: the documented entry point for the local
/// coordinator/worker runtime. Operator commands never handle private keys;
/// `run` starts real workers only for packets the protected policy's signer
/// delegated exactly. Exit codes: 0 success, 2 invalid arguments, 3 refused.
[<RequireQualifiedAccess>]
module LocalRuntimeCommands =
    let usage =
        "local run --host-config FILE --dispatch PACKET=DELEGATION [--dispatch PACKET=DELEGATION]* --target REF [--json] [IDENTITY]"
        + " | local status --host-config FILE --dispatch ID"
        + " | local packet propose --host-config FILE --group ID --worker ID --role ROLE --dispatch ID --attempt ID --parent EXE-ID --child EXE-ID --member WI=EXE-ID [--member WI=EXE-ID]* --decision ID [--decision ID]* --allow PATH [--allow PATH]* --acceptance OBLIGATION=WI:VALIDATOR [--acceptance ...]* [--prerequisite WI]* [--valid-minutes N] [--timeout-seconds N] [--max-output-bytes N] --out FILE"
        + " | local validator pin --host-config FILE --validator ID"
        + " | local operator init --host-config FILE --public-key PEM --key-id ID --approver NAME --revision REV [--declare-qualified-release]"
        + " | local operator grant-draft --host-config FILE --key-id ID --approver NAME --group ID --cohort ID --source REV --manifest PATH --blueprint PATH --member WI=REQUIREMENT [--member ...]* --decision ID [--decision ID]* [--valid-minutes N] --draft FILE --payload FILE"
        + " | local operator grant --host-config FILE --draft FILE --signature FILE --revision REV"
        + " | local operator revoke --host-config FILE --key-id ID --revision REV"
        + " | local operator review --host-config FILE --packet FILE --key-id ID --approver NAME --payload FILE"
        + " | local operator delegate --host-config FILE --packet FILE --key-id ID --approver NAME --signature FILE --out FILE"
        + " | local worker-bridge --git PATH (--agent-command EXE [--agent-arg ARG]* | --ollama-model MODEL [--ollama-endpoint URL])"

    let private values (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name then Some value else None)

    let private value name arguments = values name arguments |> List.tryHead
    let private flag (name: string) (arguments: string list) = List.contains name arguments

    let private required name arguments =
        match value name arguments with
        | Some text when not (String.IsNullOrWhiteSpace text) -> Ok text
        | _ -> Error(name + " is required")

    let private pairs (separator: char) name arguments =
        values name arguments |> List.map (fun (text: string) ->
            match text.IndexOf separator with
            | index when index > 0 && index < text.Length - 1 -> Ok(text.Substring(0, index), text.Substring(index + 1))
            | _ -> Error(sprintf "%s expects KEY%cVALUE, got '%s'" name separator text))
        |> List.fold (fun acc item -> acc |> Result.bind (fun items -> item |> Result.map (fun pair -> items @ [ pair ]))) (Ok [])

    let private number name fallback arguments =
        match value name arguments with
        | None -> Ok fallback
        | Some text ->
            match Int32.TryParse text with
            | true, parsed when parsed > 0 -> Ok parsed
            | _ -> Error(name + " must be a positive integer")

    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()

    let private attempt = ResultBuilder()

    /// Refused operations exit 3; malformed arguments exit 2.
    type private Failure = Invalid of string | Refused of string

    let private finish (result: Result<int, Failure>) =
        match result with
        | Ok code -> code
        | Error(Invalid message) ->
            eprintfn "ERROR %s" message
            2
        | Error(Refused message) ->
            eprintfn "REFUSED %s" message
            3

    let private invalid result = result |> Result.mapError Invalid
    let private refused result = result |> Result.mapError Refused

    let private configuration root arguments =
        required "--host-config" arguments |> invalid |> Result.bind (fun path ->
            LocalHostConfiguration.load (Path.GetFullPath path) |> refused |> Result.bind (fun loaded ->
                let normalize (value: string) = Path.TrimEndingDirectorySeparator(Path.GetFullPath value)
                if normalize loaded.RepositoryRoot <> normalize root then Error(Refused "host configuration names a different repository root")
                else Ok loaded))

    let private readText path = try Ok(File.ReadAllText(Path.GetFullPath path)) with error -> Error(Invalid(error.Message))
    let private readBytes path = try Ok(File.ReadAllBytes(Path.GetFullPath path)) with error -> Error(Invalid(error.Message))

    /// Write a new file only; outputs never overwrite existing evidence.
    let private writeNew (path: string) (bytes: byte array) =
        try
            use output = new FileStream(Path.GetFullPath path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            output.Write bytes
            output.Flush true
            Ok()
        with error -> Error(Refused("cannot create " + path + ": " + error.Message))

    let private utf8 (text: string) = Text.UTF8Encoding(false).GetBytes text

    let private readPacket path =
        readText path |> Result.bind (fun text -> LocalAgentHandoffJson.readPacket text |> Result.mapError (fun error -> Invalid(string error)))

    // ---------------------------------------------------------------- policy

    /// The same quality-readiness and durable-checkpoint gates as `work complete`.
    let private nativePolicy root : LocalRuntimeNativePolicy =
        let evaluate ids evidence =
            FileCompletionReadiness.evaluate root ids evidence (FileGroupGates.facet root evidence)
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
          CheckpointGuard = CheckpointCommands.completionGuard root }

    // ------------------------------------------------------------------- run

    let private report (outcome: LocalRunReport) =
        let node = JsonObject()
        node["dispatchId"] <- JsonValue.Create outcome.DispatchId
        node["stage"] <- JsonValue.Create(match outcome.Stage with LocalRunStage.Dispatched -> "dispatched" | LocalRunStage.Resumed -> "resumed")
        node["targetRef"] <- JsonValue.Create outcome.TargetRef
        node["targetCommit"] <- JsonValue.Create outcome.TargetCommit
        node["completedMembers"] <- JsonArray(outcome.CompletedMembers |> List.map (fun id -> JsonValue.Create id :> JsonNode) |> List.toArray)
        outcome.WorkerWorktree |> Option.iter (fun path -> node["workerWorktree"] <- JsonValue.Create path)
        node

    let private run root (actor: Actor) arguments =
        configuration root arguments |> Result.bind (fun loaded ->
            required "--target" arguments |> invalid |> Result.bind (fun target ->
                pairs '=' "--dispatch" arguments |> invalid |> Result.bind (fun dispatches ->
                    if dispatches.IsEmpty then Error(Invalid "at least one --dispatch PACKET=DELEGATION is required")
                    else
                        let policy = nativePolicy root
                        dispatches |> List.fold (fun acc (packetPath, delegationPath) ->
                            acc |> Result.bind (fun reports ->
                                readPacket packetPath |> Result.bind (fun packet ->
                                    let request = { Packet = packet; DelegationPath = Path.GetFullPath delegationPath; TargetRef = target }
                                    FileLocalRuntime.run loaded policy actor request CancellationToken.None
                                    |> Result.mapError (fun error -> Refused(sprintf "dispatch %s stopped: %s" packet.DispatchId error))
                                    |> Result.map (fun outcome ->
                                        if not (flag "--json" arguments) then
                                            printfn "%s: %s -> %s %s; completed %s" outcome.DispatchId
                                                (match outcome.Stage with LocalRunStage.Dispatched -> "dispatched" | LocalRunStage.Resumed -> "resumed")
                                                outcome.TargetRef outcome.TargetCommit (String.concat ", " outcome.CompletedMembers)
                                        reports @ [ outcome ])))) (Ok [])
                        |> Result.map (fun reports ->
                            if flag "--json" arguments then
                                printfn "%s" ((JsonArray(reports |> List.map (report >> fun node -> node :> JsonNode) |> List.toArray)).ToJsonString())
                            0))))

    let private status root arguments =
        configuration root arguments |> Result.bind (fun loaded ->
            required "--dispatch" arguments |> invalid |> Result.bind (fun dispatchId ->
                FileLocalDispatchJournal.create loaded.RepositoryRoot loaded.JournalRoot |> refused |> Result.bind (fun journal ->
                    match journal.Load loaded.RepositoryIdentity dispatchId with
                    | Error error -> Error(Refused("no dispatch journal: " + error))
                    | Ok saved ->
                        printfn "dispatch %s: %A" dispatchId (LocalDispatchJournal.phase saved)
                        FileLocalIntegrationJournal.create loaded.RepositoryRoot loaded.IntegrationRoot |> refused |> Result.map (fun integration ->
                            match integration.Load loaded.RepositoryIdentity dispatchId with
                            | Ok(Some recorded) ->
                                printfn "integration %s: %s -> %s (%s)" recorded.Plan.TargetRef recorded.Plan.BeforeCommit recorded.Plan.CandidateCommit
                                    (match recorded.AppliedAt with Some at -> "applied " + at.ToString("O") | None -> "prepared, not applied")
                            | Ok None -> printfn "integration: not prepared"
                            | Error error -> printfn "integration: unreadable (%s)" error
                            0))))

    // ---------------------------------------------------------------- packets

    let private propose root arguments =
        attempt {
            let! loaded = configuration root arguments
            let text name = required name arguments |> invalid
            let! group = text "--group"
            let! worker = text "--worker"
            let! roleText = text "--role"
            let! dispatch = text "--dispatch"
            let! attemptId = text "--attempt"
            let! parent = text "--parent"
            let! child = text "--child"
            let! out = text "--out"
            let! members = pairs '=' "--member" arguments |> invalid
            let! acceptance = pairs '=' "--acceptance" arguments |> invalid
            let! minutes = number "--valid-minutes" 120 arguments |> invalid
            let! timeout = number "--timeout-seconds" 1800 arguments |> invalid
            let! output = number "--max-output-bytes" 65536 arguments |> invalid
            let! role = ExecutionRole.tryParse roleText |> Option.map Ok |> Option.defaultValue (Error(Invalid("unknown role " + roleText)))
            let! obligations =
                acceptance |> List.map (fun (obligation, target: string) ->
                    match target.Split(':') with
                    | [| workItem; validator |] when workItem <> "" && validator <> "" -> Ok(obligation, workItem, validator)
                    | _ -> Error(Invalid "--acceptance expects OBLIGATION=WI:VALIDATOR"))
                |> List.fold (fun acc item -> acc |> Result.bind (fun items -> item |> Result.map (fun value -> items @ [ value ]))) (Ok [])
            let now = DateTimeOffset.UtcNow
            let issued = DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero)
            let proposal : LocalPacketProposal =
                { DispatchId = dispatch; AttemptId = attemptId; ParentExecutionId = parent; ChildExecutionId = child
                  WorkerId = worker; Role = role; GroupId = group; Members = members
                  DecisionIds = values "--decision" arguments; AllowedPaths = values "--allow" arguments
                  Prerequisites = values "--prerequisite" arguments; Acceptance = obligations
                  IssuedAt = issued; ExpiresAt = issued.AddMinutes(float minutes)
                  TimeoutSeconds = timeout; MaxOutputBytes = output }
            let! packet = LocalOperator.propose loaded proposal |> refused
            do! writeNew out (utf8 (LocalAgentHandoffJson.renderPacket packet))
            LocalOperator.review packet |> List.iter (printfn "%s")
            printfn "packet written to %s; it has no authority until the operator delegates it" out
            return 0
        }

    let private pin root arguments =
        attempt {
            let! loaded = configuration root arguments
            let! id = required "--validator" arguments |> invalid
            let! digest = FileLocalRuntime.validatorPin loaded id |> refused
            printfn "%s" digest
            return 0
        }

    // --------------------------------------------------------------- operator

    let private operator root arguments =
        match arguments with
        | "init" :: rest ->
            attempt {
                let! loaded = configuration root rest
                let! pem = required "--public-key" rest |> invalid |> Result.bind readText
                let! keyId = required "--key-id" rest |> invalid
                let! approver = required "--approver" rest |> invalid
                let! revision = required "--revision" rest |> invalid
                let qualified = flag "--declare-qualified-release" rest
                do! LocalOperator.initialize loaded revision keyId approver pem qualified |> refused
                printfn "host policy %s initialized at %s with signer %s (%s); qualified release declared: %b" revision loaded.PolicyRoot keyId approver qualified
                return 0
            }
        | "grant-draft" :: rest ->
            attempt {
                let! loaded = configuration root rest
                let text name = required name rest |> invalid
                let! keyId = text "--key-id"
                let! approver = text "--approver"
                let! group = text "--group"
                let! cohort = text "--cohort"
                let! source = text "--source"
                let! manifest = text "--manifest"
                let! blueprint = text "--blueprint"
                let! draftPath = text "--draft"
                let! payloadPath = text "--payload"
                let! members = pairs '=' "--member" rest |> invalid
                let! minutes = number "--valid-minutes" 240 rest |> invalid
                let issued = DateTimeOffset.UtcNow.AddSeconds(-1.)
                let! draft =
                    LocalOperator.grantDraft loaded keyId approver group cohort source manifest blueprint (Map.ofList members)
                        (values "--decision" rest) issued (issued.AddMinutes(float (min minutes 1440))) CancellationToken.None
                    |> refused
                do! writeNew draftPath (utf8 (LocalOperator.renderDraft draft))
                do! writeNew payloadPath (LocalOperator.grantPayload draft)
                printfn "grant draft %s and signing payload %s written; sign the payload bytes with the operator key (ECDSA P-256, SHA-256)" draftPath payloadPath
                return 0
            }
        | "grant" :: rest ->
            attempt {
                let! loaded = configuration root rest
                let! text = required "--draft" rest |> invalid |> Result.bind readText
                let! draft = LocalOperator.readDraft text |> invalid
                let! signature = required "--signature" rest |> invalid |> Result.bind readBytes
                let! revision = required "--revision" rest |> invalid
                do! LocalOperator.applyGrant loaded draft signature revision DateTimeOffset.UtcNow |> refused
                printfn "group %s granted; host policy is now %s" draft.Approval.Scope.GroupId revision
                return 0
            }
        | "revoke" :: rest ->
            attempt {
                let! loaded = configuration root rest
                let! keyId = required "--key-id" rest |> invalid
                let! revision = required "--revision" rest |> invalid
                do! LocalOperator.revoke loaded keyId revision DateTimeOffset.UtcNow |> refused
                printfn "signer %s revoked; host policy is now %s" keyId revision
                return 0
            }
        | "review" :: rest ->
            attempt {
                let! _ = configuration root rest
                let! packet = required "--packet" rest |> invalid |> Result.bind readPacket
                let! keyId = required "--key-id" rest |> invalid
                let! approver = required "--approver" rest |> invalid
                let! payloadPath = required "--payload" rest |> invalid
                LocalOperator.review packet |> List.iter (printfn "%s")
                do! writeNew payloadPath (LocalOperator.delegationPayload keyId approver packet)
                printfn "review the assignment above; signing %s delegates exactly this packet" payloadPath
                return 0
            }
        | "delegate" :: rest ->
            attempt {
                let! loaded = configuration root rest
                let! packet = required "--packet" rest |> invalid |> Result.bind readPacket
                let! keyId = required "--key-id" rest |> invalid
                let! approver = required "--approver" rest |> invalid
                let! signature = required "--signature" rest |> invalid |> Result.bind readBytes
                let! out = required "--out" rest |> invalid
                let! envelope = LocalOperator.assembleDelegation loaded keyId approver packet signature DateTimeOffset.UtcNow |> refused
                do! writeNew out (utf8 envelope)
                printfn "delegation for %s verified against the protected policy and written to %s" packet.DispatchId out
                return 0
            }
        | _ -> Error(Invalid("unknown operator command; usage: praxis " + usage))

    // ----------------------------------------------------------------- worker

    let private bridge arguments =
        required "--git" arguments |> invalid |> Result.bind (fun git ->
            let backend =
                match value "--agent-command" arguments, value "--ollama-model" arguments with
                | Some command, None -> Ok(LocalBridgeBackend.Command(command, values "--agent-arg" arguments))
                | None, Some model ->
                    let endpoint = value "--ollama-endpoint" arguments |> Option.defaultValue "http://127.0.0.1:11434"
                    match Uri.TryCreate(endpoint, UriKind.Absolute) with
                    | true, uri -> Ok(LocalBridgeBackend.Ollama(uri, model))
                    | _ -> Error "--ollama-endpoint must be an absolute URL"
                | _ -> Error "exactly one of --agent-command or --ollama-model is required"
            backend |> invalid |> Result.bind (fun backend ->
                let settings = { Git = git; Backend = backend; Worktree = Directory.GetCurrentDirectory() }
                LocalWorkerBridge.run settings (Console.In.ReadToEnd()) |> refused |> Result.map (fun result ->
                    Console.Out.Write(LocalAgentHandoffJson.renderResult result)
                    0)))

    let route (root: string) (arguments: string list) (withActor: string list -> (Actor -> int) -> int) =
        match arguments with
        | "run" :: rest -> withActor rest (fun actor -> run root actor rest |> finish)
        | "status" :: rest -> status root rest |> finish
        | "packet" :: "propose" :: rest -> propose root rest |> finish
        | "validator" :: "pin" :: rest -> pin root rest |> finish
        | "operator" :: rest -> operator root rest |> finish
        | "worker-bridge" :: rest -> bridge rest |> finish
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2
