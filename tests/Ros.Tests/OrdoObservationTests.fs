namespace Ros.Tests

open System
open System.IO
open Ros.Application.Ordo
open Ros.Contracts.Ordo
open Ros.Domain.Ordo
open Ros.Infrastructure.Ordo

[<RequireQualifiedAccess>]
module OrdoObservationTests =
    let private resolutionJson id completedAt =
        $"""{
  "schema": "ordo.resolution-observation",
  "schemaVersion": 2,
  "resolutionId": "{id}",
  "correlationId": "corr-1",
  "causedBy": null,
  "mode": "decide",
  "contractId": "contract-1",
  "contractVersion": 3,
  "requestId": "request-1",
  "stateFingerprint": "sha256:abc",
  "stateViewSchema": {{"id":"state-view","version":1}},
  "coverage": [
    {{"scope":"repository.files","status":"complete","provenanceEvidenceIds":["EV-1"]}}
  ],
  "provider": {{"id":"provider","model":"model","modelVersion":"1","adapterVersion":"2"}},
  "startedAt": "2026-09-20T10:00:00+00:00",
  "completedAt": "{completedAt}",
  "durationMilliseconds": 1000,
  "outcome": "selected",
  "selectedChoice": "accept",
  "confidence": {{"magnitude":0.8,"provenance":"provider-self-report"}},
  "evidenceUsed": ["EV-1"],
  "escalation": [],
  "transition": null,
  "policy": null,
  "usage": {{"inputTokens":10,"outputTokens":5,"cachedInputTokens":null,"providerReportedCost":"0.01"}},
  "transportRetries": 0,
  "experimentReference": "WI-1"
}}"""

    let private parsed id completed =
        match ObservationJson.parseResolutionObservation (resolutionJson id completed) with
        | Ok value -> value
        | Error error -> failwith error

    let private withTempRoot action =
        let root = Path.Combine(Path.GetTempPath(), $"ros-ordo-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        try action root
        finally Directory.Delete(root, true)

    let tests =
        [ { Name = "Ordo v2 observation preserves state view and scoped coverage"
            Run =
              fun () ->
                  let observation = parsed "res-1" "2026-09-20T10:00:01+00:00"
                  Assert.equal "state-view" observation.StateViewSchema.Id
                  Assert.equal 1 observation.Coverage.Length
                  Assert.equal CoverageStatus.Complete observation.Coverage.Head.Status
                  Assert.equal [ "EV-1" ] observation.Coverage.Head.ProvenanceEvidenceIds
                  Assert.equal (Some "WI-1") observation.ExperimentReference }

          { Name = "future Ordo observation schema fails closed"
            Run =
              fun () ->
                  let raw = resolutionJson "res-1" "2026-09-20T10:00:01+00:00"
                  let future = raw.Replace(""schemaVersion": 2", ""schemaVersion": 99")
                  match ObservationJson.parseResolutionObservation future with
                  | Error message -> Assert.isTrue (message.Contains("supported: 2")) message
                  | Ok _ -> failwith "Future schema was accepted." }

          { Name = "effective current is deterministic and retains superseded resolutions"
            Run =
              fun () ->
                  let older = parsed "res-a" "2026-09-20T10:00:01+00:00"
                  let newer = parsed "res-b" "2026-09-20T10:00:02+00:00"
                  let projection = Projection.effectiveCurrent [ newer; older ] []
                  Assert.equal (Some "res-b") (projection.Resolution |> Option.map (fun x -> x.ResolutionId))
                  Assert.equal [ "res-a" ] projection.SupersededResolutionIds
                  Assert.equal 2 projection.BasisCount }

          { Name = "search observation keeps searched-not-found separate from absence"
            Run =
              fun () ->
                  let raw =
                      """{"schema":"ros.search-observation","schemaVersion":1,"observationId":"search-1","target":"config.json","scope":"repository.files","method":"git-tree","query":"config.json","stateReference":"commit:abc","coverage":"partial","coverageEvidenceIds":["EV-2"],"exclusions":["vendor/"],"errors":[],"outcome":"searched-not-found","observedAt":"2026-09-20T10:00:00+00:00"}"""
                  match ObservationJson.parseSearchObservation raw with
                  | Ok value ->
                      Assert.equal SearchOutcome.SearchedNotFound value.Outcome
                      Assert.equal CoverageStatus.Partial value.Coverage
                  | Error error -> failwith error }

          { Name = "unknown external effect remains unknown and can require reconciliation"
            Run =
              fun () ->
                  let raw =
                      """{"schema":"ros.effect-observation","schemaVersion":1,"observationId":"effect-1","resolutionId":"res-1","effectId":"github-write","outcome":"unknown","attemptedAt":"2026-09-20T10:00:00+00:00","observedAt":"2026-09-20T10:00:02+00:00","reconciliationRequested":true,"reconciliationResult":null,"retryBlocked":true,"compensationBlocked":false}"""
                  match ObservationJson.parseEffectObservation raw with
                  | Ok value ->
                      Assert.equal EffectOutcome.Unknown value.Outcome
                      Assert.equal true value.ReconciliationRequested
                      Assert.equal true value.RetryBlocked
                  | Error error -> failwith error }

          { Name = "file observation ingestion is idempotent and conflicts on changed identity"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let repository = FileObservationRepository.create root
                      let first = parsed "res-1" "2026-09-20T10:00:01+00:00"
                      let changed = { first with Outcome = "different" }
                      Assert.equal StoreOutcome.Stored (ObservationOperations.ingestResolution repository (resolutionJson "res-1" "2026-09-20T10:00:01+00:00") first)
                      Assert.equal StoreOutcome.AlreadyPresent (ObservationOperations.ingestResolution repository "same semantic observation" first)
                      match ObservationOperations.ingestResolution repository "different" changed with
                      | StoreOutcome.Conflict _ -> ()
                      | other -> failwith $"Expected conflict, got {other}"
                      Assert.equal 1 (repository.ListResolutions()).Length) }

          { Name = "assessment requires an ingested resolution and becomes effective current assessment"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let repository = FileObservationRepository.create root
                      let observation = parsed "res-1" "2026-09-20T10:00:01+00:00"
                      ObservationOperations.ingestResolution repository "raw" observation |> ignore
                      let assessment =
                          { AssessmentId = "assess-1"
                            ResolutionId = "res-1"
                            Semantic = SemanticAssessment.Confirmed
                            Operational = OperationalAssessment.Succeeded
                            AssessedAt = DateTimeOffset.Parse("2026-09-20T11:00:00+00:00")
                            EvidenceReferences = [ "EV-outcome" ]
                            Method = "post-condition"
                            Limitations = [] }
                      Assert.equal StoreOutcome.Stored (ObservationOperations.recordAssessment repository assessment)
                      let projection = ObservationOperations.effectiveCurrent repository
                      Assert.equal (Some "assess-1") (projection.Assessment |> Option.map (fun x -> x.AssessmentId))) } ]
