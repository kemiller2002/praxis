namespace Praxis.Cli

open System.Text.Json.Nodes
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// `work group cost GROUP-ID [--json]` (PRX-GRP-154, 156, 158): read-only.
/// Each group execution's shared total apportioned over its members, and
/// its frozen prediction against the observed outcome.
[<RequireQualifiedAccess>]
module WorkGroupCostCommands =
    let usage = "work group cost GROUP-ID [--json]"

    let private amount (value: decimal option) : JsonNode = value |> Option.map (fun number -> JsonValue.Create number :> JsonNode) |> Option.toObj

    let private line (cost: CostLine) : JsonNode =
        WorkGroupJson.record [ "member", WorkGroupJson.optionalText cost.Member; "amount", amount cost.Amount; "quality", WorkGroupJson.text cost.Quality ]

    let private shown (value: decimal option) = value |> Option.map string |> Option.defaultValue "unknown"

    let run (root: string) (rawArguments: string list) =
        let command = "work group cost"
        let arguments = WorkGroupCommands.parse [] [] rawArguments
        let asJson = arguments.Switches.Contains "--json"

        match arguments.Positional, arguments.Unexpected with
        | [ groupId ], [] ->
            match FileWorkGroupRepository.read root |> Result.map (fun groups -> WorkGroups.tryFind groups groupId) with
            | Error message -> WorkGroupCommands.reportFailure asJson command message
            | Ok None ->
                WorkGroupCommands.reportRejections asJson command groupId [ GroupRejection.UnknownGroup groupId ]
            | Ok(Some group) ->
                let rows = FileGroupExecution.apportionments root group

                if asJson then
                    let execution (record: GroupExecutionRecord, split: CostApportionment) : JsonNode =
                        WorkGroupJson.record
                            [ "groupExecutionId", WorkGroupJson.text record.Id
                              "method", WorkGroupJson.text split.Method
                              "total", amount split.Total
                              "currency", WorkGroupJson.optionalText split.Currency
                              "direct", split.Direct |> List.map line |> WorkGroupJson.array
                              "groupShared", line split.GroupShared
                              "allocated", split.Allocated |> List.map line |> WorkGroupJson.array
                              "statement", WorkGroupJson.text split.Statement
                              "prediction", (record.Prediction |> Option.map WorkGroupJson.predictionNode |> Option.toObj)
                              "outcome", (record.Outcome |> Option.map WorkGroupJson.outcomeNode |> Option.toObj) ]

                    WorkGroupCommands.printJson (
                        WorkGroupCommands.envelope command "found" [ "groupId", WorkGroupJson.text groupId; "groupExecutions", rows |> List.map execution |> WorkGroupJson.array ]
                    )
                else
                    match rows with
                    | [] -> printfn "%s has no group executions" groupId
                    | rows ->
                        for record, split in rows do
                            printfn "%s (%s): %s" record.Id (ExecutionMode.code record.Mode) split.Statement

                            for cost in split.Allocated do
                                printfn "  %-24s %s (allocated)" (cost.Member |> Option.defaultValue "-") (shown cost.Amount)

                            printfn "  %-24s %s" "group-shared residue" (shown split.GroupShared.Amount)
                            record.Outcome |> Option.iter (fun outcome -> printfn "  outcome: %s" outcome.Statement)

                0
        | _ -> WorkGroupCommands.reportArgumentErrors command usage [ $"{command} takes exactly one GROUP-ID" ]
