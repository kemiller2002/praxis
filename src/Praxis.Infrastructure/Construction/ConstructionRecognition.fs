namespace Praxis.Infrastructure.Construction

open Ordo.Core

/// One approved decision Ordo's recognition pass attaches to delegated work,
/// with every requirement key of the packets that carry it and the conflicts
/// it resolves (Ordo's typed `resolves` link, ordo#88).
type RecognizedDecision = { NodeId: string; Justification: string option; RequirementKeys: string list; Resolves: string list }

/// What Praxis takes from Ordo's recognition pass (CONSTRUCT-010): whether
/// generation is worthwhile, the approved decisions delegated work needs,
/// the requirements an agent may only escalate, and the forbidden paths.
/// The full report stays host-side, by digest, for provenance.
type RecognitionSummary =
    { ReportDigest: string
      ReportJson: string
      GenerationWorthwhile: bool
      Decisions: RecognizedDecision list
      EscalationRequired: Set<string>
      /// Requirement keys Ordo delegates to the agent (in some packet).
      Delegated: Set<string>
      ForbiddenPaths: string list
      Measurements: (string * int) list }

/// The recognition pass Praxis calls before default-plan generation.
/// `Registry` (the pinned registry's label) enters the reuse cache key.
type RecognitionSeam =
    { Registry: string
      /// prepared cohort -> exact approved decision-record text -> grant-authorized
      /// decision ids -> escalate open questions -> host-permitted paths
      Recognize: ConstructionIr.PreparedCohort -> string -> Set<string> -> bool -> string list -> Result<RecognitionSummary, string list> }

/// The single seam between Praxis and Ordo's pattern recognition
/// (`Ordo.Core.PatternRecognitionWire`, Ordo 1.15.0).
[<RequireQualifiedAccess>]
module ConstructionRecognition =
    let private summarize (report: PatternRecognition.RecognitionReport) : RecognitionSummary =
        let packets = report.Packets
        let decisions =
            packets
            |> List.collect (fun p -> p.ApprovedDecisions |> List.map (fun d -> d.NodeId, (d.Justification, p.RequirementKeys, d.Resolves)))
            |> List.groupBy fst
            |> List.map (fun (nodeId, carried) ->
                { NodeId = nodeId
                  Justification = carried |> List.tryPick (fun (_, (justification, _, _)) -> justification)
                  RequirementKeys = carried |> List.collect (fun (_, (_, keys, _)) -> keys) |> List.distinct
                  Resolves = carried |> List.collect (fun (_, (_, _, resolves)) -> resolves) |> List.distinct |> List.sort })
        let m = report.Measurements
        { ReportDigest = PatternRecognitionWire.reportDigest report
          ReportJson = PatternRecognitionWire.encodeReport report
          GenerationWorthwhile = report.GenerationWorthwhile
          Decisions = decisions
          EscalationRequired = packets |> List.collect _.EscalationRequired |> Set.ofList
          Delegated = packets |> List.collect _.RequirementKeys |> Set.ofList
          ForbiddenPaths = packets |> List.collect _.ForbiddenPaths |> List.distinct
          Measurements =
            [ "assignedRequirements", m.AssignedRequirements; "constructible", m.Constructible
              "decisionRequired", m.DecisionRequired; "delegated", m.Delegated; "blocked", m.Blocked
              "partiallyGenerated", m.PartiallyGenerated; "delegationPackets", m.DelegationPackets
              "delegationBytes", m.DelegationBytes ] }

    /// THE SEAM: Ordo's recognition over the grant-bound inputs. The registry
    /// is Ordo's built-in one; refusals are reported verbatim.
    let recognize (prepared: ConstructionIr.PreparedCohort) (decisionRecords: string) (authorized: Set<string>) (escalate: bool) (allowedPaths: string list) =
        ConstructionDecisions.readDecisionRecords decisionRecords
        |> Result.mapError (fun reason -> [ "decision records refused: " + reason ])
        |> Result.bind (fun records ->
            let view = ConstructionIr.preparedCohortView prepared
            let input : PatternRecognition.RecognitionInput =
                { Manifest = view.Manifest
                  Blueprint = view.Blueprint
                  CohortId = view.CohortId
                  AuthorizedDecisionIds = authorized
                  Records = Some records
                  Policy = { AllowedPaths = allowedPaths; ArchitectureConstraints = []; EscalateOpenQuestions = escalate } }
            PatternRecognitionWire.recognize PatternRegistry.BuiltIn.registry None input
            |> Result.mapError (List.map (fun error -> sprintf "%A" error)))
        |> Result.map summarize

    let seam () : RecognitionSeam = { Registry = "ordo.patterns/1 (built-in)"; Recognize = recognize }
