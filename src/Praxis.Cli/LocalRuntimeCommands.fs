namespace Praxis.Cli

open System
open System.Text.Json.Nodes
open Praxis.Domain.Execution
open Praxis.Domain.Provenance
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
        + " | local operator grant-draft --host-config FILE --key-id ID --approver NAME --group ID --cohort ID --source REV --manifest PATH --blueprint PATH --member WI=REQUIREMENT [--member ...]* --decision ID [--decision ID]* [--decisions FILE] [--valid-minutes N] --draft FILE --payload FILE"
        + " | local operator grant --host-config FILE --draft FILE --signature FILE --revision REV"
        + " | local operator revoke --host-config FILE --key-id ID --revision REV"
        + " | local operator review --host-config FILE --packet FILE --key-id ID --approver NAME --payload FILE"
        + " | local operator delegate --host-config FILE --packet FILE --key-id ID --approver NAME --signature FILE --out FILE"
        + " | " + LocalContainmentCommands.usage
        + " | local worker-bridge --git PATH (--agent-command EXE [--agent-arg ARG]* | --ollama-model MODEL [--ollama-endpoint URL] | --openai-model MODEL [--openai-endpoint URL])"
        + ConstructionCommands.usage

    let private values (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name then Some value else None)

    let private value name arguments = values name arguments |> List.tryHead
    let private flag (name: string) (arguments: string list) = List.contains name arguments

    type private ResultBuilder() =
        member _.Bind(result, next) = Result.bind next result
        member _.Return result = Ok result
        member _.ReturnFrom result = result

    let private attempt = ResultBuilder()

    let private required name arguments =
        match value name arguments with
        | Some text when not (String.IsNullOrWhiteSpace text) -> Ok text
        | _ -> Error(LocalCommandFailure.Invalid(name + " is required"))

    let private pairs (separator: char) name arguments =
        values name arguments
        |> List.map (fun (text: string) ->
            match text.IndexOf separator with
            | index when index > 0 && index < text.Length - 1 -> Ok(text.Substring(0, index), text.Substring(index + 1))
            | _ -> Error(LocalCommandFailure.Invalid(sprintf "%s expects KEY%cVALUE, got '%s'" name separator text)))
        |> List.fold (fun acc item -> acc |> Result.bind (fun items -> item |> Result.map (fun pair -> items @ [ pair ]))) (Ok [])

    let private number name fallback arguments =
        match value name arguments with
        | None -> Ok fallback
        | Some text ->
            match Int32.TryParse text with
            | true, parsed when parsed > 0 -> Ok parsed
            | _ -> Error(LocalCommandFailure.Invalid(name + " must be a positive integer"))

    let private finish (result: Result<int, LocalCommandFailure>) =
        match result with
        | Ok code -> code
        | Error(LocalCommandFailure.Invalid message) ->
            eprintfn "ERROR %s" message
            2
        | Error(LocalCommandFailure.Refused message) ->
            eprintfn "REFUSED %s" message
            3

    let private lines (result: Result<string list, LocalCommandFailure>) =
        result |> Result.map (fun output -> output |> List.iter (printfn "%s"); 0)

    let private run root (actor: Actor) arguments =
        attempt {
            let! config = required "--host-config" arguments
            let! target = required "--target" arguments
            let! dispatches = pairs '=' "--dispatch" arguments
            let json = flag "--json" arguments
            let announce (outcome: LocalRunReport) =
                if not json then
                    printfn "%s: %s -> %s %s; completed %s" outcome.DispatchId (LocalRuntimeService.stageName outcome.Stage)
                        outcome.TargetRef outcome.TargetCommit (String.concat ", " outcome.CompletedMembers)
            if dispatches.IsEmpty then return! Error(LocalCommandFailure.Invalid "at least one --dispatch PACKET=DELEGATION is required")
            else
                let! reports = LocalRuntimeService.run root actor config dispatches target (CheckpointCommands.completionGuard root) announce
                let rendered = JsonArray(reports |> List.map (LocalRuntimeService.reportNode >> fun node -> node :> JsonNode) |> List.toArray)
                let () = if json then printfn "%s" (rendered.ToJsonString())
                return 0
        }

    let private propose root arguments =
        attempt {
            let text name = required name arguments
            let! config = text "--host-config"
            let! group = text "--group"
            let! worker = text "--worker"
            let! roleText = text "--role"
            let! dispatch = text "--dispatch"
            let! attemptId = text "--attempt"
            let! parent = text "--parent"
            let! child = text "--child"
            let! out = text "--out"
            let! members = pairs '=' "--member" arguments
            let! acceptance = pairs '=' "--acceptance" arguments
            let! minutes = number "--valid-minutes" 120 arguments
            let! timeout = number "--timeout-seconds" 1800 arguments
            let! output = number "--max-output-bytes" 65536 arguments
            let! role = ExecutionRole.tryParse roleText |> Option.map Ok |> Option.defaultValue (Error(LocalCommandFailure.Invalid("unknown role " + roleText)))
            let! obligations =
                acceptance |> List.map (fun (obligation, target: string) ->
                    match target.Split(':') with
                    | [| workItem; validator |] when workItem <> "" && validator <> "" -> Ok(obligation, workItem, validator)
                    | _ -> Error(LocalCommandFailure.Invalid "--acceptance expects OBLIGATION=WI:VALIDATOR"))
                |> List.fold (fun acc item -> acc |> Result.bind (fun items -> item |> Result.map (fun value -> items @ [ value ]))) (Ok [])
            let proposal : LocalPacketProposal =
                { DispatchId = dispatch; AttemptId = attemptId; ParentExecutionId = parent; ChildExecutionId = child
                  WorkerId = worker; Role = role; GroupId = group; Members = members
                  DecisionIds = values "--decision" arguments; AllowedPaths = values "--allow" arguments
                  Prerequisites = values "--prerequisite" arguments; Acceptance = obligations
                  IssuedAt = DateTimeOffset.MinValue; ExpiresAt = DateTimeOffset.MinValue
                  TimeoutSeconds = timeout; MaxOutputBytes = output }
            return! LocalRuntimeService.propose root config proposal minutes out |> lines
        }

    let private operator root arguments =
        let text name rest = required name rest
        match arguments with
        | "init" :: rest ->
            attempt {
                let! config = text "--host-config" rest
                let! pem = text "--public-key" rest
                let! keyId = text "--key-id" rest
                let! approver = text "--approver" rest
                let! revision = text "--revision" rest
                return! LocalRuntimeService.initialize root config pem keyId approver revision (flag "--declare-qualified-release" rest) |> lines
            }
        | "grant-draft" :: rest ->
            attempt {
                let! config = text "--host-config" rest
                let! keyId = text "--key-id" rest
                let! approver = text "--approver" rest
                let! group = text "--group" rest
                let! cohort = text "--cohort" rest
                let! source = text "--source" rest
                let! manifest = text "--manifest" rest
                let! blueprint = text "--blueprint" rest
                let! draft = text "--draft" rest
                let! payload = text "--payload" rest
                let! members = pairs '=' "--member" rest
                let! minutes = number "--valid-minutes" 240 rest
                return!
                    LocalRuntimeService.grantDraft root config
                        { KeyId = keyId; Approver = approver; GroupId = group; CohortId = cohort; Source = source; Manifest = manifest
                          Blueprint = blueprint; Members = Map.ofList members; Decisions = values "--decision" rest
                          DecisionRecordsPath = value "--decisions" rest
                          ValidMinutes = minutes; DraftPath = draft; PayloadPath = payload }
                    |> lines
            }
        | "grant" :: rest ->
            attempt {
                let! config = text "--host-config" rest
                let! draft = text "--draft" rest
                let! signature = text "--signature" rest
                let! revision = text "--revision" rest
                return! LocalRuntimeService.grant root config draft signature revision |> lines
            }
        | "revoke" :: rest ->
            attempt {
                let! config = text "--host-config" rest
                let! keyId = text "--key-id" rest
                let! revision = text "--revision" rest
                return! LocalRuntimeService.revoke root config keyId revision |> lines
            }
        | "review" :: rest ->
            attempt {
                let! config = text "--host-config" rest
                let! packet = text "--packet" rest
                let! keyId = text "--key-id" rest
                let! approver = text "--approver" rest
                let! payload = text "--payload" rest
                return! LocalRuntimeService.review root config packet keyId approver payload |> lines
            }
        | "delegate" :: rest ->
            attempt {
                let! config = text "--host-config" rest
                let! packet = text "--packet" rest
                let! keyId = text "--key-id" rest
                let! approver = text "--approver" rest
                let! signature = text "--signature" rest
                let! out = text "--out" rest
                return! LocalRuntimeService.delegation root config packet keyId approver signature out |> lines
            }
        | _ -> Error(LocalCommandFailure.Invalid("unknown operator command; usage: praxis " + usage))

    let private bridge arguments =
        let endpoint name fallback =
            match Uri.TryCreate(value name arguments |> Option.defaultValue fallback, UriKind.Absolute) with
            | true, uri -> Ok uri
            | _ -> Error(LocalCommandFailure.Invalid(name + " must be an absolute URL"))
        attempt {
            let! git = required "--git" arguments
            let! backend =
                match value "--agent-command" arguments, value "--ollama-model" arguments, value "--openai-model" arguments with
                | Some command, None, None -> Ok(LocalBridgeBackend.Command(command, values "--agent-arg" arguments))
                | None, Some model, None -> endpoint "--ollama-endpoint" "http://127.0.0.1:11434" |> Result.map (fun uri -> LocalBridgeBackend.Ollama(uri, model))
                | None, None, Some model -> endpoint "--openai-endpoint" "http://127.0.0.1:8080" |> Result.map (fun uri -> LocalBridgeBackend.OpenAiCompatible(uri, model))
                | _ -> Error(LocalCommandFailure.Invalid "exactly one of --agent-command, --ollama-model or --openai-model is required")
            let! result = LocalRuntimeService.bridge git backend
            Console.Out.Write result
            return 0
        }

    /// `handoff` (read-only inspection) and `local` share one CLI entry.
    let route (root: string) (family: string) (arguments: string list) (withActor: string list -> (Actor -> int) -> int) =
        match arguments with
        | _ when family = "handoff" -> LocalHandoffCommands.run root arguments
        | _ when family = "construct" -> ConstructionCommands.run root arguments
        | "run" :: rest -> withActor rest (fun actor -> run root actor rest |> finish)
        | "status" :: rest ->
            attempt {
                let! config = required "--host-config" rest
                let! dispatch = required "--dispatch" rest
                return! LocalRuntimeService.status root config dispatch |> lines
            } |> finish
        | "packet" :: "propose" :: rest -> propose root rest |> finish
        | "validator" :: "pin" :: rest ->
            attempt {
                let! config = required "--host-config" rest
                let! id = required "--validator" rest
                return! LocalRuntimeService.pin root config id |> lines
            } |> finish
        | "operator" :: rest -> operator root rest |> finish
        | "worker-bridge" :: rest -> bridge rest |> finish
        | "containment" :: rest -> LocalContainmentCommands.run root rest
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2
