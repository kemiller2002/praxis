namespace Praxis.Infrastructure.Planning

open System.IO
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// PRX-GRP-153: a session that carries a group execution is ingested once,
/// into the group execution, as content-free metric values (PRX-GRP-157).
/// Its snapshot ID guarantees no member execution counts it too: a snapshot
/// a member execution already holds is refused here, and a snapshot a group
/// execution holds is refused by member ingestion.
[<RequireQualifiedAccess>]
module FileGroupTelemetry =
    type IngestOutcome =
        { GroupId: string
          GroupExecutionId: string
          SnapshotId: string
          Changed: bool
          Metrics: GroupMetric list }

    let ingest (root: string) (groupExecutionId: string) (adapter: string) (inputPath: string) : Result<IngestOutcome, string> =
        let path = if Path.IsPathRooted inputPath then inputPath else Path.Combine(root, inputPath)

        let text =
            try
                if File.Exists path then Ok(File.ReadAllText path) else Error $"{inputPath} does not exist"
            with :? IOException as error ->
                Error $"cannot read {inputPath}: {error.Message}"

        text
        |> Result.bind (FileTelemetryFinalizationRepository.adaptToMetrics root adapter)
        |> Result.bind (fun (snapshotId, metrics) ->
            match FileTelemetryFinalizationRepository.snapshotIngested root snapshotId with
            | Some executionId ->
                Error $"telemetry snapshot '{snapshotId}' was already ingested into member execution {executionId}; a shared session is counted once (PRX-GRP-153)"
            | None ->
                let recorded =
                    metrics
                    |> List.map (fun (id, value, unit, currency, quality) -> { MetricId = id; Value = value; Unit = unit; Currency = currency; Quality = quality })

                FileWorkGroupRepository.transact root false (fun groups ->
                    match groups |> List.tryFind (fun group -> group.Executions |> List.exists (fun execution -> execution.Id = groupExecutionId)) with
                    | None -> Error $"group execution {groupExecutionId} is not recorded"
                    | Some group ->
                        let execution = group.Executions |> List.find (fun execution -> execution.Id = groupExecutionId)

                        let outcome changed =
                            { GroupId = group.Declaration.Id
                              GroupExecutionId = groupExecutionId
                              SnapshotId = snapshotId
                              Changed = changed
                              Metrics = recorded }

                        if execution.Telemetry |> List.exists (fun snapshot -> snapshot.SnapshotId = snapshotId) then
                            Ok(groups, outcome false)
                        else
                            let snapshot =
                                { SnapshotId = snapshotId
                                  Adapter = adapter
                                  CollectedAt = FileWorkGroupFacts.now ()
                                  Metrics = recorded }

                            let updated =
                                { group with
                                    Executions =
                                        group.Executions
                                        |> List.map (fun existing -> if existing.Id = groupExecutionId then { existing with Telemetry = existing.Telemetry @ [ snapshot ] } else existing) }

                            Ok(WorkGroups.upsert groups updated, outcome true))
                |> Result.bind id)
