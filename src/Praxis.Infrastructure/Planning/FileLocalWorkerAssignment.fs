namespace Praxis.Infrastructure.Planning

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Contracts.Execution
open Praxis.Contracts.Work
open Praxis.Domain.Execution
open Praxis.Domain.Work
open Praxis.Infrastructure.Git
open Praxis.Infrastructure.Work

/// Read under the host's native leases. This binds recorded assignments, not
/// containment, semantic acceptance or authority supplied by worker output.
[<RequireQualifiedAccess>]
module FileLocalWorkerAssignment =
    let private id (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 256
        && value |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_')

    let private read path =
        if not (LocalHostJournalFiles.noLinks path) then invalidOp "native assignment path is linked"
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        if input.Length > 16777216L then invalidOp "native assignment file is oversized"
        use output = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 8192
        let mutable finished = false
        while not finished do
            let count = input.Read(buffer, 0, min buffer.Length (16777217 - int output.Length))
            if count = 0 then finished <- true
            else
                output.Write(buffer, 0, count)
                if output.Length > 16777216L then invalidOp "native assignment file is oversized"
        let text = UTF8Encoding(false, true).GetString(output.ToArray())
        use document = JsonDocument.Parse text
        let rec unique (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Object ->
                let fields = element.EnumerateObject() |> Seq.toList
                if fields.Length <> (fields |> List.map _.Name |> Set.ofList).Count then invalidOp "duplicate native assignment JSON field"
                fields |> List.iter (fun field -> unique field.Value)
            | JsonValueKind.Array -> element.EnumerateArray() |> Seq.iter unique
            | _ -> ()
        unique document.RootElement
        text

    let private envelope root executionId =
        let node = read (Path.Combine(root, ".ros", "executions", executionId, "envelope.json")) |> JsonNode.Parse
        if node["schema"].GetValue<string>() <> "ordo.execution/1" then invalidOp "native execution schema differs"
        ExecutionJson.readEnvelope node |> function Ok value -> value | Error error -> invalidOp error

    let validate root repositoryIdentity (packet: LocalWorkerPacket) =
        try
            let require condition reason = if not condition then invalidOp reason
            require ((LocalAgentHandoff.packetProblems packet).IsEmpty) "native assignment packet is malformed"
            let config = read (Path.Combine(root, "ros.json")) |> JsonNode.Parse
            let configured =
                match config["repository"] with
                | :? JsonObject as repository -> repository["id"].GetValue<string>()
                | _ -> config["name"].GetValue<string>()
            require (packet.RepositoryIdentity = repositoryIdentity && configured = repositoryIdentity) "native assignment repository differs"
            require ([ packet.ParentExecutionId; packet.ChildExecutionId ] @ (packet.Members |> List.map _.ExecutionId) |> List.forall id) "native assignment execution identity is not a portable filename"
            let groups = read (FileWorkGroupRepository.path root) |> WorkGroupJson.readGroupStore |> function Ok store -> store.Groups | Error error -> invalidOp error
            let group = match groups |> List.filter (fun g -> g.Declaration.Id = packet.GroupId) with [ g ] -> g | _ -> invalidOp "native assignment group is missing or ambiguous"
            let members = packet.Members |> List.map _.WorkItemId |> Set.ofList
            require (group.HomeRepository = Some repositoryIdentity && not group.Declaration.CrossRepository
                     && group.Declaration.Members.Length = packet.Members.Length
                     && Set.ofList group.Declaration.Members = members) "native assignment group membership or home differs"
            let links = packet.Members |> List.map (fun m -> m.WorkItemId, m.ExecutionId) |> Set.ofList
            let runs = group.Executions |> List.filter (fun e ->
                e.GroupId = packet.GroupId && e.Repository = repositoryIdentity
                && (e.Members |> List.map (fun m -> m.WorkItemId, m.ExecutionId) |> Set.ofList) = links)
            require (runs.Length = 1 && runs.Head.Members.Length = packet.Members.Length) "native assignment group execution links differ or are ambiguous"
            let context = read (Path.Combine(root, ".ros", "context", "current.json")) |> WorkContextPlanContract.parseJson |> function Ok view -> view.WorkItems | Error error -> invalidOp error
            let telemetryRoot = Path.Combine(root, ".ros", "telemetry", "executions")
            require (LocalHostJournalFiles.noLinks telemetryRoot) "native telemetry directory is linked"
            let files = Directory.GetFiles(telemetryRoot, "*.json")
            require (files.Length <= 4096) "native telemetry inventory exceeds assignment bound"
            let telemetry = files |> Array.map (fun file ->
                let node = read file |> JsonNode.Parse
                let executionId = node["executionId"].GetValue<string>()
                require (Path.GetFileNameWithoutExtension file = executionId) "native telemetry filename differs from identity"
                let status = node["status"].GetValue<string>()
                require (status = "active" || status = "finalized") "native telemetry state is unknown"
                executionId, node["workItemId"].GetValue<string>(), status)
            for memberItem in packet.Members do
                let item = match context |> List.filter (fun i -> i.Id = memberItem.WorkItemId) with [ i ] -> i | _ -> invalidOp "native member is missing or duplicated"
                require (item.SemanticState = LiveWorkState.Active && List.contains memberItem.ExecutionId item.TelemetryExecutionIds
                         && item.TelemetryExecutionIds.Length = (Set.ofList item.TelemetryExecutionIds).Count) "native member is inactive or execution link changed"
                let active = telemetry |> Array.filter (fun (_, work, state) -> work = memberItem.WorkItemId && state = "active")
                require (active = [| memberItem.ExecutionId, memberItem.WorkItemId, "active" |]) "native member active telemetry differs or is ambiguous"
                let execution = envelope root memberItem.ExecutionId
                require (execution.ExecutionId = memberItem.ExecutionId && execution.WorkItem = repositoryIdentity + ":" + memberItem.WorkItemId
                         && execution.State = ExecutionState.Active && execution.BaselineRevision = packet.SourceCommit) "native member envelope or baseline differs"
            let parent, child = envelope root packet.ParentExecutionId, envelope root packet.ChildExecutionId
            require (parent.ExecutionId = packet.ParentExecutionId && parent.State = ExecutionState.Active
                     && parent.WorkItem.StartsWith(repositoryIdentity + ":", StringComparison.Ordinal)) "native coordinator envelope differs"
            require (child.ExecutionId = packet.ChildExecutionId && child.State = ExecutionState.Active
                     && child.WorkItem = parent.WorkItem && child.Parent = Some parent.ExecutionId
                     && child.Authority.Role = packet.Role && child.Actor.Id = packet.WorkerId
                     && child.BaselineRevision = packet.SourceCommit) "native worker lineage, role, identity or baseline differs"
            ProcessGitRepository.readLines root [ "rev-parse"; "--verify"; packet.SourceCommit + "^{commit}" ]
            |> Result.mapError _.Message |> Result.bind (fun commits ->
                if commits <> [ packet.SourceCommit ] then Error "native source commit differs"
                else ProcessGitRepository.readLines root [ "merge-base"; "--is-ancestor"; packet.SourceCommit; "HEAD" ]
                     |> Result.mapError _.Message |> Result.map ignore)
        with e -> Error("native local assignment refused: " + e.GetType().Name + ": " + e.Message)
