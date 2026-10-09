namespace Praxis.Tests

open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module EcirGateTests =
    let private t name action = { Name = "ECIR: " + name; Run = action }
    let private has code (observation: EcirExecutionObservation) =
        Assert.isTrue (EcirGates.problems observation |> List.contains code) ("expected " + code)

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

    let tests =
        [ t "a coherent validated authorized cohort satisfies the domain gate" (fun () ->
              Assert.empty (EcirGates.problems good))
          t "unqualified or unavailable external authority fails closed" (fun () ->
              let unsafe =
                  { good with
                      ValidatorPinned = false
                      ArtifactCommitVerified = false
                      OrdoValidation = Error [ "missing installed Ordo" ]
                      DecisionAuthorization = Error [ "missing approval" ] }
              for code in [ "ecir-validator-unpinned"; "ecir-artifact-not-at-verified-commit"; "ecir-ordo-rejected"; "ecir-authorization-missing" ] do
                  has code unsafe)
          t "a blueprint cannot be substituted after validation" (fun () ->
              let changed =
                  { good with
                      ValidatedBlueprintDigest = Some "sha256:other"
                      BlueprintSourceDigest = "sha256:replaced" }
              has "ecir-source-manifest-mismatch" changed
              has "ecir-blueprint-digest-mismatch" changed)
          t "duplicate source keys and omitted cohort keys fail" (fun () ->
              let bad = { good with CohortKeys = [ "requirements/domain.md#R1"; "requirements/domain.md#R1" ] }
              has "ecir-cohort-requirements-not-exact" bad)
          t "an unresolved requirement blocks its cohort" (fun () ->
              let bad = { good with BlockedRequirements = [ "requirements/domain.md#R2" ] }
              has "ecir-requirement-blocked:requirements/domain.md#R2" bad)
          t "missing external approvals and an empty cohort are not acceptable" (fun () ->
              let bad =
                  { good with
                      CohortKeys = []
                      GroupRequirementKeys = []
                      DecisionAuthorization = Error [] }
              has "ecir-cohort-requirements-not-exact" bad
              has "ecir-authorization-without-reason" bad)
          t "ECIR groups cannot bypass the legacy execution gate" (fun () ->
              Assert.isTrue (EcirGates.isEcirGroup [ "context"; "ecir/1:COHORT-001" ]) "ECIR group was not detected"
              Assert.isTrue (not (EcirGates.isEcirGroup [ "context"; "legacy" ])) "non-ECIR group was misidentified") ]
