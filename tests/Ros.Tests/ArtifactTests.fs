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
        if File.Exists(Path.Combine(directory.FullName, "release.json"))
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

    /// Runs `operation` against an empty temporary repository holding the
    /// given `(relativePath, content)` files, and always removes it.
    let private withRepository (files: (string * string) list) operation =
        let temporary = Path.Combine(Path.GetTempPath(), $"ros-fsharp-{Guid.NewGuid():N}")

        try
            files
            |> List.iter (fun (relativePath, content) ->
                let file = Path.Combine(temporary, relativePath)
                Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                File.WriteAllText(file, content))

            operation temporary
        finally
            if Directory.Exists temporary then Directory.Delete(temporary, true)

    let private artifactText (fields: (string * string) list) =
        let lines = fields |> List.map (fun (field, value) -> $"{field}: {value}") |> String.concat "\n"
        $"---\n{lines}\n---\n\n# Body\n"

    let private conceptPath = "research/concepts/CN-TEST-2026-A001--registry-projection.md"
    let private glossaryPath = "research/glossary/GL-TEST-2026-A002--canonical-record.md"

    let private conceptAndGlossary =
        [ conceptPath,
          artifactText
              [ "id", "CN-TEST-2026-A001"
                "title", "Registry projection"
                "status", "accepted"
                "related_documents", "[GL-TEST-2026-A002]" ]
          glossaryPath,
          artifactText [ "id", "GL-TEST-2026-A002"; "title", "Canonical record"; "status", "draft" ] ]

    let private configurationFor prefix =
        ArtifactKinds.configurations |> List.find (fun configuration -> configuration.IdentifierPrefix = prefix)

    let private completedBuild repository =
        match ArtifactOperations.buildRegistries false repository with
        | RegistryBuildOutcome.Completed changes -> changes
        | outcome -> failwith $"Expected completed build, received {outcome}"

    let private registryCheck repository =
        match ArtifactOperations.checkRegistries repository with
        | RegistryCheckOutcome.Completed findings -> findings
        | outcome -> failwith $"Expected completed check, received {outcome}"

    let private findingsAt path (findings: ArtifactFinding list) =
        findings |> List.filter (fun finding -> finding.Path = path) |> List.map (fun finding -> finding.Field, finding.Message)

    let private starterManifestDestinations profile =
        use document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root (), "starter", profile, "manifest.json")))

        document.RootElement.GetProperty("files").EnumerateArray()
        |> Seq.map (fun entry -> entry.GetProperty("destination").GetString())
        |> Seq.toList

    let tests =
        [ { Name = "legacy research-package identifiers remain valid without widening other artifact kinds"
            Run = fun () ->
                Assert.equal true (ArtifactPolicy.isValidIdentifier "RP-EDF-2026-002")
                Assert.equal true (ArtifactPolicy.isValidIdentifier "REP-NHEA-2026-001")
                Assert.equal false (ArtifactPolicy.isValidIdentifier "EV-EDF-2026-001")
                ArtifactPolicy.validate [] legacyResearchPackageDocuments |> Assert.empty }
          { Name = "artifact identifiers with a trailing newline are invalid, and do not qualify for the legacy file-name rule"
            Run = fun () ->
                Assert.equal true (ArtifactPolicy.isValidIdentifier "EV-TEST-2026-A001")
                Assert.equal false (ArtifactPolicy.isValidIdentifier "EV-TEST-2026-A001\n")
                Assert.equal false (ArtifactPolicy.isValidIdentifier "RP-EDF-2026-002\n")

                let lineBroken =
                    { RelativePath = "research/packages/RP-EDF-2026-002\n.md"
                      FileName = "RP-EDF-2026-002\n.md"
                      Metadata =
                        Map.ofList
                            [ "id", ArtifactValue.Text "RP-EDF-2026-002\n"
                              "title", ArtifactValue.Text "Legacy EDF research package" ] }

                let messages = ArtifactPolicy.validate [] [ lineBroken ] |> List.map (fun finding -> finding.Message)
                Assert.isTrue (messages |> List.contains "invalid identifier 'RP-EDF-2026-002\n'") "expected the identifier finding"
                Assert.isTrue (messages |> List.contains "filename must start with 'RP-EDF-2026-002\n--'") "the legacy name rule must not apply" }
          { Name = "concept and glossary are artifact kinds with their own root directory and optional registry"
            Run = fun () ->
                let concept = configurationFor "CN"
                let glossary = configurationFor "GL"
                Assert.equal (ArtifactKind.Concept, "research/concepts", "registries/concepts.json", true)
                    (concept.Kind, concept.SourceDirectory, concept.RegistryPath, concept.OptionalRegistry)
                Assert.equal (ArtifactKind.Glossary, "research/glossary", "registries/glossary.json", true)
                    (glossary.Kind, glossary.SourceDirectory, glossary.RegistryPath, glossary.OptionalRegistry)
                Assert.equal (Some ArtifactKind.Concept) (ArtifactKinds.tryFindByIdentifier "CN-TEST-2026-A001" |> Option.map _.Kind)
                Assert.equal (Some ArtifactKind.Glossary) (ArtifactKinds.tryFindByIdentifier "GL-TEST-2026-A002" |> Option.map _.Kind)

                let distinct project = ArtifactKinds.configurations |> List.map project |> List.distinct |> List.length
                let count = ArtifactKinds.configurations.Length
                Assert.equal (count, count, count) (distinct _.IdentifierPrefix, distinct _.SourceDirectory, distinct _.RegistryPath) }
          { Name = "concept and glossary records are discovered, validated and projected into their registries"
            Run = fun () ->
                withRepository conceptAndGlossary (fun repository ->
                    let artifacts = FileArtifactRepository.create repository
                    artifactFindings artifacts |> Assert.empty

                    let written = completedBuild artifacts |> List.map _.Path |> List.sort
                    Assert.equal [ "registries/concepts.json"; "registries/glossary.json" ] (written |> List.filter (fun path -> path.Contains "concepts" || path.Contains "glossary"))

                    let registry name = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "registries", name)))
                    use concepts = registry "concepts.json"
                    use glossary = registry "glossary.json"

                    let entries (document: JsonDocument) =
                        document.RootElement.EnumerateArray()
                        |> Seq.map (fun entry -> entry.GetProperty("id").GetString(), entry.GetProperty("path").GetString())
                        |> Seq.toList

                    Assert.equal [ "CN-TEST-2026-A001", conceptPath ] (entries concepts)
                    Assert.equal [ "GL-TEST-2026-A002", glossaryPath ] (entries glossary)
                    Assert.equal 0 (completedBuild artifacts).Length
                    registryCheck artifacts |> Assert.empty) }
          { Name = "registry check reports a stale or missing concept or glossary registry once records exist"
            Run = fun () ->
                withRepository conceptAndGlossary (fun repository ->
                    let artifacts = FileArtifactRepository.create repository
                    completedBuild artifacts |> ignore
                    File.WriteAllText(Path.Combine(repository, "registries", "concepts.json"), "[]\n")
                    File.Delete(Path.Combine(repository, "registries", "glossary.json"))

                    let stale = registryCheck artifacts |> List.map (fun finding -> finding.Path, finding.Message)

                    Assert.equal
                        [ "registries/concepts.json", "registry is stale; run 'praxis registry build'"
                          "registries/glossary.json", "registry is stale; run 'praxis registry build'" ]
                        stale) }
          { Name = "a repository without concept or glossary records needs no concept or glossary registry"
            Run = fun () ->
                withRepository [ validDocument.RelativePath, artifactText [ "id", "EV-TEST-2026-A001"; "title", "Example"; "status", "accepted" ] ] (fun repository ->
                    let artifacts = FileArtifactRepository.create repository
                    let written = completedBuild artifacts |> List.map _.Path
                    Assert.isTrue (not (written |> List.exists (fun path -> path.Contains "concepts" || path.Contains "glossary"))) $"unexpected registries: {written}"
                    registryCheck artifacts |> Assert.empty) }
          { Name = "concept and glossary records get the identifier, filename, status, reference and supersession checks"
            Run = fun () ->
                let files =
                    [ "research/concepts/CN-TEST-2026-B001--bad-status.md",
                      artifactText [ "id", "CN-TEST-2026-B001"; "title", "Bad status"; "status", "established"; "supports", "[GL-TEST-2026-FFFF]" ]
                      "research/concepts/wrong-name.md", artifactText [ "id", "CN-TEST-2026-B002"; "title", "Wrong name" ]
                      "research/glossary/GL-TEST-2026-B003--one.md", artifactText [ "id", "GL-TEST-2026-B003"; "title", "One"; "status", "withdrawn" ]
                      "research/glossary/GL-TEST-2026-B003--two.md", artifactText [ "id", "GL-TEST-2026-B003"; "title", "Two" ]
                      "research/glossary/GL-bad--term.md", artifactText [ "id", "GL-bad"; "title", "Bad identifier" ]
                      "research/glossary/GL-TEST-2026-B004--no-title.md", artifactText [ "id", "GL-TEST-2026-B004"; "status", "review" ]
                      "research/glossary/GL-TEST-2026-B005--newer.md",
                      artifactText [ "id", "GL-TEST-2026-B005"; "title", "Newer"; "status", "accepted"; "supersedes", "[GL-TEST-2026-B006]" ]
                      "research/glossary/GL-TEST-2026-B006--older.md",
                      artifactText [ "id", "GL-TEST-2026-B006"; "title", "Older"; "status", "superseded" ] ]

                withRepository files (fun repository ->
                    let findings = FileArtifactRepository.create repository |> artifactFindings
                    let duplicate = "duplicate 'GL-TEST-2026-B003' also in research/glossary/GL-TEST-2026-B003--one.md, research/glossary/GL-TEST-2026-B003--two.md"

                    Assert.equal
                        [ "status", "'established' is not allowed for CN"; "supports", "broken reference 'GL-TEST-2026-FFFF'" ]
                        (findingsAt "research/concepts/CN-TEST-2026-B001--bad-status.md" findings)
                    Assert.equal [ "id", "filename must start with 'CN-TEST-2026-B002--'" ] (findingsAt "research/concepts/wrong-name.md" findings)
                    Assert.equal [ "id", duplicate ] (findingsAt "research/glossary/GL-TEST-2026-B003--one.md" findings)
                    Assert.equal [ "id", duplicate ] (findingsAt "research/glossary/GL-TEST-2026-B003--two.md" findings)
                    Assert.equal [ "id", "invalid identifier 'GL-bad'" ] (findingsAt "research/glossary/GL-bad--term.md" findings)
                    Assert.equal [ "title", "required field is missing" ] (findingsAt "research/glossary/GL-TEST-2026-B004--no-title.md" findings)
                    Assert.equal [ "supersedes", "'GL-TEST-2026-B006' is not reciprocal" ] (findingsAt "research/glossary/GL-TEST-2026-B005--newer.md" findings)
                    Assert.empty (findingsAt "research/glossary/GL-TEST-2026-B006--older.md" findings)) }
          { Name = "concept and glossary statuses follow the research artifact lifecycle"
            Run = fun () ->
                let lifecycle = [ "draft"; "review"; "accepted"; "superseded"; "withdrawn" ]

                let statusFindings prefix status =
                    { RelativePath = $"research/x/{prefix}-TEST-2026-A001--x.md"
                      FileName = $"{prefix}-TEST-2026-A001--x.md"
                      Metadata =
                        Map.ofList
                            [ "id", ArtifactValue.Text $"{prefix}-TEST-2026-A001"
                              "title", ArtifactValue.Text "X"
                              "status", ArtifactValue.Text status ] }
                    |> List.singleton
                    |> ArtifactPolicy.validate []

                [ "CN"; "GL" ]
                |> List.iter (fun prefix ->
                    lifecycle |> List.iter (fun status -> statusFindings prefix status |> Assert.empty)

                    Assert.equal
                        [ $"'proposed' is not allowed for {prefix}" ]
                        (statusFindings prefix "proposed" |> List.map _.Message)) }
          { Name = "starter scaffolds agree with the artifact kinds they enumerate"
            Run = fun () ->
                let mandatoryRegistries =
                    ArtifactKinds.configurations
                    |> List.filter (fun configuration -> not configuration.OptionalRegistry)
                    |> List.map _.RegistryPath
                    |> Set.ofList

                let kindDirectories = ArtifactKinds.configurations |> List.map _.SourceDirectory |> Set.ofList

                [ "greenfield"; "project-administration" ]
                |> List.iter (fun profile ->
                    let destinations = starterManifestDestinations profile

                    let registries =
                        destinations
                        |> List.filter (fun destination -> destination.StartsWith("registries/", StringComparison.Ordinal))
                        |> Set.ofList

                    Assert.equal mandatoryRegistries registries

                    let scaffolded =
                        destinations
                        |> List.filter (fun destination -> destination.StartsWith("research/", StringComparison.Ordinal) && destination.EndsWith("/.gitkeep", StringComparison.Ordinal))
                        |> List.map (fun destination -> destination[.. destination.Length - "/.gitkeep".Length - 1])
                        |> Set.ofList

                    Assert.isTrue (Set.isSubset scaffolded kindDirectories) $"{profile} scaffolds non-kind directories: {Set.difference scaffolded kindDirectories}")

                let starterRegistryFiles =
                    Directory.EnumerateFiles(Path.Combine(root (), "starter", "greenfield", "registries"), "*.json")
                    |> Seq.map (fun file -> $"registries/{Path.GetFileName file}")
                    |> Set.ofSeq

                Assert.equal mandatoryRegistries starterRegistryFiles }
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
                | outcome -> failwith $"Expected incomplete outcome, received {outcome}" }
          { Name = "frozen valid fixture has the preregistered registry bytes"
            Run = fun () ->
                use document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root (), "tests", "fixtures", "artifacts", "manifest.json")))

                let case =
                    document.RootElement.GetProperty("cases").EnumerateArray()
                    |> Seq.find (fun item -> item.GetProperty("id").GetString() = "valid-all-kinds")

                let expected = case.GetProperty("registrySha256").EnumerateObject() |> Seq.toList
                Assert.isTrue (not expected.IsEmpty) "the manifest must preregister registry hashes"

                for registry in expected do
                    let actual = sha256 (Path.Combine(fixtureRoot "valid-all-kinds", "registries", registry.Name))
                    Assert.equal (registry.Value.GetString().ToUpperInvariant()) actual }
          { Name = "registry build reclaims a stale lease left by a dead owner in the on-disk lock format"
            Run = fun () ->
                withFixture "valid-all-kinds" (fun fixture ->
                    let resource = "artifact-registries"
                    let hash = SHA256.HashData(Text.Encoding.UTF8.GetBytes resource) |> Convert.ToHexString
                    let lockPath = Path.Combine(fixture, ".ros", "locks", $"{hash.ToLowerInvariant()}.lock")
                    Directory.CreateDirectory(Path.GetDirectoryName lockPath) |> ignore

                    File.WriteAllText(
                        lockPath,
                        """{"pid":999999,"ownerToken":"fsharp-stale-owner","resource":"artifact-registries","acquiredAt":"2026-09-08T00:00:00.0000000Z"}""" + "\n"
                    )

                    File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddSeconds -61.0)

                    match ArtifactOperations.buildRegistries true (FileArtifactRepository.create fixture) with
                    | RegistryBuildOutcome.Completed changes -> Assert.equal 0 changes.Length
                    | outcome -> failwith $"Expected the stale lease to be reclaimed, received {outcome}"

                    Assert.isTrue (not (File.Exists lockPath)) "the stale lock must be removed") }
          { Name = "registry build replays a pending registry transaction before building"
            Run = fun () ->
                withFixture "valid-all-kinds" (fun fixture ->
                    let registryPath = "registries/evidence.json"
                    let registry = Path.Combine(fixture, registryPath)
                    let expected = File.ReadAllText registry
                    let transaction = Path.Combine(fixture, ".ros", "transactions", "artifact-registries.json")
                    Directory.CreateDirectory(Path.GetDirectoryName transaction) |> ignore

                    let pending =
                        Nodes.JsonObject(
                            [ Collections.Generic.KeyValuePair<string, Nodes.JsonNode>("schemaVersion", Nodes.JsonValue.Create "1.0.0")
                              Collections.Generic.KeyValuePair<string, Nodes.JsonNode>("resource", Nodes.JsonValue.Create "artifact-registries")
                              Collections.Generic.KeyValuePair<string, Nodes.JsonNode>(
                                  "writes",
                                  Nodes.JsonArray(
                                      Nodes.JsonObject(
                                          [ Collections.Generic.KeyValuePair<string, Nodes.JsonNode>("path", Nodes.JsonValue.Create registryPath)
                                            Collections.Generic.KeyValuePair<string, Nodes.JsonNode>("content", Nodes.JsonValue.Create expected) ]
                                      )
                                  )
                              ) ]
                        )

                    File.WriteAllText(transaction, pending.ToJsonString() + "\n")
                    File.WriteAllText(registry, "[]\n")

                    match ArtifactOperations.buildRegistries false (FileArtifactRepository.create fixture) with
                    | RegistryBuildOutcome.Completed changes -> Assert.equal 0 changes.Length
                    | outcome -> failwith $"Expected the pending transaction to be replayed, received {outcome}"

                    Assert.equal expected (File.ReadAllText registry)
                    Assert.isTrue (not (File.Exists transaction)) "the transaction must be completed") } ]
