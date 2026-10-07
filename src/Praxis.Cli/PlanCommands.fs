namespace Praxis.Cli

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions
open Praxis.Application.Planning
open Praxis.Contracts.Planning
open Praxis.Domain.Planning
open Praxis.Infrastructure.Planning

/// `praxis plan ...`: the advisory, read-only planner
/// (requirements/PLANNING-OPTIMIZATION.md). This module parses, delegates to
/// the Application/Domain planner over a read-only port, and renders. No
/// plan command writes anything except `plan replay --record`, which appends
/// only to the calibration history (PRX-PLAN-170).
[<RequireQualifiedAccess>]
module PlanCommands =
    let usage =
        "plan analyze|simulate [--groups]|compare [--groups]|explain ID|groups|explain-group GROUP-ID|replay [--record]|freshness --plan FILE [--for {baseline|speed|balanced|cost|max-parallel}] [--max-concurrency N] [--budget AMOUNT [--currency CODE]] [--deadline DURATION] [--observations FILE] [--observe-ci] [--config FILE] [--as-of TIMESTAMP] [--details] [--json]"

    type private Options =
        { Json: bool
          Details: bool
          Groups: bool
          Objective: string option
          MaxConcurrency: string option
          Budget: string option
          Currency: string option
          Deadline: string option
          Observations: string option
          Configuration: string option
          AsOf: string option
          Plan: string option
          ObserveCi: bool
          Record: bool
          Positional: string list
          Unexpected: string list }

    let private empty =
        { Json = false
          Details = false
          Groups = false
          Objective = None
          MaxConcurrency = None
          Budget = None
          Currency = None
          Deadline = None
          Observations = None
          Configuration = None
          AsOf = None
          Plan = None
          ObserveCi = false
          Record = false
          Positional = []
          Unexpected = [] }

    let rec private parse (options: Options) (arguments: string list) =
        match arguments with
        | [] -> options
        | "--json" :: rest -> parse { options with Json = true } rest
        | "--details" :: rest -> parse { options with Details = true } rest
        | "--groups" :: rest -> parse { options with Groups = true } rest
        | "--observe-ci" :: rest -> parse { options with ObserveCi = true } rest
        | "--record" :: rest -> parse { options with Record = true } rest
        | flag :: value :: rest when flag.StartsWith "--" && not (value.StartsWith "--") ->
            let next =
                match flag with
                | "--for" -> Some { options with Objective = Some value }
                | "--max-concurrency" -> Some { options with MaxConcurrency = Some value }
                | "--budget" -> Some { options with Budget = Some value }
                | "--currency" -> Some { options with Currency = Some value }
                | "--deadline" -> Some { options with Deadline = Some value }
                | "--observations" -> Some { options with Observations = Some value }
                | "--config" -> Some { options with Configuration = Some value }
                | "--as-of" -> Some { options with AsOf = Some value }
                | "--plan" -> Some { options with Plan = Some value }
                | _ -> None

            match next with
            | Some next -> parse next rest
            | None -> parse { options with Unexpected = options.Unexpected @ [ flag ] } (value :: rest)
        | token :: rest when token.StartsWith "--" -> parse { options with Unexpected = options.Unexpected @ [ token ] } rest
        | token :: rest -> parse { options with Positional = options.Positional @ [ token ] } rest

    let private deadlinePattern = Regex(@"^(?:(\d+)h)?(?:(\d+)m)?$", RegexOptions.CultureInvariant)

    /// `4h`, `90m`, `1h30m` as milliseconds.
    let parseDeadline (value: string) : int64 option =
        let found = deadlinePattern.Match value

        if not found.Success || value.Length = 0 then
            None
        else
            let part (index: int) = if found.Groups[index].Success then int64 found.Groups[index].Value else 0L
            let total = (part 1 * 60L + part 2) * 60_000L
            if total > 0L then Some total else None

    let private objective (options: Options) : Result<OptimizationObjective, string> =
        match options.Budget, options.Deadline with
        | Some _, Some _ -> Error "--budget and --deadline cannot be combined"
        | Some budget, None ->
            match Decimal.TryParse(budget, NumberStyles.Number, CultureInfo.InvariantCulture) with
            | true, amount when amount > 0m ->
                Ok(OptimizationObjective.BudgetConstrained { Amount = amount; Currency = options.Currency |> Option.defaultValue "USD" })
            | _ -> Error $"--budget '{budget}' is not a positive amount"
        | None, Some deadline ->
            match parseDeadline deadline with
            | Some ms -> Ok(OptimizationObjective.DeadlineConstrained ms)
            | None -> Error $"--deadline '{deadline}' is not a duration such as 4h, 90m or 1h30m"
        | None, None ->
            match options.Objective |> Option.defaultValue "balanced" with
            | "baseline" | "fifo" -> Ok OptimizationObjective.Baseline
            | "speed" | "duration" -> Ok OptimizationObjective.MinimumDuration
            | "balanced" -> Ok OptimizationObjective.Balanced
            | "cost" -> Ok OptimizationObjective.MinimumCost
            | "max-parallel" | "parallel" -> Ok OptimizationObjective.MaximumSafeParallelism
            | other -> Error $"--for '{other}' is not one of baseline, speed, balanced, cost, max-parallel"

    let private maxConcurrency (options: Options) : Result<int option, string> =
        match options.MaxConcurrency with
        | None -> Ok None
        | Some value ->
            match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, count when count >= 1 -> Ok(Some count)
            | _ -> Error $"--max-concurrency '{value}' must be a positive integer"

    let private plannedAt (options: Options) : Result<string, string> =
        match options.AsOf with
        | None -> Ok(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
        | Some value ->
            match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, _ -> Ok value
            | _ -> Error $"--as-of '{value}' is not a timestamp"

    let private fail (code: int) (message: string) =
        eprintfn "ERROR %s" message
        code

    let private resolve (root: string) (path: string) = if Path.IsPathRooted path then path else Path.GetFullPath(Path.Combine(root, path))

    /// Parses, then gathers input through the read-only port.
    let private withAnalysis (root: string) (version: string) (options: Options) (run: PlanningInput -> PlanningAnalysis -> int) =
        match options.Unexpected @ options.Positional, plannedAt options with
        | unexpected, _ when not unexpected.IsEmpty ->
            let names = String.concat " " unexpected
            fail 2 $"unexpected argument(s): {names}"
        | _, Error message -> fail 2 message
        | _, Ok timestamp ->
            let port =
                FilePlanningRepository.createObserving root (options.Observations |> Option.map (resolve root)) (options.Configuration |> Option.map (resolve root)) options.ObserveCi

            match PlanningOperations.analyze port timestamp version with
            | Error message -> fail 1 message
            | Ok(input, analysis) -> run input analysis

    // ---- text rendering ---------------------------------------------------------

    let private duration = PlanningJson.describeDuration

    let private money (estimate: Estimate<Money>) =
        match estimate.Lower, estimate.Upper with
        | Some lower, Some upper -> $"{lower.Amount}-{upper.Amount} {upper.Currency}"
        | _ -> "unknown"

    let private snapshotLines (snapshot: PlanSnapshot) =
        let commit = snapshot.Commit |> Option.map (fun value -> value.Substring(0, min 12 value.Length)) |> Option.defaultValue "unknown"
        let branch = snapshot.Branch |> Option.defaultValue "unknown"

        [ $"Repository {snapshot.Repository} at {commit} ({branch}); planned {snapshot.PlannedAt} by planner {snapshot.PlannerVersion}"
          $"Work-state fingerprint {snapshot.WorkStateFingerprint.Substring(0, 16)}"
          Planner.advisoryStatement ]

    let private reasonLine (indent: string) (reason: PlanningReason) = $"{indent}- [{ReasonCode.code reason.Code}] {reason.Message}"

    let private findingLines (findings: PlanningFinding list) =
        findings
        |> List.map (fun finding ->
            let action = finding.RecommendedAction |> Option.map (fun action -> $" -> {action}") |> Option.defaultValue ""
            $"  {FindingSeverity.code finding.Severity} {FindingCode.code finding.Code}: {finding.Message}{action}")

    let private analysisText (analysis: PlanningAnalysis) =
        let count state = analysis.Items |> List.filter (fun item -> item.PlanningState = state) |> List.length

        let states =
            PlanningWorkState.all
            |> List.filter (fun state -> not (PlanningWorkState.isTerminal state))
            |> List.map (fun state -> $"{PlanningWorkState.code state} {count state}")
            |> String.concat ", "

        let open' = analysis.Items |> List.filter (fun item -> not (PlanningWorkState.isTerminal item.PlanningState))

        [ yield! snapshotLines analysis.Snapshot
          ""
          $"Planning confidence: {EvidenceConfidence.code analysis.Confidence} ({analysis.ConfidenceStatement})"
          $"Nonterminal work: {open'.Length} ({states}); terminal: {analysis.Items.Length - open'.Length}"
          ""
          "Work items:"
          yield!
              open'
              |> List.map (fun item ->
                  let where = [ (if item.InQueue then "queue" else ""); (if item.InContext then "context" else "") ] |> List.filter ((<>) "") |> String.concat "+"
                  $"  {item.Id} [{PlanningWorkState.code item.PlanningState}] recorded {item.LifecycleState} ({where}); remaining {duration item.RemainingDuration} ({EvidenceConfidence.code item.RemainingDuration.Confidence}, {RemainingBasis.code item.Basis})")
          ""
          "Findings:"
          yield! findingLines analysis.Findings
          ""
          $"Critical path: {analysis.CriticalPath.Explanation}"
          yield! analysis.Unlocks |> List.map (fun unlock -> $"  unlock {unlock.WorkItem}: {unlock.Explanation}")
          ""
          $"History: {analysis.History.DurationSamples} finalized executions with duration evidence; {analysis.History.ActiveExecutionsExcluded} active execution(s) excluded"
          yield!
              analysis.History.Distributions
              |> List.filter (fun distribution -> distribution.TaskClass = "all")
              |> List.map (fun distribution -> $"  typical execution {distribution.Median / 60_000L} min (IQR {distribution.Lower / 60_000L}-{distribution.Upper / 60_000L} min, {EvidenceConfidence.code distribution.Confidence})")
          $"  {analysis.History.Cost.Statement}"
          yield! analysis.History.Drift |> Option.map (fun drift -> $"  {drift.Statement}") |> Option.toList
          ""
          "Evidence that would most improve this plan:"
          yield! analysis.EvidenceRecommendations |> List.map (fun recommendation -> $"  - {recommendation}") ]

    let private excludedLines (title: string) (items: ExcludedItem list) =
        match items with
        | [] -> []
        | _ ->
            [ yield ""
              yield $"{title}:"
              for item in items do
                  yield $"  {item.WorkItem} [{PlanningWorkState.code item.State}]"
                  yield! item.Reasons |> List.map (reasonLine "    ") ]

    let private planText (details: bool) (document: PlanDocument) =
        let plan = document.Plan
        let availability = StrategyAvailability.reason plan.Availability |> Option.map (fun reason -> $" ({reason})") |> Option.defaultValue ""

        [ yield! snapshotLines document.Snapshot
          yield ""
          yield $"Strategy {OptimizationObjective.code plan.Objective}: {StrategyAvailability.code plan.Availability}{availability}"
          yield $"Risk policy: {plan.RiskPolicy}"
          yield! plan.Weights |> Option.map (fun weights -> $"Weights: duration {weights.Duration}, cost {weights.Cost}, conflict risk {weights.ConflictRisk}, uncertainty {weights.Uncertainty}, context reuse {weights.ContextReuse}, completion likelihood {weights.CompletionLikelihood}") |> Option.toList
          yield $"Expected duration: {duration plan.ExpectedDuration}; expected cost: {money plan.ExpectedCost}; confidence {EvidenceConfidence.code plan.Confidence}"
          yield $"Known contributors: {plan.Proxy.Executions} executions ({plan.Proxy.Continuations} continuations), {plan.Proxy.ContextAcquisitions} context acquisitions, peak concurrency {plan.Proxy.PeakConcurrency}, {plan.Proxy.RiskAcceptedPairs} risk-accepted parallel pair(s)"
          yield! plan.Constraint |> Option.map (fun assessment -> $"Constraint {assessment.Constraint}: {ConstraintVerdict.code assessment.Verdict} - {assessment.Reason}") |> Option.toList
          for wave in plan.Waves do
              yield ""
              let kind = if wave.Kind = WaveKind.Reconciliation then " (reconcile stale state)" else ""
              yield $"Wave {wave.Number}{kind}: {duration wave.ExpectedDuration}"

              for entry in wave.Entries do
                  yield $"  {RecommendedAction.code entry.Action} {entry.WorkItem} ({duration entry.Remaining})"
                  let reasons = if details then entry.Reasons else entry.Reasons |> List.truncate 3
                  yield! reasons |> List.map (reasonLine "    ")
          yield! excludedLines "State cleanup (stale-state candidates)" plan.StaleStateCandidates
          yield! excludedLines "Blocked (cannot currently execute)" plan.Blocked
          yield! excludedLines "Needs triage (not runnable)" plan.NeedsTriage
          match plan.ContextRecommendations with
          | [] -> ()
          | recommendations ->
              yield ""
              yield "Context reuse:"
              yield! recommendations |> List.map (fun recommendation -> $"  - {recommendation.Reason}") ]

    let private comparisonText (snapshot: PlanSnapshot) (comparison: StrategyComparison) =
        [ yield! snapshotLines snapshot
          yield ""
          yield "Strategy       availability    expected duration                     peak  risk  ctx  frontier"
          for summary in comparison.Strategies do
              let frontier = if summary.OnFrontier then "yes" else ""
              yield $"{summary.Objective,-14} {StrategyAvailability.code summary.Availability,-15} {duration summary.ExpectedDuration,-37} {summary.PeakConcurrency,4}  {summary.RiskAcceptedPairs,4}  {summary.ContextAcquisitions,3}  {frontier}"
              match StrategyAvailability.reason summary.Availability with
              | Some reason -> yield $"               {reason}"
              | None -> ()
          yield ""
          yield "Concurrency (speed strategy):"
          for point in comparison.ConcurrencyCurve do
              let saving = point.MarginalSavingMs |> Option.map (fun ms -> $", saves {ms / 60_000L} min") |> Option.defaultValue ""
              let flag = if point.DiminishingReturns then " (diminishing returns)" else ""
              yield $"  {point.MaxConcurrency} executor(s): {duration point.ExpectedDuration}{saving}{flag}"
          yield! comparison.DiminishingReturns |> Option.toList
          yield ""
          yield comparison.Statement ]

    let private explanationText (snapshot: PlanSnapshot) (explanation: ItemExplanation) =
        let item = explanation.Item

        [ yield! snapshotLines snapshot
          yield ""
          yield $"{item.Id}: {item.Title}"
          yield $"Recorded state {item.LifecycleState}; planning state {PlanningWorkState.code item.PlanningState}; in queue {item.InQueue}, in live context {item.InContext}"
          yield! item.Checkpoint |> Option.map (fun checkpoint -> $"Checkpoint {checkpoint.CheckpointId} on {checkpoint.Branch} @ {checkpoint.Commit}: {checkpoint.Summary}; next: {checkpoint.NextAction}") |> Option.toList
          yield! item.BlockReason |> Option.map (fun reason -> $"Recorded blocker: {reason}") |> Option.toList
          yield $"Remaining: {duration item.RemainingDuration} (full effort {duration item.FullDuration})"
          yield ""
          yield "Estimate evidence:"
          yield! explanation.EstimateBasis |> List.map (fun line -> $"  - {line}")
          yield ""
          yield "Dependencies:"
          yield!
              match item.Dependencies with
              | [] -> [ "  none recorded or inferred" ]
              | dependencies ->
                  dependencies
                  |> List.map (fun resolved ->
                      $"  - {DependencyKind.code resolved.Dependency.Kind} {DependencyOrigin.code resolved.Dependency.Origin} on {DependencyTarget.describe resolved.Dependency.Target}: {DependencyStatus.code resolved.Status} ({resolved.Reason})")
          yield ""
          yield "Placement by strategy:"
          for placement in explanation.Placements do
              let wave = placement.Wave |> Option.map (fun number -> $" {number}") |> Option.defaultValue ""
              let action = placement.Action |> Option.map (fun action -> $" ({RecommendedAction.code action})") |> Option.defaultValue ""
              yield $"  {placement.Objective}: {placement.Section}{wave}{action}"
              yield! placement.Reasons |> List.map (reasonLine "    ")
          yield ""
          yield $"On critical path: {explanation.OnCriticalPath}"
          yield! explanation.Unlock |> Option.map (fun unlock -> $"Unlock value: {unlock.Explanation}") |> Option.toList
          yield!
              explanation.Collisions
              |> List.map (fun collision ->
                  let other = if collision.Left = item.Id then collision.Right else collision.Left
                  let signals = collision.Signals |> List.map CollisionSignal.describe |> String.concat "; "
                  $"Collision with {other}: {CollisionRisk.code collision.Risk} ({signals})")
          yield!
              explanation.Affinity
              |> List.map (fun (other, reasons) ->
                  let because = String.concat ", " reasons
                  $"Context affinity with {other}: {because}")
          if not explanation.Findings.IsEmpty then
              yield ""
              yield "Findings:"
              yield! findingLines explanation.Findings ]

    // ---- work groups ------------------------------------------------------------

    let private executionText (execution: GroupExecution) =
        match execution with
        | GroupExecution.OneSequentialAgent -> "one sequential agent"
        | GroupExecution.OneOwnerIndependentSubtasks -> "one owner; subtasks may run in parallel"
        | GroupExecution.SplitByRepository -> "split into repository-local groups first"

    let private groupLines (group: WorkGroup) (relations: GroupRelation list) =
        let id = WorkGroupId.value group.Id

        [ yield id
          yield $"Area: {group.Area} ({GroupKind.code group.Kind}, {GroupOrigin.code group.Origin}); executes in {group.ExecutionRepository}"
          yield $"Members: {group.Members.Length} ({group.Progress.Statement})"
          for entry in group.Members do
              let gated = if entry.GatedBy.IsEmpty then "" else $"""; waits on blocked {String.concat ", " entry.GatedBy}"""
              yield $"  {entry.WorkItemId} [{MemberStatus.code entry.Status}, {PlanningWorkState.code entry.PlanningState}] {EvidenceConfidence.code entry.Confidence}: {entry.Reason}{gated}"
          yield $"Affinity: {ContextAffinity.code group.Affinity}; confidence {EvidenceConfidence.code group.Confidence}"
          yield "Why grouped:"
          yield! group.Cohesion |> List.truncate 6 |> List.map (fun line -> $"  - {line.Statement}")
          let ordered = group.Cohesion |> List.exists (fun line -> match line.Signal with AffinitySignal.HardDependency _ -> true | _ -> false)
          yield (if ordered then $"""Required sequence: {String.concat " -> " group.RequiredSequence}""" else $"""Sequence: no ordering constraint between members (listed {String.concat ", " group.RequiredSequence})""")
          yield $"Estimated context reuse: unknown ({group.ContextCost.IndependentAcquisitions} independent context acquisitions -> {group.ContextCost.GroupedAcquisitions})"
          yield $"Collision risk: {CollisionRisk.code group.Collision}; parallel-safe: {group.ParallelSafe}"
          yield $"Recommended execution: {executionText group.Execution}"
          yield! group.ExecutionReasons |> List.map (fun reason -> $"  - {reason}")
          yield!
              relations
              |> List.filter (fun relation -> relation.Left = id || relation.Right = id)
              |> List.map (fun relation ->
                  let other = if relation.Left = id then relation.Right else relation.Left
                  let verdict = if relation.MayRunConcurrently then "may run independently from" else "should not run alongside"
                  $"  {verdict} {other}: {relation.Reason}")
          yield! group.Notes |> List.map (fun entry -> $"  {FindingSeverity.code entry.Severity} {GroupNoteCode.code entry.Code}: {entry.Message}") ]

    let private groupsText (snapshot: PlanSnapshot) (report: GroupingReport) =
        [ yield! snapshotLines snapshot
          yield report.Statement
          yield ""
          yield $"Settings: preferred size {report.Settings.PreferredMinimumSize}..{report.Settings.PreferredMaximumSize}, automatic maximum {report.Settings.MaximumAutomaticSize}, minimum affinity {ContextAffinity.code report.Settings.MinimumAffinity}, cross-group policy {report.Settings.RiskPolicy}"
          match report.Groups with
          | [] -> yield "No group recommended: no set of open items shares qualifying evidence."
          | groups ->
              for group in groups do
                  yield ""
                  yield! groupLines group report.Relations
          yield ""
          yield "Group dependencies:"
          match report.Dependencies with
          | [] -> yield "  none"
          | edges ->
              yield!
                  edges
                  |> List.map (fun edge ->
                      let gating = if edge.Gating then "" else " (satisfied or soft)"
                      $"""  {GroupEndpoint.describe edge.From} -> {GroupEndpoint.describe edge.To}{gating}: {String.concat "; " edge.Via}""")
          yield! report.Cycles |> List.map (fun cycle -> $"""  cycle: {String.concat " -> " cycle}""")
          yield ""
          yield $"Ungrouped ({report.Ungrouped.Length}):"
          yield! report.Ungrouped |> List.map (fun entry -> $"  {entry.WorkItem} [{PlanningWorkState.code entry.PlanningState}] affinity {ContextAffinity.code entry.Affinity}: {entry.Reason}")
          yield ""
          yield "Unknown:"
          yield! report.UnknownEvidence |> List.map (fun line -> $"  - {line}") ]

    let private groupExplanationText (snapshot: PlanSnapshot) (explanation: GroupExplanation) =
        let section (title: string) (lines: string list) =
            [ yield ""
              yield title
              match lines with
              | [] -> yield "  none"
              | _ -> yield! lines |> List.map (fun line -> $"  - {line}") ]

        [ yield! snapshotLines snapshot
          yield ""
          yield! groupLines explanation.Group []
          yield! section "Why are these items together?" explanation.WhyTogether
          yield! section "Why were other related items excluded?" (explanation.Excluded |> List.map (fun entry -> $"{entry.WorkItem} ({ContextAffinity.code entry.Affinity}): {entry.Reason}"))
          yield! section "What evidence supports the group?" (explanation.Evidence |> List.map (fun (basis, statement) -> $"[{SignalBasis.code basis}] {statement}"))
          yield! section "What is inferred?" explanation.Inferred
          yield! section "What is unknown?" explanation.Unknown
          yield! section "What architecture is shared?" explanation.SharedArchitecture
          yield! section "What dependency order exists?" explanation.DependencyOrder
          yield! section "What collision risk exists?" explanation.CollisionRisk
          yield! section "Why one agent versus several?" explanation.ExecutionRationale
          yield! section "What evidence would change the recommendation?" explanation.WouldChange ]

    let private scheduleLines (schedule: GroupSchedule) =
        [ yield $"Group schedule (max concurrency {schedule.MaxConcurrency}, cross-unit policy {schedule.RiskPolicy}): {duration schedule.ExpectedDuration}"
          yield $"Context acquisitions: {schedule.ContextAcquisitions} as scheduled, {schedule.IndependentContextAcquisitions} if every item ran independently"
          for wave in schedule.Waves do
              yield ""
              yield $"Wave {wave.Number}: {duration wave.ExpectedDuration}"

              for entry in wave.Units do
                  let members = if entry.IsGroup then $""" [{String.concat " -> " entry.Members}]""" else ""
                  yield $"  {entry.Unit}{members} ({duration entry.Remaining})"
                  yield! entry.Reasons |> List.map (fun reason -> $"    - {reason}")
          if not schedule.NotScheduled.IsEmpty then
              yield ""
              yield "Not scheduled:"
              yield! schedule.NotScheduled |> List.map (fun (unit, why) -> $"  {unit}: {why}")
          yield ""
          yield schedule.Statement ]

    let private groupComparisonText (snapshot: PlanSnapshot) (comparison: GroupComparison) =
        [ yield! snapshotLines snapshot
          yield ""
          for tradeoff in comparison.Tradeoffs do
              yield $"""{tradeoff.Group} ({String.concat ", " tradeoff.Members})"""
              if not tradeoff.NotYetRunnable.IsEmpty then
                  yield $"""  hypothetical until triaged: {String.concat ", " tradeoff.NotYetRunnable} cannot run yet"""
              yield $"  independent: {tradeoff.Independent.Executions} executions, {tradeoff.Independent.ContextAcquisitions} context acquisitions, {tradeoff.Independent.DesignOwners} design owners, peak {tradeoff.Independent.PeakConcurrency}, {duration tradeoff.Independent.ExpectedDuration}"
              yield $"  grouped:     {tradeoff.Grouped.Executions} execution, {tradeoff.Grouped.ContextAcquisitions} context acquisition, {tradeoff.Grouped.DesignOwners} design owner, peak {tradeoff.Grouped.PeakConcurrency}, {duration tradeoff.Grouped.ExpectedDuration}"
              yield $"  context: {tradeoff.ContextSaving}"
              yield $"  architecture: {tradeoff.ArchitectureConsideration}"
              yield $"  context pressure: {tradeoff.ContextPressureRisk}"
              yield ""
          yield $"Item-level speed plan: {duration comparison.ItemPlanDuration}; {comparison.ItemPlanContextAcquisitions} context acquisitions"
          yield! scheduleLines comparison.Portfolio
          yield ""
          yield comparison.Statement ]

    let private emit (options: Options) (json: unit -> Text.Json.Nodes.JsonNode) (lines: unit -> string list) =
        if options.Json then printf "%s" (PlanningJson.render (json ()))
        else lines () |> List.iter (printfn "%s")

        0

    // ---- commands -------------------------------------------------------------

    let run (root: string) (version: string) (arguments: string list) : int =
        match arguments with
        | "analyze" :: rest ->
            let options = parse empty rest

            withAnalysis root version options (fun _ analysis ->
                emit options (fun () -> PlanningJson.analysis analysis) (fun () -> analysisText analysis))
        | "simulate" :: rest ->
            let options = parse empty rest

            match objective options, maxConcurrency options with
            | Error message, _
            | _, Error message -> fail 2 message
            | Ok _, Ok limit when options.Groups ->
                withAnalysis root version options (fun input analysis ->
                    let schedule = Grouping.schedule input.Configuration analysis (Grouping.recommend input analysis) limit

                    emit
                        options
                        (fun () -> PlanningJson.groupSimulation analysis.Snapshot schedule)
                        (fun () -> snapshotLines analysis.Snapshot @ [ ""; Grouping.advisoryStatement; "" ] @ scheduleLines schedule))
            | Ok objective, Ok limit ->
                withAnalysis root version options (fun input analysis ->
                    let document = Planner.plan analysis input.Configuration objective limit
                    emit options (fun () -> PlanningJson.plan document) (fun () -> planText options.Details document))
        | "compare" :: rest ->
            let options = parse empty rest

            match maxConcurrency options with
            | Error message -> fail 2 message
            | Ok limit when options.Groups ->
                withAnalysis root version options (fun input analysis ->
                    let speed = Scheduling.simulate analysis input.Configuration OptimizationObjective.MinimumDuration limit
                    let comparison = Grouping.compare input.Configuration analysis (Grouping.recommend input analysis) speed limit
                    emit options (fun () -> PlanningJson.groupComparison analysis.Snapshot comparison) (fun () -> groupComparisonText analysis.Snapshot comparison))
            | Ok limit ->
                withAnalysis root version options (fun input analysis ->
                    let comparison = Planner.compare analysis input.Configuration limit
                    emit options (fun () -> PlanningJson.comparison analysis.Snapshot comparison) (fun () -> comparisonText analysis.Snapshot comparison))
        | "explain" :: rest ->
            let options = parse empty rest

            match options.Positional, maxConcurrency options with
            | _, Error message -> fail 2 message
            | [ id ], Ok limit ->
                withAnalysis root version { options with Positional = [] } (fun input analysis ->
                    match Planner.explain analysis input.Configuration limit id with
                    | Error message -> fail 1 message
                    | Ok explanation ->
                        emit options (fun () -> PlanningJson.explanation analysis.Snapshot explanation) (fun () -> explanationText analysis.Snapshot explanation))
            | _ -> fail 2 "plan explain requires exactly one work-item ID"
        | "groups" :: rest ->
            let options = parse empty rest

            withAnalysis root version options (fun input analysis ->
                let report = Grouping.recommend input analysis
                emit options (fun () -> PlanningJson.groups analysis.Snapshot report) (fun () -> groupsText analysis.Snapshot report))
        | "explain-group" :: rest ->
            let options = parse empty rest

            match options.Positional with
            | [ id ] ->
                withAnalysis root version { options with Positional = [] } (fun input analysis ->
                    let report = Grouping.recommend input analysis

                    match Grouping.explain input analysis report id with
                    | Error message -> fail 1 message
                    | Ok explanation ->
                        emit options (fun () -> PlanningJson.groupExplanation analysis.Snapshot explanation) (fun () -> groupExplanationText analysis.Snapshot explanation))
            | _ -> fail 2 "plan explain-group requires exactly one group ID (see 'plan groups')"
        | "replay" :: rest ->
            let options = parse empty rest

            withAnalysis root version options (fun input analysis ->
                let report = Replay.replay input.Configuration input.Queue input.Executions
                // --record is the one plan write: the calibration history only (PRX-PLAN-170).
                let recorded = if options.Record then FilePlanningRepository.recordCalibration root (Calibration.entry input.PlannedAt analysis.Snapshot report) |> Result.map Some else Ok None

                match recorded, FilePlanningRepository.readCalibration root with
                | Error message, _ | _, Error message -> fail 1 message
                | Ok recorded, Ok history ->
                    let trend = Calibration.trend history
                    emit options (fun () -> PlanningJson.replay report trend recorded) (fun () -> PlanEvidenceText.replay options.Details report trend recorded))
        | "freshness" :: rest ->
            let options = parse empty rest

            match options.Plan with
            | None -> fail 2 "plan freshness requires --plan FILE (a document written by 'plan simulate --json')"
            | Some path ->
                let path = resolve root path

                if not (File.Exists path) then
                    fail 2 $"{path} does not exist"
                else
                    match PlanningJson.parsePlan (File.ReadAllText path) with
                    | Error message -> fail 2 message
                    | Ok previous ->
                        withAnalysis root version options (fun input analysis ->
                            let review = Comparison.review previous analysis.Snapshot input.Executions
                            emit options (fun () -> PlanningJson.review analysis.Snapshot review) (fun () -> PlanEvidenceText.review review) |> ignore
                            if review.Freshness.Stale then 3 else 0)
        | _ -> fail 2 $"usage: {usage}"
