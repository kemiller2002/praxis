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
    let private latestValue (record: JsonObject) (metricId: string) : decimal option =
        match record["metrics"] with
        | :? JsonArray as metrics ->
            metrics
            |> Seq.choose (function
                | :? JsonObject as metric when (match metric["id"] with :? JsonValue as id -> id.GetValue<string>() = metricId | _ -> false) ->
                    match metric["value"] with
                    | :? JsonValue as value -> Some(value.GetValue<decimal>())
                    | _ -> None
                | _ -> None)
            |> Seq.tryLast
        | _ -> None

    let private latestMetric (record: JsonObject) (metricId: string) : int option = latestValue record metricId |> Option.map int

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
                  Prediction =
                    planned
                    |> Option.map (fun planned ->
                        fun mode (members: string list) ->
                            let arm = if mode = ExecutionMode.Grouped then planned.Pricing.Grouped else planned.Pricing.Independent
                            let count = decimal members.Length

                            let durations =
                                members |> List.map (fun id -> analysis.Items |> List.tryFind (fun item -> item.Id = id) |> Option.map (fun item -> item.FullDuration))

                            let sum (pick: Estimate<int64> -> int64 option) =
                                if durations |> List.forall (Option.bind pick >> Option.isSome) then
                                    Some(durations |> List.sumBy (Option.bind pick >> Option.defaultValue 0L) |> decimal)
                                else
                                    None

                            { Mode = mode
                              Members = members
                              Cost =
                                { Lower = arm.CostRange |> Option.map (fun (low, _) -> low * count)
                                  Upper = arm.CostRange |> Option.map (fun (_, high) -> high * count)
                                  Basis = $"{ExecutionMode.code mode} per-member cost range x {members.Length} members ({arm.Coverage})" }
                              Currency = arm.Currency
                              DurationMs =
                                { Lower = sum (fun estimate -> estimate.Lower)
                                  Upper = sum (fun estimate -> estimate.Upper)
                                  Basis = "sum of the members' planner duration estimates" } })
                  Outcome =
                    Some(fun (execution: GroupExecutionRecord) (endedAt: string) ->
                        let memberCost (begun: GroupExecutionMember) =
                            FileTelemetryQueryRepository.readByExecutionId root begun.ExecutionId
                            |> Result.toOption
                            |> Option.bind (fun record -> latestValue record "cost.execution_total")

                        let cost, currency =
                            match GroupCost.sharedTotal execution "cost.execution_total" with
                            | Some(total, currency) -> Some total, currency
                            | None ->
                                let costs = execution.Members |> List.map memberCost
                                if not costs.IsEmpty && costs |> List.forall Option.isSome then Some(costs |> List.sumBy Option.get), None else None, None

                        let duration = Option.map2 (fun (started: DateTimeOffset) (ended: DateTimeOffset) -> int64 (ended - started).TotalMilliseconds) (parse execution.StartedAt) (parse endedAt)

                        let within (range: PredictedRange) (observed: decimal option) =
                            match range.Lower, range.Upper, observed with
                            | Some low, Some high, Some value -> Some(value >= low && value <= high)
                            | _ -> None

                        let costWithin = execution.Prediction |> Option.bind (fun prediction -> within prediction.Cost cost)
                        let durationWithin = execution.Prediction |> Option.bind (fun prediction -> within prediction.DurationMs (duration |> Option.map decimal))
                        let describe (value: bool option) = match value with Some true -> "within" | Some false -> "outside" | None -> "not comparable (unknown)"

                        { EndedAt = endedAt
                          MembersBegun = execution.Members.Length
                          Cost = cost
                          Currency = currency
                          DurationMs = duration
                          CostWithinPrediction = costWithin
                          DurationWithinPrediction = durationWithin
                          Statement = $"cost {describe costWithin} the frozen prediction; duration {describe durationWithin} it (PRX-GRP-158)" })
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
        // ECIR groups cannot enter the legacy execution route without
        // pinned Ordo validation and independent decision authorization.
        // Fail before the CLI performs work begin or any repository mutation.
        |> Result.bind (fun (facts, configuration, repository) ->
            match facts.Group with
            | Some group when EcirGates.isEcirGroup group.Declaration.SharedContext ->
                Error "ECIR group execution refused: pinned Ordo verification and independent decision authorization are not yet integrated; nothing was begun."
            | _ -> Ok(facts, configuration, repository))

    /// The execution `work begin` just started for a member: the last one
    /// its live context lists.
    let memberExecution (root: string) (workItemId: string) : string option =
        FilePlanningRepository.readLive root
        |> Result.toOption
        |> Option.bind (List.tryFind (fun item -> item.Id = workItemId))
        |> Option.bind (fun item -> item.TelemetryExecutionIds |> List.tryLast)

    /// PRX-GRP-154: each group execution's shared total apportioned over the
    /// members it began: direct usage from each member's own
    /// `cost.execution_total`, the remainder group-shared, allocated
    /// `equal-share` and labelled `allocated`. Unknown totals stay unknown.
    let apportionments (root: string) (group: StoredWorkGroup) : (GroupExecutionRecord * CostApportionment) list =
        group.Executions
        |> List.map (fun execution ->
            let byMember =
                execution.Members
                |> List.map (fun begun ->
                    begun.WorkItemId,
                    FileTelemetryQueryRepository.readByExecutionId root begun.ExecutionId
                    |> Result.toOption
                    |> Option.bind (fun record -> latestValue record "cost.execution_total"))
                |> Map.ofList

            let total, currency =
                match GroupCost.sharedTotal execution "cost.execution_total" with
                | Some(amount, currency) -> Some amount, currency
                | None -> None, None

            execution, GroupCost.apportion total currency (execution.Members |> List.map (fun begun -> begun.WorkItemId)) (fun id -> byMember.TryFind id |> Option.flatten))
