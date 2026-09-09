namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Json
open Ros.Domain.Telemetry

/// Reads the same `.ros/telemetry/executions/*.json` records production's
/// `showTelemetry`/`loadExecutions` observe, so the shadow telemetry
/// resolution CLI can be given real candidate-execution evidence instead of
/// only synthetic `--candidate` flags. Read-only: it never creates, links, or
/// finalizes an execution record.
[<RequireQualifiedAccess>]
module FileTelemetryStateRepository =
    let private parseStatus (value: string) =
        match value with
        | "active" -> Some ExecutionStatus.Active
        | "finalized" -> Some ExecutionStatus.Finalized
        | _ -> None

    let private readCandidate (path: string) =
        use document = JsonDocument.Parse(File.ReadAllText path)
        let root = document.RootElement

        parseStatus (root.GetProperty("status").GetString())
        |> Option.map (fun status ->
            { ExecutionId = root.GetProperty("executionId").GetString()
              WorkItemId = root.GetProperty("workItemId").GetString()
              Status = status })

    /// Every execution record for the given work item, in the same
    /// lexicographic-by-executionId order production's `executionFiles`
    /// sort produces.
    let readCandidates (root: string) (workItemId: string) : ExecutionLinkCandidate list =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")

        if not (Directory.Exists directory) then
            []
        else
            Directory.GetFiles(directory, "*.json")
            |> Array.sort
            |> Array.choose readCandidate
            |> Array.filter (fun candidate -> candidate.WorkItemId = workItemId)
            |> Array.toList
