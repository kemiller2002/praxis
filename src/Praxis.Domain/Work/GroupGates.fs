namespace Praxis.Domain.Work

open System

/// The grouped-execution completion gates (PRX-GRP-133..135): a member of
/// a grouped-mode group execution completes only with a committed
/// `praxis.group-analysis/1` and a `praxis.group-verification/1`. The
/// gates check shape and consistency; they never judge whether the reuse
/// choices were right. Missing, malformed or inconsistent evidence is never
/// a pass. Every function here is pure.

type ReuseEntry =
    { Element: string
      Location: string
      Disposition: string
      Reason: string }

type SearchEntry = { Command: string; Finding: string }

type NewAbstraction =
    { Name: string
      ConsideredExisting: string
      WhyNotReused: string }

type AnalysedMember =
    { WorkItemId: string
      Repository: string option
      AcceptanceCriteria: string list }

/// `praxis.group-analysis/1` (PRX-GRP-133): the machine-checkable reuse
/// inventory and member table; the Markdown template stays the readable form.
type GroupAnalysis =
    { GroupId: string
      GroupExecutionId: string option
      Members: AnalysedMember list
      ReuseInventory: ReuseEntry list
      Searches: SearchEntry list
      NewAbstractions: NewAbstraction list }

[<RequireQualifiedAccess>]
type CriterionStatus =
    | Met
    | PartiallyMet
    | NotMet
    | Unknown

[<RequireQualifiedAccess>]
module CriterionStatus =
    let code status =
        match status with
        | CriterionStatus.Met -> "met"
        | CriterionStatus.PartiallyMet -> "partially-met"
        | CriterionStatus.NotMet -> "not-met"
        | CriterionStatus.Unknown -> "unknown"

    let tryParse value =
        match value with
        | "met" -> Some CriterionStatus.Met
        | "partially-met" -> Some CriterionStatus.PartiallyMet
        | "not-met" -> Some CriterionStatus.NotMet
        | "unknown" -> Some CriterionStatus.Unknown
        | _ -> None

type CriterionEvidence =
    { Kind: string
      Reference: string
      Result: string }

type VerificationRow =
    { Member: string
      Criterion: string
      Status: CriterionStatus
      Evidence: CriterionEvidence
      DeferredTo: string option }

/// `praxis.group-verification/1` (PRX-GRP-134): one row per acceptance
/// criterion of a member.
type GroupVerification =
    { GroupId: string
      GroupExecutionId: string option
      Rows: VerificationRow list }

/// The completing member and the group execution it ran in.
type GateSubject =
    { WorkItemId: string
      GroupId: string
      GroupExecutionId: string }

/// Repository facts the gate needs, observed at the edge.
type GateFacts =
    { /// The analysis was committed in an ancestor of the member's first
      /// attributed commit (`Ok true`), not (`Ok false`), or why unknown.
      AnalysisPrecedesChanges: Result<bool, string>
      /// A repository-relative path exists at the completion commit.
      PathExists: string -> bool
      /// A `deferredTo` item's recorded standing.
      Standing: string -> MemberStanding }

[<RequireQualifiedAccess>]
module GroupGates =
    let analysisSchema = "praxis.group-analysis/1"
    let verificationSchema = "praxis.group-verification/1"

    let private observed (evidenceType: string) (observation: SourceObservation<'T>) : Result<'T, string> =
        match observation with
        | SourceObservation.NotSupplied -> Error $"no {evidenceType} evidence was supplied (work complete --evidence {evidenceType}=PATH)"
        | SourceObservation.Ambiguous paths ->
            let joined = String.concat ", " paths
            Error $"more than one {evidenceType} evidence entry was supplied ({joined}); supply exactly one"
        | SourceObservation.Supplied(path, EvidenceReading.Unsupported reason) -> Error $"{evidenceType} evidence '{path}' is unsupported: {reason}"
        | SourceObservation.Supplied(path, EvidenceReading.Malformed reason) -> Error $"{evidenceType} evidence '{path}' is malformed: {reason}"
        | SourceObservation.Supplied(_, EvidenceReading.Parsed value) -> Ok value

    /// The path part of a `path:line` location reference.
    let locationPath (reference: string) =
        match reference.LastIndexOf ':' with
        | index when index > 0 && reference.Substring(index + 1) |> Seq.forall Char.IsDigit && index < reference.Length - 1 -> reference.Substring(0, index)
        | _ -> reference

    /// Every reason the analysis does not hold for this subject.
    let analysisProblems (subject: GateSubject) (facts: GateFacts) (analysis: GroupAnalysis) : string list =
        [ if analysis.GroupId <> subject.GroupId then
              yield $"the group analysis names group {analysis.GroupId}, not {subject.GroupId}"
          match analysis.GroupExecutionId with
          | Some id when id = subject.GroupExecutionId -> ()
          | Some id -> yield $"the group analysis names group execution {id}, not {subject.GroupExecutionId}"
          | None -> yield $"the group analysis names no group execution; it must name {subject.GroupExecutionId}"
          if not (analysis.Members |> List.exists (fun row -> row.WorkItemId = subject.WorkItemId)) then
              yield $"the group analysis does not list {subject.WorkItemId}"
          for row in analysis.Members do
              if row.AcceptanceCriteria.IsEmpty then
                  yield $"the group analysis enumerates no acceptance criteria for {row.WorkItemId}"
          if analysis.ReuseInventory.IsEmpty && analysis.Searches.IsEmpty then
              yield "the reuse inventory is empty and no searches establish that nothing exists to reuse"
          for entry in analysis.NewAbstractions do
              if String.IsNullOrWhiteSpace entry.ConsideredExisting then
                  yield $"new abstraction '{entry.Name}' names no existing element it considered"
          match facts.AnalysisPrecedesChanges with
          | Ok true -> ()
          | Ok false -> yield "the group analysis was not committed before the member's first attributed change (PRX-GRP-133)"
          | Error reason -> yield $"whether the analysis preceded the member's changes is unknown: {reason}" ]

    /// Every reason the verification does not hold for this subject.
    let verificationProblems (subject: GateSubject) (facts: GateFacts) (analysis: GroupAnalysis) (verification: GroupVerification) : string list =
        let rows = verification.Rows |> List.filter (fun row -> row.Member = subject.WorkItemId)

        let expected =
            analysis.Members
            |> List.tryFind (fun row -> row.WorkItemId = subject.WorkItemId)
            |> Option.map (fun row -> row.AcceptanceCriteria)
            |> Option.defaultValue []

        let recorded = rows |> List.map (fun row -> row.Criterion)

        [ if verification.GroupId <> subject.GroupId then
              yield $"the verification names group {verification.GroupId}, not {subject.GroupId}"
          if verification.GroupExecutionId <> Some subject.GroupExecutionId then
              yield $"the verification does not name group execution {subject.GroupExecutionId}"
          if rows.IsEmpty then
              yield $"the verification has no row for {subject.WorkItemId}"
          for duplicate in recorded |> List.countBy id |> List.filter (fun (_, count) -> count > 1) |> List.map fst do
              yield $"criterion is verified more than once: {duplicate}"
          for missing in expected |> List.filter (fun criterion -> not (List.contains criterion recorded)) do
              yield $"criterion enumerated in the analysis is not verified: {missing}"
          for extra in recorded |> List.distinct |> List.filter (fun criterion -> not (List.contains criterion expected)) do
              yield $"criterion is not enumerated in the analysis: {extra}"
          for row in rows do
              match row.Status, row.DeferredTo with
              | CriterionStatus.Met, _ -> ()
              | status, None ->
                  yield $"criterion is {CriterionStatus.code status} and is not deferred to a recorded work item: {row.Criterion}"
              | status, Some item ->
                  match facts.Standing item with
                  | MemberStanding.Open _ -> ()
                  | MemberStanding.Terminal state -> yield $"criterion is {CriterionStatus.code status} and deferred to {item}, which is {state}: {row.Criterion}"
                  | MemberStanding.Unknown -> yield $"criterion is {CriterionStatus.code status} and deferred to {item}, which is not a recorded work item: {row.Criterion}"
              if String.IsNullOrWhiteSpace row.Evidence.Reference then
                  yield $"criterion has no evidence reference: {row.Criterion}"
              elif row.Evidence.Kind = "location" && not (facts.PathExists(locationPath row.Evidence.Reference)) then
                  yield $"evidence location {row.Evidence.Reference} does not exist at the completion commit" ]

    /// The `group-verified` facet for a grouped-mode member (PRX-GRP-135).
    /// A missing or unreadable document is unavailable; an inconsistent one
    /// is not satisfied. Both block, because the facet is required.
    let judge
        (subject: GateSubject)
        (facts: GateFacts)
        (analysis: SourceObservation<GroupAnalysis>)
        (verification: SourceObservation<GroupVerification>)
        : FacetStatus =
        match observed QualityEvidenceTypes.groupAnalysis analysis, observed QualityEvidenceTypes.groupVerification verification with
        | Error first, Error second -> FacetStatus.Unavailable [ first; second ]
        | Error reason, _
        | _, Error reason -> FacetStatus.Unavailable [ reason ]
        | Ok analysis, Ok verification ->
            match analysisProblems subject facts analysis @ verificationProblems subject facts analysis verification with
            | [] ->
                let rows = verification.Rows |> List.filter (fun row -> row.Member = subject.WorkItemId)
                let deferred = rows |> List.filter (fun row -> row.DeferredTo.IsSome) |> List.length

                FacetStatus.Satisfied
                    [ $"group analysis for {analysis.GroupId} ({subject.GroupExecutionId}): {analysis.ReuseInventory.Length} reuse entries, {analysis.NewAbstractions.Length} new abstractions"
                      $"{rows.Length} criteria verified for {subject.WorkItemId} ({deferred} deferred)" ]
            | problems -> FacetStatus.NotSatisfied problems
