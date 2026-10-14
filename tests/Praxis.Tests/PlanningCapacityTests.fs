namespace Praxis.Tests

open System
open System.Text.Json.Nodes
open Praxis.Application.Pacing
open Praxis.Contracts.Planning
open Praxis.Domain.Pacing
open Praxis.Domain.Planning
open Praxis.Infrastructure.Planning
open PlanningFixtures

/// PRX-QUAL-009: provider capacity reaches the planner through a
/// provider-neutral port; uncertainty is not zero; ordering changes are
/// explained; the planner never switches provider.
module PlanningCapacityTests =
    let private t name run = { Name = $"planning capacity: {name}"; Run = run }

    let private now = DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)

    let private providerFree (item: Praxis.Domain.Planning.PlanningQueueItem) = { item with Tags = item.Tags @ [ Capacity.ProviderFreeTag ] }

    // B is older, so the baseline (oldest first) runs it first unless capacity
    // moves the provider-free A ahead of it.
    let private queue = [ queued "B" "ready" "2026-09-01T00:00:00.000Z"; providerFree (queued "A" "ready" "2026-09-02T00:00:00.000Z") ]

    let private capacityOf provider state =
        { Provider = provider
          State = state
          Provenance = Provenance.create EvidenceSource.ExternalObservation "fixture#capacity" }

    let private analyzeWith capacity entries =
        Planner.analyze { input entries [] history [] stateSafe with Capacity = capacity }

    let private baselineOrder analysis =
        simulate analysis stateSafe OptimizationObjective.Baseline None |> scheduled

    let private codes (analysis: PlanningAnalysis) = analysis.Findings |> List.map (fun finding -> FindingCode.code finding.Code)

    let tests =
        [ t "no observed capacity is unknown, not zero, and changes nothing" (fun () ->
              let analysis = analyzeWith [] queue
              Assert.isTrue (not analysis.Capacity.Limited) "nothing observed is not limited"
              Assert.isTrue (not analysis.Capacity.AffectsOrdering) "ordering unaffected"
              Assert.isTrue (analysis.Capacity.Statement.Contains "unknown (not zero)") analysis.Capacity.Statement
              Assert.equal [ "B"; "A" ] (baselineOrder analysis))

          t "an exhausted provider orders provider-free work first and explains why" (fun () ->
              let analysis = analyzeWith [ capacityOf "agent-x" (CapacityState.Exhausted(Some "2026-10-07T17:00:00Z", "hard limit")) ] queue
              Assert.isTrue analysis.Capacity.AffectsOrdering "capacity affects ordering"
              let plan = simulate analysis stateSafe OptimizationObjective.Baseline None
              Assert.equal [ "A"; "B" ] (scheduled plan)
              Assert.isTrue (reasonsOf plan "A" |> List.exists (fun reason -> reason.Code = ReasonCode.ProviderFreeFirst)) "A says why it moved"
              let deferred = reasonsOf plan "B" |> List.find (fun reason -> reason.Code = ReasonCode.ProviderCapacityDeferred)
              Assert.isTrue (deferred.Message.Contains "does not choose providers") "the planner never switches provider"
              Assert.isTrue (List.contains "provider-capacity-limited" (codes analysis)) "a capacity finding")

          t "a constrained provider affects every strategy's order, not only the baseline" (fun () ->
              let analysis = analyzeWith [ capacityOf "agent-x" (CapacityState.Constrained(None, "weekly pacing hold")) ] queue

              for objective in [ OptimizationObjective.Baseline; OptimizationObjective.Balanced; OptimizationObjective.MinimumDuration ] do
                  let first = simulate analysis stateSafe objective None |> scheduled |> List.head
                  Assert.equal "A" first)

          t "unknown capacity is reported separately and never treated as exhausted" (fun () ->
              let analysis = analyzeWith [ capacityOf "agent-x" (CapacityState.Unknown "provider offline") ] queue
              Assert.isTrue (not analysis.Capacity.Limited) "unknown is not limited"
              Assert.isTrue (List.contains "provider-capacity-unknown" (codes analysis)) "an unknown-capacity finding"
              Assert.isTrue (not (List.contains "provider-capacity-limited" (codes analysis))) "not a limited finding"
              Assert.equal [ "B"; "A" ] (baselineOrder analysis))

          t "limited capacity with no provider-free work leaves ordering unchanged and says so" (fun () ->
              let analysis = analyzeWith [ capacityOf "agent-x" (CapacityState.Exhausted(None, "hard limit")) ] [ queued "B" "ready" "2026-09-01T00:00:00.000Z"; queued "C" "ready" "2026-09-03T00:00:00.000Z" ]
              Assert.isTrue analysis.Capacity.Limited "limited"
              Assert.isTrue (not analysis.Capacity.AffectsOrdering) "nothing to move"
              Assert.isTrue (analysis.Capacity.Statement.Contains "ordering is unchanged") analysis.Capacity.Statement)

          t "the analysis document reports capacity and that provider switching is never chosen" (fun () ->
              let analysis = analyzeWith [ capacityOf "agent-x" (CapacityState.Exhausted(Some "2026-10-07T17:00:00Z", "hard limit")) ] queue
              let node = PlanningJson.analysis analysis
              let capacity = node["capacity"]
              Assert.equal true (capacity["affectsOrdering"].GetValue<bool>())
              let provider = capacity["providers"].[0]
              Assert.equal "exhausted" (provider["state"].GetValue<string>())
              Assert.equal "2026-10-07T17:00:00Z" (provider["until"].GetValue<string>())
              Assert.isTrue ((capacity["providerSwitching"].GetValue<string>()).StartsWith "never") "never switches")

          t "capacity is part of the snapshot evidence only when observed" (fun () ->
              let without = analyzeWith [] queue
              let again = analyzeWith [] queue
              Assert.equal without.Snapshot.InputFingerprint again.Snapshot.InputFingerprint
              let observed = analyzeWith [ capacityOf "agent-x" CapacityState.Available ] queue
              Assert.isTrue (observed.Snapshot.InputFingerprint <> without.Snapshot.InputFingerprint) "capacity changes the evidence")

          t "supplied capacity decodes provider-neutral states and refuses unknown ones" (fun () ->
              let json = """{"observations":[],"capacity":[{"provider":"agent-x","state":"exhausted","until":"2026-10-07T17:00:00Z","reason":"hard limit"},{"provider":"agent-y","state":"unknown"}]}"""

              match PlanningJson.parseCapacity "obs.json" json with
              | Ok [ first; second ] ->
                  Assert.equal (CapacityState.Exhausted(Some "2026-10-07T17:00:00Z", "hard limit")) first.State
                  Assert.equal (CapacityState.Unknown "no reason given") second.State
              | other -> failwith $"{other}"

              Assert.equal (Ok []) (PlanningJson.parseCapacity "obs.json" """{"observations":[]}""")

              match PlanningJson.parseCapacity "obs.json" """{"capacity":[{"provider":"x","state":"infinite"}]}""" with
              | Error message -> Assert.isTrue (message.Contains "infinite") message
              | Ok _ -> failwith "an unknown state must be refused")

          t "pacing state maps onto capacity: hard limit exhausted, weekly constrained, stale or unreadable unknown" (fun () ->
              let hold provider key basis =
                  { Key = key
                    Provider = provider
                    Scope = QuotaScope.Global
                    Since = now
                    Basis = basis
                    Evidence = HoldEvidence.Unrecorded }

              let state =
                  PacingState.ofHolds
                      [ hold ProviderId.Codex "codex/session" (HoldBasis.HardLimit(99m, now + TimeSpan.FromHours 1.0))
                        hold ProviderId.Claude "claude/seven_day" (HoldBasis.WeeklyLead(Some(now + TimeSpan.FromDays 2.0))) ]

              let recent _ = Some(now - TimeSpan.FromMinutes 1.0)
              let read = PacingStateRead.Current(state, 1L)

              match FilePacingCapacity.fromState "/pacing" now read recent with
              | [ codex; claude ] ->
                  Assert.equal "codex" codex.Provider
                  Assert.equal "exhausted" (CapacityState.code codex.State)
                  Assert.equal "constrained" (CapacityState.code claude.State)
              | other -> failwith $"{other}"

              match FilePacingCapacity.fromState "/pacing" now PacingStateRead.Absent recent with
              | [ codex; claude ] -> Assert.equal [ CapacityState.Available; CapacityState.Available ] [ codex.State; claude.State ]
              | other -> failwith $"{other}"

              let stale _ = Some(now - TimeSpan.FromHours 2.0)

              match FilePacingCapacity.fromState "/pacing" now PacingStateRead.Absent stale with
              | first :: _ -> Assert.equal "unknown" (CapacityState.code first.State)
              | [] -> failwith "readings exist"

              match FilePacingCapacity.fromState "/pacing" now (PacingStateRead.Unreadable "corrupt") recent with
              | first :: _ -> Assert.equal "unknown" (CapacityState.code first.State)
              | [] -> failwith "readings exist"

              Assert.equal [] (FilePacingCapacity.fromState "/pacing" now PacingStateRead.Absent (fun _ -> None))) ]
