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
        $"""{{
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
                  let future = raw.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 99")
                  match ObservationJson.parseResolutionObservation future with
                  | Error message -> Assert.isTrue (message.Contains("supported: 2")) message
                  | Ok _ -> failwith "Future schema was accepted." }

          { Name = "Ordo v2 ingestion tolerates unknown additive fields"
            Run =
              fun () ->
                  let raw =
                      resolutionJson "res-additive" "2026-09-20T10:00:01+00:00"
                      |> fun text -> text.Replace("\"transportRetries\": 0", "\"futureSemanticDecoration\":{\"ignored\":true},\n  \"transportRetries\": 0")

                  match ObservationJson.parseResolutionObservation raw with
                  | Ok value -> Assert.equal "res-additive" value.ResolutionId
                  | Error error -> failwith error }

          { Name = "unavailable provider usage remains unavailable rather than synthetic zero"
            Run =
              fun () ->
                  let raw =
                      resolutionJson "res-unavailable" "2026-09-20T10:00:01+00:00"
                      |> fun text -> text.Replace("\"inputTokens\":10", "\"inputTokens\":null")
                      |> fun text -> text.Replace("\"outputTokens\":5", "\"outputTokens\":null")

                  match ObservationJson.parseResolutionObservation raw with
                  | Ok value ->
                      Assert.equal None value.Usage.InputTokens
                      Assert.equal None value.Usage.OutputTokens
                      Assert.equal None value.Usage.CachedInputTokens
                  | Error error -> failwith error }

          { Name = "missing required Ordo semantic fields fail closed rather than being guessed"
            Run =
              fun () ->
                  let raw =
                      resolutionJson "res-missing" "2026-09-20T10:00:01+00:00"
                      |> fun text -> text.Replace("  \"stateViewSchema\": {\"id\":\"state-view\",\"version\":1},\n", "")

                  match ObservationJson.parseResolutionObservation raw with
                  | Error message -> Assert.isTrue (message.Contains("stateViewSchema")) message
                  | Ok _ -> failwith "Missing stateViewSchema was silently accepted." }

          { Name = "resolution ingestion retains the exact raw payload separately from normalization"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let repository = FileObservationRepository.create root
                      let raw =
                          resolutionJson "res-raw" "2026-09-20T10:00:01+00:00"
                          |> fun text -> text.Replace("\"transportRetries\": 0", "\"unknownAdditiveField\":\"preserve me\",\n  \"transportRetries\": 0")
                      let observation =
                          match ObservationJson.parseResolutionObservation raw with
                          | Ok value -> value
                          | Error error -> failwith error

                      Assert.equal StoreOutcome.Stored (ObservationOperations.ingestResolution repository raw observation)
                      let rawFiles = Directory.GetFiles(Path.Combine(root, ".ros", "ordo", "raw", "resolutions"), "*.json")
                      Assert.equal 1 rawFiles.Length
                      Assert.equal raw (File.ReadAllText rawFiles[0])) }

          { Name = "effective current requires explicit authority and never infers latest as current"
            Run =
              fun () ->
                  let older = parsed "res-a" "2026-09-20T10:00:01+00:00"
                  let newer = parsed "res-b" "2026-09-20T10:00:02+00:00"

                  match Projection.effectiveCurrent None [] [ newer; older ] [] with
                  | Ok projection ->
                      Assert.equal None (projection.Resolution |> Option.map (fun x -> x.ResolutionId))
                      Assert.equal [] projection.SupersededResolutionIds
                  | Error error -> failwith error

                  match Projection.effectiveCurrent (Some "res-a") [ "res-b" ] [ newer; older ] [] with
                  | Ok projection ->
                      Assert.equal (Some "res-a") (projection.Resolution |> Option.map (fun x -> x.ResolutionId))
                      Assert.equal [ "res-b" ] projection.SupersededResolutionIds
                      Assert.equal 2 projection.BasisCount
                  | Error error -> failwith error }

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
                      """{"schema":"ros.effect-observation","schemaVersion":1,"observationId":"effect-1","resolutionId":"res-1","effectId":"github-write","outcome":"unknown","attemptedAt":"2026-09-20T10:00:00+00:00","observedAt":"2026-09-20T10:00:02+00:00","reconciliationRequested":true,"reconciliationResult":null,"reconciledAt":null,"retryBlocked":true,"compensationBlocked":false}"""
                  match ObservationJson.parseEffectObservation raw with
                  | Ok value ->
                      Assert.equal EffectOutcome.Unknown value.Outcome
                      Assert.equal true value.ReconciliationRequested
                      Assert.equal None (EffectObservation.timeToResolution value)
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
                            Assessor = "integration-test"
                            AssessedAt = DateTimeOffset.Parse("2026-09-20T11:00:00+00:00")
                            RecordedAt = DateTimeOffset.Parse("2026-09-20T11:01:00+00:00")
                            EvidenceReferences = [ "EV-outcome" ]
                            Method = "post-condition"
                            Limitations = [] }
                      Assert.equal StoreOutcome.Stored (ObservationOperations.recordAssessment repository assessment)

                      match ObservationOperations.effectiveCurrent repository (Some "res-1") [] with
                      | Ok projection ->
                          Assert.equal (Some "assess-1") (projection.Assessment |> Option.map (fun x -> x.AssessmentId))
                      | Error error -> failwith error) }

          { Name = "effect reconciliation records measurable time to resolution"
            Run =
              fun () ->
                  let raw =
                      """{"schema":"ros.effect-observation","schemaVersion":1,"observationId":"effect-resolved","resolutionId":"res-1","effectId":"github-write","outcome":"unknown","attemptedAt":"2026-09-20T10:00:00+00:00","observedAt":"2026-09-20T10:00:02+00:00","reconciliationRequested":true,"reconciliationResult":"write-present","reconciledAt":"2026-09-20T10:03:00+00:00","retryBlocked":true,"compensationBlocked":false}"""

                  match ObservationJson.parseEffectObservation raw with
                  | Ok value ->
                      Assert.equal (Some(TimeSpan.FromMinutes 3.0)) (EffectObservation.timeToResolution value)
                  | Error error -> failwith error }

          { Name = "handoff carries explicit authority artifacts history verification and selected resolution"
            Run =
              fun () ->
                  let observation = parsed "res-1" "2026-09-20T10:00:01+00:00"

                  match
                      Projection.effectiveCurrent (Some "res-1") [] [ observation ] []
                      |> Result.map (
                          Projection.handoff
                              "commit:abc"
                              "context/CURRENT-STATE.md"
                              [ "SDE-MAP.md"; "context/CURRENT-STATE.md" ]
                              [ "DF-OLD" ]
                              [ "fact" ]
                              [ "assumption" ]
                              [ "unknown" ]
                              [ "obligation" ]
                              [ "ros validate"; "npm test" ]
                              [ "next" ]
                      )
                  with
                  | Ok handoff ->
                      Assert.equal [ "SDE-MAP.md"; "context/CURRENT-STATE.md" ] handoff.AuthoritativeArtifacts
                      Assert.equal [ "DF-OLD" ] handoff.HistoricalDecisionReferences
                      Assert.equal [ "ros validate"; "npm test" ] handoff.CompletedVerification
                      Assert.equal (Some "res-1") handoff.ResolutionId
                  | Error error -> failwith error } ]
