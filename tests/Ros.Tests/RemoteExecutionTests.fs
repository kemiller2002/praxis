namespace Ros.Tests

open System
open System.Text.Json
open Ros.Contracts.Remote
open Ros.Domain.Provenance
open Ros.Domain.Remote

/// The pure planning, persistence, and journal rules behind
/// `praxis remote execute` (PRAXIS-REMOTE-03). The end-to-end behaviour
/// against real repositories is pinned by `tests/remote-execute.test.mjs`.
[<RequireQualifiedAccess>]
module RemoteExecutionTests =
    let private sha = "59b4e032818a4c765886e48c117595dc58019d43"
    let private at = "2026-09-28T08:00:00.000Z"

    let private request operation arguments : Request =
        { ProtocolVersion = ProtocolVersion.current
          RequestId = "req-execution-0001"
          Operation = operation
          Repository = { Ref = Some "refs/heads/main"; ExpectedSha = Some sha }
          Actor =
            Some
                { Actor =
                    { Kind = ActorKind.Agent
                      Id = "example/cloud-agent"
                      Provider = Some "example"
                      Model = Some "unknown"
                      Runtime = Some "cloud-agent" }
                  SessionId = None }
          ExecutionId = None
          Arguments = arguments
          RequestedAt = None }

    let private command plan =
        match plan with
        | ExecutionPlan.Command arguments -> arguments
        | other -> failwith $"expected a command, got {other}"

    let private finding severity path message : ValidationFinding =
        { Severity = severity
          Path = path
          Field = "provenance"
          Message = message }

    let tests =
        [ { Name = "remote execution: every mutating operation maps onto the local command with the executor's clock"
            Run =
              fun () ->
                  let start =
                      request Operation.WorkStart (Arguments.WorkStart { WorkItemIds = [ "WI-1"; "WI-2" ]; Type = Some "task"; Classifications = [ "feature" ] })
                      |> ExecutionPlan.forRequest at
                      |> command

                  Assert.equal
                      [ "work"; "start"; "--id"; "WI-1"; "--id"; "WI-2"; "--occurred-at"; at; "--type"; "task"; "--classification"; "feature" ]
                      start

                  let block =
                      request Operation.WorkBlock (Arguments.WorkBlock([ "WI-1" ], "waiting on review")) |> ExecutionPlan.forRequest at |> command

                  Assert.equal [ "work"; "block"; "--id"; "WI-1"; "--occurred-at"; at; "--reason"; "waiting on review" ] block

                  let complete =
                      request
                          Operation.WorkComplete
                          (Arguments.WorkComplete
                              { WorkItemIds = [ "WI-1" ]
                                Evidence = [ { Type = "tests"; Path = "tests/a.fs" } ]
                                Conclusion = None })
                      |> ExecutionPlan.forRequest at
                      |> command

                  Assert.equal [ "work"; "complete"; "--id"; "WI-1"; "--occurred-at"; at; "--evidence"; "tests=tests/a.fs" ] complete

                  let reconcile =
                      request
                          Operation.WorkReconcile
                          (Arguments.WorkReconcile
                              { WorkItemId = "WI-1"
                                Reason = "committed before begin"
                                Commits = [ sha ]
                                Ranges = []
                                Paths = [ "src/a.fs" ] })
                      |> ExecutionPlan.forRequest at
                      |> command

                  Assert.equal
                      [ "work"; "reconcile"; "--id"; "WI-1"; "--occurred-at"; at; "--reason"; "committed before begin"; "--json"; "--commit"; sha; "--path"; "src/a.fs" ]
                      reconcile }

          { Name = "remote execution: read operations map onto their machine-readable local commands"
            Run =
              fun () ->
                  Assert.equal [ "validate"; "--json" ] (request Operation.Validate Arguments.NoArguments |> ExecutionPlan.forRequest at |> command)
                  Assert.equal [ "status"; "--json" ] (request Operation.Status Arguments.NoArguments |> ExecutionPlan.forRequest at |> command)
                  Assert.equal [ "provenance"; "identity"; "--json" ] (request Operation.ProvenanceIdentity Arguments.NoArguments |> ExecutionPlan.forRequest at |> command)
                  Assert.equal [ "work"; "context"; "WI-1" ] (request Operation.WorkContext (Arguments.WorkContext "WI-1") |> ExecutionPlan.forRequest at |> command)
                  Assert.equal ExecutionPlan.Describe (request Operation.Describe Arguments.NoArguments |> ExecutionPlan.forRequest at)
                  Assert.equal (ExecutionPlan.RequestStatus "req-x-00000001") (request Operation.RequestStatus (Arguments.RequestStatus "req-x-00000001") |> ExecutionPlan.forRequest at) }

          { Name = "remote execution: unlabelled remote telemetry is recorded as agent-reported and targets the requester's execution"
            Run =
              fun () ->
                  let record: TelemetryRecordArguments =
                      { WorkItemId = Some "WI-1"
                        Metric = "tokens.input"
                        Value = 1532m
                        Unit = Some "tokens"
                        Currency = None
                        Quality = None
                        Confidence = None
                        Scope = None
                        SourceType = None
                        SourceName = None
                        Mechanism = None
                        PricingSource = None
                        PricingVersion = None
                        CollectedAt = None }

                  let byWorkItem = request Operation.TelemetryRecord (Arguments.TelemetryRecord record) |> ExecutionPlan.forRequest at |> command
                  Assert.equal [ "telemetry"; "record"; "WI-1"; "--metric"; "tokens.input"; "--value"; "1532"; "--unit"; "tokens"; "--source-type"; "agent-report" ] byWorkItem

                  let byExecution =
                      { request Operation.TelemetryRecord (Arguments.TelemetryRecord record) with ExecutionId = Some "EXE-1" }
                      |> ExecutionPlan.forRequest at
                      |> command

                  Assert.equal "EXE-1" byExecution[2] }

          { Name = "remote execution: validated values can never be read as an option by the command"
            Run =
              fun () ->
                  // Every value slot in a planned command follows its flag, and
                  // RequestValidation refuses values starting with '-'; so the
                  // only arguments starting with '-' are the plan's own flags.
                  let plan =
                      request Operation.WorkStart (Arguments.WorkStart { WorkItemIds = [ "WI-1" ]; Type = None; Classifications = [] })
                      |> ExecutionPlan.forRequest at
                      |> command

                  let flags = plan |> List.filter (fun argument -> argument.StartsWith "-")
                  Assert.equal [ "--id"; "--occurred-at" ] flags }

          { Name = "remote execution: remote mutation is opt-in and grants only ever narrow"
            Run =
              fun () ->
                  Assert.equal (set [ Capability.Read ]) RemotePolicy.defaultRepositoryCapabilities
                  Assert.equal (set [ Capability.Read ]) (RemotePolicy.effectiveGrants RemotePolicy.defaultRepositoryCapabilities (set Capability.all))
                  Assert.equal (set [ Capability.Read; Capability.Mutate ]) (RemotePolicy.effectiveGrants (set Capability.all) (set [ Capability.Read; Capability.Mutate ])) }

          { Name = "remote execution: only Praxis-owned, non-transient state may be persisted"
            Run =
              fun () ->
                  let owned, other =
                      RemotePersistence.partition
                          [ ".ros/events/events.jsonl"
                            ".ros/locks/abc.lock"
                            ".ros/transactions/work-state.json"
                            "src/Program.fs"
                            ".rosy/file"
                            ".ros/../src/escape"
                            ".ros/context/current.json" ]

                  Assert.equal [ ".ros/context/current.json"; ".ros/events/events.jsonl" ] owned
                  Assert.equal [ ".ros/../src/escape"; ".rosy/file"; "src/Program.fs" ] other
                  Assert.equal ".ros/remote/requests/req~a~b.json" (RemotePersistence.journalPath "req:a:b") }

          { Name = "remote execution: command exit codes map to Praxis outcomes without blaming the caller for executor defects"
            Run =
              fun () ->
                  Assert.equal None (CommandOutcome.classify Operation.WorkStart 0)
                  Assert.equal (Some FailureCode.DomainRejected) (CommandOutcome.classify Operation.WorkStart 1)
                  Assert.equal (Some FailureCode.ValidationFailed) (CommandOutcome.classify Operation.Validate 1)
                  Assert.equal (Some FailureCode.Internal) (CommandOutcome.classify Operation.WorkStart 2)
                  Assert.equal (Some FailureCode.Internal) (CommandOutcome.classify Operation.WorkStart 134) }

          { Name = "remote execution: a mutation may not introduce validation errors, but pre-existing findings are not its doing"
            Run =
              fun () ->
                  let existing = finding "error" "a.md" "missing provenance"
                  let warning = finding "warning" "b.md" "stale"
                  let introduced = finding "error" "c.md" "contradicting actor"
                  Assert.empty (ValidationRegression.introduced [ existing ] [ existing; warning ])
                  Assert.equal [ introduced ] (ValidationRegression.introduced [ existing ] [ existing; introduced ])
                  Assert.empty (ValidationRegression.introduced [ existing; introduced ] [ introduced ]) }

          { Name = "remote execution: a journal entry round-trips its fingerprint and response and replays flagged"
            Run =
              fun () ->
                  let accepted = request Operation.WorkResume (Arguments.WorkResume [ "WI-1" ])
                  let response = Response.succeeded "3.4.0" accepted (Some sha) (Some "{\"ok\":true}")

                  let entry: RemoteJournal.Entry =
                      { Request = accepted
                        Fingerprint = RequestFingerprint.compute accepted
                        RecordedAt = at
                        Principal = Some "github:octocat"
                        Response = RemoteJson.renderResponse response }

                  let rendered = RemoteJournal.render entry

                  match RemoteJournal.read rendered with
                  | Ok(fingerprint, recorded) ->
                      Assert.equal (RequestFingerprint.compute accepted) fingerprint
                      use replayed = JsonDocument.Parse(RemoteJournal.replayedResponse recorded)
                      Assert.isTrue (replayed.RootElement.GetProperty("replayed").GetBoolean()) "replay flagged"
                      Assert.equal "succeeded" (replayed.RootElement.GetProperty("outcome").GetString())
                  | Error message -> failwith message

                  use journal = JsonDocument.Parse rendered
                  let requester = journal.RootElement.GetProperty "requester"
                  Assert.equal "asserted-by-request" (requester.GetProperty("assurance").GetString())
                  Assert.equal "example/cloud-agent" (requester.GetProperty("actor").GetProperty("id").GetString())
                  Assert.equal "github:octocat" (journal.RootElement.GetProperty("principal").GetString()) }

          { Name = "remote execution: an unreadable journal entry fails closed instead of reading as absent"
            Run =
              fun () ->
                  [ "{}"; "not json"; "{\"schema\":\"praxis.remote-journal\",\"schemaVersion\":2,\"fingerprint\":\"x\",\"response\":{}}" ]
                  |> List.iter (fun text ->
                      match RemoteJournal.read text with
                      | Error _ -> ()
                      | Ok _ -> failwith $"expected {text} to be refused") }

          { Name = "remote execution: non-JSON command output is preserved as text rather than dropped"
            Run =
              fun () ->
                  use wrapped = JsonDocument.Parse(RemoteJson.commandResult "validation passed\n")
                  Assert.equal "validation passed\n" (wrapped.RootElement.GetProperty("text").GetString())
                  Assert.equal "{\"valid\":true}" (RemoteJson.commandResult " {\"valid\":true} ")

                  Assert.equal
                      (Some [ finding "error" "x.md" "bad" ])
                      (RemoteJson.parseValidationFindings "{\"valid\":false,\"findings\":[{\"severity\":\"error\",\"path\":\"x.md\",\"field\":\"provenance\",\"message\":\"bad\"}]}")

                  Assert.equal None (RemoteJson.parseValidationFindings "validation passed") } ]
