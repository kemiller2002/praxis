namespace Praxis.Cli

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Provenance
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Work

/// Composition root for the first-class execution-step command family.
/// State and attribution rules live in Domain/Infrastructure; this module
/// only parses CLI arguments, resolves the current process identity, and
/// renders deterministic output (`DF-ROS-2026-A037`).
[<RequireQualifiedAccess>]
module StepCommands =
    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private optionValue name (arguments: string list) =
        arguments
        |> List.tryFindIndex ((=) name)
        |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

    let private optionValues name (arguments: string list) =
        arguments
        |> List.mapi (fun index value -> index, value)
        |> List.choose (fun (index, value) -> if value = name then arguments |> List.tryItem (index + 1) else None)

    let private stringField (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private intField (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number -> Some(value.GetValue<int>())
        | _ -> None

    let private render (json: bool) (node: JsonObject) =
        if json then
            printfn "%s" (node.ToJsonString jsonOptions)
        else
            printfn "%s  #%d  %s  %s"
                (stringField node "stepId" |> Option.defaultValue "?")
                (intField node "sequence" |> Option.defaultValue 0)
                (stringField node "status" |> Option.defaultValue "?")
                (stringField node "name" |> Option.defaultValue "")

        0

    let private renderResult json result =
        match result with
        | Ok node -> render json node
        | Error message ->
            eprintfn "ERROR %s" message
            2

    let private withIdentity arguments action =
        match FileTelemetryExecutionRepository.resolveIdentity (ProvenanceCommands.identityOverridesFrom arguments) with
        | Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok(actor, identity, _) -> action actor identity

    let private require name arguments =
        match optionValue name arguments with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"step command requires {name} VALUE"

    let private lifecycleRequest target arguments actor identity =
        require "--occurred-at" arguments
        |> Result.map (fun occurredAt ->
            { ExecutionId = optionValue "--execution" arguments
              StepId = optionValue "--id" arguments
              Target = target
              Reason = optionValue "--reason" arguments
              OccurredAt = occurredAt
              Actor = actor
              Identity = identity })

    let private transition target arguments actor identity =
        match lifecycleRequest target arguments actor identity with
        | Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok request -> FileStepRepository.transition Environment.CurrentDirectory request |> renderResult (arguments |> List.contains "--json")

    let private create status arguments actor identity =
        match require "--name" arguments, require "--occurred-at" arguments with
        | Error message, _
        | _, Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok name, Ok occurredAt ->
            FileStepRepository.create
                Environment.CurrentDirectory
                { ExecutionId = optionValue "--execution" arguments
                  Name = name
                  Description = optionValue "--description" arguments
                  Classifications = optionValues "--classification" arguments
                  ParentStepId = optionValue "--parent" arguments
                  Status = status
                  OccurredAt = occurredAt
                  Actor = actor
                  Identity = identity }
            |> renderResult (arguments |> List.contains "--json")

    let private parseFloat (field: string) (value: string) =
        match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, number -> Ok number
        | _ -> Error $"{field} requires a finite numeric value"

    let private confidenceNode arguments : JsonNode =
        match optionValue "--confidence" arguments with
        | None -> null
        | Some text ->
            match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value -> JsonValue.Create value
            | _ -> JsonValue.Create text

    let private optionToResult message value =
        match value with
        | Some item -> Ok item
        | None -> Error message

    let private recordMetric (arguments: string list) (actor: Actor) (identity: Identity) =
        match require "--metric" arguments, require "--value" arguments, (optionValue "--collected-at" arguments |> Option.orElse (optionValue "--occurred-at" arguments) |> optionToResult "step record requires --collected-at TIMESTAMP") with
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok metricId, Ok valueText, Ok collectedAt ->
            match parseFloat "--value" valueText with
            | Error message ->
                eprintfn "ERROR %s" message
                2
            | Ok value ->
                let source: CapabilitySource =
                    { Type = optionValue "--source-type" arguments |> Option.defaultValue "agent-report"
                      Name = optionValue "--source-name" arguments |> Option.defaultValue "step-metric"
                      Mechanism = optionValue "--source-mechanism" arguments |> Option.defaultValue "explicit" }

                FileStepRepository.recordMetric
                    Environment.CurrentDirectory
                    { ExecutionId = optionValue "--execution" arguments
                      StepId = optionValue "--id" arguments
                      MetricId = metricId
                      Value = value
                      Unit = optionValue "--unit" arguments
                      Currency = optionValue "--currency" arguments
                      Quality = optionValue "--quality" arguments |> Option.defaultValue "observed"
                      Confidence = confidenceNode arguments
                      CollectedAt = collectedAt
                      Source = source
                      PricingSource = optionValue "--pricing-source" arguments
                      PricingVersion = optionValue "--pricing-version" arguments
                      PricingEffectiveAt = optionValue "--pricing-effective-at" arguments
                      CalculationMethod = optionValue "--calculation-method" arguments
                      Model = optionValue "--pricing-model" arguments |> Option.orElse identity.Model
                      TokenMeasurementIds = optionValues "--token-measurement" arguments
                      Actor = actor
                      Identity = identity }
                |> renderResult (arguments |> List.contains "--json")

    let private availability (arguments: string list) (actor: Actor) (identity: Identity) =
        match require "--metric" arguments, require "--status" arguments, require "--reason" arguments, require "--occurred-at" arguments with
        | Error message, _, _, _
        | _, Error message, _, _
        | _, _, Error message, _
        | _, _, _, Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok metricId, Ok status, Ok reason, Ok occurredAt ->
            FileStepRepository.recordAvailability
                Environment.CurrentDirectory
                { ExecutionId = optionValue "--execution" arguments
                  StepId = optionValue "--id" arguments
                  MetricId = metricId
                  Status = status
                  Reason = reason
                  OccurredAt = occurredAt
                  Actor = actor
                  Identity = identity }
            |> renderResult (arguments |> List.contains "--json")

    let private checkpoint (arguments: string list) (actor: Actor) (identity: Identity) =
        match require "--phase" arguments, require "--occurred-at" arguments with
        | Error message, _
        | _, Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok phase, Ok occurredAt ->
            FileStepRepository.checkpoint
                Environment.CurrentDirectory
                { ExecutionId = optionValue "--execution" arguments
                  StepId = optionValue "--id" arguments
                  Phase = phase
                  MeasurementIds = optionValues "--measurement" arguments
                  SnapshotIds = optionValues "--snapshot" arguments
                  OccurredAt = occurredAt
                  Actor = actor
                  Identity = identity }
            |> renderResult (arguments |> List.contains "--json")

    let private link (arguments: string list) (actor: Actor) (identity: Identity) =
        match require "--kind" arguments, require "--value" arguments, require "--occurred-at" arguments with
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok kind, Ok value, Ok occurredAt ->
            FileStepRepository.link
                Environment.CurrentDirectory
                { ExecutionId = optionValue "--execution" arguments
                  StepId = optionValue "--id" arguments
                  Kind = kind
                  Value = value
                  Source = optionValue "--source" arguments |> Option.defaultValue "agent-report"
                  OccurredAt = occurredAt
                  Actor = actor
                  Identity = identity }
            |> renderResult (arguments |> List.contains "--json")

    let private listSteps arguments =
        let steps = FileStepRepository.list Environment.CurrentDirectory (optionValue "--execution" arguments) (optionValue "--work-item" arguments)

        if arguments |> List.contains "--json" then
            let output = JsonObject()
            let array = JsonArray()
            steps |> List.iter (fun step -> array.Add(step: JsonNode))
            output["steps"] <- array
            output["count"] <- JsonValue.Create steps.Length
            printfn "%s" (output.ToJsonString jsonOptions)
        else
            for step in steps do
                render false step |> ignore

        0

    let private showStep arguments =
        match optionValue "--id" arguments |> Option.orElse (arguments |> List.tryFind (fun value -> value.StartsWith("STEP-"))) with
        | None ->
            eprintfn "ERROR step show requires STEP-ID or --id STEP-ID"
            2
        | Some stepId -> FileStepRepository.show Environment.CurrentDirectory stepId |> renderResult true

    let private help () =
        printfn "step plan|begin --name NAME --occurred-at TS [--description TEXT] [--classification TYPE]* [--parent STEP] [--execution EXE]"
        printfn "step begin|resume --id STEP --occurred-at TS [--execution EXE]"
        printfn "step complete|block|abandon [--id STEP] --occurred-at TS [--reason TEXT] [--execution EXE]"
        printfn "step record --metric ID --value N --collected-at TS [--id STEP] [quality/cost provenance flags]"
        printfn "step availability --metric ID --status STATE --reason TEXT --occurred-at TS [--id STEP]"
        printfn "step checkpoint --phase begin|end (--measurement MEAS|--snapshot SNAP)+ --occurred-at TS [--id STEP]"
        printfn "step link --kind KIND --value VALUE --occurred-at TS [--id STEP]"
        printfn "step list [--execution EXE|--work-item ID] [--json]; step show STEP-ID"
        0

    let run root arguments =
        let previous = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- root

            match arguments with
            | []
            | [ "help" ]
            | [ "--help" ] -> help ()
            | "list" :: rest -> listSteps rest
            | "show" :: rest -> showStep rest
            | "plan" :: rest -> withIdentity rest (create StepStatus.Planned rest)
            | "begin" :: rest when optionValue "--id" rest |> Option.isSome -> withIdentity rest (transition StepStatus.Active rest)
            | "begin" :: rest -> withIdentity rest (create StepStatus.Active rest)
            | "resume" :: rest -> withIdentity rest (transition StepStatus.Active rest)
            | "complete" :: rest -> withIdentity rest (transition StepStatus.Completed rest)
            | "block" :: rest when optionValue "--reason" rest |> Option.isNone ->
                eprintfn "ERROR step block requires --reason TEXT"
                2
            | "block" :: rest -> withIdentity rest (transition StepStatus.Blocked rest)
            | "abandon" :: rest -> withIdentity rest (transition StepStatus.Abandoned rest)
            | "record" :: rest -> withIdentity rest (recordMetric rest)
            | "availability" :: rest -> withIdentity rest (availability rest)
            | "checkpoint" :: rest -> withIdentity rest (checkpoint rest)
            | "link" :: rest -> withIdentity rest (link rest)
            | _ ->
                eprintfn "ERROR unknown step command"
                help () |> ignore
                2
        finally
            Environment.CurrentDirectory <- previous
