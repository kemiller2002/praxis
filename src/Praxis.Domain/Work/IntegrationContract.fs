namespace Praxis.Domain.Work

open System
open Praxis.Domain.Git
open Praxis.Domain.Planning

// Integration contracts for parallel workstreams (`praxis.integration-contract/1`,
// docs/work-protocol.md "Integrating parallel workstreams"). A stream is a
// work item worked on its own ref. The contract says which paths each stream
// owns, which shared interfaces need an approval to change, the order streams
// integrate in, and what makes a stream mergeable. Everything below is a pure
// function of the contract and of Git facts gathered by a thin shell.

/// How a stream's ownership is matched: an Ordo glob (`*`, `?`, `**`), or a
/// planner-area path, which owns itself and everything below it (the
/// prefix rule of `praxis plan analyze`).
[<RequireQualifiedAccess>]
type PathRule =
    | Glob of string
    | AreaPrefix of string

[<RequireQualifiedAccess>]
module PathRule =
    let private normalize (value: string) = value.Replace('\\', '/').Trim('/')

    let matches (rule: PathRule) (path: string) =
        match rule with
        | PathRule.Glob pattern -> Praxis.Domain.Execution.Glob.isMatch pattern path
        | PathRule.AreaPrefix prefix ->
            let prefix, path = normalize prefix, normalize path
            path = prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal)

    let anyMatches (rules: PathRule list) (path: string) = rules |> List.exists (fun rule -> matches rule path)

    let globs (patterns: string list) = patterns |> List.map PathRule.Glob

/// Who authorized a change to a shared interface: a Decision Record that
/// must exist at the integration target, or an approval recorded in the
/// contract itself by a named approver.
[<RequireQualifiedAccess>]
type ApprovalAuthority =
    | Decision of id: string
    | Recorded of approver: string

type InterfaceApproval =
    { Stream: string
      /// Globs within the interface; empty means the whole interface.
      Paths: string list
      Authority: ApprovalAuthority
      Reason: string option }

type SharedInterface =
    { Id: string
      Owner: string
      Paths: string list
      Approvals: InterfaceApproval list }

[<RequireQualifiedAccess>]
type ReadinessCondition =
    | Ownership
    | Interfaces
    | Overlap
    | Dependencies
    | Artifacts
    | VerifiedCompletion

[<RequireQualifiedAccess>]
module ReadinessCondition =
    let all =
        [ ReadinessCondition.Ownership
          ReadinessCondition.Interfaces
          ReadinessCondition.Overlap
          ReadinessCondition.Dependencies
          ReadinessCondition.Artifacts
          ReadinessCondition.VerifiedCompletion ]

    let code condition =
        match condition with
        | ReadinessCondition.Ownership -> "ownership"
        | ReadinessCondition.Interfaces -> "interfaces"
        | ReadinessCondition.Overlap -> "overlap"
        | ReadinessCondition.Dependencies -> "dependencies"
        | ReadinessCondition.Artifacts -> "artifacts"
        | ReadinessCondition.VerifiedCompletion -> "verified-completion"

    let tryParse (value: string) = all |> List.tryFind (fun condition -> code condition = value)

/// Where a stream's owned paths come from: declared in the contract, or the
/// work item's `planner.areas` entry (never re-declared).
[<RequireQualifiedAccess>]
type OwnershipSource =
    | Declared of globs: string list
    | PlannerArea

type StreamContract =
    { Id: string
      WorkItem: string
      Ref: string
      Owns: OwnershipSource
      DependsOn: string list
      RequiredTests: string list
      Readiness: ReadinessCondition list
      Artifacts: string list }

type IntegrationContract =
    { Target: string option
      /// Paths merged by tooling (for example `.ros/**`, merged by `praxis
      /// work history merge`): neither owned nor counted as overlap.
      ManagedPaths: string list
      /// Paths any stream may change; overlap on them is still reported.
      CommonPaths: string list
      Streams: StreamContract list
      Interfaces: SharedInterface list }

[<RequireQualifiedAccess>]
module IntegrationContract =
    let Schema = "praxis.integration-contract/1"

/// A stream with its ownership and dependencies resolved against the
/// planner configuration.
type ResolvedStream =
    { Contract: StreamContract
      Ownership: PathRule list
      OwnershipOrigin: string
      /// Each dependency and where it was declared (`contract` or `planner`).
      Dependencies: (string * string) list }

/// What the Git shell observed about one stream at its ref.
[<RequireQualifiedAccess>]
type CompletionEvidence =
    /// No `work.completion.verified` event for the stream's work item at its tip.
    | Missing
    /// The latest such event: the verified commit, its receipt digests, and
    /// the paths changed between the verified commit and the tip.
    | Verified of commit: string * receiptDigests: string list * changedSince: string list

type StreamFacts =
    { Stream: string
      /// `None` when the ref does not resolve.
      Tip: string option
      MergeBase: string option
      /// Changed from the merge base with the target to the tip.
      PendingPaths: string list
      /// Changed from the merge base to the target.
      TargetIncomingPaths: string list
      /// The tip is reachable from the target.
      Integrated: bool
      /// Declared artifact patterns that match a path at the tip.
      PresentArtifacts: string list
      Completion: CompletionEvidence }

/// Changes of two streams since their own merge base.
type PairFacts =
    { Left: string
      Right: string
      LeftPaths: string list
      RightPaths: string list }

type IntegrationFacts =
    { Target: string
      TargetCommit: string option
      Streams: StreamFacts list
      Pairs: PairFacts list
      /// Decision Record IDs found at the target.
      KnownDecisions: string list }

[<RequireQualifiedAccess>]
type PathFinding =
    /// Changed outside the stream's ownership; `owners` are streams owning it.
    | OutsideOwnership of path: string * owners: string list
    /// A shared interface changed by a non-owner without an approval.
    | InterfaceUnapproved of path: string * interfaceId: string * owner: string
    /// An approval cites a Decision Record that is not at the target.
    | DecisionNotFound of path: string * interfaceId: string * decision: string

[<RequireQualifiedAccess>]
module PathFinding =
    let code finding =
        match finding with
        | PathFinding.OutsideOwnership _ -> "outside-ownership"
        | PathFinding.InterfaceUnapproved _ -> "interface-unapproved"
        | PathFinding.DecisionNotFound _ -> "approval-decision-not-found"

    let path finding =
        match finding with
        | PathFinding.OutsideOwnership(path, _)
        | PathFinding.InterfaceUnapproved(path, _, _)
        | PathFinding.DecisionNotFound(path, _, _) -> path

/// Why a changed path is within the stream's authority.
type CoveredPath = { Path: string; Basis: string }

type ConditionResult =
    { Condition: ReadinessCondition
      /// `None`: no evidence either way (unverified), never assumed met.
      Met: bool option
      Detail: string }

[<RequireQualifiedAccess>]
type Readiness =
    | Ready
    | Unverified
    | Blocked
    | Unavailable

[<RequireQualifiedAccess>]
module Readiness =
    let code readiness =
        match readiness with
        | Readiness.Ready -> "ready"
        | Readiness.Unverified -> "unverified"
        | Readiness.Blocked -> "blocked"
        | Readiness.Unavailable -> "unavailable"

type RequiredTestStatus = { Test: string; Status: string; Detail: string }

type StreamReport =
    { Stream: string
      WorkItem: string
      Ref: string
      Tip: string option
      OwnershipOrigin: string
      Integrated: bool
      Findings: PathFinding list
      Covered: CoveredPath list
      ManagedPaths: string list
      TargetOverlap: string list
      RequiredTests: RequiredTestStatus list
      Conditions: ConditionResult list
      Readiness: Readiness }

type StreamOverlap =
    { Left: string
      Right: string
      Paths: string list
      ManagedPaths: string list }

type IntegrationReport =
    { Target: string
      TargetCommit: string option
      ContractFindings: string list
      Streams: StreamReport list
      SiblingOverlaps: StreamOverlap list
      Sequence: string list
      Cycles: string list list }

[<RequireQualifiedAccess>]
module IntegrationReport =
    /// Passing means every reported stream is ready, the contract is
    /// consistent and its dependencies are acyclic.
    let passes (report: IntegrationReport) =
        report.ContractFindings.IsEmpty
        && report.Cycles.IsEmpty
        && report.Streams |> List.forall (fun stream -> stream.Readiness = Readiness.Ready)

[<RequireQualifiedAccess>]
module IntegrationStreams =
    let private sortPaths (paths: string list) = paths |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    /// Ownership from the contract or from `planner.areas`; dependencies from
    /// the contract plus hard planner dependencies between bound work items.
    let resolve (areas: (string * string list) list) (plannerDependencies: DeclaredDependency list) (contract: IntegrationContract) : ResolvedStream list =
        let byWorkItem = contract.Streams |> List.map (fun stream -> stream.WorkItem, stream.Id) |> Map.ofList

        contract.Streams
        |> List.map (fun stream ->
            let ownership, origin =
                match stream.Owns with
                | OwnershipSource.Declared globs -> PathRule.globs globs, "contract"
                | OwnershipSource.PlannerArea ->
                    let paths = areas |> List.tryFind (fun (item, _) -> item = stream.WorkItem) |> Option.map snd |> Option.defaultValue []
                    paths |> List.map PathRule.AreaPrefix, $"planner.areas[{stream.WorkItem}]"

            let planned =
                plannerDependencies
                |> List.filter (fun dependency -> dependency.From = stream.WorkItem && dependency.Kind = DependencyKind.Hard)
                |> List.choose (fun dependency -> byWorkItem.TryFind dependency.To)
                |> List.filter (fun id -> not (stream.DependsOn |> List.contains id))
                |> List.map (fun id -> id, "planner")

            { Contract = stream
              Ownership = ownership
              OwnershipOrigin = origin
              Dependencies = (stream.DependsOn |> List.map (fun id -> id, "contract")) @ planned })

    /// Internal consistency: unique IDs, known references, non-empty ownership.
    let contractFindings (contract: IntegrationContract) (resolved: ResolvedStream list) =
        let ids = contract.Streams |> List.map _.Id
        let known = Set.ofList ids

        [ for id in ids |> List.countBy id |> List.filter (fun (_, count) -> count > 1) |> List.map fst do
              yield $"stream '{id}' is declared more than once"
          for stream in resolved do
              if stream.Ownership.IsEmpty then
                  yield $"stream '{stream.Contract.Id}' owns no paths ({stream.OwnershipOrigin} is empty)"
              for dependency, _ in stream.Dependencies do
                  if not (known.Contains dependency) then
                      yield $"stream '{stream.Contract.Id}' depends on unknown stream '{dependency}'"
          for shared in contract.Interfaces do
              if not (known.Contains shared.Owner) then
                  yield $"interface '{shared.Id}' names unknown owner '{shared.Owner}'"
              for approval in shared.Approvals do
                  if not (known.Contains approval.Stream) then
                      yield $"interface '{shared.Id}' approves unknown stream '{approval.Stream}'" ]

    /// One changed path judged against the contract for `stream`: `Ok basis`
    /// when the stream may change it, or the finding.
    let classify (contract: IntegrationContract) (resolved: ResolvedStream list) (knownDecisions: Set<string>) (stream: string) (path: string) : Result<string, PathFinding> =
        let interfaces = contract.Interfaces |> List.filter (fun shared -> PathRule.anyMatches (PathRule.globs shared.Paths) path)
        let owns (candidate: ResolvedStream) = PathRule.anyMatches candidate.Ownership path
        let self = resolved |> List.tryFind (fun candidate -> candidate.Contract.Id = stream)

        match interfaces with
        | _ :: _ when interfaces |> List.exists (fun shared -> shared.Owner = stream) ->
            Ok $"interface-owner:{(interfaces |> List.find (fun shared -> shared.Owner = stream)).Id}"
        | first :: _ ->
            let approvals =
                interfaces
                |> List.collect (fun shared ->
                    shared.Approvals
                    |> List.filter (fun approval ->
                        approval.Stream = stream
                        && (approval.Paths.IsEmpty || PathRule.anyMatches (PathRule.globs approval.Paths) path))
                    |> List.map (fun approval -> shared, approval))

            let valid =
                approvals
                |> List.tryFind (fun (_, approval) ->
                    match approval.Authority with
                    | ApprovalAuthority.Recorded _ -> true
                    | ApprovalAuthority.Decision id -> knownDecisions.Contains id)

            match valid, approvals with
            | Some(shared, approval), _ ->
                let authority =
                    match approval.Authority with
                    | ApprovalAuthority.Recorded approver -> $"recorded:{approver}"
                    | ApprovalAuthority.Decision id -> $"decision:{id}"

                Ok $"approved:{shared.Id}:{authority}"
            | None, (shared, { Authority = ApprovalAuthority.Decision id }) :: _ -> Error(PathFinding.DecisionNotFound(path, shared.Id, id))
            | None, _ -> Error(PathFinding.InterfaceUnapproved(path, first.Id, first.Owner))
        | [] when self |> Option.exists owns -> Ok "owned"
        | [] when PathRule.anyMatches (PathRule.globs contract.CommonPaths) path -> Ok "common"
        | [] ->
            let owners = resolved |> List.filter (fun candidate -> candidate.Contract.Id <> stream && owns candidate) |> List.map _.Contract.Id
            Error(PathFinding.OutsideOwnership(path, owners))

    let private managed (contract: IntegrationContract) (path: string) =
        PathRule.anyMatches (PathRule.globs contract.ManagedPaths) path

    /// Overlap on unmanaged paths, and separately on managed ones.
    let private splitOverlap (contract: IntegrationContract) (left: string list) (right: string list) =
        let paths = UpstreamSync.overlap (sortPaths left) (sortPaths right)
        paths |> List.filter (managed contract >> not), paths |> List.filter (managed contract)

    let private requiredTests (stream: StreamContract) (facts: StreamFacts) : RequiredTestStatus list =
        stream.RequiredTests
        |> List.map (fun test ->
            match facts.Completion with
            | CompletionEvidence.Verified(commit, _, changedSince) when changedSince |> List.forall (fun path -> path.StartsWith(".ros/", StringComparison.Ordinal)) ->
                { Test = test
                  Status = "covered-by-verified-completion"
                  Detail = $"verified completion at {commit}; the event binds the validations Dokimos verified, it does not name this test" }
            | CompletionEvidence.Verified(commit, _, _) ->
                { Test = test
                  Status = "unverified"
                  Detail = $"the verified completion at {commit} predates source changes on the stream" }
            | CompletionEvidence.Missing ->
                { Test = test
                  Status = "unverified"
                  Detail = "no work.completion.verified event for the work item at the stream tip" })

    let private evaluate
        (contract: IntegrationContract)
        (stream: ResolvedStream)
        (facts: StreamFacts)
        (findings: PathFinding list)
        (targetOverlap: string list)
        (siblingOverlap: StreamOverlap list)
        (dependencyIntegrated: string -> bool option)
        (cycles: string list list)
        =
        let condition kind (met: bool option) detail : ConditionResult = { Condition = kind; Met = met; Detail = detail }
        let codes kind = findings |> List.filter (fun finding -> PathFinding.code finding = kind) |> List.length

        stream.Contract.Readiness
        |> List.map (fun kind ->
            match kind with
            | ReadinessCondition.Ownership ->
                let count = codes "outside-ownership"
                condition kind (Some(count = 0)) (if count = 0 then "every changed path is owned, common, managed or approved" else $"{count} path(s) outside the stream's ownership")
            | ReadinessCondition.Interfaces ->
                let count = codes "interface-unapproved" + codes "approval-decision-not-found"
                condition kind (Some(count = 0)) (if count = 0 then "no unapproved shared-interface change" else $"{count} shared-interface path(s) changed without a valid approval")
            | ReadinessCondition.Overlap ->
                let siblings = siblingOverlap |> List.filter (fun overlap -> not overlap.Paths.IsEmpty)

                match targetOverlap, siblings with
                | [], [] -> condition kind (Some true) "no unmanaged path overlaps the target or a sibling stream"
                | _ ->
                    let names = siblings |> List.map (fun overlap -> if overlap.Left = stream.Contract.Id then overlap.Right else overlap.Left)
                    let siblingText = if names.IsEmpty then "" else $"""; sibling(s) {String.Join(", ", names)}"""
                    condition kind (Some false) $"{targetOverlap.Length} path(s) also changed on the target{siblingText}"
            | ReadinessCondition.Dependencies ->
                let inCycle = cycles |> List.exists (List.contains stream.Contract.Id)
                let states = stream.Dependencies |> List.map (fun (id, _) -> id, dependencyIntegrated id)
                let pending = states |> List.filter (fun (_, state) -> state <> Some true) |> List.map fst

                if inCycle then condition kind (Some false) "the stream is in a dependency cycle"
                elif pending.IsEmpty then condition kind (Some true) "every dependency is integrated into the target"
                else condition kind (Some false) $"""not yet integrated: {String.Join(", ", pending)}"""
            | ReadinessCondition.Artifacts ->
                let missing = stream.Contract.Artifacts |> List.filter (fun artifact -> not (facts.PresentArtifacts |> List.contains artifact))
                if missing.IsEmpty then condition kind (Some true) "every expected artifact exists at the tip"
                else condition kind (Some false) $"""missing at the tip: {String.Join(", ", missing)}"""
            | ReadinessCondition.VerifiedCompletion ->
                match facts.Completion with
                | CompletionEvidence.Missing -> condition kind None "unverified: no work.completion.verified event for the work item at the tip"
                | CompletionEvidence.Verified(commit, _, changedSince) ->
                    let source = changedSince |> List.filter (fun path -> not (path.StartsWith(".ros/", StringComparison.Ordinal)))
                    if source.IsEmpty then condition kind (Some true) $"verified completion at {commit}"
                    else condition kind None $"unverified: {source.Length} source path(s) changed after the verified commit {commit}")

    let private readinessOf (conditions: ConditionResult list) =
        if conditions |> List.exists (fun condition -> condition.Met = Some false) then Readiness.Blocked
        elif conditions |> List.exists (fun condition -> condition.Met.IsNone) then Readiness.Unverified
        else Readiness.Ready

    /// The whole check. `selected` limits the streams reported (and the
    /// sibling pairs to those involving them); the sequence always covers
    /// every stream.
    let check
        (contract: IntegrationContract)
        (resolved: ResolvedStream list)
        (selected: string list option)
        (facts: IntegrationFacts)
        : IntegrationReport =
        let knownDecisions = Set.ofList facts.KnownDecisions
        let factsOf id = facts.Streams |> List.tryFind (fun stream -> stream.Stream = id)
        let isSelected id = selected |> Option.forall (List.contains id)

        let prerequisites id =
            resolved |> List.tryFind (fun stream -> stream.Contract.Id = id) |> Option.map (fun stream -> stream.Dependencies |> List.map fst) |> Option.defaultValue []

        let known = resolved |> List.map _.Contract.Id
        let sequence, cycles = Grouping.topologicalOrder known (prerequisites >> List.filter (fun id -> known |> List.contains id))

        let siblingOverlaps =
            facts.Pairs
            |> List.filter (fun pair -> isSelected pair.Left || isSelected pair.Right)
            |> List.map (fun pair ->
                let paths, managedPaths = splitOverlap contract pair.LeftPaths pair.RightPaths
                ({ Left = pair.Left; Right = pair.Right; Paths = paths; ManagedPaths = managedPaths }: StreamOverlap))

        let streams =
            resolved
            |> List.filter (fun stream -> isSelected stream.Contract.Id)
            |> List.map (fun stream ->
                let id = stream.Contract.Id

                match factsOf id |> Option.filter (fun streamFacts -> streamFacts.Tip.IsSome) with
                | None ->
                    { Stream = id
                      WorkItem = stream.Contract.WorkItem
                      Ref = stream.Contract.Ref
                      Tip = None
                      OwnershipOrigin = stream.OwnershipOrigin
                      Integrated = false
                      Findings = []
                      Covered = []
                      ManagedPaths = []
                      TargetOverlap = []
                      RequiredTests = stream.Contract.RequiredTests |> List.map (fun test -> ({ Test = test; Status = "unverified"; Detail = "the stream ref does not resolve" }: RequiredTestStatus))
                      Conditions = stream.Contract.Readiness |> List.map (fun kind -> ({ Condition = kind; Met = None; Detail = $"the ref '{stream.Contract.Ref}' does not resolve" }: ConditionResult))
                      Readiness = Readiness.Unavailable }
                | Some streamFacts ->
                    let pending = sortPaths streamFacts.PendingPaths
                    let managedPaths, judged = pending |> List.partition (managed contract)
                    let outcomes = judged |> List.map (fun path -> path, classify contract resolved knownDecisions id path)
                    let findings = outcomes |> List.choose (fun (_, outcome) -> match outcome with Error finding -> Some finding | Ok _ -> None)
                    let covered = outcomes |> List.choose (fun (path, outcome) -> match outcome with Ok basis -> Some { Path = path; Basis = basis } | Error _ -> None)
                    let targetOverlap, _ = splitOverlap contract pending streamFacts.TargetIncomingPaths
                    let mine = siblingOverlaps |> List.filter (fun overlap -> overlap.Left = id || overlap.Right = id)
                    let integrated dependency = factsOf dependency |> Option.map _.Integrated
                    let conditions = evaluate contract stream streamFacts findings targetOverlap mine integrated cycles

                    { Stream = id
                      WorkItem = stream.Contract.WorkItem
                      Ref = stream.Contract.Ref
                      Tip = streamFacts.Tip
                      OwnershipOrigin = stream.OwnershipOrigin
                      Integrated = streamFacts.Integrated
                      Findings = findings
                      Covered = covered
                      ManagedPaths = managedPaths
                      TargetOverlap = targetOverlap
                      RequiredTests = requiredTests stream.Contract streamFacts
                      Conditions = conditions
                      Readiness = readinessOf conditions })

        { Target = facts.Target
          TargetCommit = facts.TargetCommit
          ContractFindings = contractFindings contract resolved
          Streams = streams
          SiblingOverlaps = siblingOverlaps
          Sequence = sequence
          Cycles = cycles }
