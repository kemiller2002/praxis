namespace Praxis.Cli

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Pacing
open Praxis.Domain.Pacing
open Praxis.Infrastructure.Pacing

[<RequireQualifiedAccess>]
module PacingCommands =
    let usage =
        "pacing status [--provider codex|claude] [--model MODEL] [--json] [--state-dir PATH] | pacing gate [--provider codex|claude] [--model MODEL] [--state-dir PATH] | pacing override {on|off} [--state-dir PATH] | pacing state quarantine [--state-dir PATH]"

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

    let payloadEvent (payload: string) : string =
        try
            use document = JsonDocument.Parse payload
            let mutable value = Unchecked.defaultof<JsonElement>

            if document.RootElement.ValueKind = JsonValueKind.Object
               && document.RootElement.TryGetProperty("hook_event_name", &value)
               && value.ValueKind = JsonValueKind.String then
                value.GetString() |> Option.ofObj |> Option.defaultValue "PreToolUse"
            else
                "PreToolUse"
        with :? JsonException ->
            "PreToolUse"

    /// One hook invocation: resolve the model from the payload, run the gate
    /// and return the denial document, or `None` to let the call proceed.
    /// A hook that crashes is treated by agent runtimes as a non-blocking
    /// error, i.e. permission to continue. Every non-proceed outcome is
    /// therefore rendered as an explicit deny: the gate fails closed.
    let gateHook (runtime: PacingRuntime) (provider: ProviderId) (explicitModel: string option) (payload: string) : string option =
        let model = PacingOperations.resolveModel runtime provider explicitModel payload
        let event = payloadEvent payload

        match PacingOperations.gate runtime provider model with
        | PacingGateOutcome.Proceed -> None
        | PacingGateOutcome.Denied reason -> Some(PacingHookContract.denyOutput provider event reason.Detail)
        | PacingGateOutcome.Faulted fault ->
            Some(PacingHookContract.denyOutput provider event $"pacing safety state is unavailable [{PacingStoreFault.code fault}] {PacingStoreFault.message fault}")

    let private runGate
        (runtime: PacingRuntime)
        (provider: ProviderId)
        (explicitModel: string option)
        : int =
        let payload =
            if Console.IsInputRedirected then
                Console.In.ReadToEnd()
            else
                "{}"

        gateHook runtime provider explicitModel payload |> Option.iter (printf "%s")
        0

    let private runStatus
        (runtime: PacingRuntime)
        (directory: string)
        (provider: ProviderId)
        (model: string option)
        (asJson: bool)
        : int =
        match PacingOperations.status runtime directory provider model with
        | Error fault ->
            eprintfn "ERROR [%s] %s" (PacingStoreFault.code fault) (PacingStoreFault.message fault)
            3
        | Ok status ->
            if asJson then
                printf "%s" (PacingStatusDocument.renderJson status)
            else
                printf "%s" (PacingStatusDocument.renderText status)

            match status.FreshnessState, status.StateIntegrity with
            | _, Praxis.Domain.Pacing.StateIntegrity.Indeterminate _ -> 3
            | PacingFreshnessState.Unavailable, _ -> 1
            | _ -> 0

    let private reportOverride enabled (result: Result<unit, string>) =
        match result with
        | Ok() ->
            printfn "Pacing override %s" (if enabled then "on" else "off")
            0
        | Error message ->
            eprintfn "ERROR pacing override could not be turned %s: %s" (if enabled then "on" else "off") message
            6

    let run (_root: string) (arguments: string list) : int =
        let directory = stateDirectory arguments

        let provider =
            optionValue "--provider" arguments
            |> Option.defaultValue "codex"
            |> ProviderId.tryParse

        let model = optionValue "--model" arguments
        let runtime = PacingAdapters.create directory

        match arguments |> List.filter (fun value -> value <> "--json") with
        | "status" :: _ when provider.IsSome ->
            runStatus runtime directory provider.Value model (arguments |> List.contains "--json")
        | "gate" :: _ when provider.IsSome ->
            runGate runtime provider.Value model
        | [ "override"; "on" ]
        | [ "override"; "on"; "--state-dir"; _ ] ->
            PacingOperations.setOverride runtime true |> reportOverride true
        | [ "override"; "off" ]
        | [ "override"; "off"; "--state-dir"; _ ] ->
            PacingOperations.setOverride runtime false |> reportOverride false
        | [ "state"; "quarantine" ]
        | [ "state"; "quarantine"; "--state-dir"; _ ] ->
            match PacingOperations.quarantineState runtime with
            | Ok(Some path) ->
                printfn "Pacing state quarantined to %s; pacing restarts from empty state" path
                0
            | Ok None ->
                printfn "No pacing state to quarantine"
                0
            | Error message ->
                eprintfn "ERROR %s" message
                5
        | _ ->
            eprintfn "ERROR expected %s" usage
            2
