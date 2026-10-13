namespace Praxis.Infrastructure.Construction

open Ordo.Core

/// One typed obligation of an Ordo default plan (`ordo.construction-obligations/1`),
/// as Praxis carries it. `Kind` is the Ordo wire kind: `extensionBehavior`,
/// `undecidedEffectFailure`, `conflict`, `uncoveredRequirement` or
/// `unsupportedVocabulary`. `ExtensionSurface` names generated extension
/// contracts or ports the obligation touches.
type DefaultPlanObligation =
    { Id: string
      Kind: string
      RequirementKeys: string list
      NodeIds: string list
      Statement: string
      ExtensionSurface: string list }

type DefaultPlanProvenance =
    { GeneratorId: string
      GeneratorVersion: string
      VocabularyVersion: string
      /// (input name, digest) for every input, in Ordo's order.
      InputDigests: (string * string) list
      /// Digest over the whole default plan, as Ordo computes it.
      DefaultPlanDigest: string }

/// What mechanical-first generation returns (CONSTRUCT-008): an ordinary
/// `ordo.construction-plan/1` document with an Ordo-computed binding, its
/// digest, typed obligations, the generated layout and provenance.
type DefaultPlanOutput =
    { PlanText: string
      PlanDigest: string
      Obligations: DefaultPlanObligation list
      GeneratedLayout: string list
      Provenance: DefaultPlanProvenance }

/// The generator Praxis calls, injectable so tests can use a fake one.
/// `Version` enters the reuse cache key before generation runs.
type DefaultPlanGenerator =
    { Id: string
      Version: string
      /// prepared cohort -> exact approved decision-record text -> vocabulary version
      Generate: ConstructionIr.PreparedCohort -> string -> string -> Result<DefaultPlanOutput, string list> }

/// The single seam between Praxis and Ordo's default-plan generator.
[<RequireQualifiedAccess>]
module OrdoDefaultPlan =
    [<Literal>]
    let GeneratorId = "ordo.default-plan"

    /// The default vocabulary of mechanical-first plans (CONSTRUCT-008 runs in
    /// vocabulary 1 so generated F# matches the plan-driven arm); `prepare
    /// --vocabulary` may choose another supported one.
    let VocabularyVersion = ConstructionPlan.VocabularyVersion1

    let SupportedVocabularies = ConstructionPlan.SupportedVocabularyVersions

    /// THE SEAM: Ordo's `generateDefaultPlan`, mapped to Praxis's record.
    /// Refusals are reported verbatim; nothing is invented or patched here.
    let generate (prepared: ConstructionIr.PreparedCohort) (decisionRecords: string) (vocabularyVersion: string) : Result<DefaultPlanOutput, string list> =
        Ordo.Core.ConstructionDecisions.readDecisionRecords decisionRecords
        |> Result.mapError (fun reason -> [ "decision records refused: " + reason ])
        |> Result.bind (fun records ->
            ConstructionDefaultPlan.ConstructionVocabulary.ofVersion vocabularyVersion
            |> Result.mapError (fun refusal -> [ sprintf "%A" refusal ])
            |> Result.bind (fun vocabulary ->
                ConstructionDefaultPlan.generateDefaultPlan prepared records vocabulary
                |> Result.mapError (List.map (sprintf "%A"))))
        |> Result.map (fun plan ->
            let p = plan.Provenance
            { PlanText = ConstructionPlan.canonicalJson plan.ValidatedPlan
              PlanDigest = plan.PlanDigest
              Obligations =
                plan.Obligations |> List.map (fun o ->
                    { Id = o.Id; Kind = ConstructionDefaultPlan.obligationKindName o.Kind; RequirementKeys = o.RequirementKeys
                      NodeIds = o.NodeIds; Statement = o.Question; ExtensionSurface = o.ExtensionSurface })
              GeneratedLayout = plan.GeneratedLayout
              Provenance =
                { GeneratorId = p.GeneratorId; GeneratorVersion = p.GeneratorVersion; VocabularyVersion = p.VocabularyVersion
                  InputDigests = [ "blueprint", p.BlueprintDigest; "sourceManifest", p.SourceManifestDigest; "decisionRecords", p.DecisionRecordsDigest ]
                  DefaultPlanDigest = p.DefaultPlanDigest } })

    let generator () : DefaultPlanGenerator =
        { Id = ConstructionDefaultPlan.GeneratorId; Version = ConstructionDefaultPlan.GeneratorVersion; Generate = generate }
