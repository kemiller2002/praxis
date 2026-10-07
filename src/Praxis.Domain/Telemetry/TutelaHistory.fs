namespace Praxis.Domain.Telemetry

open System

/// One Tutela exception as an assessment records it.
type TutelaException =
    { Id: string
      CreatedAt: DateTimeOffset option
      ExpiresAt: DateTimeOffset option }

/// One ingested Tutela security assessment with the provenance Praxis
/// records for it (TUT-3): the repository, the assessed ref and the commit it
/// resolved to when this repository knows it, when it was collected, and the
/// digest of the source document. Lists hold identifiers only, never content.
type TutelaObservation =
    { Repository: string
      Ref: string
      Commit: string option
      CollectedAt: DateTimeOffset
      SourceSha256: string
      /// Invariant id and state (Verified, Violated, Unknown, Stale, NotApplicable).
      Invariants: (string * string) list
      UnknownEffects: string list
      Findings: string list
      /// Evidence id and when it was observed.
      Evidence: (string * DateTimeOffset) list
      StaleEvidence: string list
      Exceptions: TutelaException list
      /// Invariants requiring independent verification, and how many carry an
      /// independent attestation. None when the assessment does not say.
      IndependentRequired: int option
      IndependentAttested: int option
      /// Security boundary changes recorded in the assessment (trust-root changes).
      TrustRootChanges: string list
      /// Security-sensitive paths changed since the previous observation's
      /// commit. None when it could not be measured (no configured sensitive
      /// paths, no previous commit, or a commit this repository lacks).
      SensitiveChanges: string list option }

/// A measurement or an explicit unknown (TUT-5): never a zero standing in for
/// a missing measurement.
[<RequireQualifiedAccess>]
type MetricValue =
    | Measured of value: float
    | Unknown of reason: string

type SecurityMetric =
    { Id: string
      Dimensions: (string * string) list
      Unit: string
      Value: MetricValue }

type SecurityMetricPoint =
    { Repository: string
      Ref: string
      Commit: string option
      CollectedAt: DateTimeOffset
      SourceSha256: string
      Metrics: SecurityMetric list }

/// Security metrics over time (TUT-1, TUT-2). Decision support, not
/// certification (TUT-4): there is deliberately no aggregate score.
[<RequireQualifiedAccess>]
module TutelaHistory =
    [<Literal>]
    let Disclaimer =
        "Security engineering telemetry for decision support. It is not a certification, and there is no single security score. Unknown means not measured, never zero."

    let invariantStates = [ "Verified"; "Violated"; "Unknown"; "Stale"; "NotApplicable" ]

    let private metric id dimensions unit value =
        { Id = id
          Dimensions = dimensions
          Unit = unit
          Value = value }

    let private count id dimensions (n: int) = metric id dimensions "count" (MetricValue.Measured(float n))
    let private unknown id dimensions unit reason = metric id dimensions unit (MetricValue.Unknown reason)
    let private days (span: TimeSpan) = Math.Round(span.TotalDays, 3)
    let private first = "no earlier observation of this repository"

    let private stateOf (observation: TutelaObservation) =
        observation.Invariants |> Map.ofList

    /// Identifiers that were open in an observation: unknown effects,
    /// findings and violated invariants, each with its kind.
    let private openItems (o: TutelaObservation) =
        [ yield! o.UnknownEffects |> List.map (fun id -> "unknown-effect", id)
          yield! o.Findings |> List.map (fun id -> "finding", id)
          yield!
              o.Invariants
              |> List.filter (fun (_, state) -> state = "Violated")
              |> List.map (fun (id, _) -> "violated-invariant", id) ]
        |> Set.ofList

    let private point (earlier: TutelaObservation list) (current: TutelaObservation) (sensitiveConfigured: bool) (boundaryChanges: int option) =
        let previous = List.tryLast earlier
        let states = stateOf current

        let invariantCounts =
            invariantStates
            |> List.map (fun state ->
                count "security.invariants" [ "state", state ] (current.Invariants |> List.filter (snd >> (=) state) |> List.length))

        let transitions =
            match previous with
            | None -> [ unknown "security.invariant_transitions" [] "count" first ]
            | Some p ->
                let before = stateOf p

                let moved =
                    states
                    |> Map.toList
                    |> List.choose (fun (id, state) ->
                        match Map.tryFind id before with
                        | Some old when old <> state -> Some(old, state)
                        | _ -> None)

                count "security.invariant_transitions" [] moved.Length
                :: (moved
                    |> List.countBy id
                    |> List.sort
                    |> List.map (fun ((fromState, toState), n) ->
                        count "security.invariant_transitions" [ "from", fromState; "to", toState ] n))

        let openEffects = Set.ofList current.UnknownEffects

        let effects =
            [ count "security.unknown_effects" [ "state", "open" ] openEffects.Count
              match previous with
              | None ->
                  unknown "security.unknown_effects" [ "state", "created" ] "count" first
                  unknown "security.unknown_effects" [ "state", "resolved" ] "count" first
              | Some p ->
                  let before = Set.ofList p.UnknownEffects
                  count "security.unknown_effects" [ "state", "created" ] (Set.difference openEffects before).Count
                  count "security.unknown_effects" [ "state", "resolved" ] (Set.difference before openEffects).Count ]

        let evidence =
            [ match current.Evidence with
              | [] ->
                  unknown "security.evidence_age" [ "statistic", "max" ] "days" "the assessment records no evidence observation times"
                  unknown "security.evidence_age" [ "statistic", "mean" ] "days" "the assessment records no evidence observation times"
              | items ->
                  let ages = items |> List.map (fun (_, at) -> days (current.CollectedAt - at))
                  metric "security.evidence_age" [ "statistic", "max" ] "days" (MetricValue.Measured(List.max ages))
                  metric "security.evidence_age" [ "statistic", "mean" ] "days" (MetricValue.Measured(Math.Round(List.average ages, 3)))
              count "security.evidence" [ "state", "stale" ] (List.length (List.distinct current.StaleEvidence)) ]

        let isExpired (e: TutelaException) =
            e.ExpiresAt |> Option.exists (fun at -> at <= current.CollectedAt)

        let active = current.Exceptions |> List.filter (isExpired >> not)

        let exceptions =
            [ count "security.exceptions" [ "state", "active" ] active.Length
              count "security.exceptions" [ "state", "expired" ] (current.Exceptions |> List.filter isExpired |> List.length)
              match previous with
              | None -> unknown "security.exceptions" [ "state", "created" ] "count" first
              | Some p ->
                  let before = p.Exceptions |> List.map _.Id |> Set.ofList
                  count "security.exceptions" [ "state", "created" ] (current.Exceptions |> List.filter (fun e -> not (before.Contains e.Id)) |> List.length)
              match active |> List.choose _.ExpiresAt with
              | [] -> unknown "security.exception_time_to_expiry" [ "statistic", "min" ] "days" "no active exception records an expiry"
              | expiries ->
                  metric "security.exception_time_to_expiry" [ "statistic", "min" ] "days" (MetricValue.Measured(expiries |> List.map (fun at -> days (at - current.CollectedAt)) |> List.min)) ]

        let earlierFindings = earlier |> List.map (fun o -> Set.ofList o.Findings)
        let currentFindings = Set.ofList current.Findings

        let findings =
            [ count "security.findings" [ "state", "open" ] currentFindings.Count
              match previous with
              | None ->
                  unknown "security.findings" [ "state", "recurring" ] "count" first
                  unknown "security.findings" [ "state", "reopened" ] "count" first
              | Some p ->
                  let seenBefore = earlierFindings |> Set.unionMany
                  let inPrevious = Set.ofList p.Findings
                  count "security.findings" [ "state", "recurring" ] (Set.intersect currentFindings seenBefore).Count
                  count "security.findings" [ "state", "reopened" ] (currentFindings |> Set.filter (fun f -> seenBefore.Contains f && not (inPrevious.Contains f))).Count ]

        let churn =
            match current.SensitiveChanges with
            | Some paths ->
                let hotspots =
                    (earlier |> List.choose _.SensitiveChanges) @ [ paths ]
                    |> List.collect List.distinct
                    |> List.countBy id
                    |> List.filter (fun (_, n) -> n > 1)

                [ count "security.sensitive_churn" [] (List.length (List.distinct paths))
                  count "security.hotspots" [] hotspots.Length ]
            | None ->
                let reason =
                    if not sensitiveConfigured then "no security-sensitive paths are configured (ros.json tutela.sensitivePaths)"
                    elif previous.IsNone then first
                    else "the change between the observed commits could not be read"

                [ unknown "security.sensitive_churn" [] "count" reason; unknown "security.hotspots" [] "count" reason ]

        let boundary =
            [ match previous with
              | None -> unknown "security.boundary_changes" [ "source", "trust-root" ] "count" first
              | Some p ->
                  let before = Set.ofList p.TrustRootChanges
                  count "security.boundary_changes" [ "source", "trust-root" ] (current.TrustRootChanges |> List.filter (before.Contains >> not) |> List.distinct |> List.length)
              match boundaryChanges with
              | Some n -> count "security.boundary_changes" [ "source", "execution-scope-expansion" ] n
              | None -> unknown "security.boundary_changes" [ "source", "execution-scope-expansion" ] "count" first ]

        let leadTimes =
            match previous with
            | None -> [ unknown "security.remediation_lead_time" [ "statistic", "mean" ] "days" first ]
            | Some p ->
                let resolved = Set.difference (openItems p) (openItems current)

                let firstSeen item =
                    earlier |> List.tryFind (fun o -> (openItems o).Contains item) |> Option.map _.CollectedAt

                let times = resolved |> Set.toList |> List.choose firstSeen |> List.map (fun at -> days (current.CollectedAt - at))

                [ count "security.remediated" [] resolved.Count
                  match times with
                  | [] -> unknown "security.remediation_lead_time" [ "statistic", "mean" ] "days" "nothing was remediated since the previous observation"
                  | ts ->
                      metric "security.remediation_lead_time" [ "statistic", "mean" ] "days" (MetricValue.Measured(Math.Round(List.average ts, 3)))
                      metric "security.remediation_lead_time" [ "statistic", "max" ] "days" (MetricValue.Measured(List.max ts)) ]

        let coverage =
            match current.IndependentRequired, current.IndependentAttested with
            | Some 0, _ -> unknown "security.independent_verification" [] "ratio" "no invariant requires independent verification"
            | Some required, Some attested -> metric "security.independent_verification" [] "ratio" (MetricValue.Measured(Math.Round(float attested / float required, 4)))
            | _ -> unknown "security.independent_verification" [] "ratio" "the assessment does not record independent verification"

        { Repository = current.Repository
          Ref = current.Ref
          Commit = current.Commit
          CollectedAt = current.CollectedAt
          SourceSha256 = current.SourceSha256
          Metrics = invariantCounts @ transitions @ effects @ evidence @ exceptions @ findings @ churn @ boundary @ leadTimes @ [ coverage ] }

    /// Metrics for every observation, each compared with the earlier
    /// observations of the same repository. `boundaryChangesBetween` counts
    /// Praxis execution scope expansions in a time window.
    let derive
        (sensitiveConfigured: bool)
        (boundaryChangesBetween: DateTimeOffset -> DateTimeOffset -> int)
        (observations: TutelaObservation list)
        : SecurityMetricPoint list =
        observations
        |> List.groupBy _.Repository
        |> List.collect (fun (_, history) ->
            let ordered = history |> List.sortBy _.CollectedAt

            ordered
            |> List.mapi (fun index current ->
                let earlier = List.take index ordered

                let boundary =
                    earlier |> List.tryLast |> Option.map (fun p -> boundaryChangesBetween p.CollectedAt current.CollectedAt)

                point earlier current sensitiveConfigured boundary))
        |> List.sortBy (fun p -> p.Repository, p.CollectedAt)
