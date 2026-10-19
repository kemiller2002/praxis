namespace Praxis.Tests

open System.Text.Json
open Praxis.Application.Work
open Praxis.Domain.Work

/// Completion now requires an authority bound to a ready Ordo scope
/// (REL-GATE-002). Tests whose subject is not the contract bind their host
/// input to a generated `ecir/1` contract that is ready for exactly the
/// requirements and decisions the input names: every requirement modeled,
/// verified and owned by one cohort, every decision linked to the first
/// requirement and authorized by the input's `RequiredDecisionIds`.
[<RequireQualifiedAccess>]
module OrdoTestContracts =
    let private digest (fill: char) = "sha256:" + System.String(fill, 64)

    /// A ready contract for `requirements` (qualified keys) and `decisions`.
    let contract (requirements: string list) (decisions: string list) =
        let first = List.head requirements
        let verify index = $"VERIFY-{index + 1}"

        let requirement index (key: string) =
            {| source =
                {| key = key
                   originalId = key
                   document = "requirements/test.md"
                   location = $"L{index + 1}"
                   revision = digest 'a'
                   contentDigest = digest 'b' |}
               disposition = {| kind = "modeled" |}
               nodeIds = [ verify index; "COHORT-TEST" ] @ (if key = first then decisions else []) |}

        let node id kind (keys: string list) (justification: string option) =
            match justification with
            | Some why -> JsonSerializer.SerializeToNode {| id = id; kind = kind; requirementKeys = keys; dependsOn = List.empty<string>; justification = why |}
            | None -> JsonSerializer.SerializeToNode {| id = id; kind = kind; requirementKeys = keys; dependsOn = List.empty<string> |}

        let nodes =
            [ yield node "COHORT-TEST" "cohort" requirements None
              for index, key in List.indexed requirements do
                  yield node (verify index) "verificationObligation" [ key ] None
              for id in decisions do
                  yield node id "decision" [ first ] (Some "decided by the test host") ]

        JsonSerializer.Serialize
            {| schemaVersion = "ecir/1"
               sourceManifestDigest = digest 'c'
               requirements = requirements |> List.mapi requirement
               nodes = nodes |}

    /// `input` bound to the generated contract ready for it. The declared
    /// identity is cleared so the contract supplies version and digest.
    let bind (input: CompletionAuthorityInput) : BoundCompletionAuthority =
        let cleared = { input with OrdoContractVersion = ""; OrdoContractDigest = ""; SupportedOrdoContractVersions = [] }

        match OrdoBoundAuthority.bind None (contract input.RequiredRequirementIds input.RequiredDecisionIds) cleared with
        | Ok bound -> bound
        | Error findings -> failwith $"the generated contract must be ready for the test authority: {findings}"
