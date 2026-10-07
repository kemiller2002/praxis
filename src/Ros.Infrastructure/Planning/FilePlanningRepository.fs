namespace Ros.Infrastructure.Planning

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Contracts.Work
open Ros.Domain.Planning
open Ros.Domain.Work
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work

/// The planner's read-only view of a repository: `.ros/work/queue.json`,
/// `.ros/context/current.json`, telemetry execution records and Git. It
/// opens files for reading and runs only read-only Git queries.
[<RequireQualifiedAccess>]
module FilePlanningRepository =
    let private queuePath (root: string) = Path.Combine(root, ".ros", "work", "queue.json")
    let private contextPath (root: string) = Path.Combine(root, ".ros", "context", "current.json")

    let private readObject (path: string) : JsonObject option =
        if File.Exists path then
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as value -> Some value
            | _ -> None
        else
            None

    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private number (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number -> Some(value.GetValue<decimal>())
        | _ -> None

    let private texts (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as values ->
            values
            |> Seq.choose (function
                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private objects (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as values -> values |> Seq.choose (function :? JsonObject as value -> Some value | _ -> None) |> Seq.toList
        | _ -> []

    let private child (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonObject as value -> Some value
        | _ -> None

    let readQueue (root: string) : Result<PlanningQueueItem list, string> =
        try
            readObject (queuePath root)
            |> Option.map (fun queue ->
                objects queue "items"
                |> List.choose (fun item ->
                    match text item "id", text item "status" with
                    | Some id, Some status ->
                        Some
                            { Id = id
                              Title = text item "title" |> Option.defaultValue id
                              Description = text item "description"
                              Tags = texts item "tags"
                              Priority = text item "priority"
                              Status = status
                              CreatedAt = text item "createdAt"
                              DependsOn = texts item "dependsOn" }
                    | _ -> None))
            |> Option.defaultValue []
            |> Ok
        with error ->
            Error $"cannot read {queuePath root}: {error.Message}"

    let private checkpointSummary (id: string) (item: JsonObject) : CheckpointSummary option =
        match item["latestCheckpoint"] with
        | null -> None
        | node ->
            // Only a checkpoint that re-verifies as a durable checkpoint counts.
            match CheckpointJson.tryParseProjection id node with
            | Ok(Some recorded) ->
                let checkpoint = recorded.Recorded
                let git = Ros.Domain.Work.DurableLocation.git checkpoint.Location

                Some
                    { CheckpointId = recorded.CheckpointId
                      ExecutionId = checkpoint.ExecutionId
                      RecordedAt = checkpoint.RecordedAt
                      Branch = git.Branch
                      Commit = git.LocalCommit.Value
                      Summary = checkpoint.Summary
                      NextAction = checkpoint.NextAction
                      Verified = true }
            | _ -> None

    let readLive (root: string) : Result<PlanningLiveItem list, string> =
        let path = contextPath root

        if not (File.Exists path) then
            Ok []
        else
            try
                let content = File.ReadAllText path

                WorkContextPlanContract.parseJson content
                |> Result.map (fun view ->
                    let raw =
                        match JsonNode.Parse content with
                        | :? JsonObject as document -> objects document "workItems" |> List.choose (fun item -> text item "id" |> Option.map (fun id -> id, item)) |> Map.ofList
                        | _ -> Map.empty

                    view.WorkItems
                    |> List.map (fun item ->
                        { Id = item.Id
                          State = item.SemanticState
                          BlockReason = item.BlockReason
                          UpdatedAt = item.UpdatedAt
                          Checkpoint = raw.TryFind item.Id |> Option.bind (checkpointSummary item.Id)
                          TelemetryExecutionIds = item.TelemetryExecutionIds }))
            with error ->
                Error $"cannot read {path}: {error.Message}"

    let private costKind (metric: JsonObject) =
        let sourceType = child metric "source" |> Option.bind (fun source -> text source "type") |> Option.defaultValue ""

        match text metric "quality" with
        | Some "observed" when sourceType.Contains("provider", StringComparison.OrdinalIgnoreCase) -> CostEvidenceKind.ProviderReported
        | Some "observed" -> CostEvidenceKind.Observed
        | Some "derived" -> CostEvidenceKind.Calculated
        | Some "estimated" -> CostEvidenceKind.Estimated
        | _ -> CostEvidenceKind.Unavailable

    let private execution (record: JsonObject) : HistoricalExecution option =
        match text record "executionId", text record "startedAt" with
        | Some executionId, Some startedAt ->
            let metrics = objects record "metrics"
            let latest (id: string) = metrics |> List.filter (fun metric -> text metric "id" = Some id) |> List.tryLast |> Option.bind (fun metric -> number metric "value")
            let identity = child record "identity"

            Some
                { ExecutionId = executionId
                  WorkItemId = text record "workItemId" |> Option.orElse (child record "links" |> Option.bind (fun links -> text links "workItemId")) |> Option.defaultValue ""
                  Status = if text record "status" = Some "finalized" then ExecutionStatus.Finalized else ExecutionStatus.Active
                  StartedAt = startedAt
                  FinalizedAt = text record "finalizedAt"
                  Classes = child record "classification" |> Option.map (fun classification -> texts classification "types") |> Option.defaultValue []
                  WallMs = latest "time.wall_ms" |> Option.map int64
                  BlockedMs = latest "time.blocked_ms" |> Option.map int64
                  Provider = identity |> Option.bind (fun value -> text value "provider") |> Option.defaultValue "unknown"
                  Runtime = identity |> Option.bind (fun value -> text value "runtime") |> Option.defaultValue "unknown"
                  Model = identity |> Option.bind (fun value -> text value "model")
                  Costs =
                    metrics
                    |> List.choose (fun metric ->
                        match text metric "id", number metric "value" with
                        | Some id, Some value when id.StartsWith("cost.", StringComparison.Ordinal) ->
                            Some
                                { MetricId = id
                                  Amount = value
                                  Currency = text metric "currency"
                                  Kind = costKind metric }
                        | _ -> None)
                  TokenMetrics = metrics |> List.filter (fun metric -> text metric "id" |> Option.exists (fun id -> id.StartsWith("tokens.", StringComparison.Ordinal))) |> List.length }
        | _ -> None

    let readExecutions (root: string) : HistoricalExecution list =
        FileTelemetryQueryRepository.readAll root
        |> List.choose execution
        |> List.sortWith (fun left right -> String.CompareOrdinal(left.ExecutionId, right.ExecutionId))

    let private repositoryName (root: string) =
        [ readObject (queuePath root); readObject (contextPath root) ]
        |> List.tryPick (Option.bind (fun document -> text document "repository"))
        |> Option.defaultValue (Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)))

    let readRepository (root: string) : RepositoryIdentity =
        let branch, commit = ProcessGitRepository.readBranchAndCommit root
        { Name = repositoryName root; Commit = commit; Branch = branch }

    /// The branch completed work integrates into, when the repository has one.
    let integrationRef (root: string) : string option =
        [ "origin/HEAD"; "origin/main"; "origin/master"; "main"; "master" ]
        |> List.tryFind (fun candidate ->
            match ProcessGitRepository.readLines root [ "rev-parse"; "--verify"; "--quiet"; candidate + "^{commit}" ] with
            | Ok(_ :: _) -> true
            | _ -> false)

    let private mergedPullRequest = Regex(@"^Merge pull request #(\d+)|\(#(\d+)\)\s*$", RegexOptions.CultureInvariant)

    /// Evidence Git itself shows: pull requests merged into the integration
    /// branch (merge or squash subjects), existing tags, and checkpoint
    /// commits already reachable from the integration branch.
    let readRepositoryObservations (root: string) (live: PlanningLiveItem list) : Observation list =
        match integrationRef root with
        | None -> []
        | Some integration ->
            let git reference = Provenance.create EvidenceSource.Git reference

            let pullRequests =
                match ProcessGitRepository.readLines root [ "log"; "--format=%s"; integration ] with
                | Ok subjects ->
                    subjects
                    |> List.choose (fun subject ->
                        let found = mergedPullRequest.Match subject

                        if not found.Success then None
                        else
                            let digits = if found.Groups[1].Success then found.Groups[1].Value else found.Groups[2].Value
                            Some(int digits))
                    |> List.distinct
                    |> List.sort
                    |> List.map (fun number -> { Kind = ObservationKind.PullRequestMerged number; Provenance = git $"git log {integration}: PR #{number}" })
                | Error _ -> []

            let tags =
                match ProcessGitRepository.readLines root [ "tag"; "--list" ] with
                | Ok tags -> tags |> List.sortWith (fun left right -> String.CompareOrdinal(left, right)) |> List.map (fun tag -> { Kind = ObservationKind.ReleaseExists tag; Provenance = git $"git tag {tag}" })
                | Error _ -> []

            let history = ProcessGitRepository.createHistory root

            // The integration branch's own name (origin/HEAD -> main). A
            // checkpoint recorded on that branch is reachable from it by
            // construction, which says nothing about the work being finished.
            let integrationBranch =
                let symbolic =
                    match ProcessGitRepository.readLines root [ "rev-parse"; "--abbrev-ref"; integration ] with
                    | Ok [ name ] -> name
                    | _ -> integration

                if symbolic.StartsWith("origin/", StringComparison.Ordinal) then symbolic.Substring("origin/".Length) else symbolic

            let checkpoints =
                live
                |> List.choose (fun item -> item.Checkpoint)
                |> List.filter (fun checkpoint -> checkpoint.Branch <> integrationBranch)
                |> List.distinctBy (fun checkpoint -> checkpoint.Commit)
                |> List.sortWith (fun left right -> String.CompareOrdinal(left.Commit, right.Commit))
                |> List.choose (fun checkpoint ->
                    match history.IsAncestor checkpoint.Commit integration with
                    | Ok true ->
                        Some
                            { Kind = ObservationKind.CommitMerged(checkpoint.Commit, integration)
                              Provenance = git $"git merge-base --is-ancestor {checkpoint.Commit} {integration}" }
                    | _ -> None)

            pullRequests @ tags @ checkpoints

    let private readOptionalFile (path: string option) (parse: string -> string -> Result<'a, string>) (fallback: 'a) : Result<'a, string> =
        match path with
        | None -> Ok fallback
        | Some path when not (File.Exists path) -> Error $"{path} does not exist"
        | Some path -> parse path (File.ReadAllText path)

    /// `.ros/work/groups.json`: human-declared groups (PRX-GRP-073).
    let groupStorePath (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

    /// The stored declarations; no store means none.
    let readStoredGroups (root: string) : Result<StoredGroup list, string> =
        let path = groupStorePath root

        if File.Exists path then
            PlanningJson.parseGroupStore (File.ReadAllText path) |> Result.mapError (fun message -> $"{path}: {message}")
        else
            Ok []

    /// The planner reads stored declarations exactly as `grouping.groups`.
    let private readConfiguration (root: string) (configurationFile: string option) =
        readOptionalFile configurationFile (fun _ content -> PlanningJson.parseConfiguration content) PlannerConfiguration.defaults
        |> Result.bind (fun configuration -> readStoredGroups root |> Result.map (fun stored -> GroupDeclaration.mergeInto stored configuration))

    let create (root: string) (observationsFile: string option) (configurationFile: string option) : PlanningReadPort =
        { Repository = fun () -> readRepository root
          Queue = fun () -> readQueue root
          Live = fun () -> readLive root
          Executions = fun () -> readExecutions root
          RepositoryObservations = readRepositoryObservations root
          SuppliedObservations = fun () -> readOptionalFile observationsFile PlanningJson.parseObservations []
          Configuration = fun () -> readConfiguration root configurationFile }
