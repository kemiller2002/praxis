namespace Praxis.Cli

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Pacing
open Praxis.Infrastructure.Pacing

[<RequireQualifiedAccess>]
module PacingCommands =
    let usage =
        "pacing status [--provider codex|claude] [--model MODEL] [--json] [--state-dir PATH] | pacing gate [--provider codex|claude] [--model MODEL] [--state-dir PATH] | pacing override {on|off} [--state-dir PATH]"

    let private stateDirectory (arguments: string list) : string =
        match arguments |> List.tryFindIndex ((=) "--state-dir") with
        | Some index ->
            arguments
            |> List.tryItem (index + 1)
            |> Option.map Path.GetFullPath
            |> Option.defaultValue (
                Path.Combine(
                    Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                    ".praxis",
                    "usage-pacing"
                )
            )
        | None ->
            match Environment.GetEnvironmentVariable "PRAXIS_PACING_DIR" with
            | null
            | "" ->
                Path.Combine(
                    Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                    ".praxis",
                    "usage-pacing"
                )
            | value ->
                Path.GetFullPath value

    let private optionValue (name: string) (arguments: string list) : string option =
        arguments
        |> List.tryFindIndex ((=) name)
        |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

    let private payloadEvent (payload: string) : string =
        try
            use document = JsonDocument.Parse payload
            let mutable value = Unchecked.defaultof<JsonElement>

            if document.RootElement.ValueKind = JsonValueKind.Object
               && document.RootElement.TryGetProperty("hook_event_name", &value)
               && value.ValueKind = JsonValueKind.String then
                value.GetString() |> Option.ofObj |> Option.defaultValue "PreToolUse"
            else
                "PreToolUse"
        with _ ->
            "PreToolUse"

    let private deny (provider: string) (event: string) (detail: string) : unit =
        let reason =
            $"Praxis usage pacing hold remains active: {detail}. Retry after quota refresh/reset."

        if event = "PreToolUse" then
            let root = JsonObject()
            let output = JsonObject()
            output["hookEventName"] <- JsonValue.Create(event)
            output["permissionDecision"] <- JsonValue.Create("deny")
            output["permissionDecisionReason"] <- JsonValue.Create(reason)
            root["hookSpecificOutput"] <- output
            printf "%s" (root.ToJsonString())
        elif provider = "codex" then
            let root = JsonObject()
            root["continue"] <- JsonValue.Create(false)
            root["stopReason"] <- JsonValue.Create(reason)
            printf "%s" (root.ToJsonString())
        else
            let root = JsonObject()
            root["decision"] <- JsonValue.Create("block")
            root["reason"] <- JsonValue.Create(reason)
            printf "%s" (root.ToJsonString())

    let private runGate
        (runtime: PacingRuntime)
        (provider: string)
        (explicitModel: string option)
        : int =
        let payload =
            if Console.IsInputRedirected then
                Console.In.ReadToEnd()
            else
                "{}"

        let model = PacingOperations.resolveModel runtime provider explicitModel payload
        let event = payloadEvent payload

        match PacingOperations.gate runtime provider model with
        | PacingGateOutcome.Proceed -> 0
        | PacingGateOutcome.Denied reason ->
            deny provider event reason.Detail
            0

    let private runStatus
        (runtime: PacingRuntime)
        (directory: string)
        (provider: string)
        (model: string option)
        (asJson: bool)
        : int =
        let status = PacingOperations.status runtime directory provider model

        if asJson then
            printf "%s" (PacingStatus.renderJson status)
        else
            printf "%s" (PacingStatus.renderText status)

        match status.FreshnessState with
        | PacingFreshnessState.Unavailable -> 1
        | _ -> 0

    let run (_root: string) (arguments: string list) : int =
        let directory = stateDirectory arguments

        let provider =
            optionValue "--provider" arguments
            |> Option.defaultValue "codex"
            |> fun value -> value.ToLowerInvariant()

        let model = optionValue "--model" arguments
        let runtime = PacingAdapters.create directory

        match arguments |> List.filter (fun value -> value <> "--json") with
        | "status" :: _ when provider = "codex" || provider = "claude" ->
            runStatus runtime directory provider model (arguments |> List.contains "--json")
        | "gate" :: _ when provider = "codex" || provider = "claude" ->
            runGate runtime provider model
        | [ "override"; "on" ]
        | [ "override"; "on"; "--state-dir"; _ ] ->
            PacingOperations.setOverride runtime true
            printfn "Pacing override on"
            0
        | [ "override"; "off" ]
        | [ "override"; "off"; "--state-dir"; _ ] ->
            PacingOperations.setOverride runtime false
            printfn "Pacing override off"
            0
        | _ ->
            eprintfn "ERROR expected %s" usage
            2
