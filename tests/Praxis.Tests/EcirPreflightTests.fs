namespace Praxis.Tests

open System
open System.IO
open Praxis.Infrastructure.Planning

[<RequireQualifiedAccess>]
module EcirPreflightTests =
    open PraxisCli

    let private t name run = { Name = "ECIR committed input: " + name; Run = run }

    let private digest = "sha256:" + String('a', 64)
    let private manifest = sprintf """{"digest":"%s","requirements":[{"key":"docs/R-1","originalId":"R-1"}]}""" digest
    let private blueprint = sprintf """{"schemaVersion":"ecir/1","sourceManifestDigest":"%s","requirements":[]}""" digest

    let private withDocuments callback =
        let parent = GitFixture.temporaryDirectory "ecir-preflight"
        try
            let _, clone = installedRepository parent "clone"
            Directory.CreateDirectory(Path.Combine(clone, "ecir")) |> ignore
            File.WriteAllText(Path.Combine(clone, "ecir", "manifest.json"), manifest)
            File.WriteAllText(Path.Combine(clone, "ecir", "blueprint.json"), blueprint)
            pushAll clone "commit ECIR source and proposal" |> ignore
            let commit = GitFixture.git clone [ "rev-parse"; "HEAD" ] |> fun s -> s.Trim()
            callback clone
                { Commit = commit
                  ManifestPath = "ecir/manifest.json"
                  BlueprintPath = "ecir/blueprint.json"
                  SourceManifestDigest = digest }
        finally
            GitFixture.cleanup parent


    let private validatorOutput digest imported represented modeled unresolved deferred authorization =
        sprintf """{"schemaVersion":"ecir.validate/1","status":"trace-validated","blueprintDigest":"%s","sourceRequirements":%d,"representedRequirements":%d,"modeledRequirements":%d,"unresolvedRequirements":%d,"deferredRequirements":%d,"executionAuthorized":%s}"""
            digest imported represented modeled unresolved deferred authorization

    let tests =
        [ t "both inputs come from the same immutable Git commit rather than working tree" (fun () ->
              withDocuments (fun clone reference ->
                  let observed = FileEcirPreflight.readCommitted clone reference
                  match observed with
                  | Error problem -> failwith problem
                  | Ok pair ->
                      Assert.equal manifest pair.Manifest
                      Assert.equal blueprint pair.Blueprint
                      Assert.equal reference.Commit pair.Commit

                  File.WriteAllText(Path.Combine(clone, "ecir", "manifest.json"), """{"digest":"tampered"}""")
                  match FileEcirPreflight.readCommitted clone reference with
                  | Error problem -> failwith problem
                  | Ok pair -> Assert.equal manifest pair.Manifest))

          t "short revision, wrong externally pinned source digest and traversal all fail closed" (fun () ->
              withDocuments (fun clone reference ->
                  for change in [
                      { reference with Commit = "HEAD" }
                      { reference with Commit = "0123" }
                      { reference with ManifestPath = "../manifest.json" }
                      { reference with BlueprintPath = "/tmp/blueprint.json" }
                      { reference with BlueprintPath = reference.ManifestPath }
                      { reference with SourceManifestDigest = "sha256:" + String('b', 64) } ] do
                      Assert.isTrue (FileEcirPreflight.readCommitted clone change |> Result.isError)
                          ("invalid ECIR input was accepted: " + change.ManifestPath)))

          t "host-pinned Ordo response must preserve source cardinality and explicitly refuse authority" (fun () ->
              let expected = "sha256:" + String('b', 64)
              let pinned = "sha256:" + String('c', 64)
              let valid = validatorOutput expected 10 10 8 2 0 "false"
              match FileEcirValidator.parseResponse expected pinned valid with
              | Error problem -> failwith problem
              | Ok observation ->
                  Assert.equal 10 observation.SourceRequirements
                  Assert.equal 2 observation.UnresolvedRequirements
                  Assert.equal pinned observation.ValidatedByExecutableSha256

              for corrupted in [
                  validatorOutput expected 10 9 8 2 0 "false"
                  validatorOutput expected 10 10 8 2 0 "true"
                  validatorOutput "sha256:changed" 10 10 8 2 0 "false"
                  valid.Replace("trace-validated", "approved")
                  valid.Replace("ecir.validate/1", "ecir.validate/2")
                  "{broken}" ] do
                  Assert.isTrue
                      (FileEcirValidator.parseResponse expected pinned corrupted |> Result.isError)
                      "unsafe Ordo output was accepted")

          t "derive a cohort's actual decision and requirement scope from Ordo-reviewed blueprint" (fun () ->
              let complete =
                  """{"schemaVersion":"ecir/1","sourceManifestDigest":"sha256:manifest","requirements":[{"source":{"key":"docs/x.md#R1"},"disposition":{"kind":"modeled"}},{"source":{"key":"docs/x.md#R2"},"disposition":{"kind":"modeled"}}],"nodes":[{"id":"COHORT-A","kind":"cohort","requirementKeys":["docs/x.md#R1","docs/x.md#R2"]},{"id":"DEC-A","kind":"decision","requirementKeys":["docs/x.md#R1"]},{"id":"DEC-B","kind":"decision","requirementKeys":["docs/x.md#R2"]},{"id":"DEC-OTHER","kind":"decision","requirementKeys":["docs/z.md#Z1"]}]}"""
              let docs =
                  { Commit = String('c', 40)
                    Manifest = manifest
                    Blueprint = complete
                    SourceManifestDigest = digest }
              let observation: EcirValidatorResult =
                  { BlueprintDigest = "sha256:" + String('b', 64)
                    SourceRequirements = 2
                    RepresentedRequirements = 2
                    ModeledRequirements = 2
                    UnresolvedRequirements = 0
                    DeferredRequirements = 0
                    ValidatedByExecutableSha256 = digest }

              match FileEcirAuthorization.deriveScope docs observation "GROUP-A" "COHORT-A" [ "docs/x.md#R2"; "docs/x.md#R1" ] with
              | Error message -> failwith message
              | Ok scope ->
                  Assert.equal "GROUP-A" scope.GroupId
                  Assert.equal docs.Commit scope.SourceCommit
                  Assert.equal 2 scope.RequirementKeys.Length
                  Assert.equal [ "DEC-A"; "DEC-B" ] (scope.DecisionIds |> List.sort)

              for invalid in [
                  [ "docs/x.md#R1" ]
                  [ "docs/x.md#R1"; "docs/x.md#R1" ]
                  [ "docs/x.md#R1"; "docs/x.md#R2"; "extra" ] ] do
                  Assert.isTrue
                      (FileEcirAuthorization.deriveScope docs observation "GROUP-A" "COHORT-A" invalid |> Result.isError)
                      "invalid group membership was accepted"

              let blocked =
                  { docs with Blueprint = complete.Replace("\"kind\":\"modeled\"","\"kind\":\"unresolved\"") }
              Assert.isTrue
                  (FileEcirAuthorization.deriveScope blocked observation "GROUP-A" "COHORT-A" [ "docs/x.md#R1"; "docs/x.md#R2" ] |> Result.isError)
                  "unresolved cohort was declared executable"
              Assert.isTrue
                  (FileEcirAuthorization.deriveScope docs observation "GROUP-A" "UNKNOWN" [ "docs/x.md#R1"; "docs/x.md#R2" ] |> Result.isError)
                  "unknown cohort was accepted")

          t "nonexistent committed files and fake Git SHA do not turn into approvals" (fun () ->
              withDocuments (fun clone reference ->
                  Assert.isTrue
                      (FileEcirPreflight.readCommitted clone { reference with BlueprintPath = "ecir/missing.json" } |> Result.isError)
                      "missing committed blueprint must fail"
                  Assert.isTrue
                      (FileEcirPreflight.readCommitted clone { reference with Commit = String('f', 40) } |> Result.isError)
                      "unresolvable commit SHA must fail")) ]
