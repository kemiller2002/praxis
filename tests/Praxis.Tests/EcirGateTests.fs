module Praxis.Tests.EcirGateTests

open Xunit
open Praxis.Domain.Work

let private good =
    { GroupId = "GROUP-ECHELON-DEMO-001"
      CohortId = "COHORT-001"
      ManifestDigest = "sha256:source"
      BlueprintSourceDigest = "sha256:source"
      ExpectedBlueprintDigest = "sha256:blueprint"
      ValidatedBlueprintDigest = Some "sha256:blueprint"
      OrdoValidation = Ok ()
      DecisionAuthorization = Ok ()
      CohortKeys = [ "requirements/domain.md#R1"; "requirements/domain.md#R2" ]
      GroupRequirementKeys = [ "requirements/domain.md#R1"; "requirements/domain.md#R2" ]
      BlockedRequirements = []
      ValidatorPinned = true
      ArtifactCommitVerified = true }

[<Fact>]
let ``coherent independently verified facts satisfy the construction gate`` () =
    Assert.Empty(EcirGates.problems good)

[<Fact>]
let ``unpinned and unverified Ordo artifacts cannot authorize group execution`` () =
    let unsafe =
        { good with
            ValidatorPinned = false
            ArtifactCommitVerified = false
            OrdoValidation = Error [ "missing Ordo installation" ]
            DecisionAuthorization = Error [ "not approved" ] }
    Assert.False(EcirGates.allowsExecution unsafe)
    Assert.Contains("ecir-validator-unpinned", EcirGates.problems unsafe)
    Assert.Contains("ecir-artifact-not-at-verified-commit", EcirGates.problems unsafe)
    Assert.Contains("ecir-ordo-rejected", EcirGates.problems unsafe)
    Assert.Contains("ecir-authorization-missing", EcirGates.problems unsafe)

[<Fact>]
let ``an agent cannot swap the blueprint after validation`` () =
    let swapped =
        { good with ValidatedBlueprintDigest = Some "sha256:different"
                    BlueprintSourceDigest = "sha256:replaced" }
    Assert.False(EcirGates.allowsExecution swapped)
    Assert.Contains("ecir-source-manifest-mismatch", EcirGates.problems swapped)
    Assert.Contains("ecir-blueprint-digest-mismatch", EcirGates.problems swapped)

[<Fact>]
let ``reused requirement identity and missing cohort member are refused`` () =
    let repeated =
        { good with
            CohortKeys = [ "requirements/domain.md#R1"; "requirements/domain.md#R1" ] }
    Assert.Contains("ecir-cohort-requirements-not-exact", EcirGates.problems repeated)

[<Fact>]
let ``unresolved or deferred source requirement blocks the whole affected cohort`` () =
    let unresolved = { good with BlockedRequirements = [ "requirements/domain.md#R2" ] }
    Assert.Contains("ecir-requirement-blocked:requirements/domain.md#R2", EcirGates.problems unresolved)

[<Fact>]
let ``empty requirement set and unknown external authority are not approvals`` () =
    let unknown =
        { good with
            GroupRequirementKeys = []
            CohortKeys = []
            DecisionAuthorization = Error [] }
    Assert.Contains("ecir-cohort-requirements-not-exact", EcirGates.problems unknown)
    Assert.Contains("ecir-authorization-without-reason", EcirGates.problems unknown)
