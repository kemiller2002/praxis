namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Application.Artifacts
open Ros.Domain.Artifacts
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Provenance
open Ros.Infrastructure.Work

/// File-level provenance effects against a temporary repository: recording
/// into front matter, the `artifact.contributed` event, identity inheritance
/// from the execution record, refusal to impersonate, legacy execution
/// records, policy configuration, and export through adapter publication
/// and generated registries.
[<RequireQualifiedAccess>]
module ProvenanceEffectTests =
    let private requirementPath = "research/requirements/RQ-TEST-2026-A001--requirement.md"

    let private requirementText =
        "---\nid: RQ-TEST-2026-A001\ntitle: Requirement\nstatus: draft\ncreated: 2026-09-25\nupdated: 2026-09-25\n---\n\n# Requirement\n\nBody text.\n"

    let private withRepository (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-provenance-{Guid.NewGuid():N}")
        Directory.CreateDirectory(Path.Combine(root, "research", "requirements")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, ".ros", "telemetry", "executions")) |> ignore
        File.WriteAllText(Path.Combine(root, "ros.json"), """{"repository":{"id":"provenance-test"}}""")
        File.WriteAllText(Path.Combine(root, requirementPath), requirementText)

        try
            run root
        finally
            Directory.Delete(root, true)

    /// Writes an execution record the way `work begin` does, with or without
    /// the `actorKind` field older CLI versions never wrote.
    let private writeExecutionWithSession root (executionId: string) (sessionId: string option) (status: string) (provider: string) (runtime: string) (actorKind: string option) (mechanism: string) =
        let identity = JsonObject()
        sessionId |> Option.iter (fun session -> identity["sessionId"] <- JsonValue.Create session)
        identity["provider"] <- JsonValue.Create provider
        identity["model"] <- null
        identity["runtime"] <- JsonValue.Create runtime
        identity["agentId"] <- null
        actorKind |> Option.iter (fun kind -> identity["actorKind"] <- JsonValue.Create kind)
        let source = JsonObject()
        source["type"] <- JsonValue.Create "environment"
        source["name"] <- JsonValue.Create "runtime-identity"
        source["mechanism"] <- JsonValue.Create mechanism
        let provenance = JsonObject()
        provenance["sources"] <- JsonArray(source)
        let record = JsonObject()
        record["schemaVersion"] <- JsonValue.Create "1.0.0"
        record["executionId"] <- JsonValue.Create executionId
        record["workItemId"] <- JsonValue.Create "FEAT-1"
        record["status"] <- JsonValue.Create status
        record["startedAt"] <- JsonValue.Create "2026-09-25T10:00:00.000Z"
        record["identity"] <- identity
        record["provenance"] <- provenance
        File.WriteAllText(Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json"), record.ToJsonString())

    let private writeExecution root executionId status provider runtime actorKind mechanism =
        writeExecutionWithSession root executionId None status provider runtime actorKind mechanism

    let private claudeOverrides =
        { IdentityInputs.empty with
            Provider = Some "anthropic"
            Runtime = Some "claude-code"
            Model = Some "unknown"
            ActorKind = Some "agent" }

    let private codexOverrides =
        { IdentityInputs.empty with
            Provider = Some "openai"
            Runtime = Some "codex"
            Model = Some "unknown"
            ActorKind = Some "agent" }

    let private humanOverrides =
        { IdentityInputs.empty with
            AgentId = Some "kevin"
            ActorKind = Some "human" }

    /// Every request declares its identity explicitly so the outcome never
    /// depends on the agent or CI environment the test suite runs in.
    let private request operation overrides : ContributionRecordRequest =
        { Target = "RQ-TEST-2026-A001"
          Operation = operation
          Reason = Some "Because: tests, and more"
          Evidence = []
          DerivedFrom = []
          ExecutionId = None
          ExecutionFromEnvironment = false
          OccurredAt = "2026-09-25T10:05:00.000Z"
          IdentityOverrides = overrides }

    let private recordOk root req =
        match FileProvenanceRepository.record root req with
        | Ok outcome -> outcome
        | Error message -> failwith message

    let private readProvenance root =
        match FrontMatter.parse requirementPath (File.ReadAllText(Path.Combine(root, requirementPath))) with
        | Error message -> failwith message
        | Ok document ->
            match ArtifactProvenance.parse document.Metadata with
            | Ok(Some provenance) -> provenance
            | other -> failwith $"{other}"

    let private events root =
        let path = Path.Combine(root, ".ros", "events", "events.jsonl")

        if File.Exists path then
            File.ReadAllLines path |> Array.filter (fun line -> line.Length > 0) |> Array.map JsonNode.Parse |> Array.toList
        else
            []

    /// Reads a string at a key path (array positions as decimal strings).
    let private textAt (node: JsonNode) (path: string list) =
        let step (current: JsonNode) (key: string) =
            match current with
            | :? JsonArray as array -> array[int key]
            | other -> other.AsObject()[key]

        (path |> List.fold step node).GetValue<string>()

    let private countAt (node: JsonNode) (key: string) =
        let value = node.AsObject()[key]
        value.AsArray().Count

    let private rawAt (node: JsonNode) (key: string) =
        let value = node.AsObject()[key]
        value.ToJsonString()

    let private exeClaude = "EXE-20260925T100000000Z-11111111"
    let private exeCodex = "EXE-20260925T110000000Z-22222222"

    let tests =
        [ { Name = "recording inherits the execution's identity, writes front matter, and emits an attributed event"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      let outcome = recordOk root { request ContributionOperation.Created claudeOverrides with Evidence = [ "RQ-TEST-2026-A001" ] }
                      Assert.equal true outcome.Changed
                      Assert.equal exeClaude outcome.Contribution.Key
                      Assert.equal ActorKind.Agent outcome.Contribution.Actor.Kind

                      let text = File.ReadAllText(Path.Combine(root, requirementPath))
                      Assert.isTrue (text.EndsWith "---\n\n# Requirement\n\nBody text.\n") "body untouched"
                      Assert.equal (Some exeClaude) (ArtifactProvenance.originator (readProvenance root) |> Option.map _.Key)

                      let event = Assert.single (events root)
                      Assert.equal "artifact.contributed" (textAt event [ "type" ])
                      Assert.equal "agent" (textAt event [ "actor"; "kind" ])
                      Assert.equal exeClaude (textAt event [ "telemetryExecutions"; "0" ])
                      Assert.equal outcome.BeforeSha256 (textAt event [ "previous"; "sha256" ])
                      Assert.equal outcome.AfterSha256 (textAt event [ "current"; "sha256" ])
                      Assert.equal "RQ-TEST-2026-A001" (textAt event [ "artifact"; "id" ])) }
          { Name = "re-recording the same operation is idempotent and writes no second event"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      recordOk root (request ContributionOperation.Created claudeOverrides) |> ignore
                      let before = File.ReadAllText(Path.Combine(root, requirementPath))
                      let again = recordOk root (request ContributionOperation.Created claudeOverrides)
                      Assert.equal false again.Changed
                      Assert.equal before (File.ReadAllText(Path.Combine(root, requirementPath)))
                      Assert.equal 1 (events root).Length) }
          { Name = "a second agent's modification accumulates and the originator is preserved"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "finalized" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      writeExecution root exeCodex "active" "openai" "codex" (Some "agent") "whitelisted-codex-environment"
                      recordOk root { request ContributionOperation.Created claudeOverrides with ExecutionId = Some exeClaude } |> ignore
                      recordOk root { request ContributionOperation.Modified codexOverrides with OccurredAt = "2026-09-25T11:05:00.000Z" } |> ignore
                      let provenance = readProvenance root
                      Assert.equal [ exeClaude; exeCodex ] (provenance.Contributions |> List.map _.Key)
                      Assert.equal "anthropic/claude-code" (ArtifactProvenance.originator provenance).Value.Actor.Id
                      Assert.equal true (Involvement.describe provenance).AgentToAgentRevision) }
          { Name = "an agent cannot record into another agent's execution"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      let refused = FileProvenanceRepository.record root { request ContributionOperation.Modified codexOverrides with ExecutionId = Some exeClaude }
                      Assert.isTrue (refused |> Result.isError) "impersonation must be refused"
                      Assert.equal requirementText (File.ReadAllText(Path.Combine(root, requirementPath)))
                      Assert.empty (events root)) }
          { Name = "an agent with no execution is refused; a declared human contributes under a CTB key"
            Run =
              fun () ->
                  withRepository (fun root ->
                      Assert.isTrue (FileProvenanceRepository.record root (request ContributionOperation.Created codexOverrides) |> Result.isError) "agent needs an execution"
                      let outcome = recordOk root (request ContributionOperation.Created humanOverrides)
                      Assert.isTrue (outcome.Contribution.Key.StartsWith "CTB-") outcome.Contribution.Key
                      Assert.equal ActorKind.Human outcome.Contribution.Actor.Kind
                      Assert.equal None outcome.Contribution.Actor.Provider
                      let event = Assert.single (events root)
                      Assert.equal 0 (countAt event "telemetryExecutions")) }
          { Name = "a human approval alongside an active agent execution is recorded separately, never merged into it"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      recordOk root (request ContributionOperation.Created claudeOverrides) |> ignore
                      recordOk root { request ContributionOperation.Approved humanOverrides with OccurredAt = "2026-09-25T12:00:00.000Z" } |> ignore
                      let provenance = readProvenance root
                      Assert.equal 2 provenance.Contributions.Length
                      Assert.equal "agent-created, human-approved" (Involvement.describe provenance).Label) }
          { Name = "another session of the same agent cannot record into this session's execution"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecutionWithSession root exeClaude (Some "session-A") "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      let otherSession = { claudeOverrides with SessionId = Some "session-B" }
                      Assert.isTrue (FileProvenanceRepository.record root (request ContributionOperation.Modified otherSession) |> Result.isError) "implicit"
                      Assert.isTrue (FileProvenanceRepository.record root { request ContributionOperation.Modified otherSession with ExecutionId = Some exeClaude } |> Result.isError) "explicit"
                      let sameSession = { claudeOverrides with SessionId = Some "session-A" }
                      Assert.equal exeClaude (recordOk root (request ContributionOperation.Created sameSession)).Contribution.Key) }
          { Name = "a human contribution outside any execution is idempotent for an identical recording"
            Run =
              fun () ->
                  withRepository (fun root ->
                      let first = recordOk root (request ContributionOperation.Reviewed humanOverrides)
                      let second = recordOk root (request ContributionOperation.Reviewed humanOverrides)
                      Assert.equal first.Contribution.Key second.Contribution.Key
                      Assert.equal false second.Changed
                      Assert.equal 1 (readProvenance root).Contributions.Length
                      Assert.equal 1 (events root).Length

                      // Later the same day: the same entry, advanced `last`.
                      let later = recordOk root { request ContributionOperation.Approved humanOverrides with OccurredAt = "2026-09-25T18:00:00.000Z" }
                      Assert.equal first.Contribution.Key later.Contribution.Key
                      Assert.equal (Some "2026-09-25T18:00:00.000Z") later.Contribution.Last
                      Assert.equal 1 (readProvenance root).Contributions.Length) }
          { Name = "an execution inherited from ROS_EXECUTION_ID is honoured only by a process with its own identity (RQ-ROS-2026-A016)"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      let inherited = { request ContributionOperation.Created IdentityInputs.empty with ExecutionId = Some exeClaude; ExecutionFromEnvironment = true }

                      // Run the refusal with no identity environment at all, so the
                      // outcome does not depend on the agent or CI running the suite.
                      let identityVariables =
                          [ "CLAUDE_CODE_SESSION_ID"; "CODEX_SESSION_ID"; "CODEX_THREAD_ID"; "COPILOT_SESSION_ID"; "GEMINI_SESSION_ID"
                            "GITHUB_ACTIONS"; "GITHUB_RUN_ID"; "OLLAMA_HOST"; "ROS_ACTOR"; "ROS_ACTOR_KIND"; "ROS_TELEMETRY_CONVERSATION_ID"
                            "ROS_TELEMETRY_MODEL"; "ROS_TELEMETRY_MODEL_VERSION"; "ROS_TELEMETRY_PROVIDER"; "ROS_TELEMETRY_RUNTIME"
                            "ROS_TELEMETRY_RUNTIME_VERSION"; "ROS_TELEMETRY_RUN_ID"; "ROS_TELEMETRY_SESSION_ID" ]

                      let saved = identityVariables |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)

                      let refused =
                          try
                              identityVariables |> List.iter (fun name -> Environment.SetEnvironmentVariable(name, null))
                              FileProvenanceRepository.record root inherited
                          finally
                              saved |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

                      Assert.isTrue (refused |> Result.isError) "an identity-less process must not inherit a run from its environment"
                      Assert.isTrue (not ((File.ReadAllText(Path.Combine(root, requirementPath))).Contains "provenance:")) "a refused recording writes nothing"
                      let honoured = recordOk root { inherited with IdentityOverrides = claudeOverrides }
                      Assert.equal exeClaude honoured.Contribution.Key
                      let otherAgent = FileProvenanceRepository.record root { inherited with Operation = ContributionOperation.Modified; IdentityOverrides = codexOverrides }
                      Assert.isTrue (otherAgent |> Result.isError) "an inherited execution still must agree with the process identity") }
          { Name = "a multi-line or padded reason is normalized and recorded, and a blank reason is none"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      let outcome = recordOk root { request ContributionOperation.Created claudeOverrides with Reason = Some "  first line\nsecond: line, \"quoted\"  " }
                      Assert.equal (Some "  first line second: line, \"quoted\"  ") outcome.Contribution.Reason
                      Assert.equal outcome.Contribution (readProvenance root).Contributions.Head
                      let blank = recordOk root { request ContributionOperation.Modified claudeOverrides with Reason = Some "   "; OccurredAt = "2026-09-25T10:06:00.000Z" }
                      Assert.equal true blank.Changed) }
          { Name = "derived-from lineage is recorded on the artifact and in the event"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      recordOk root { request ContributionOperation.Created claudeOverrides with DerivedFrom = [ "EV-TEST-2026-A001"; "ordo:resolution/r-9" ] } |> ignore

                      match FrontMatter.parse requirementPath (File.ReadAllText(Path.Combine(root, requirementPath))) with
                      | Ok document -> Assert.equal [ "EV-TEST-2026-A001"; "ordo:resolution/r-9" ] (Lineage.sources document)
                      | Error message -> failwith message

                      let event = Assert.single (events root)
                      Assert.equal 2 (countAt event "derivedFrom")) }
          { Name = "malformed existing provenance is never overwritten"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      let malformed = requirementText.Replace("updated: 2026-09-25\n", "updated: 2026-09-25\nprovenance: someone\n")
                      File.WriteAllText(Path.Combine(root, requirementPath), malformed)
                      Assert.isTrue (FileProvenanceRepository.record root (request ContributionOperation.Modified claudeOverrides) |> Result.isError) "malformed"
                      Assert.equal malformed (File.ReadAllText(Path.Combine(root, requirementPath)))) }
          { Name = "legacy execution records without actorKind project from their preserved discovery mechanism"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "finalized" "anthropic" "claude-code" None "whitelisted-claude-environment"
                      writeExecution root exeCodex "finalized" "local" "ollama" None "whitelisted-local-runtime-environment"
                      let actors = FileProvenanceRepository.readExecutionActors root
                      Assert.equal ActorKind.Agent actors[exeClaude].Kind
                      Assert.equal "anthropic/claude-code" actors[exeClaude].Id
                      Assert.equal ActorKind.Unknown actors[exeCodex].Kind) }
          { Name = "policy configuration: absent is not enforced; enforced without a date is an error"
            Run =
              fun () ->
                  withRepository (fun root ->
                      Assert.equal (Ok ProvenancePolicy.notConfigured) (FileProvenanceRepository.readPolicy root)
                      File.WriteAllText(Path.Combine(root, "ros.json"), """{"provenance":{"enforce":true}}""")
                      Assert.isTrue (FileProvenanceRepository.readPolicy root |> Result.isError) "missing date"
                      File.WriteAllText(Path.Combine(root, "ros.json"), """{"provenance":{"enforce":true,"requiredFrom":"2026-09-25"}}""")

                      match FileProvenanceRepository.readPolicy root with
                      | Ok policy -> Assert.equal (true, Some "2026-09-25", Set.ofList [ "RQ" ]) (policy.Enforced, policy.RequiredFrom, policy.RequireOriginator)
                      | Error message -> failwith message) }
          { Name = "adapter publication exports the attributed event verbatim (provenance survives the boundary)"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      recordOk root (request ContributionOperation.Created claudeOverrides) |> ignore

                      match FileAdapterRepository.publish root "exported/events.jsonl" with
                      | Error message -> failwith message
                      | Ok _ ->
                          let exported = File.ReadAllLines(Path.Combine(root, "exported", "events.jsonl")) |> Array.filter (fun line -> line.Length > 0)
                          let local = File.ReadAllLines(Path.Combine(root, ".ros", "events", "events.jsonl")) |> Array.filter (fun line -> line.Length > 0)
                          let exportedEvent = JsonNode.Parse(Assert.single (Array.toList exported))
                          let localEvent = JsonNode.Parse(Array.exactlyOne local)
                          Assert.equal (rawAt localEvent "actor") (rawAt exportedEvent "actor")
                          Assert.equal exeClaude (textAt exportedEvent [ "contribution" ])) }
          { Name = "generated registries carry artifact provenance for downstream consumers"
            Run =
              fun () ->
                  withRepository (fun root ->
                      writeExecution root exeClaude "active" "anthropic" "claude-code" (Some "agent") "whitelisted-claude-environment"
                      recordOk root (request ContributionOperation.Created claudeOverrides) |> ignore
                      let repository = FileArtifactRepository.create root

                      match ArtifactOperations.buildRegistries false repository with
                      | RegistryBuildOutcome.Completed _ ->
                          let registry = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "registries", "requirements.json")))
                          let entry = registry.AsArray()[0]
                          Assert.equal "agent" (textAt entry [ "provenance"; "contributions"; exeClaude; "actor"; "kind" ])
                      | outcome -> failwith $"{outcome}") }
          { Name = "the base-revision reader returns an artifact as committed, and nothing for a path that did not exist"
            Run =
              fun () ->
                  withRepository (fun root ->
                      let git (arguments: string list) =
                          let info = Diagnostics.ProcessStartInfo("git", arguments)
                          info.WorkingDirectory <- root
                          info.RedirectStandardOutput <- true
                          info.RedirectStandardError <- true
                          use child = Diagnostics.Process.Start info
                          child.WaitForExit()
                          Assert.equal 0 child.ExitCode

                      git [ "init"; "-q" ]
                      git [ "-c"; "user.email=t@example.invalid"; "-c"; "user.name=t"; "add"; "." ]
                      git [ "-c"; "user.email=t@example.invalid"; "-c"; "user.name=t"; "commit"; "-qm"; "base" ]
                      File.WriteAllText(Path.Combine(root, requirementPath), requirementText.Replace("Body text.", "Edited."))
                      Assert.equal (Some requirementText) (Ros.Infrastructure.Git.ProcessGitRepository.readFileAtRevision root "HEAD" requirementPath)
                      Assert.equal None (Ros.Infrastructure.Git.ProcessGitRepository.readFileAtRevision root "HEAD" "research/requirements/missing.md")) }
          { Name = "an optional registry kind with no documents is not stale and is not created"
            Run =
              fun () ->
                  withRepository (fun root ->
                      File.Delete(Path.Combine(root, requirementPath))
                      let repository = FileArtifactRepository.create root

                      match ArtifactOperations.buildRegistries false repository with
                      | RegistryBuildOutcome.Completed _ -> ()
                      | outcome -> failwith $"{outcome}"

                      Assert.isTrue (not (File.Exists(Path.Combine(root, "registries", "requirements.json")))) "no empty optional registry"

                      match ArtifactOperations.checkRegistries repository with
                      | RegistryCheckOutcome.Completed findings -> Assert.empty findings
                      | outcome -> failwith $"{outcome}") } ]
