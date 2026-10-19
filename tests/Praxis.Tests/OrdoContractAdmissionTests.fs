/// Producer/consumer compatibility for the Ordo constructibility contract
/// (PRX-REL-008, interface I3). The golden corpus is copied verbatim from Ordo
/// (Fixtures/ordo-contract/PROVENANCE.md); Praxis must accept and reject
/// exactly as Ordo does and reproduce Ordo's canonical digest byte-for-byte.
namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Praxis.Domain.Ordo

[<RequireQualifiedAccess>]
module OrdoContractAdmissionTests =

    let private equal (expected: 'value, actual: 'value) = Assert.equal expected actual
    let private holds (condition: bool, message: string) = Assert.isTrue condition message

    let private fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ordo-contract")
    let private read name = File.ReadAllText(Path.Combine(fixtureDir, name))

    let private sha256Text (value: string) =
        "sha256:" + (value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

    let private admit = OrdoContractAdmission.admit OrdoContractAdmission.implementedVersions

    let private expectAdmitted json =
        match admit json with
        | Ok admitted -> admitted
        | Error diagnostics -> failwithf "refused: %A" diagnostics

    let private codesOf (diagnostics: OrdoDiagnostic list) = diagnostics |> List.map (fun d -> d.Code, d.Subject)

    type private Case =
        { Name: string
          Contract: string
          ExpectedDigest: string option
          Outcome: string
          Identity: JsonElement option
          Diagnostics: (string * string option) list }

    let private cases () =
        use document = JsonDocument.Parse(read "expectations.json")
        let optionalText (element: JsonElement) (name: string) =
            match element.TryGetProperty name with
            | true, value -> Option.ofObj (value.GetString())
            | _ -> None
        [ for case in document.RootElement.GetProperty("cases").EnumerateArray() ->
            { Name = case.GetProperty("name").GetString() |> string
              Contract = case.GetProperty("contract").GetString() |> string
              ExpectedDigest = optionalText case "expectedDigest"
              Outcome = case.GetProperty("outcome").GetString() |> string
              Identity =
                match case.TryGetProperty "identity" with
                | true, identity -> Some(identity.Clone())
                | _ -> None
              Diagnostics =
                match case.TryGetProperty "diagnostics" with
                | true, items ->
                    [ for item in items.EnumerateArray() -> item.GetProperty("code").GetString() |> string, optionalText item "subject" ]
                | _ -> [] } ]

    let private stringsOf (element: JsonElement) (name: string) =
        [ for item in element.GetProperty(name).EnumerateArray() -> item.GetString() |> string ]

    let private ``Praxis reproduces every Ordo golden admission outcome exactly`` () =
        let all = cases ()
        holds(all.Length >= 20, "the copied Ordo corpus is incomplete")
        for case in all do
            let outcome = OrdoContractAdmission.admitPinned OrdoContractAdmission.implementedVersions case.ExpectedDigest (read case.Contract)
            match case.Outcome, outcome with
            | "admitted", Ok admitted ->
                let identity = case.Identity |> Option.get
                equal(identity.GetProperty("contractSchemaVersion").GetString(), admitted.Version)
                equal(identity.GetProperty("contractDigest").GetString(), admitted.Digest)
                equal(stringsOf identity "requirementKeys", admitted.RequirementIds)
                equal(stringsOf identity "decisionIds", admitted.DecisionIds)
                equal(stringsOf identity "conflictIds", admitted.ConflictResolutionIds)
            | "rejected", Error diagnostics ->
                holds((case.Diagnostics = codesOf diagnostics), sprintf "%s: expected %A got %A" case.Name case.Diagnostics (codesOf diagnostics))
            | expected, actual -> failwithf "%s: Ordo says %s, Praxis says %A" case.Name expected actual

    let private ``Praxis renders Ordo's published canonical form byte for byte`` () =
        let canonical = read "valid-contract.canonical.json"
        for file in [ "valid-contract.json"; "reordered-contract.json"; "valid-contract.canonical.json" ] do
            match OrdoContractAdmission.canonicalForm (read file) with
            | Ok text -> equal(canonical, text)
            | Error diagnostics -> failwithf "%s: %A" file diagnostics
        let admitted = expectAdmitted (read "valid-contract.json")
        equal(sha256Text canonical, admitted.Digest)
        holds(Text.RegularExpressions.Regex.IsMatch(admitted.Digest, "^sha256:[0-9a-f]{64}$"), admitted.Digest)

    /// Independent differential oracle: the Ordo binary Praxis already vendors
    /// (EchelonFoundry.Ordo.Core 1.8.2) has the same canonical codec and
    /// validator as the contract source. Praxis's own wire implementation must
    /// agree with it on digest and on accept/reject for every input. 1.8.2
    /// predates the REL-GATE-002 admission rule (ordo 1f39ff0), so the oracle
    /// applies that one rule on top of the vendored `validate`: a modeled
    /// requirement linking a conflict node is refused.
    let private ordoBinary (json: string) =
        try
            match Ordo.Core.ConstructionIrWire.readBlueprint json with
            | Error _ -> None
            | Ok blueprint ->
                let manifest: Ordo.Core.ConstructionIr.SourceManifest =
                    { Digest = blueprint.SourceManifestDigest
                      Requirements = blueprint.Requirements |> List.map _.Source |> List.distinctBy _.Key }
                let conflicts =
                    blueprint.Nodes |> List.filter (fun node -> node.Kind = Ordo.Core.ConstructionIr.Conflict) |> List.map _.Id |> Set.ofList
                let unresolvedConflict =
                    blueprint.Requirements
                    |> List.exists (fun requirement ->
                        requirement.Disposition = Ordo.Core.ConstructionIr.Modeled && requirement.NodeIds |> List.exists conflicts.Contains)
                match Ordo.Core.ConstructionIr.validate manifest blueprint with
                | [] when not unresolvedConflict -> Some(Ordo.Core.ConstructionIrWire.blueprintDigest blueprint)
                | _ -> None
        with :? InvalidOperationException -> None

    let rec private reorder (pick: JsonElement list -> JsonElement list) (element: JsonElement) : string =
        match element.ValueKind with
        | JsonValueKind.Object ->
            let members = [ for p in element.EnumerateObject() -> p ] |> List.rev
            "{" + String.Join(",", members |> List.map (fun p -> JsonSerializer.Serialize(p.Name) + ":" + reorder pick p.Value)) + "}"
        | JsonValueKind.Array ->
            "[" + String.Join(",", [ for item in element.EnumerateArray() -> item ] |> pick |> List.map (reorder pick)) + "]"
        | _ -> element.GetRawText()

    let private mutations (json: string) =
        use document = JsonDocument.Parse json
        let root = document.RootElement
        let rotate items = match items with [] -> [] | head :: rest -> rest @ [ head ]
        [ reorder List.rev root; reorder rotate root; reorder (rotate >> rotate) root ]

    let private ``Praxis agrees with the vendored Ordo binary on digest and verdict for every fixture and reordering`` () =
        let files = Directory.GetFiles(fixtureDir, "*.json") |> Array.map Path.GetFileName |> Array.filter ((<>) "expectations.json")
        holds(files.Length >= 19, "the copied Ordo corpus is incomplete")
        for file in files do
            let json = read file
            let praxis = admit json |> Result.toOption |> Option.map _.Digest
            holds((praxis = ordoBinary json), sprintf "%s: Praxis %A, Ordo 1.8.2 %A" file praxis (ordoBinary json))
            if praxis.IsSome then
                for variant in mutations json do
                    equal(praxis, admit variant |> Result.toOption |> Option.map _.Digest)
                    equal(praxis, ordoBinary variant)

    let private ``Praxis agrees with the vendored Ordo binary on systematically edited contracts`` () =
        let valid = read "valid-contract.json"
        let edits =
            [ "\"DEC-CONFIRM\",", "\"DEC-CONFIRM\", \"DEC-CONFIRM\","
              "\"kind\": \"cohort\"", "\"kind\": \"contract\""
              "\"kind\": \"verificationObligation\"", "\"kind\": \"invariant\""
              "\"kind\": \"deferral\"", "\"kind\": \"conflict\""
              "\"kind\": \"decision\"", "\"kind\": \"engineeringRationale\""
              "\"kind\": \"modeled\"", "\"kind\": \"rejected\", \"reason\": \"no\""
              "\"dependsOn\": []", "\"dependsOn\": [\"VERIFY-CONFIRM\"]"
              "\"DEF-OPERATIONS\"\n      ]", "\"DEF-OPERATIONS\", \"VERIFY-CONFIRM\"\n      ]"
              "\"requirements/operations.md#R-001\"\n      ],", "\"requirements/operations.md#R-001\", \"requirements/domain.md#R-001\"\n      ],"
              "\"justification\": \"domain transition must be authorized\"", "\"justification\": \" \""
              "\"location\": \"L13\"", "\"location\": \"L13\", \"extra\": 1"
              "\"ecir/1\"", "\"ECIR/1\""
              "\"L9\"", "\"L9\\u0000\\u001f\"" ]
        for (find, replacement) in edits do
            let edited = valid.Replace(find, replacement)
            holds(valid <> edited, "edit did not apply: " + find)
            let praxis = admit edited |> Result.toOption |> Option.map _.Digest
            holds((praxis = ordoBinary edited), sprintf "%s -> %s: Praxis %A, Ordo %A" find replacement praxis (ordoBinary edited))

    let private ``callers can narrow but never widen the supported contract versions`` () =
        let valid = read "valid-contract.json"
        match OrdoContractAdmission.admit [] valid with
        | Error diagnostics -> equal([ OrdoContractAdmission.UnsupportedSchemaCode, Some "ecir/1" ], codesOf diagnostics)
        | Ok _ -> failwith "an empty supported set admitted a contract"
        match OrdoContractAdmission.admit [ "ecir/1"; "ecir/2" ] (read "unsupported-version-contract.json") with
        | Error diagnostics -> equal([ OrdoContractAdmission.UnsupportedSchemaCode, Some "ecir/2" ], codesOf diagnostics)
        | Ok _ -> failwith "a caller widened the consumer beyond what it can interpret"

    let private ``a held digest detects that the Ordo contract has changed`` () =
        let current = expectAdmitted (read "valid-contract.json")
        let previous = expectAdmitted (read "previous-contract.json")
        equal(Ok current, OrdoContractAdmission.checkReference current.Digest current)
        match OrdoContractAdmission.checkReference previous.Digest current with
        | Error diagnostics ->
            equal(
                [ OrdoContractAdmission.StaleReferenceCode, Some(previous.Digest + "->" + current.Digest) ], codesOf diagnostics)
        | Ok _ -> failwith "a stale reference was admitted"
        for malformed in [ ""; "sha256:"; current.Digest.ToUpperInvariant(); current.Digest.Substring 7 ] do
            match OrdoContractAdmission.checkReference malformed current with
            | Error diagnostics -> equal(OrdoContractAdmission.InvalidReferenceDigestCode, diagnostics.Head.Code)
            | Ok _ -> failwithf "malformed pin %s admitted" malformed

    let private ``admission is deterministic and never throws on hostile input`` () =
        for hostile in [ ""; "null"; "[]"; "\"ecir/1\""; "{"; "{\"schemaVersion\":7}"; "{\"schemaVersion\":\"\\ud800\"}"
                         String('[', 10000) + String(']', 10000) ] do
            match admit hostile with
            | Error (first :: _) -> holds(first.Code.StartsWith("ORDO.CONTRACT.", StringComparison.Ordinal), first.Code)
            | other -> failwithf "%s: %A" hostile other
        let json = read "unicode-ordinal-contract.json"
        equal(admit json, admit json)

    let private ``praxis ordo contract check refuses before any agent work`` () =
        let root = CliHarness.temporaryDirectory "praxis-ordo-contract-"
        try
            for name in [ "valid-contract.json"; "dangling-dependency-contract.json"; "unsupported-version-contract.json" ] do
                File.Copy(Path.Combine(fixtureDir, name), Path.Combine(root, name))
            let digest = (expectAdmitted (read "valid-contract.json")).Digest
            let admitted = CliHarness.ros root [ "ordo"; "contract"; "check"; "--contract"; "valid-contract.json"; "--json" ]
            equal((0, ""), (admitted.Exit, admitted.Err))
            let body = JsonDocument.Parse(admitted.Out).RootElement
            equal(("admitted", digest), (body.GetProperty("status").GetString(), body.GetProperty("digest").GetString()))
            equal(false, body.GetProperty("executionAuthorized").GetBoolean())
            let pinned = CliHarness.ros root [ "ordo"; "contract"; "check"; "--contract"; "valid-contract.json"; "--expected-digest"; digest ]
            equal(0, pinned.Exit)
            let stale = "sha256:" + String('0', 64)
            let refused = CliHarness.ros root [ "ordo"; "contract"; "check"; "--contract"; "valid-contract.json"; "--expected-digest"; stale; "--json" ]
            equal(1, refused.Exit)
            holds(refused.Out.Contains(OrdoContractAdmission.StaleReferenceCode) && refused.Out.Contains(stale + "->" + digest), refused.Out)
            for name, code in [ "dangling-dependency-contract.json", "ORDO.CONTRACT.MISSING_DEPENDENCY"
                                "unsupported-version-contract.json", OrdoContractAdmission.UnsupportedSchemaCode
                                "absent.json", "ORDO.CONTRACT.MISSING_FILE" ] do
                let result = CliHarness.ros root [ "ordo"; "contract"; "check"; "--contract"; name ]
                equal(1, result.Exit)
                holds(result.Err.Contains code, result.Err)
            equal(2, (CliHarness.ros root [ "ordo"; "contract"; "check" ]).Exit)
            // Read-only: the check wrote nothing next to the contract.
            equal(3, (Directory.GetFileSystemEntries root).Length)
        finally
            CliHarness.removeDirectory root

    // -- REL-GATE-002: unresolved conflicts and requirement readiness ----------

    type private ReadinessCase =
        { Name: string
          Contract: string
          ExpectedDigest: string option
          RequirementKeys: string list
          AuthorizedDecisionIds: string list
          Outcome: string
          Scope: JsonElement option
          Diagnostics: (string * string option) list }

    let private readinessCases () =
        use document = JsonDocument.Parse(read "expectations.json")
        let optionalText (element: JsonElement) (name: string) =
            match element.TryGetProperty name with
            | true, value -> Option.ofObj (value.GetString())
            | _ -> None
        [ for case in document.RootElement.GetProperty("readinessCases").EnumerateArray() ->
            { Name = case.GetProperty("name").GetString() |> string
              Contract = case.GetProperty("contract").GetString() |> string
              ExpectedDigest = optionalText case "expectedDigest"
              RequirementKeys = stringsOf case "requirementKeys"
              AuthorizedDecisionIds = stringsOf case "authorizedDecisionIds"
              Outcome = case.GetProperty("outcome").GetString() |> string
              Scope =
                match case.TryGetProperty "scope" with
                | true, scope -> Some(scope.Clone())
                | _ -> None
              Diagnostics =
                match case.TryGetProperty "diagnostics" with
                | true, items ->
                    [ for item in items.EnumerateArray() -> item.GetProperty("code").GetString() |> string, optionalText item "subject" ]
                | _ -> [] } ]

    let private ``Praxis reproduces every Ordo golden readiness outcome exactly`` () =
        let all = readinessCases ()
        holds(all.Length >= 10, "the copied Ordo readiness corpus is incomplete")
        for case in all do
            let outcome =
                OrdoContractAdmission.assessReadiness
                    OrdoContractAdmission.implementedVersions case.ExpectedDigest (read case.Contract) case.RequirementKeys case.AuthorizedDecisionIds
            match case.Outcome, outcome with
            | "ready", Ok ready ->
                let scope = case.Scope |> Option.get
                let view = OrdoContractAdmission.readyScopeView ready
                equal(scope.GetProperty("contractSchemaVersion").GetString(), view.Contract.Version)
                equal(scope.GetProperty("contractDigest").GetString(), view.Contract.Digest)
                equal(stringsOf scope "requirementKeys", view.RequirementKeys)
                equal(stringsOf scope "requiredRequirementKeys", view.RequiredRequirementKeys)
                equal(stringsOf scope "requiredNodeIds", view.RequiredNodeIds)
                equal(stringsOf scope "requiredDecisionIds", view.RequiredDecisionIds)
                equal(stringsOf scope "requiredRationaleIds", view.RequiredRationaleIds)
                equal(stringsOf scope "authorizedDecisionIds", view.AuthorizedDecisionIds)
            | "refused", Error diagnostics ->
                holds((case.Diagnostics = codesOf diagnostics), sprintf "%s: expected %A got %A" case.Name case.Diagnostics (codesOf diagnostics))
            | expected, actual -> failwithf "%s: Ordo says %s, Praxis says %A" case.Name expected actual

    let private ``contradictory decisions with an open conflict are refused at admission`` () =
        match admit (read "contradictory-decisions-contract.json") with
        | Error diagnostics ->
            equal([ OrdoContractAdmission.UnresolvedConflictCode, Some "requirements/domain.md#R-001->CONFLICT-REOPEN" ], codesOf diagnostics)
        | Ok _ -> failwith "a modeled requirement linking an unresolved conflict was admitted"
        // The conflict rule is independent of member and array order.
        for variant in mutations (read "contradictory-decisions-contract.json") do
            holds(admit variant |> Result.isError, "a reordering admitted the contradictory contract")

    let private ``completion readiness executes only the requirements the contract does not exclude`` () =
        let assess contract required authorized =
            OrdoContractAdmission.assessCompletionReadiness OrdoContractAdmission.implementedVersions None (read contract) required authorized
            |> Result.map (OrdoContractAdmission.readyScopeView >> _.RequirementKeys)
            |> Result.mapError codesOf
        let both = [ "requirements/domain.md#R-001"; "requirements/operations.md#R-001" ]
        // The deferred requirement is accounted for, not executed; the modeled one needs its decision.
        equal(Ok [ "requirements/domain.md#R-001" ], assess "valid-contract.json" both [ "DEC-CONFIRM" ])
        equal(Error [ OrdoContractAdmission.MissingDecisionAuthorizationCode, Some "DEC-CONFIRM" ], assess "valid-contract.json" both [])
        // Completing only a deferred requirement executes nothing.
        equal(Error [ OrdoContractAdmission.EmptyScopeCode, None ], assess "valid-contract.json" [ "requirements/operations.md#R-001" ] [])
        // An unresolved requirement is executed, and blocks.
        equal(
            Error
                [ OrdoContractAdmission.BlockedRequirementCode, Some "requirements/domain.md#R-001"
                  OrdoContractAdmission.UnresolvedConflictCode, Some "requirements/domain.md#R-001->CONFLICT-CONFIRM" ],
            assess "unresolved-requirement-contract.json" both [])

    let private ``praxis ordo contract check reports readiness before any agent work`` () =
        let root = CliHarness.temporaryDirectory "praxis-ordo-readiness-"
        try
            for name in [ "valid-contract.json"; "unresolved-requirement-contract.json"; "contradictory-decisions-contract.json" ] do
                File.Copy(Path.Combine(fixtureDir, name), Path.Combine(root, name))
            let check contract extra = CliHarness.ros root ([ "ordo"; "contract"; "check"; "--contract"; contract; "--json" ] @ extra)
            let domain = [ "--requirement"; "requirements/domain.md#R-001" ]
            let ready = check "valid-contract.json" (domain @ [ "--authorized-decision"; "DEC-CONFIRM" ])
            equal((0, ""), (ready.Exit, ready.Err))
            let body = JsonDocument.Parse(ready.Out).RootElement
            equal("ready", body.GetProperty("status").GetString())
            equal([ "DEC-CONFIRM" ], stringsOf body "authorizedDecisionIds")
            equal(false, body.GetProperty("executionAuthorized").GetBoolean())
            for contract, extra, code in
                [ "valid-contract.json", domain, OrdoContractAdmission.MissingDecisionAuthorizationCode
                  "unresolved-requirement-contract.json", domain, OrdoContractAdmission.BlockedRequirementCode
                  "contradictory-decisions-contract.json", domain @ [ "--authorized-decision"; "DEC-ALLOW-REOPEN" ], OrdoContractAdmission.UnresolvedConflictCode
                  "contradictory-decisions-contract.json", [], OrdoContractAdmission.UnresolvedConflictCode ] do
                let refused = check contract extra
                equal(1, refused.Exit)
                holds(refused.Out.Contains code, refused.Out)
            // The unresolved contract is well formed: plain admission still succeeds.
            equal(0, (check "unresolved-requirement-contract.json" []).Exit)
            equal(2, (check "valid-contract.json" [ "--authorized-decision"; "DEC-CONFIRM" ]).Exit)
        finally
            CliHarness.removeDirectory root

    let tests: TestCase list =
        [ { Name = "ordo contract: Praxis reproduces every Ordo golden admission outcome exactly"; Run = fun () -> ``Praxis reproduces every Ordo golden admission outcome exactly`` () }
          { Name = "ordo contract: Praxis renders Ordo's published canonical form byte for byte"; Run = fun () -> ``Praxis renders Ordo's published canonical form byte for byte`` () }
          { Name = "ordo contract: Praxis agrees with the vendored Ordo binary on digest and verdict for every fixture and reordering"; Run = fun () -> ``Praxis agrees with the vendored Ordo binary on digest and verdict for every fixture and reordering`` () }
          { Name = "ordo contract: Praxis agrees with the vendored Ordo binary on systematically edited contracts"; Run = fun () -> ``Praxis agrees with the vendored Ordo binary on systematically edited contracts`` () }
          { Name = "ordo contract: callers can narrow but never widen the supported contract versions"; Run = fun () -> ``callers can narrow but never widen the supported contract versions`` () }
          { Name = "ordo contract: a held digest detects that the Ordo contract has changed"; Run = fun () -> ``a held digest detects that the Ordo contract has changed`` () }
          { Name = "ordo contract: admission is deterministic and never throws on hostile input"; Run = fun () -> ``admission is deterministic and never throws on hostile input`` () }
          { Name = "ordo contract: praxis ordo contract check refuses before any agent work"; Run = fun () -> ``praxis ordo contract check refuses before any agent work`` () }
          { Name = "ordo contract: Praxis reproduces every Ordo golden readiness outcome exactly"; Run = fun () -> ``Praxis reproduces every Ordo golden readiness outcome exactly`` () }
          { Name = "ordo contract: contradictory decisions with an open conflict are refused at admission"; Run = fun () -> ``contradictory decisions with an open conflict are refused at admission`` () }
          { Name = "ordo contract: completion readiness executes only the requirements the contract does not exclude"; Run = fun () -> ``completion readiness executes only the requirements the contract does not exclude`` () }
          { Name = "ordo contract: praxis ordo contract check reports readiness before any agent work"; Run = fun () -> ``praxis ordo contract check reports readiness before any agent work`` () } ]
