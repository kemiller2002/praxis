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

          t "nonexistent committed files and fake Git SHA do not turn into approvals" (fun () ->
              withDocuments (fun clone reference ->
                  Assert.isTrue (FileEcirPreflight.readCommitted clone { reference with BlueprintPath = "ecir/missing.json" } |> Result.isError)
                  Assert.isTrue (FileEcirPreflight.readCommitted clone { reference with Commit = String('f', 40) } |> Result.isError))) ]
