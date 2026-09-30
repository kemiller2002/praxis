namespace Ros.Domain.Planning

open System
open System.Text.RegularExpressions
open Ros.Domain.Work

/// The unified planning inventory (PRX-PLAN-010..013), dependency modelling
/// (PRX-PLAN-040..044), evidence-backed state freshness (PRX-PLAN-020..022)
/// and checkpoint-aware remaining work (PRX-PLAN-030..033).
[<RequireQualifiedAccess>]
module Inventory =
    // ---- text evidence -----------------------------------------------------

    let private idToken = Regex(@"(?<![A-Za-z0-9-])[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)+(?![A-Za-z0-9-])", RegexOptions.CultureInvariant)
    let private pullRequest = Regex(@"(?:\bPR\b|\bpull request\b)\s*#(\d+)", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)
    let private release = Regex(@"\brelease\s+(v?\d+\.\d+\.\d+[A-Za-z0-9.-]*)", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)
    let private ci = Regex(@"\bCI\b|continuous integration", RegexOptions.CultureInvariant)
    let private human = Regex(@"\bhuman\b|\bmanual(?:ly)?\b|\bapproval\b|\bone-time\b|\bsign-?off\b", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)
    /// Explicit dependency phrasing only: narrative words such as "after"
    /// produced false dependencies on real backlog text.
    let private dependencyIntent =
        Regex(@"\bdepends? on\b|\bdependent on\b|\brequires\b|\bprerequisites?\b|\bblocked (?:by|on)\b|\bwaits? (?:for|on)\b", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)
    let private acknowledged = Regex(@"\b(?:complete|completed|merged|done|verified|finished|landed)\b", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)
    let private negation = Regex(@"\bnot\b|\byet\b", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)
    let private clauseBoundary = Regex(@";|\.(?:\s+|$)", RegexOptions.CultureInvariant)

    let clauses (text: string) =
        clauseBoundary.Split text |> Array.map (fun clause -> clause.Trim()) |> Array.filter (fun clause -> clause.Length > 0) |> Array.toList

    let private workItemRefs (known: Set<string>) (self: string) (text: string) =
        idToken.Matches text
        |> Seq.map (fun token -> token.Value)
        |> Seq.filter (fun token -> token <> self && known.Contains token)
        |> Seq.toList
        |> Text.distinctOrdinal

    let private pullRequestRefs (text: string) =
        pullRequest.Matches text |> Seq.map (fun token -> int token.Groups[1].Value) |> Seq.distinct |> Seq.sort |> Seq.toList

    let private releaseRefs (text: string) =
        release.Matches text |> Seq.map (fun token -> token.Groups[1].Value) |> Seq.toList |> Text.distinctOrdinal

    let private dependency target kind origin statement provenance =
        { Target = target
          Kind = kind
          Origin = origin
          Statement = statement
          Provenance = provenance }

    /// Dependencies stated in an item's free-text description: only clauses
    /// that express dependency intent, and only ids that exist in the
    /// inventory. Always `Inferred` (PRX-PLAN-043).
    let descriptionDependencies (known: Set<string>) (id: string) (description: string) =
        let provenance = Provenance.create EvidenceSource.InferredDependency $".ros/work/queue.json#{id}.description"

        clauses description
        |> List.choose (fun clause ->
            let intent = dependencyIntent.Match clause
            // Only ids named after the dependency phrase are its targets.
            if intent.Success then Some(clause, clause.Substring(intent.Index)) else None)
        |> List.collect (fun (clause, stated) ->
            workItemRefs known id stated
            |> List.map (fun target -> dependency (DependencyTarget.WorkItem target) DependencyKind.Hard DependencyOrigin.Inferred clause provenance))

    /// Prerequisites named by a recorded blocker. A clause that already says
    /// its references are complete/merged is acknowledged history, not an
    /// open prerequisite, so it cannot later count as stale evidence.
    let blockerDependencies (known: Set<string>) (id: string) (reason: string) =
        let provenance = Provenance.create EvidenceSource.InferredDependency $".ros/context/current.json#{id}.blockReason"

        clauses reason
        |> List.filter (fun clause -> not (acknowledged.IsMatch clause && not (negation.IsMatch clause)))
        |> List.collect (fun clause ->
            [ yield!
                  workItemRefs known id clause
                  |> List.map (fun target -> dependency (DependencyTarget.WorkItem target) DependencyKind.Hard DependencyOrigin.Inferred clause provenance)
              yield!
                  pullRequestRefs clause
                  |> List.map (fun number -> dependency (DependencyTarget.PullRequest number) DependencyKind.External DependencyOrigin.Inferred clause provenance)
              yield!
                  releaseRefs clause
                  |> List.map (fun tag -> dependency (DependencyTarget.Release tag) DependencyKind.External DependencyOrigin.Inferred clause provenance)
              if ci.IsMatch clause then
                  yield dependency (DependencyTarget.ContinuousIntegration id) DependencyKind.Evidence DependencyOrigin.Inferred clause provenance
              if human.IsMatch clause then
                  yield dependency (DependencyTarget.HumanAction clause) DependencyKind.Human DependencyOrigin.Inferred clause provenance ])

    /// A checkpoint whose next action waits on CI names an evidence dependency.
    let checkpointDependencies (id: string) (checkpoint: CheckpointSummary) =
        if ci.IsMatch checkpoint.NextAction then
            [ dependency
                  (DependencyTarget.ContinuousIntegration id)
                  DependencyKind.Evidence
                  DependencyOrigin.Inferred
                  checkpoint.NextAction
                  (Provenance.create EvidenceSource.Checkpoint $"checkpoint {checkpoint.CheckpointId}.nextAction") ]
        else
            []

    let structuredDependencies (configuration: PlannerConfiguration) (queueItem: PlanningQueueItem option) (id: string) =
        let fromQueue =
            queueItem
            |> Option.map (fun item -> item.DependsOn)
            |> Option.defaultValue []
            |> List.map (fun target ->
                dependency
                    (DependencyTarget.WorkItem target)
                    DependencyKind.Hard
                    DependencyOrigin.Structured
                    $"dependsOn {target}"
                    (Provenance.create EvidenceSource.StructuredDependency $".ros/work/queue.json#{id}.dependsOn"))

        let fromConfiguration =
            configuration.Dependencies
            |> List.filter (fun declared -> declared.From = id)
            |> List.map (fun declared ->
                dependency
                    (DependencyTarget.WorkItem declared.To)
                    declared.Kind
                    DependencyOrigin.Structured
                    $"declared {DependencyKind.code declared.Kind} dependency on {declared.To}"
                    (Provenance.create EvidenceSource.PlannerConfiguration $"dependencies[{declared.From}->{declared.To}]"))

        fromQueue @ fromConfiguration

    /// Structured evidence wins over inferred evidence for the same target.
    let private deduplicate (dependencies: Dependency list) =
        dependencies
        |> List.sortBy (fun item -> (if item.Origin = DependencyOrigin.Structured then 0 else 1), DependencyTarget.kindCode item.Target, DependencyTarget.value item.Target)
        |> List.distinctBy (fun item -> item.Target, item.Kind)

    // ---- resolution against effective state and observations ---------------

    let private observed (observations: Observation list) (predicate: ObservationKind -> bool) =
        observations |> List.tryFind (fun observation -> predicate observation.Kind)

    let resolve (lifecycle: Map<string, string>) (checkpoints: Map<string, CheckpointSummary>) (observations: Observation list) (item: Dependency) : ResolvedDependency =
        let result status reason = { Dependency = item; Status = status; Reason = reason }

        match item.Target with
        | DependencyTarget.WorkItem target ->
            match lifecycle.TryFind target with
            | Some "complete" -> result DependencyStatus.Satisfied $"{target} is complete (effective state)"
            | Some "abandoned" -> result DependencyStatus.Undetermined $"{target} was abandoned; the dependency needs review"
            | Some state -> result DependencyStatus.Unsatisfied $"{target} is {state}"
            | None -> result DependencyStatus.Undetermined $"{target} is not in the planning inventory"
        | DependencyTarget.PullRequest number ->
            match observed observations (function ObservationKind.PullRequestMerged merged -> merged = number | _ -> false) with
            | Some observation -> result DependencyStatus.Satisfied $"PR #{number} merged ({observation.Provenance.Reference})"
            | None -> result DependencyStatus.Undetermined $"no evidence that PR #{number} merged"
        | DependencyTarget.Release tag ->
            match observed observations (function ObservationKind.ReleaseExists existing -> existing = tag | _ -> false) with
            | Some observation -> result DependencyStatus.Satisfied $"release {tag} exists ({observation.Provenance.Reference})"
            | None -> result DependencyStatus.Undetermined $"no evidence that release {tag} exists"
        | DependencyTarget.ContinuousIntegration subject ->
            let commit = checkpoints.TryFind subject |> Option.map (fun checkpoint -> checkpoint.Commit)
            let about value = value = subject || Some value = commit

            match observed observations (function ObservationKind.ContinuousIntegrationFailed value -> about value | _ -> false),
                  observed observations (function ObservationKind.ContinuousIntegrationPassed value -> about value | _ -> false) with
            | Some observation, _ -> result DependencyStatus.Unsatisfied $"CI failed ({observation.Provenance.Reference})"
            | None, Some observation -> result DependencyStatus.Satisfied $"CI passed ({observation.Provenance.Reference})"
            | None, None -> result DependencyStatus.Undetermined "no CI evidence observed"
        | DependencyTarget.HumanAction _ -> result DependencyStatus.Undetermined "human action cannot be observed by the planner"

    // ---- classification -----------------------------------------------------

    let private tagClasses =
        [ "documentation", "documentation"
          "docs", "documentation"
          "testing", "testing-verification"
          "tests", "testing-verification"
          "validation", "testing-verification"
          "research", "research"
          "architecture", "architecture-design"
          "design", "architecture-design"
          "ci", "infrastructure-devops"
          "infrastructure", "infrastructure-devops"
          "release", "infrastructure-devops"
          "distribution", "infrastructure-devops"
          "maintenance", "maintenance" ]
        |> Map.ofList

    let private deferredTags = set [ "follow-up"; "later"; "deferred"; "someday"; "icebox" ]

    let private implementWords = Regex(@"\b(?:implement|add|write|build|fix|refactor|port|migrate|design|create|rewrite)\b", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)
    let private verifyWords = Regex(@"\b(?:CI|tests?|verify|validate|confirm|review|check)\b", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)
    let private finalizeWords = Regex(@"\b(?:complete|completion|merge|close|record|publish|push|release)\b", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)

    /// The remaining-work basis a checkpoint's next action implies. Inferred
    /// from text, so continuation estimates are capped at medium confidence.
    let basisFor (checkpoint: CheckpointSummary option) (lifecycle: string) =
        match checkpoint with
        | Some checkpoint when checkpoint.Verified ->
            if implementWords.IsMatch checkpoint.NextAction then RemainingBasis.ImplementationInProgress
            elif verifyWords.IsMatch checkpoint.NextAction then RemainingBasis.VerificationRemaining
            elif finalizeWords.IsMatch checkpoint.NextAction then RemainingBasis.FinalizationOnly
            else RemainingBasis.UnclassifiedContinuation
        | _ when lifecycle = "active" -> RemainingBasis.UnclassifiedContinuation
        | _ -> RemainingBasis.FromScratch

    let private fractionFor (fractions: RemainingFractions) basis =
        match basis with
        | RemainingBasis.FromScratch -> 1.0m, 1.0m
        | RemainingBasis.FinalizationOnly -> fractions.FinalizationOnly
        | RemainingBasis.VerificationRemaining -> fractions.VerificationRemaining
        | RemainingBasis.ImplementationInProgress -> fractions.ImplementationInProgress
        | RemainingBasis.UnclassifiedContinuation -> fractions.Unclassified

    let private scaleMs (fraction: decimal) (value: int64) = History.roundDuration (int64 (Math.Round(decimal value * fraction)))

    /// PRX-PLAN-030: remaining effort from current state, never from zero for
    /// work with verified progress.
    let remaining (fractions: RemainingFractions) (basis: RemainingBasis) (full: Estimate<int64>) : Estimate<int64> =
        let lower, upper = fractionFor fractions basis
        let middle = (lower + upper) / 2m

        let confidence =
            match basis with
            | RemainingBasis.FromScratch -> full.Confidence
            | RemainingBasis.UnclassifiedContinuation -> EvidenceConfidence.cap EvidenceConfidence.Low full.Confidence
            | _ -> EvidenceConfidence.cap EvidenceConfidence.Medium full.Confidence

        { Lower = full.Lower |> Option.map (scaleMs lower)
          Expected = full.Expected |> Option.map (scaleMs middle)
          Upper = full.Upper |> Option.map (scaleMs upper)
          Confidence = confidence }

    let remainingCost (fractions: RemainingFractions) (basis: RemainingBasis) (full: Estimate<Money>) : Estimate<Money> =
        let lower, upper = fractionFor fractions basis
        let scale fraction (money: Money) = { money with Amount = Math.Round(money.Amount * fraction, 4) }

        { Lower = full.Lower |> Option.map (scale lower)
          Expected = full.Expected |> Option.map (scale ((lower + upper) / 2m))
          Upper = full.Upper |> Option.map (scale upper)
          Confidence = if basis = RemainingBasis.FromScratch then full.Confidence else EvidenceConfidence.cap EvidenceConfidence.Low full.Confidence }

    /// PRAXIS-PLAN-05: an item's own finalized executions with a recorded
    /// monetary total report that evidence's kind (observed platform cost
    /// stays `observed`); otherwise sufficient history gives an estimate.
    let costEvidenceFor (history: HistorySummary) (executions: HistoricalExecution list) (id: string) =
        let own =
            executions
            |> List.filter (fun execution -> execution.WorkItemId = id && execution.Status = ExecutionStatus.Finalized)
            |> List.map History.executionCostKind
            |> List.filter ((<>) CostEvidenceKind.Unavailable)

        match own |> List.sortByDescending History.costKindRank |> List.tryHead, history.Cost.Sufficient with
        | Some kind, _ -> kind
        | None, true -> CostEvidenceKind.Estimated
        | None, false -> CostEvidenceKind.Unavailable

    let taskClassFor (distributions: DurationDistribution list) (executions: HistoricalExecution list) (id: string) (tags: string list) =
        let hasHistory name = distributions |> List.exists (fun distribution -> distribution.TaskClass = name && distribution.Confidence <> EvidenceConfidence.Unknown)

        let own =
            executions
            |> List.filter (fun execution -> execution.WorkItemId = id)
            |> List.collect (fun execution -> execution.Classes)
            |> List.countBy Operators.id
            |> List.sortWith (fun (left, leftCount) (right, rightCount) ->
                match compare rightCount leftCount with
                | 0 -> Text.ordinal left right
                | order -> order)
            |> List.tryHead
            |> Option.map fst

        let fromTags = tags |> Text.sortOrdinal |> List.tryPick (fun tag -> tagClasses.TryFind(tag.ToLowerInvariant())) |> Option.filter hasHistory

        match own, fromTags with
        | Some taskClass, _ -> taskClass, "telemetry"
        | None, Some taskClass -> taskClass, "tags"
        | None, None -> History.pooledClass, "pooled"

    let triageNeeds (queueItem: PlanningQueueItem option) (dependencies: Dependency list) =
        let description = queueItem |> Option.bind (fun item -> item.Description) |> Option.filter (String.IsNullOrWhiteSpace >> not)
        let tags = queueItem |> Option.map (fun item -> item.Tags) |> Option.defaultValue [] |> List.map (fun tag -> tag.ToLowerInvariant())

        [ yield TriageNeed.NeedsTriage
          if description.IsNone then yield TriageNeed.LacksAcceptanceCriteria
          if dependencies.IsEmpty then yield TriageNeed.LacksDependencyInformation
          if tags |> List.exists deferredTags.Contains then yield TriageNeed.DeferredForLater ]

    // ---- freshness -------------------------------------------------------------

    let private finding code severity items message action confidence evidence =
        { Code = code
          Severity = severity
          WorkItems = items
          Message = message
          RecommendedAction = action
          Confidence = confidence
          Evidence = evidence }

    /// Evidence that recorded state is behind reality. Never rewrites state.
    let staleness (id: string) (lifecycle: string) (checkpoint: CheckpointSummary option) (observations: Observation list) (dependencies: ResolvedDependency list) : PlanningFinding list =
        let merged =
            match lifecycle, checkpoint with
            | ("active" | "ready"), Some checkpoint ->
                observations
                |> List.tryFind (fun observation ->
                    match observation.Kind with
                    | ObservationKind.CommitMerged(commit, _) -> commit = checkpoint.Commit
                    | _ -> false)
                |> Option.map (fun observation ->
                    let into = match observation.Kind with | ObservationKind.CommitMerged(_, into) -> into | _ -> "the default branch"

                    finding
                        FindingCode.StaleStateCandidate
                        FindingSeverity.Warning
                        [ id ]
                        $"Recorded state: {lifecycle}. Observed: checkpoint commit {checkpoint.Commit} is already merged into {into}. Assessment: the item may be finished; recorded state may be stale."
                        (Some "reconcile: verify the merged result and record completion (./praxis work complete); do not reimplement")
                        EvidenceConfidence.High
                        [ Provenance.create EvidenceSource.Checkpoint $"checkpoint {checkpoint.CheckpointId}"; observation.Provenance ])
            | _ -> None

        let ciPassed =
            match lifecycle, checkpoint with
            | "active", Some checkpoint ->
                dependencies
                |> List.tryFind (fun resolved ->
                    resolved.Status = DependencyStatus.Satisfied
                    && resolved.Dependency.Provenance.Source = EvidenceSource.Checkpoint)
                |> Option.map (fun resolved ->
                    finding
                        FindingCode.StaleStateCandidate
                        FindingSeverity.Warning
                        [ id ]
                        $"Recorded state: active. The checkpoint's next action waits for CI (\"{checkpoint.NextAction}\"); observed: {resolved.Reason}. Assessment: the remaining action may already be possible."
                        (Some "review the CI result and complete or resume")
                        EvidenceConfidence.Medium
                        [ resolved.Dependency.Provenance ])
            | _ -> None

        let blocker =
            if lifecycle <> "blocked" then
                None
            else
                let fromBlocker =
                    dependencies |> List.filter (fun resolved -> resolved.Dependency.Provenance.Reference.EndsWith(".blockReason", StringComparison.Ordinal))

                let satisfied = fromBlocker |> List.filter (fun resolved -> resolved.Status = DependencyStatus.Satisfied)

                match satisfied with
                | [] -> None
                | _ ->
                    let complete = satisfied.Length = fromBlocker.Length
                    let named = satisfied |> List.map (fun resolved -> resolved.Reason) |> String.concat "; "
                    let open' = fromBlocker |> List.filter (fun resolved -> resolved.Status <> DependencyStatus.Satisfied) |> List.map (fun resolved -> DependencyTarget.describe resolved.Dependency.Target)

                    let assessment =
                        if complete then "every prerequisite the blocker names is now satisfied; the blocker may be stale"
                        else
                            let stillOpen = String.concat ", " open'
                            $"the blocker is partially resolved; still open or unobservable: {stillOpen}"

                    Some(
                        finding
                            FindingCode.StaleBlocker
                            FindingSeverity.Warning
                            [ id ]
                            $"Recorded state: blocked. Observed: {named}. Assessment: {assessment}."
                            (Some "review the blocker; resume, re-block with a current reason, or complete")
                            (if complete then EvidenceConfidence.Medium else EvidenceConfidence.Low)
                            (satisfied |> List.map (fun resolved -> resolved.Dependency.Provenance) |> List.distinct)
                    )

        [ merged; ciPassed; blocker ] |> List.choose Operators.id

    let private classify (lifecycle: string) (stale: bool) (checkpoint: CheckpointSummary option) (dependencies: ResolvedDependency list) =
        let openBlockers =
            dependencies
            |> List.filter (fun resolved ->
                resolved.Status <> DependencyStatus.Satisfied
                && resolved.Dependency.Provenance.Reference.EndsWith(".blockReason", StringComparison.Ordinal))

        match lifecycle with
        | "complete" -> PlanningWorkState.Complete
        | "abandoned" -> PlanningWorkState.Abandoned
        | _ when stale -> PlanningWorkState.StaleStateCandidate
        | "blocked" when openBlockers |> List.exists (fun resolved -> resolved.Dependency.Kind = DependencyKind.Human) -> PlanningWorkState.AwaitingHuman
        | "blocked" when
            openBlockers
            |> List.exists (fun resolved -> resolved.Dependency.Kind = DependencyKind.External || resolved.Dependency.Kind = DependencyKind.Evidence)
            ->
            PlanningWorkState.AwaitingEvidence
        | "blocked" -> PlanningWorkState.Blocked
        | ("active" | "ready") when checkpoint |> Option.exists (fun checkpoint -> checkpoint.Verified) -> PlanningWorkState.PartiallyComplete
        | "active" -> PlanningWorkState.Active
        | "ready" -> PlanningWorkState.Ready
        // Captured, or any status this planner does not recognise: never runnable.
        | _ -> PlanningWorkState.Captured

    // ---- the unified inventory ---------------------------------------------

    let private earliest (values: string option list) =
        values |> List.choose id |> List.choose (fun value -> Text.tryTimestamp value |> Option.map (fun parsed -> parsed, value)) |> List.sortBy fst |> List.tryHead |> Option.map snd

    /// Every id in the backlog queue or the live context (PRX-PLAN-010).
    let build (input: PlanningInput) (history: HistorySummary) : ItemAnalysis list * PlanningFinding list =
        let queueById = input.Queue |> List.map (fun item -> item.Id, item) |> Map.ofList
        let liveById = input.Live |> List.map (fun item -> item.Id, item) |> Map.ofList

        let ids =
            (input.Queue |> List.map (fun item -> item.Id)) @ (input.Live |> List.map (fun item -> item.Id)) |> Text.distinctOrdinal

        let known = Set.ofList ids

        let lifecycle =
            ids
            |> List.map (fun id ->
                id,
                QueuePresentation.effectiveStatus
                    (queueById.TryFind id |> Option.map (fun item -> item.Status))
                    (liveById.TryFind id |> Option.map (fun item -> item.State))
                |> Option.defaultValue "")
            |> Map.ofList

        let checkpoints =
            input.Live |> List.choose (fun item -> item.Checkpoint |> Option.map (fun checkpoint -> item.Id, checkpoint)) |> Map.ofList

        let costWhole = History.costEstimate history.Cost input.Executions

        let analyze (id: string) =
            let queueItem = queueById.TryFind id
            let liveItem = liveById.TryFind id
            let state = lifecycle[id]
            let checkpoint = liveItem |> Option.bind (fun item -> item.Checkpoint)

            let blockReason =
                match liveItem with
                | Some item when item.State = LiveWorkState.Blocked -> item.BlockReason
                | _ -> None

            let dependencies =
                [ yield! structuredDependencies input.Configuration queueItem id
                  yield! queueItem |> Option.bind (fun item -> item.Description) |> Option.map (descriptionDependencies known id) |> Option.defaultValue []
                  yield! blockReason |> Option.map (blockerDependencies known id) |> Option.defaultValue []
                  if state = "active" then yield! checkpoint |> Option.map (checkpointDependencies id) |> Option.defaultValue [] ]
                |> deduplicate
                |> List.map (resolve lifecycle checkpoints input.Observations)

            let findings =
                if PlanningWorkState.isTerminal (classify state false None []) then []
                else staleness id state checkpoint input.Observations dependencies

            let planningState = classify state (findings |> List.exists (fun finding -> finding.Code = FindingCode.StaleStateCandidate || finding.Code = FindingCode.StaleBlocker)) checkpoint dependencies
            let tags = queueItem |> Option.map (fun item -> item.Tags) |> Option.defaultValue []
            let taskClass, classOrigin = taskClassFor history.Distributions input.Executions id tags
            let full, _ = History.estimateFor history.Distributions taskClass
            let basis = basisFor checkpoint state

            let firstExecution =
                input.Executions |> List.filter (fun execution -> execution.WorkItemId = id) |> List.map (fun execution -> Some execution.StartedAt) |> earliest

            let item =
                { Id = id
                  Title = queueItem |> Option.map (fun item -> item.Title) |> Option.defaultValue id
                  LifecycleState = state
                  InQueue = queueItem.IsSome
                  InContext = liveItem.IsSome
                  PlanningState = planningState
                  Tags = tags
                  CreatedAt = earliest [ queueItem |> Option.bind (fun item -> item.CreatedAt); firstExecution; liveItem |> Option.bind (fun item -> item.UpdatedAt) ]
                  TaskClass = taskClass
                  TaskClassOrigin = classOrigin
                  Triage = if planningState = PlanningWorkState.Captured then triageNeeds queueItem (dependencies |> List.map (fun resolved -> resolved.Dependency)) else []
                  Dependencies = dependencies
                  BlockReason = blockReason
                  Checkpoint = checkpoint
                  Basis = basis
                  FullDuration = full
                  RemainingDuration = if PlanningWorkState.isTerminal planningState then Estimate.unknown else remaining input.Configuration.RemainingFractions basis full
                  RemainingCost = if PlanningWorkState.isTerminal planningState then Estimate.unknown else remainingCost input.Configuration.RemainingFractions basis costWhole
                  CostEvidence = costEvidenceFor history input.Executions id
                  Provenance =
                    [ if queueItem.IsSome then yield Provenance.create EvidenceSource.BacklogQueue $".ros/work/queue.json#{id}"
                      if liveItem.IsSome then yield Provenance.create EvidenceSource.LiveContext $".ros/context/current.json#{id}"
                      match checkpoint with
                      | Some checkpoint -> yield Provenance.create EvidenceSource.Checkpoint $"checkpoint {checkpoint.CheckpointId} @ {checkpoint.Commit}"
                      | None -> ()
                      if classOrigin = "telemetry" then yield Provenance.create EvidenceSource.Telemetry $".ros/telemetry/executions (work item {id})" ] }

            let dependencyFindings =
                if PlanningWorkState.isTerminal planningState then
                    []
                else
                    dependencies
                    |> List.choose (fun resolved ->
                        match resolved.Dependency.Target, resolved.Status with
                        | DependencyTarget.WorkItem target, DependencyStatus.Satisfied when resolved.Dependency.Kind = DependencyKind.Hard ->
                            Some(
                                finding
                                    FindingCode.DependencySatisfied
                                    FindingSeverity.Info
                                    [ id; target ]
                                    $"{id}'s {DependencyOrigin.code resolved.Dependency.Origin} dependency on {target} is satisfied: {resolved.Reason}. It no longer blocks {id}."
                                    None
                                    (if resolved.Dependency.Origin = DependencyOrigin.Structured then EvidenceConfidence.High else EvidenceConfidence.Medium)
                                    [ resolved.Dependency.Provenance ]
                            )
                        | DependencyTarget.WorkItem target, DependencyStatus.Undetermined ->
                            Some(
                                finding
                                    FindingCode.UnresolvedDependency
                                    FindingSeverity.Advisory
                                    [ id; target ]
                                    $"{id} depends on {target}, which cannot be resolved: {resolved.Reason}."
                                    (Some "correct or remove the dependency")
                                    EvidenceConfidence.Low
                                    [ resolved.Dependency.Provenance ]
                            )
                        | _ -> None)

            let resumable =
                match planningState, checkpoint with
                | PlanningWorkState.PartiallyComplete, Some checkpoint when state = "active" ->
                    [ finding
                          FindingCode.ResumableExecution
                          FindingSeverity.Info
                          [ id ]
                          $"{id} has a verified checkpoint on {checkpoint.Branch} ({checkpoint.Commit}); if its executor is gone, continue it (./praxis work continue) rather than restarting. Next action: {checkpoint.NextAction}"
                          (Some "continue from the checkpoint")
                          EvidenceConfidence.High
                          [ Provenance.create EvidenceSource.Checkpoint $"checkpoint {checkpoint.CheckpointId}" ] ]
                | _ -> []

            item, findings @ dependencyFindings @ resumable

        let analyzed = ids |> List.map analyze
        analyzed |> List.map fst, analyzed |> List.collect snd
