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
                    Assert.isTrue (not (File.Exists transaction)) "the transaction must be completed") }
          { Name = "concept and glossary kinds have documented roots, optional registries and their own statuses"
            Run = fun () ->
                let kind prefix = ArtifactKinds.configurations |> List.find (fun configuration -> configuration.IdentifierPrefix = prefix)
                let concept = kind "CN"
                let glossary = kind "GL"
                Assert.equal (ArtifactKind.Concept, "research/concepts", "registries/concepts.json", true) (concept.Kind, concept.SourceDirectory, concept.RegistryPath, concept.OptionalRegistry)
                Assert.equal (ArtifactKind.Glossary, "research/glossary", "registries/glossary.json", true) (glossary.Kind, glossary.SourceDirectory, glossary.RegistryPath, glossary.OptionalRegistry)
                Assert.equal (Some concept) (ArtifactKinds.tryFindByIdentifier "CN-ROS-2026-A1B2")
                Assert.equal (Some glossary) (ArtifactKinds.tryFindByIdentifier "GL-ROS-2026-0001") }
          { Name = "concept and glossary records are discovered, registered, checked for staleness and validated like other kinds"
            Run = fun () ->
                withFixture "valid-all-kinds" (fun fixture ->
                    let write (relative: string) (text: string) =
                        let path = Path.Combine(fixture, relative)
                        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                        File.WriteAllText(path, text)

                    let repository = FileArtifactRepository.create fixture

                    // With no records, neither optional registry is required.
                    match ArtifactOperations.checkRegistries repository with
                    | RegistryCheckOutcome.Completed findings -> Assert.empty findings
                    | outcome -> failwith $"Expected a clean check, received {outcome}"

                    write "research/concepts/CN-ROS-2026-A1B2--work-item.md" "---\nid: CN-ROS-2026-A1B2\ntitle: Work item\nstatus: accepted\nrelated_documents: [GL-ROS-2026-0001]\n---\n# Work item\n"
                    write "research/glossary/GL-ROS-2026-0001--checkpoint.md" "---\nid: GL-ROS-2026-0001\ntitle: Checkpoint\nstatus: draft\n---\n# Checkpoint\n"
                    Assert.empty (artifactFindings repository)

                    match ArtifactOperations.checkRegistries repository with
                    | RegistryCheckOutcome.Completed findings ->
                        Assert.equal [ "registries/concepts.json"; "registries/glossary.json" ] (findings |> List.map _.Path |> List.sort)
                    | outcome -> failwith $"Expected stale findings, received {outcome}"

                    match ArtifactOperations.buildRegistries false repository with
                    | RegistryBuildOutcome.Completed changes -> Assert.equal 2 changes.Length
                    | outcome -> failwith $"Expected a completed build, received {outcome}"

                    let concepts = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "registries", "concepts.json")))
                    Assert.equal "CN-ROS-2026-A1B2" (concepts.RootElement[0].GetProperty("id").GetString())
                    Assert.equal "research/concepts/CN-ROS-2026-A1B2--work-item.md" (concepts.RootElement[0].GetProperty("path").GetString())

                    match ArtifactOperations.checkRegistries repository with
                    | RegistryCheckOutcome.Completed findings -> Assert.empty findings
                    | outcome -> failwith $"Expected a current registry, received {outcome}"

                    write "research/glossary/GL-ROS-2026-0001--checkpoint.md" "---\nid: GL-ROS-2026-0001\ntitle: Durable checkpoint\nstatus: draft\n---\n"

                    match ArtifactOperations.checkRegistries repository with
                    | RegistryCheckOutcome.Completed findings -> Assert.equal [ "registries/glossary.json" ] (findings |> List.map _.Path)
                    | outcome -> failwith $"Expected a stale glossary registry, received {outcome}"

                    // Identifier, filename, status and reference rules apply.
                    write "research/concepts/CN-ROS-2026-A1B2--work-item.md" "---\nid: CN-ROS-2026-A1B2\ntitle: Work item\nstatus: canonical\nrelated_documents: [GL-ROS-2026-FFFF]\n---\n"
                    write "research/glossary/wrong-name.md" "---\nid: GL-ROS-2026-0002\ntitle: Misnamed\nstatus: accepted\n---\n"
                    write "research/glossary/GL-bad--term.md" "---\nid: GL-bad\ntitle: Bad\n---\n"

                    Assert.equal
                        [ "research/concepts/CN-ROS-2026-A1B2--work-item.md", "related_documents", "broken reference 'GL-ROS-2026-FFFF'"
                          "research/concepts/CN-ROS-2026-A1B2--work-item.md", "status", "'canonical' is not allowed for CN"
                          "research/glossary/GL-bad--term.md", "id", "invalid identifier 'GL-bad'"
                          "research/glossary/wrong-name.md", "id", "filename must start with 'GL-ROS-2026-0002--'" ]
                        (artifactFindings repository |> List.map (fun finding -> finding.Path, finding.Field, finding.Message))) }
          { Name = "concept and glossary records are subject to the provenance policy"
            Run = fun () ->
                withFixture "valid-all-kinds" (fun fixture ->
                    let path = Path.Combine(fixture, "research", "concepts", "CN-ROS-2026-A1B2--work-item.md")
                    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                    File.WriteAllText(path, "---\nid: CN-ROS-2026-A1B2\ntitle: Work item\nstatus: accepted\ncreated: 2026-10-02\n---\n")

                    let documents =
                        match (FileArtifactRepository.create fixture).Load() with
                        | Ok load -> load.Documents
                        | Error failure -> failwith failure.Message

                    let request: Ros.Domain.Provenance.ProvenanceValidationRequest =
                        { Policy = { Enforced = true; RequiredFrom = Some "2026-01-01"; RequireOriginator = Ros.Domain.Provenance.ProvenancePolicy.defaultRequireOriginator }
                          Documents = documents
                          Executions = Map.empty
                          KnownIdentifiers = Set.empty }

                    let findings = Ros.Domain.Provenance.ProvenanceValidation.findings request

                    Assert.isTrue
                        (findings |> List.exists (fun finding -> finding.Path = "research/concepts/CN-ROS-2026-A1B2--work-item.md" && finding.Field = "provenance"))
                        $"expected a provenance finding for the concept, got %A{findings}") }
          { Name = "the Python artifact oracle enumerates the same kinds, directories, registries and statuses as the F# model"
            Run = fun () ->
                let script =
                    "import json, sys; sys.path.insert(0, 'tools'); import ros_cli as c; "
                    + "print(json.dumps({'kinds': sorted([d, r, p, n in c.OPTIONAL_REGISTRY_KINDS] for n, (d, r, p) in c.KIND_CONFIG.items()), "
                    + "'statuses': {k: sorted(v) for k, v in c.ALLOWED_STATUS.items()}, 'pattern': c.ID_RE.pattern}))"

                let startInfo = Diagnostics.ProcessStartInfo("python3")
                startInfo.WorkingDirectory <- root ()
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                [ "-c"; script ] |> List.iter startInfo.ArgumentList.Add
                use child = Diagnostics.Process.Start startInfo
                let output = child.StandardOutput.ReadToEnd()
                child.WaitForExit()
                Assert.equal 0 child.ExitCode
                use oracle = JsonDocument.Parse output

                let oracleKinds =
                    oracle.RootElement.GetProperty("kinds").EnumerateArray()
                    |> Seq.map (fun entry -> entry[0].GetString(), entry[1].GetString(), entry[2].GetString(), entry[3].GetBoolean())
                    |> Seq.toList

                let modelKinds =
                    ArtifactKinds.configurations
                    |> List.map (fun configuration -> configuration.SourceDirectory, configuration.RegistryPath, configuration.IdentifierPrefix, configuration.OptionalRegistry)
                    |> List.sort

                Assert.equal modelKinds oracleKinds

                // Every prefix the identifier pattern admits is a governed kind, and vice versa.
                let pattern = oracle.RootElement.GetProperty("pattern").GetString()
                let start = pattern.IndexOf("(?:(", StringComparison.Ordinal) + 4
                let admitted = pattern.Substring(start, pattern.IndexOf(")", start, StringComparison.Ordinal) - start).Split('|') |> Set.ofArray
                Assert.equal (modelKinds |> List.map (fun (_, _, prefix, _) -> prefix) |> Set.ofList) admitted

                // Each governed prefix agrees on its allowed statuses (an absent entry means none are enforced).
                let oracleStatuses =
                    oracle.RootElement.GetProperty("statuses").EnumerateObject()
                    |> Seq.map (fun property -> property.Name, property.Value.EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq)
                    |> Map.ofSeq

                for _, _, prefix, _ in modelKinds do
                    let identifier = $"{prefix}-ROS-2026-0001"
                    let document status =
                        { RelativePath = $"x/{identifier}--x.md"
                          FileName = $"{identifier}--x.md"
                          Metadata = Map.ofList [ "id", ArtifactValue.Text identifier; "title", ArtifactValue.Text "x"; "status", ArtifactValue.Text status ] }

                    match oracleStatuses.TryFind prefix with
                    | Some statuses ->
                        for status in statuses do
                            Assert.empty (ArtifactPolicy.validate [] [ document status ])

                        Assert.isTrue (not (ArtifactPolicy.validate [] [ document "not-a-status" ]).IsEmpty) $"{prefix} accepted an unknown status"
                    | None -> Assert.empty (ArtifactPolicy.validate [] [ document "not-a-status" ]) } ]
