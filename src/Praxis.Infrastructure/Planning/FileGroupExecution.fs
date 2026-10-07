namespace Praxis.Infrastructure.Planning

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open Praxis.Contracts.Work
open Praxis.Domain.Planning
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Json
open Praxis.Infrastructure.Work

/// The read side of `plan execute-group` (PRX-GRP-117, 136): the planner's
/// view of the group, every recorded group execution, and the
/// context-pressure signals recorded for this group's open execution. It
/// writes nothing; the command records through `FileWorkGroupRepository`.
[<RequireQualifiedAccess>]
module FileGroupExecution =
    /// A metric's latest recorded value on an execution record; `None` when
    /// it was never recorded (unknown, never zero).
    let private latestMetric (record: JsonObject) (metricId: string) : int option =
        match record["metrics"] with
        | :? JsonArray as metrics ->
            metrics
            |> Seq.choose (function
                | :? JsonObject as metric when (match metric["id"] with :? JsonValue as id -> id.GetValue<string>() = metricId | _ -> false) ->
                    match metric["value"] with
                    | :? JsonValue as value -> Some(int (value.GetValue<decimal>()))
                    | _ -> None
                | _ -> None)
            |> Seq.tryLast
        | _ -> None

    let private text (record: JsonObject) (name: string) =
        match record[name] with
        | :? JsonValue as value when value.GetValueKind() = Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private parse (value: string) =
        match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
        | true, at -> Some at
        | _ -> None

    /// `GEX-<timestamp>-<digest>`: deterministic for the same group, time
    /// and actor.
    let newIdentifier (groupId: string) (occurredAt: string) (actor: Actor) =
        let at = parse occurredAt |> Option.defaultValue DateTimeOffset.UnixEpoch
        GroupExecutions.identifier at (CanonicalJson.sha256HexPrefix 8 (String.concat "\u0000" [ groupId; occurredAt; actor.Id ]))

    /// The facts `GroupExecutions.decide` reads, as of now.
    let facts (root: string) (configurationFile: string option) (groupId: string) (actor: Actor) : Result<ExecuteGroupFacts * PlannerConfiguration * string, string> =
        let resolve (path: string) = if Path.IsPathRooted path then path else Path.GetFullPath(Path.Combine(root, path))
        let port = FilePlanningRepository.create root None (configurationFile |> Option.map resolve)
        let now = FileWorkGroupFacts.now ()

        FileWorkGroupRepository.read root
        |> Result.bind (fun groups ->
            Praxis.Application.Planning.PlanningOperations.analyze port now "execute-group"
            |> Result.map (fun (input, analysis) ->
                let report = Grouping.recommend input analysis
                let group = WorkGroups.tryFind groups groupId
                let planned = report.Groups |> List.tryFind (fun candidate -> WorkGroupId.value candidate.Id = groupId)
                let settings = input.Configuration.Grouping.GroupedExecution

                let fallback =
                    group
                    |> Option.bind (fun group -> group.Executions |> List.filter (fun execution -> GroupExecutions.isOpen execution && GroupExecutions.ownedBy actor execution) |> List.tryLast)
                    |> Option.bind (fun execution ->
                        let record executionId = FileTelemetryQueryRepository.readByExecutionId root executionId |> Result.toOption

                        let metrics executionId =
                            match record executionId with
                            | Some record -> latestMetric record "context.compactions", latestMetric record "context.repeated_file_reads"
                            | None -> None, None

                        let elapsed workItemId =
                            let begun = execution.Members |> List.tryFind (fun entry -> entry.WorkItemId = workItemId)

                            let taken =
                                begun
                                |> Option.bind (fun entry ->
                                    let ended =
                                        record entry.ExecutionId
                                        |> Option.bind (fun record -> text record "finalizedAt")
                                        |> Option.defaultValue now

                                    Option.map2 (fun (started: DateTimeOffset) (finished: DateTimeOffset) -> int64 (finished - started).TotalMilliseconds) (parse entry.BegunAt) (parse ended))

                            let upper = analysis.Items |> List.tryFind (fun item -> item.Id = workItemId) |> Option.bind (fun item -> item.FullDuration.Upper)
                            taken, upper

                        let pressure =
                            input.Observations
                            |> List.choose (fun observation ->
                                match observation.Kind with
                                | ObservationKind.ContextPressure(members, indicators) -> Some(members, indicators, observation.Provenance.Reference)
                                | _ -> None)

                        GroupExecutions.fallbackSignal settings execution metrics elapsed pressure now)

                { Group = group
                  Planned = planned
                  Cycles = Graph.cycles analysis.Items
                  AllExecutions = groups |> List.collect (fun stored -> stored.Executions)
                  WaitsOn = fun id -> (MemberFacts.ofAnalysis analysis (fun _ -> MemberStanding.Unknown) id).WaitsOn
                  Successors =
                    group
                    |> Option.bind (fun group -> group.Executions |> List.filter (fun execution -> GroupExecutions.isOpen execution && GroupExecutions.ownedBy actor execution) |> List.tryLast)
                    |> Option.map (fun execution ->
                        execution.Members
                        |> List.collect (fun begun ->
                            FileTelemetryQueryRepository.readByWorkItemId root begun.WorkItemId
                            |> List.choose (fun record ->
                                let parent =
                                    match record["identity"] with
                                    | :? JsonObject as identity -> text identity "parentExecutionId"
                                    | _ -> None

                                match parent, text record "executionId" with
                                | Some parent, Some successor when parent = begun.ExecutionId -> Some(begun.WorkItemId, successor)
                                | _ -> None)))
                    |> Option.defaultValue []
                  Fallback = fallback },
                input.Configuration,
                input.Repository))

    /// The execution `work begin` just started for a member: the last one
    /// its live context lists.
    let memberExecution (root: string) (workItemId: string) : string option =
        FilePlanningRepository.readLive root
        |> Result.toOption
        |> Option.bind (List.tryFind (fun item -> item.Id = workItemId))
        |> Option.bind (fun item -> item.TelemetryExecutionIds |> List.tryLast)
