namespace Praxis.Cli

open Praxis.Contracts.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// `telemetry ingest GEX-ID --input FILE [--adapter NAME] [--json]`
/// (PRX-GRP-153): a shared session ingested once into a group execution.
[<RequireQualifiedAccess>]
module GroupTelemetryCommands =
    let usage = "telemetry ingest GEX-ID --input FILE [--adapter NAME] [--json]"

    let run (root: string) (groupExecutionId: string) (rawArguments: string list) =
        let command = "telemetry ingest"
        let arguments = WorkGroupCommands.parse [ "--input"; "--adapter" ] [] rawArguments
        let asJson = arguments.Switches.Contains "--json"
        let value flag = arguments.Values |> Map.tryFind flag |> Option.defaultValue []

        match value "--input", value "--adapter", arguments.Unexpected @ arguments.Positional with
        | [ input ], adapters, [] when adapters.Length <= 1 ->
            match FileGroupTelemetry.ingest root groupExecutionId (adapters |> List.tryHead |> Option.defaultValue "generic") input with
            | Error message -> WorkGroupCommands.reportFailure asJson command message
            | Ok outcome ->
                if asJson then
                    WorkGroupCommands.printJson (
                        WorkGroupCommands.envelope
                            command
                            (if outcome.Changed then "recorded" else "unchanged")
                            [ "groupId", WorkGroupJson.text outcome.GroupId
                              "groupExecutionId", WorkGroupJson.text outcome.GroupExecutionId
                              "snapshotId", WorkGroupJson.text outcome.SnapshotId
                              "changed", WorkGroupJson.boolean outcome.Changed
                              "metrics", WorkGroupJson.texts (outcome.Metrics |> List.map (fun metric -> $"{metric.MetricId}={metric.Value}")) ]
                    )
                else
                    let verb = if outcome.Changed then "ingested" else "already ingested (unchanged)"
                    printfn "snapshot %s %s into %s of %s: %d metric(s)" outcome.SnapshotId verb outcome.GroupExecutionId outcome.GroupId outcome.Metrics.Length
                    if outcome.Changed then printfn "Praxis state changed in %s; commit and push it." FileWorkGroupRepository.relativePath

                0
        | _ -> WorkGroupCommands.reportArgumentErrors command usage [ $"{command} GEX-ID needs exactly one --input FILE and at most one --adapter NAME" ]
