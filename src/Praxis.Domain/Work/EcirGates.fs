namespace Praxis.Domain.Work

/// Execution is not authorized by an agent's claimed ECIR status. A caller
/// must first obtain trusted observations from a pinned Ordo validator and a
/// separately verified authorization source. See PRX ECIR-003.
type EcirExecutionObservation =
    { GroupId: string
      CohortId: string
      ManifestDigest: string
      BlueprintSourceDigest: string
      ExpectedBlueprintDigest: string
      ValidatedBlueprintDigest: string option
      /// This must be observed from a pinned, executable Ordo validator,
      /// never parsed from an LLM-authored JSON field.
      OrdoValidation: Result<unit, string list>
      /// Provenance and authorization for architectural decisions are
      /// external to the ECIR blueprint. Empty/unknown means NO authority.
      DecisionAuthorization: Result<unit, string list>
      CohortKeys: string list
      GroupRequirementKeys: string list
      BlockedRequirements: string list
      ValidatorPinned: bool
      ArtifactCommitVerified: bool }

[<RequireQualifiedAccess>]
module EcirGates =
    /// Explicit opt-in construction cohorts cannot execute through the
    /// legacy unguarded grouped-work path, even if an agent omits ECIR flags.
    let isEcirGroup (sharedContext: string list) =
        sharedContext
        |> List.exists (fun entry ->
            not (System.String.IsNullOrWhiteSpace entry)
            && entry.StartsWith("ecir/1:", System.StringComparison.Ordinal))

    /// Failure categories are stable enough for agent disposition and human
    /// review; never return an authorization-free pass on missing observations.
    let problems (value: EcirExecutionObservation) : string list =
        [ if System.String.IsNullOrWhiteSpace value.GroupId
             || System.String.IsNullOrWhiteSpace value.CohortId then
              yield "ecir-identity-missing"
          if not value.ValidatorPinned then yield "ecir-validator-unpinned"
          if not value.ArtifactCommitVerified then yield "ecir-artifact-not-at-verified-commit"
          if System.String.IsNullOrWhiteSpace value.ManifestDigest
             || value.ManifestDigest <> value.BlueprintSourceDigest then
              yield "ecir-source-manifest-mismatch"
          match value.OrdoValidation with
          | Error reasons ->
              yield "ecir-ordo-rejected"
              if reasons.IsEmpty then yield "ecir-ordo-rejection-without-reason"
          | Ok () -> ()
          if System.String.IsNullOrWhiteSpace value.ExpectedBlueprintDigest
             || Some value.ExpectedBlueprintDigest <> value.ValidatedBlueprintDigest then
              yield "ecir-blueprint-digest-mismatch"
          match value.DecisionAuthorization with
          | Error reasons ->
              yield "ecir-authorization-missing"
              if reasons.IsEmpty then yield "ecir-authorization-without-reason"
          | Ok () -> ()
          let actual = value.CohortKeys |> Set.ofList
          let declared = value.GroupRequirementKeys |> Set.ofList
          if actual <> declared
             || actual.Count <> value.CohortKeys.Length
             || declared.Count <> value.GroupRequirementKeys.Length
             || actual.IsEmpty then
              yield "ecir-cohort-requirements-not-exact"
          for key in value.BlockedRequirements |> List.distinct do
              yield "ecir-requirement-blocked:" + key ]

    let allowsExecution observation = problems observation |> List.isEmpty
