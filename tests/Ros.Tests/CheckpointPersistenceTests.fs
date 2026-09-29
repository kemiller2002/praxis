namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Work
open Ros.Domain.Git
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Work

/// `work.checkpointed` events, the `latestCheckpoint` projection, backward
/// compatibility, and offline structural validation (PRAXIS-CONT-03).
[<RequireQualifiedAccess>]
module CheckpointPersistenceTests =
    let private actor =
        { Kind = ActorKind.Agent
          Id = "example/agent-a"
          Provider = Some "example"
          Model = Some "unknown"
          Runtime = Some "agent-a" }

    let private contextJson (items: string) =
        $"""{{"schemaVersion":"1.0.0","repository":"fixture","workItems":[{items}],"protocolVersion":"1.0.0","actor":"x","updatedAt":"2026-09-29T09:00:00.000Z","startedAt":"2026-09-29T09:00:00.000Z","baselineDirtyPaths":[]}}"""

    let private activeItem id =
        $"""{{"id":"{id}","type":"task","state":"active","semanticState":"active","evidence":[],"updatedAt":"2026-09-29T09:00:00.000Z","telemetryExecutionIds":["EXE-A"]}}"""

    let private executionJson id workItem startedAt =
        $"""{{"schemaVersion":"1.0.0","executionId":"{id}","workItemId":"{workItem}","status":"active","startedAt":"{startedAt}","identity":{{"provider":"example","runtime":"agent-a","sessionId":"s-a","actorKind":"agent"}},"events":[{{"type":"step.started","stepId":"step-1","occurredAt":"{startedAt}"}}],"repository":{{"start":{{"commit":"{String.replicate 40 "0"}"}}}}}}"""

    let private withFixture (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-checkpoint-persist-{Guid.NewGuid():N}")
        Directory.CreateDirectory(Path.Combine(root, ".ros", "context")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, ".ros", "events")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, ".ros", "telemetry", "executions")) |> ignore
        File.WriteAllText(Path.Combine(root, "ros.json"), """{"repository":{"id":"fixture"},"workProtocol":{"version":"1.0.0"}}""")
        File.WriteAllText(Path.Combine(root, ".ros", "context", "current.json"), contextJson (activeItem "W-1"))
        File.WriteAllText(Path.Combine(root, ".ros", "events", "events.jsonl"), "")

        File.WriteAllText(
            Path.Combine(root, ".ros", "telemetry", "executions", "EXE-A.json"),
            executionJson "EXE-A" "W-1" "2026-09-29T09:00:00.000Z"
        )

        try
            run root
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ()

    let private checkpointAt (digit: char) (occurredAt: string) (step: string option) =
        let commit = CommitId.tryParse (String(digit, 40)) |> Option.get

        let observations =
            { WorkItemState = Some LiveWorkState.Active
              Execution = ExecutionObservation.Resolved("EXE-A", [ "step-1" ])
              Head = GitRead.Observed(HeadState.OnBranch("feature/x", commit))
              Upstream = Some(GitRead.Observed(UpstreamState.Tracking({ Name = "origin"; Url = Some "https://example.test/r.git" }, "feature/x")))
              RemoteBranch = Some(RemoteBranchObservation.At commit)
              LocalToRemote = None
              WorkingTree = GitStatusObservation.Clean
              PathFilter = PathFilterConfig.defaultConfig
              BaselineDirtyPaths = [] }

        CheckpointVerification.verify
            { WorkItemId = "W-1"
              Repository = "fixture"
              Summary = $"work up to {digit}"
              NextAction = "continue"
              StepId = step
              OccurredAt = occurredAt }
            observations
        |> Result.defaultWith (fun rejections -> failwith $"{rejections}")

    let private eventsPath root = Path.Combine(root, ".ros", "events", "events.jsonl")
    let private contextPath root = Path.Combine(root, ".ros", "context", "current.json")

    let private findingFields root =
        FileCheckpointRepository.validationFindings root |> List.map (fun finding -> finding.Field)

    let tests =
        [ { Name = "checkpoint persistence: recording appends one work.checkpointed event and sets the latestCheckpoint projection"
            Run =
              fun () ->
                  withFixture (fun root ->
                      let checkpoint = checkpointAt 'a' "2026-09-29T10:00:00.000Z" (Some "step-1")

                      match FileCheckpointRepository.record root actor [ "src/a.fs" ] checkpoint with
                      | Error message -> failwith message
                      | Ok recorded ->
                          let lines = File.ReadAllLines(eventsPath root) |> Array.filter (fun line -> line.Length > 0)
                          let event = JsonNode.Parse(Array.exactlyOne lines) :?> JsonObject
                          Assert.equal "work.checkpointed" (event["type"].GetValue<string>())
                          Assert.equal recorded.CheckpointId (event["eventId"].GetValue<string>())
                          Assert.equal "EXE-A" (event["execution"].GetValue<string>())
                          Assert.equal "step-1" (event["step"].GetValue<string>())
                          Assert.equal (String.replicate 40 "a") (event["checkpoint"].["remoteCommit"].GetValue<string>())
                          Assert.equal "src/a.fs" (event["paths"].[0].GetValue<string>())
                          Assert.equal "example/agent-a" (event["actor"].["id"].GetValue<string>())
                          Assert.isTrue (FileCheckpointRepository.eventIdMatches event) "eventId does not match content"

                          match FileCheckpointRepository.readItem root "W-1" with
                          | Ok(Some item) -> Assert.equal (Ok(Some recorded)) item.LatestCheckpoint
                          | other -> failwith $"{other}"

                          Assert.empty (FileCheckpointRepository.validationFindings root)) }
          { Name = "checkpoint persistence: a newer checkpoint never erases history; the projection is the latest only"
            Run =
              fun () ->
                  withFixture (fun root ->
                      let first = FileCheckpointRepository.record root actor [] (checkpointAt 'a' "2026-09-29T10:00:00.000Z" None) |> Result.defaultWith failwith
                      let second = FileCheckpointRepository.record root actor [] (checkpointAt 'b' "2026-09-29T11:00:00.000Z" None) |> Result.defaultWith failwith
                      let history = FileCheckpointRepository.readHistory root "W-1"
                      Assert.equal [ first.CheckpointId; second.CheckpointId ] (history |> List.map _.EventId)
                      Assert.equal (Ok first.Recorded) history.Head.Checkpoint

                      match FileCheckpointRepository.readItem root "W-1" with
                      | Ok(Some item) -> Assert.equal (Ok(Some second)) item.LatestCheckpoint
                      | other -> failwith $"{other}"

                      let context = File.ReadAllText(contextPath root)
                      Assert.isTrue (not (context.Contains first.CheckpointId)) "history was duplicated into current.json"
                      Assert.empty (FileCheckpointRepository.validationFindings root)) }
          { Name = "checkpoint persistence: recording the identical checkpoint twice writes one event"
            Run =
              fun () ->
                  withFixture (fun root ->
                      let checkpoint = checkpointAt 'a' "2026-09-29T10:00:00.000Z" None
                      FileCheckpointRepository.record root actor [] checkpoint |> Result.defaultWith failwith |> ignore
                      FileCheckpointRepository.record root actor [] checkpoint |> Result.defaultWith failwith |> ignore
                      Assert.equal 1 (FileCheckpointRepository.readHistory root "W-1" |> List.length)) }
          { Name = "compatibility: work context without checkpoint fields loads as 'no checkpoint' and validates cleanly"
            Run =
              fun () ->
                  withFixture (fun root ->
                      match FileCheckpointRepository.readItems root with
                      | Ok [ item ] ->
                          Assert.equal (Ok None) item.LatestCheckpoint
                          Assert.equal LiveWorkState.Active item.State
                      | other -> failwith $"{other}"

                      Assert.empty (FileCheckpointRepository.validationFindings root)
                      Assert.equal None (FileCheckpointRepository.readStartCommit root "W-NONE")) }
          { Name = "validation: an event whose content no longer matches its eventId is a finding"
            Run =
              fun () ->
                  withFixture (fun root ->
                      FileCheckpointRepository.record root actor [] (checkpointAt 'a' "2026-09-29T10:00:00.000Z" None) |> Result.defaultWith failwith |> ignore
                      let tampered = File.ReadAllText(eventsPath root).Replace("work up to a", "work up to z")
                      File.WriteAllText(eventsPath root, tampered)
                      let fields = findingFields root
                      Assert.isTrue (List.contains "eventId" fields) $"{fields}"
                      Assert.isTrue (List.contains "latestCheckpoint" fields) $"projection/event mismatch not reported: {fields}") }
          { Name = "validation: a projection that is not derivable from history is a finding"
            Run =
              fun () ->
                  withFixture (fun root ->
                      FileCheckpointRepository.record root actor [] (checkpointAt 'a' "2026-09-29T10:00:00.000Z" None) |> Result.defaultWith failwith |> ignore
                      // Drop the event, keep the projection.
                      File.WriteAllText(eventsPath root, "")
                      Assert.equal [ "latestCheckpoint" ] (findingFields root))

                  withFixture (fun root ->
                      FileCheckpointRepository.record root actor [] (checkpointAt 'a' "2026-09-29T10:00:00.000Z" None) |> Result.defaultWith failwith |> ignore
                      // Drop the projection, keep the event.
                      File.WriteAllText(contextPath root, contextJson (activeItem "W-1"))
                      Assert.equal [ "latestCheckpoint" ] (findingFields root)) }
          { Name = "validation: a duplicated checkpoint identity, a backdated checkpoint and an unsupported schema are findings"
            Run =
              fun () ->
                  withFixture (fun root ->
                      FileCheckpointRepository.record root actor [] (checkpointAt 'a' "2026-09-29T10:00:00.000Z" None) |> Result.defaultWith failwith |> ignore
                      let line = File.ReadAllText(eventsPath root)
                      File.AppendAllText(eventsPath root, line)
                      Assert.isTrue (findingFields root |> List.contains "eventId") "duplicate not reported")

                  withFixture (fun root ->
                      FileCheckpointRepository.record root actor [] (checkpointAt 'a' "2026-09-29T10:00:00.000Z" None) |> Result.defaultWith failwith |> ignore
                      FileCheckpointRepository.record root actor [] (checkpointAt 'b' "2026-09-29T09:30:00.000Z" None) |> Result.defaultWith failwith |> ignore
                      Assert.equal [ "occurredAt" ] (findingFields root |> List.filter ((=) "occurredAt")))

                  withFixture (fun root ->
                      FileCheckpointRepository.record root actor [] (checkpointAt 'a' "2026-09-29T10:00:00.000Z" None) |> Result.defaultWith failwith |> ignore
                      let node = JsonNode.Parse(File.ReadAllLines(eventsPath root)[0]) :?> JsonObject
                      node["schemaVersion"] <- JsonValue.Create "9.0.0"
                      node.Remove "eventId" |> ignore
                      node["eventId"] <- JsonValue.Create(Ros.Infrastructure.Json.CanonicalJson.sha256HexPrefix 24 (Ros.Infrastructure.Json.CanonicalJson.serializeCompact node))
                      File.WriteAllText(eventsPath root, node.ToJsonString() + "\n")
                      Assert.isTrue (findingFields root |> List.contains "schemaVersion") "unsupported schema not reported") }
          { Name = "validation: missing work item, missing execution and a step the execution never started are findings"
            Run =
              fun () ->
                  withFixture (fun root ->
                      FileCheckpointRepository.record root actor [] (checkpointAt 'a' "2026-09-29T10:00:00.000Z" (Some "step-1")) |> Result.defaultWith failwith |> ignore
                      // Rewrite the event to name an unknown step, then re-hash so
                      // only the reference is wrong.
                      let rehash (edit: JsonObject -> unit) =
                          let node = JsonNode.Parse(File.ReadAllLines(eventsPath root)[0]) :?> JsonObject
                          node.Remove "eventId" |> ignore
                          edit node
                          node["eventId"] <- JsonValue.Create(Ros.Infrastructure.Json.CanonicalJson.sha256HexPrefix 24 (Ros.Infrastructure.Json.CanonicalJson.serializeCompact node))
                          File.WriteAllText(eventsPath root, node.ToJsonString() + "\n")

                      rehash (fun node -> node["step"] <- JsonValue.Create "step-9")
                      Assert.isTrue (findingFields root |> List.contains "step") "invalid step not reported"
                      rehash (fun node -> node["execution"] <- JsonValue.Create "EXE-GHOST")
                      Assert.isTrue (findingFields root |> List.contains "execution") "missing execution not reported"
                      rehash (fun node -> node["workItem"] <- JsonValue.Create "W-GHOST")
                      Assert.isTrue (findingFields root |> List.contains "workItem") "missing work item not reported") } ]
