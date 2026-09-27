namespace Ros.Tests

open System
open System.IO
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module ReconciliationTests =
    let private withEnvelope (json: string) (run: string -> unit) =
        let path = Path.Combine(Path.GetTempPath(), $"praxis-envelope-{Guid.NewGuid():N}.json")
        File.WriteAllText(path, json)
        try run path
        finally File.Delete path

    let private actor =
        { ActorKind = "agent"; ActorId = "openai"; Provider = Some "openai"; Model = None; Runtime = None }

    let private envelope =
        { SchemaVersion = "1.0"
          TransactionId = "tx-1"
          WorkItem = "WI-0064"
          Branch = "WI-0064"
          BaseCommit = String.replicate 40 "a"
          PraxisInstanceId = None
          Agent = actor
          Execution = None
          Timeline = [{ Sequence = 1; Timestamp = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Action = "start" }]
          Requests = [{ RequestType = "work.start" }] }

    let private observed =
        { ActualBranch = "WI-0064"; HeadCommit = String.replicate 40 "b"; BaseCommitExists = true; TransactionAlreadyApplied = false; LocalPraxisInstanceId = None }

    let private validStep =
        { StepId = "STEP-1"; ExecutionId = "EXE-FALLBACK"; WorkItemId = "WI-0064"; Sequence = 1; ParentStepId = None; Name = "test"; Description = None; Classifications = [ "testing" ]
          Status = "completed"; PlannedAt = DateTimeOffset.Parse("2026-09-26T10:01:00Z"); StartedAt = Some(DateTimeOffset.Parse("2026-09-26T10:01:00Z")); CompletedAt = Some(DateTimeOffset.Parse("2026-09-26T10:02:00Z")); EndedAt = Some(DateTimeOffset.Parse("2026-09-26T10:02:00Z")); Actor = actor
          Transitions =
            [ { Sequence = 1; FromStatus = None; ToStatus = "active"; Timestamp = DateTimeOffset.Parse("2026-09-26T10:01:00Z") }
              { Sequence = 2; FromStatus = Some "active"; ToStatus = "completed"; Timestamp = DateTimeOffset.Parse("2026-09-26T10:02:00Z") } ]
          Measurements = []; RawTelemetry = []; Evidence = [] }

    let private stepEnvelope =
        { envelope with
            TransactionId = "tx-step"
            Execution = Some { ExecutionId = "EXE-FALLBACK"; StartedAt = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Steps = [ validStep ] } }

    let tests =
        [ { Name = "reconciliation accepts a legal envelope"
            Run = fun () -> Assert.equal Accept (Reconciliation.decide envelope observed) }
          { Name = "reconciliation rejects claimed work-item branch mismatch"
            Run = fun () ->
                match Reconciliation.decide { envelope with Branch = "main" } observed with
                | Reject findings -> Assert.isTrue (List.contains (WorkItemBranchMismatch("WI-0064", "main")) findings) "missing mismatch finding"
                | value -> failwithf "expected rejection, got %A" value }
          { Name = "reconciliation rejects spoofed observed branch"
            Run = fun () ->
                match Reconciliation.decide envelope { observed with ActualBranch = "main" } with
                | Reject findings -> Assert.isTrue (List.contains (ObservedBranchMismatch("WI-0064", "main")) findings) "missing observed mismatch"
                | value -> failwithf "expected rejection, got %A" value }
          { Name = "reconciliation treats replay as already applied"
            Run = fun () -> Assert.equal AlreadyApplied (Reconciliation.decide envelope { observed with TransactionAlreadyApplied = true }) }
          { Name = "reconciliation rejects a discontinuous timeline"
            Run = fun () ->
                let changed = { envelope with Timeline = [{ envelope.Timeline.Head with Sequence = 2 }] }
                match Reconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (List.contains InvalidTimelineSequence findings) "missing sequence finding"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "reconciliation accepts a legal step history and rejects an illegal transition history"
            Run = fun () ->
                Assert.equal Accept (Reconciliation.decide stepEnvelope observed)
                let badTransition = { validStep.Transitions[1] with FromStatus = Some "planned" }
                let badStep = { validStep with Transitions = [ validStep.Transitions[0]; badTransition ] }
                let changed = { stepEnvelope with Execution = Some { stepEnvelope.Execution.Value with Steps = [ badStep ] } }
                match Reconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (findings |> List.exists (function InvalidStepStructure value -> value.Contains "transition-status-discontinuity" | _ -> false)) "missing transition contradiction"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "reconciliation quarantine durably preserves the rejected step envelope"
            Run = fun () ->
                let root = Path.Combine(Path.GetTempPath(), $"praxis-reconcile-{Guid.NewGuid():N}")
                Directory.CreateDirectory root |> ignore
                try
                    let store = FileReconciliationStore.create root
                    store.Quarantine stepEnvelope [ "request-dispatch-not-configured" ]
                    let content = File.ReadAllText(Path.Combine(root, ".praxis", "rejected", "tx-step.json"))
                    Assert.isTrue (content.Contains "STEP-1") "quarantine dropped step boundary"
                    Assert.isTrue (content.Contains "EXE-FALLBACK") "quarantine dropped execution identity"
                finally Directory.Delete(root, true) }

          { Name = "legacy fallback JSON without execution steps remains compatible"
            Run = fun () ->
                let json = """{"schemaVersion":"1.0","transactionId":"tx-old","workItem":"WI-0064","branch":"WI-0064","baseCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","agent":{"kind":"agent","id":"openai/codex"},"timeline":[{"sequence":1,"timestamp":"2026-09-26T10:00:00Z","action":"start"}],"requests":[{"type":"work.start"}]}"""
                withEnvelope json (fun path ->
                    match ReconciliationEnvelopeJson.read path with
                    | Error codes -> failwith (String.concat "," codes)
                    | Ok parsed ->
                        Assert.equal None parsed.Execution
                        Assert.equal None parsed.PraxisInstanceId) }

          { Name = "step-aware fallback JSON preserves boundaries, transitions, availability, raw telemetry, and evidence"
            Run = fun () ->
                let json = """{"schemaVersion":"1.0","transactionId":"tx-step","workItem":"WI-0064","branch":"WI-0064","baseCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","praxisInstanceId":"PRAXIS-1","agent":{"kind":"agent","id":"openai/codex","provider":"openai","model":"unknown","runtime":"codex"},"execution":{"executionId":"EXE-FALLBACK","startedAt":"2026-09-26T10:00:00Z","steps":[{"stepId":"STEP-1","executionId":"EXE-FALLBACK","workItemId":"WI-0064","sequence":1,"name":"test","classification":["testing"],"status":"completed","plannedAt":"2026-09-26T10:01:00Z","startedAt":"2026-09-26T10:01:00Z","completedAt":"2026-09-26T10:02:00Z","endedAt":"2026-09-26T10:02:00Z","actor":{"kind":"agent","id":"openai/codex"},"transitions":[{"sequence":1,"to":"active","timestamp":"2026-09-26T10:01:00Z"},{"sequence":2,"from":"active","to":"completed","timestamp":"2026-09-26T10:02:00Z"}],"telemetry":{"measurements":[{"measurementId":"MEAS-1","metricId":"tokens.input","value":0,"unit":"tokens","quality":"reported","availability":"reported"},{"measurementId":"MEAS-2","metricId":"tokens.output","availability":"unavailable"}],"rawTelemetry":[{"providerField":"preserved"}]},"evidence":["EV-1"]}]},"timeline":[{"sequence":1,"timestamp":"2026-09-26T10:00:00Z","action":"start"}],"requests":[{"type":"work.start"}]}"""
                withEnvelope json (fun path ->
                    match ReconciliationEnvelopeJson.read path with
                    | Error codes -> failwith (String.concat "," codes)
                    | Ok parsed ->
                        Assert.equal (Some "PRAXIS-1") parsed.PraxisInstanceId
                        let step = parsed.Execution |> Option.get |> _.Steps |> List.exactlyOne
                        Assert.equal "STEP-1" step.StepId
                        Assert.equal 2 step.Transitions.Length
                        Assert.equal 2 step.Measurements.Length
                        Assert.equal (Some 0.0) step.Measurements[0].Value
                        Assert.equal "unavailable" step.Measurements[1].Availability
                        Assert.equal 1 step.RawTelemetry.Length
                        Assert.equal [ "EV-1" ] step.Evidence) }

          { Name = "reconciliation rejects a claimed Praxis instance mismatch"
            Run = fun () ->
                let changed = { envelope with PraxisInstanceId = Some "PRAXIS-REMOTE" }
                let observedWithInstance = { observed with LocalPraxisInstanceId = Some "PRAXIS-LOCAL" }
                match Reconciliation.decide changed observedWithInstance with
                | Reject findings -> Assert.isTrue (List.contains (InstanceIdentityMismatch("PRAXIS-REMOTE", "PRAXIS-LOCAL")) findings) "missing instance mismatch"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "fallback validation rejects unavailable measurements that carry exact-looking values"
            Run = fun () ->
                let step =
                    { StepId = "STEP-1"; ExecutionId = "EXE-FALLBACK"; WorkItemId = "WI-0064"; Sequence = 1; ParentStepId = None; Name = "bad"; Description = None; Classifications = [];
                      Status = "completed"; PlannedAt = DateTimeOffset.Parse("2026-09-26T10:01:00Z"); StartedAt = Some(DateTimeOffset.Parse("2026-09-26T10:01:00Z")); CompletedAt = Some(DateTimeOffset.Parse("2026-09-26T10:02:00Z")); EndedAt = Some(DateTimeOffset.Parse("2026-09-26T10:02:00Z")); Actor = actor; Transitions = [];
                      Measurements = [ { MeasurementId = "MEAS-1"; MetricId = "tokens.input"; Value = Some 0.0; Unit = Some "tokens"; Currency = None; Quality = None; Availability = "unavailable"; RawJson = "{}" } ]; RawTelemetry = []; Evidence = [] }
                let changed = { envelope with Execution = Some { ExecutionId = "EXE-FALLBACK"; StartedAt = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Steps = [ step ] } }
                match Reconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (findings |> List.exists (function InvalidStepStructure value -> value.Contains "value-forbidden" | _ -> false)) "missing availability contradiction"
                | value -> failwithf "expected rejection, got %A" value } ]
