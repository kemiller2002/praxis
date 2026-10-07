namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Ros.Application.Artifacts
open Ros.Domain.Artifacts
open Ros.Infrastructure.Artifacts

[<RequireQualifiedAccess>]
module ArtifactTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "package.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "artifacts")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private root () = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))

    let private fixtureRoot name = Path.Combine(root (), "tests", "fixtures", "artifacts", name)

    let rec private copyDirectory source destination =
        Directory.CreateDirectory(destination) |> ignore

        for file in Directory.GetFiles source do
            File.Copy(file, Path.Combine(destination, Path.GetFileName file), true)

        for child in Directory.GetDirectories source do
            copyDirectory child (Path.Combine(destination, Path.GetFileName child))

    let private copyFixture name =
        let temporary = Path.Combine(Path.GetTempPath(), $"ros-fsharp-{Guid.NewGuid():N}")
        Directory.CreateDirectory temporary |> ignore
        let target = Path.Combine(temporary, name)
        copyDirectory (fixtureRoot name) target
        temporary, target

    let private withFixture name operation =
        let temporary, fixture = copyFixture name

        try
            operation fixture
        finally
            if Directory.Exists temporary then Directory.Delete(temporary, true)

    let private artifactFindings repository =
        match ArtifactOperations.validate repository with
        | ValidationOutcome.Completed findings -> findings
        | ValidationOutcome.DependencyFailure failure -> failwith $"Unexpected dependency failure: {failure.Message}"

    let private expectedFindings () =
        use document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root (), "tests", "fixtures", "artifacts", "manifest.json")))

        let case =
            document.RootElement.GetProperty("cases").EnumerateArray()
            |> Seq.find (fun item -> item.GetProperty("id").GetString() = "invalid-mixed")

        case.GetProperty("expectedFindings").EnumerateArray()
        |> Seq.map (fun item ->
            { Path = item.GetProperty("path").GetString()
              Field = item.GetProperty("field").GetString()
              Message = item.GetProperty("message").GetString() })
        |> Seq.toList

    let private sha256 file =
        use stream = File.OpenRead file
        SHA256.HashData(stream) |> Convert.ToHexString

    /// The frozen golden fixtures predate optional-registry kinds (RQ); an
    /// optional kind with no documents writes no registry file at all.
    let private registryNames =
        ArtifactKinds.configurations
        |> List.filter (fun configuration -> not configuration.OptionalRegistry)
        |> List.map (fun configuration -> Path.GetFileName configuration.RegistryPath)

    let private validDocument =
        { RelativePath = "research/evidence/EV-TEST-2026-A001--example.md"
          FileName = "EV-TEST-2026-A001--example.md"
          Metadata =
            Map.ofList
                [ "id", ArtifactValue.Text "EV-TEST-2026-A001"
                  "title", ArtifactValue.Text "Example"
                  "status", ArtifactValue.Text "accepted"
                  "evidence_type", ArtifactValue.Text "primary" ] }

    let private legacyResearchPackageDocuments =
        [ { RelativePath = "research/packages/RP-EDF-2026-002.md"
            FileName = "RP-EDF-2026-002.md"
            Metadata =
                Map.ofList
                    [ "id", ArtifactValue.Text "RP-EDF-2026-002"
                      "title", ArtifactValue.Text "Legacy EDF research package" ] }
          { RelativePath = "research/packages/REP-NHEA-2026-001.md"
            FileName = "REP-NHEA-2026-001.md"
            Metadata =
                Map.ofList
                    [ "id", ArtifactValue.Text "REP-NHEA-2026-001"
                      "title", ArtifactValue.Text "Legacy non-human evidence package" ] } ]

    let tests =
        [ { Name = "legacy research-package identifiers remain valid without widening other artifact kinds"
            Run = fun () ->
                Assert.equal true (ArtifactPolicy.isValidIdentifier "RP-EDF-2026-002")
                Assert.equal true (ArtifactPolicy.isValidIdentifier "REP-NHEA-2026-001")
                Assert.equal false (ArtifactPolicy.isValidIdentifier "EV-EDF-2026-001")
                ArtifactPolicy.validate [] legacyResearchPackageDocuments |> Assert.empty }
          { Name = "valid fixture passes typed artifact validation"
            Run = fun () ->
                withFixture "valid-all-kinds" (fun fixture ->
                    FileArtifactRepository.create fixture
                    |> artifactFindings
                    |> Assert.empty) }
          { Name = "invalid fixture preserves characterized finding identities"
            Run = fun () ->
                withFixture "invalid-mixed" (fun fixture ->
                    let actual = FileArtifactRepository.create fixture |> artifactFindings
                    Assert.equal (expectedFindings ()) actual) }
          { Name = "registry build preserves golden bytes and canonical inputs"
            Run = fun () ->
                withFixture "valid-all-kinds" (fun fixture ->
                    let canonicalFiles =
                        Directory.EnumerateFiles(Path.Combine(fixture, "research"), "*.md", SearchOption.AllDirectories)
                        |> Seq.map (fun file -> file, sha256 file)
                        |> Map.ofSeq

                    let registries = Path.Combine(fixture, "registries")
                    Directory.Delete(registries, true)

                    let repository = FileArtifactRepository.create fixture

                    let changes =
                        match ArtifactOperations.buildRegistries false repository with
                        | RegistryBuildOutcome.Completed completed -> completed
                        | outcome -> failwith $"Expected completed build, received {outcome}"

                    Assert.equal registryNames.Length changes.Length

                    for name in registryNames do
                        let expected = File.ReadAllText(Path.Combine(fixtureRoot "valid-all-kinds", "registries", name))
                        let actual = File.ReadAllText(Path.Combine(fixture, "registries", name))
                        Assert.equal expected actual

                    for KeyValue(file, before) in canonicalFiles do
                        Assert.equal before (sha256 file)

                    match ArtifactOperations.buildRegistries false repository with
                    | RegistryBuildOutcome.Completed completed -> Assert.equal 0 completed.Length
                    | outcome -> failwith $"Expected idempotent build, received {outcome}") }
          { Name = "registry check detects stale outputs and dry run does not write"
            Run = fun () ->
                withFixture "valid-all-kinds" (fun fixture ->
                    let registry = Path.Combine(fixture, "registries", "evidence.json")
                    File.WriteAllText(registry, "[]\n")
                    let repository = FileArtifactRepository.create fixture

                    match ArtifactOperations.checkRegistries repository with
                    | RegistryCheckOutcome.Completed findings ->
                        Assert.equal 1 findings.Length
                        Assert.equal "registries/evidence.json" findings[0].Path
                    | outcome -> failwith $"Expected stale finding, received {outcome}"

                    match ArtifactOperations.buildRegistries true repository with
                    | RegistryBuildOutcome.Completed changes -> Assert.equal 1 changes.Length
                    | outcome -> failwith $"Expected dry-run completion, received {outcome}"

                    Assert.equal "[]\n" (File.ReadAllText registry)) }
          { Name = "front-matter parser rejects missing close and preserves inline list values"
            Run = fun () ->
                match FrontMatter.parse "bad.md" "---\nid: EV-TEST-2026-A001" with
                | Error message -> Assert.equal "missing closing '---'" message
                | Ok _ -> failwith "Expected missing-closing failure"

                match FrontMatter.parse "list.md" "---\nid: EV-TEST-2026-A001\ntitle: Example\ntags: [one, 'two words']\n---\n" with
                | Error message -> failwith $"Unexpected parser error: {message}"
                | Ok document ->
                    Assert.equal
                        (Some(ArtifactValue.Sequence [ ArtifactValue.Text "one"; ArtifactValue.Text "two words" ]))
                        (document.Metadata |> Map.tryFind "tags") }
          { Name = "partial registry write reports an indeterminate incomplete outcome"
            Run = fun () ->
                let writes = ResizeArray<string>()

                let repository =
                    { Load =
                        fun () ->
                            Ok
                                { Documents = [ validDocument ]
                                  ParseFindings = [] }
                      ReadRegistry = fun _ -> Ok None
                      WriteRegistry =
                        fun path _ ->
                            if writes.Count = 0 then
                                writes.Add path
                                Ok()
                            else
                                Error
                                    { Operation = "write registry"
                                      Path = Some path
                                      Message = "injected failure"
                                      Outcome = DependencyOutcome.Indeterminate }
                      AcquireRegistryWriteLease =
                        fun () ->
                            Ok
                                { Recover = fun () -> Ok()
                                  Prepare = fun _ -> Ok()
                                  Complete = fun () -> Ok()
                                  Release = fun () -> Ok() } }

                match ArtifactOperations.buildRegistries false repository with
                | RegistryBuildOutcome.Incomplete(written, pending, failure) ->
                    Assert.equal 1 written.Length
                    Assert.equal 7 pending.Length
                    Assert.equal DependencyOutcome.Indeterminate failure.Outcome
                | outcome -> failwith $"Expected incomplete outcome, received {outcome}" } ]
