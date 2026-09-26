namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Artifacts
open Ros.Domain.Provenance
open Ros.Infrastructure.Artifacts

/// The provenance interchange record (RQ-ROS-2026-A013..A015): the shared
/// conformance fixtures under `schemas/conformance/provenance-record/` that
/// every Echelon system's codec is tested against, lossless round trips,
/// destructive-transformation detection, version handling, and the
/// cross-system end-to-end chain.
[<RequireQualifiedAccess>]
module ProvenanceRecordTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "package.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "schemas", "conformance", "provenance-record")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private conformance () =
        Path.Combine(repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory())), "schemas", "conformance", "provenance-record")

    let private load (relative: string) =
        JsonNode.Parse(File.ReadAllText(Path.Combine(conformance (), relative))).AsObject()

    let private manifest () = load "manifest.json"

    let private text (node: JsonNode) = node.GetValue<string>()

    let private status (node: JsonObject) =
        match ProvenanceRecordJson.validate node with
        | Ok(ProvenanceRecordReading.Current _) -> "valid"
        | Ok(ProvenanceRecordReading.Unversioned _) -> "valid-unversioned"
        | Ok(ProvenanceRecordReading.Unsupported _) -> "unsupported-version"
        | Error _ -> "invalid"

    let private recordOf (node: JsonObject) =
        match ProvenanceRecordJson.validate node with
        | Ok(ProvenanceRecordReading.Current(record, _))
        | Ok(ProvenanceRecordReading.Unversioned(record, _)) -> record
        | Ok(ProvenanceRecordReading.Unsupported(version, _)) -> failwith $"unexpected unsupported version {version}"
        | Error problems -> failwith $"unexpected problems: {problems}"

    let private agent id provider model runtime =
        { Kind = ActorKind.Agent
          Id = id
          Provider = Some provider
          Model = Some model
          Runtime = Some runtime }

    let private human id =
        { Kind = ActorKind.Human
          Id = id
          Provider = None
          Model = None
          Runtime = None }

    let private gemini = agent "google/gemini-cli" "google" "gemini-2.5-pro" "gemini-cli"
    let private claude = agent "anthropic/claude-code" "anthropic" "unknown" "claude-code"

    let private contribution key operations at actor =
        { Key = key
          Operations = operations
          At = at
          Last = None
          Actor = actor
          Reason = None
          Evidence = [] }

    let private ok result =
        match result with
        | Ok value -> value
        | Error problems -> failwith $"unexpected error: {problems}"

    let tests =
        [ { Name = "interchange record: every shared conformance case reads as its manifest expects"
            Run =
              fun () ->
                  let index = manifest ()
                  let cases = index["cases"].AsArray() |> Seq.map (fun item -> item.AsObject()) |> Seq.toList
                  Assert.isTrue (cases.Length >= 25) "the conformance suite lost cases"

                  for case in cases do
                      let file = text case["file"]
                      let expected = text case["expect"]
                      let why = text case["why"]
                      let actual = status (load file)
                      Assert.isTrue (actual = expected) $"{file}: expected {expected} but read {actual} ({why})" }

          { Name = "interchange record: successor pairs separate preserving from destructive transformations"
            Run =
              fun () ->
                  let index = manifest ()

                  for pair in index["successors"].AsArray() |> Seq.map (fun item -> item.AsObject()) do
                      let name = text pair["before"]
                      let why = text pair["why"]
                      let problems = ProvenanceRecordJson.successorProblems (load name) (load (text pair["after"]))

                      match text pair["expect"] with
                      | "preserved" -> Assert.isTrue problems.IsEmpty $"{name}: {problems}"
                      | _ -> Assert.isTrue (not problems.IsEmpty) $"{name}: destructive change went undetected ({why})" }

          { Name = "interchange record: appending preserves unknown fields and every other contributor verbatim"
            Run =
              fun () ->
                  let original = load "valid/unknown-fields-preserved.json"
                  let next = contribution "EXE-aegis.gh-run-77" [ ContributionOperation.Reviewed ] "2026-09-26T11:00:00.000Z" gemini
                  let appended = ProvenanceRecordJson.append next original |> ok

                  Assert.equal [] (ProvenanceRecordJson.successorProblems original appended)
                  Assert.isTrue (JsonNode.DeepEquals(original["x-future-envelope"], appended["x-future-envelope"])) "top-level unknown field lost"

                  let first = appended["contributions"]["EXE-20260926T080000000Z-a1a1a1a1"]
                  Assert.equal "platform" (text (first["actor"]["x-team"]))
                  Assert.equal "opaque" (text (first["attestation"]["value"]))

                  let reread = recordOf (JsonNode.Parse(ProvenanceRecordJson.render appended).AsObject())
                  Assert.equal 2 reread.Provenance.Contributions.Length
                  Assert.equal "EXE-20260926T080000000Z-a1a1a1a1" (ArtifactProvenance.originator reread.Provenance).Value.Key }

          { Name = "interchange record: a second run of the same agent is a new entry; the same run merges into its own"
            Run =
              fun () ->
                  let original = load "valid/minimal-agent.json"
                  let secondRun = contribution "EXE-20260926T110000000Z-12341234" [ ContributionOperation.Modified ] "2026-09-26T11:00:00.000Z" claude
                  let sameRun = contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Modified ] "2026-09-26T08:40:00.000Z" claude

                  let twice = ProvenanceRecordJson.append secondRun original |> ok
                  Assert.equal 2 (twice["contributions"].AsObject().Count)

                  let merged = ProvenanceRecordJson.append sameRun original |> ok
                  let entry = merged["contributions"]["EXE-20260926T080000000Z-a1a1a1a1"]
                  Assert.equal 1 (merged["contributions"].AsObject().Count)
                  Assert.equal "2026-09-26T08:40:00.000Z" (text entry["last"])
                  Assert.equal [ "created"; "modified" ] (entry["operations"].AsArray() |> Seq.map text |> Seq.toList) }

          { Name = "interchange record: appending refuses re-attribution, a second creation, malformed input, and unsupported majors"
            Run =
              fun () ->
                  let original = load "valid/minimal-agent.json"
                  let impostor = contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Modified ] "2026-09-26T09:00:00.000Z" gemini
                  let secondCreation = contribution "EXE-20260926T090000000Z-b2b2b2b2" [ ContributionOperation.Created ] "2026-09-26T09:00:00.000Z" gemini
                  let anything = contribution "CTB-20260926-00001111" [ ContributionOperation.Reviewed ] "2026-09-26T09:00:00.000Z" (human "kevin")

                  Assert.isTrue (ProvenanceRecordJson.append impostor original |> Result.isError) "re-attribution accepted"
                  Assert.isTrue (ProvenanceRecordJson.append secondCreation original |> Result.isError) "second created accepted"
                  Assert.isTrue (ProvenanceRecordJson.append anything (load "invalid/two-created.json") |> Result.isError) "malformed provenance extended"

                  let future = load "unsupported/future-major.json"
                  Assert.isTrue (ProvenanceRecordJson.append anything future |> Result.isError) "unsupported major extended"
                  Assert.equal [] (ProvenanceRecordJson.successorProblems future (future.DeepClone().AsObject())) }

          { Name = "interchange record: a legacy unversioned registry block is read, and gains the envelope only when extended"
            Run =
              fun () ->
                  let legacy = load "unversioned/legacy-registry-block.json"
                  Assert.equal "valid-unversioned" (status legacy)
                  let extended = ProvenanceRecordJson.append (contribution "CTB-20260926-00001111" [ ContributionOperation.Approved ] "2026-09-26T09:00:00.000Z" (human "kevin")) legacy |> ok
                  Assert.equal ProvenanceRecord.ContractName (text extended["contract"])
                  Assert.equal "1.0.0" (text extended["version"])
                  Assert.equal [] (ProvenanceRecordJson.successorProblems legacy extended) }

          { Name = "interchange record: derivation authors the new subject and carries sources as lineage, never as authorship"
            Run =
              fun () ->
                  let requirement = load "e2e/02-requirement-modified.json"
                  let creator = contribution "EXE-20260926T100000000Z-c3c3c3c3" [ ContributionOperation.Created ] "2026-09-26T10:00:00.000Z" claude

                  let derived =
                      ProvenanceRecordJson.derive "git:commit/abc123" creator [ "praxis:RQ-APP-2026-A007", requirement ] [ "praxis:DF-APP-2026-A001" ] |> ok

                  let record = recordOf derived
                  Assert.equal [ "EXE-20260926T100000000Z-c3c3c3c3" ] (record.Provenance.Contributions |> List.map _.Key)
                  Assert.equal [ "praxis:RQ-APP-2026-A007"; "praxis:DF-APP-2026-A001" ] record.DerivedFrom
                  Assert.isTrue (JsonNode.DeepEquals(requirement, derived["sources"]["praxis:RQ-APP-2026-A007"])) "source snapshot not verbatim"

                  let notCreated = { creator with Operations = [ ContributionOperation.Modified ] }
                  Assert.isTrue (ProvenanceRecordJson.derive "git:commit/abc123" notCreated [] [] |> Result.isError) "derivation without creation" }

          { Name = "interchange record: the cross-system end-to-end chain is reconstructable without a single author"
            Run =
              fun () ->
                  let index = manifest ()
                  let e2e = index["e2e"].AsObject()

                  for step in e2e["steps"].AsArray() |> Seq.map (fun item -> item.AsObject()) do
                      let file = text step["file"]
                      Assert.equal "valid" (status (load file))

                      match step["successorOf"] with
                      | null -> ()
                      | previous ->
                          Assert.equal [] (ProvenanceRecordJson.successorProblems (load (text previous)) (load file))

                  let expected = e2e["expected"].AsObject()
                  let final = recordOf (load (text expected["final"]))
                  let links = ProvenanceRecord.chain final

                  let actual =
                      links
                      |> List.map (fun link ->
                          link.Subject |> Option.defaultValue "",
                          link.Contribution.Key,
                          link.Contribution.Actor.Id,
                          link.Contribution.Operations |> List.map ContributionOperation.code)

                  let wanted =
                      expected["chain"].AsArray()
                      |> Seq.map (fun item ->
                          text item["subject"], text item["key"], text item["actor"], item["operations"].AsArray() |> Seq.map text |> Seq.toList)
                      |> Seq.toList

                  Assert.equal wanted actual

                  let origin = (ArtifactProvenance.originator final.Provenance).Value
                  Assert.equal (text (expected["originatorOfFinal"]["key"])) origin.Key
                  Assert.equal ActorKind.Automation origin.Actor.Kind

                  // No actor originates every subject in the chain, and the
                  // discovering agent never appears among the follow-up's own
                  // contributors.
                  let originators =
                      links
                      |> List.filter (fun link -> ContributionOperation.Created |> fun op -> List.contains op link.Contribution.Operations)
                      |> List.map (fun link -> link.Contribution.Actor.Id)
                      |> List.distinct

                  Assert.isTrue (originators.Length >= 4) $"expected several originators, got {originators}"
                  Assert.isTrue (final.Provenance.Contributions |> List.forall (fun item -> item.Actor.Id <> "google/gemini-cli")) "discoverer merged into follow-up authorship"

                  let remediated = recordOf (load "e2e/10-aegis-finding-remediated.json")
                  Assert.equal "google/gemini-cli" (ArtifactProvenance.originator remediated.Provenance).Value.Actor.Id }

          { Name = "interchange record: a downstream record's contributions land in Praxis artifact front matter unchanged"
            Run =
              fun () ->
                  let record = recordOf (load "e2e/09-followup-validated.json")
                  let initial = "---\nid: RQ-TEST-2026-A900\ntitle: Imported\n---\n\nBody\n"

                  let written =
                      record.Provenance.Contributions
                      |> List.fold (fun text item -> ProvenanceFrontMatter.writeContribution item text |> Result.defaultWith failwith) initial

                  let document =
                      match FrontMatter.parse "research/requirements/RQ-TEST-2026-A900--imported.md" written with
                      | Ok document -> document
                      | Error message -> failwith message

                  match ArtifactProvenance.parse document.Metadata with
                  | Ok(Some parsed) -> Assert.equal record.Provenance parsed
                  | other -> failwith $"unexpected {other}" }

          { Name = "interchange record: foreign execution keys are namespaced and never mistaken for Praxis executions"
            Run =
              fun () ->
                  Assert.equal (Ok "EXE-aegis.gh-run-9001") (ProvenanceRecord.foreignExecutionKey "aegis" "gh-run-9001")
                  Assert.isTrue (ProvenanceRecord.foreignExecutionKey "Aegis" "1" |> Result.isError) "uppercase system accepted"
                  Assert.isTrue (ProvenanceRecord.foreignExecutionKey "aegis" "../x" |> Result.isError) "path-like run accepted"
                  Assert.isTrue (ProvenanceRecord.isForeignExecution "EXE-vigila.20260926T130000000Z-0c0c0c0c") "foreign key not recognized"
                  Assert.isTrue (not (ProvenanceRecord.isForeignExecution "EXE-20260926T080000000Z-a1a1a1a1")) "native key treated as foreign"
                  Assert.isTrue (Contribution.isExecutionId "EXE-aegis.gh-run-9001") "foreign key must still be an execution key" }

          { Name = "interchange record: contract versions are parsed strictly and only the supported major is interpreted"
            Run =
              fun () ->
                  Assert.equal (Some { Major = 1; Minor = 4; Patch = 2 }) (ContractVersion.tryParse "1.4.2")
                  Assert.equal None (ContractVersion.tryParse "1.0")
                  Assert.equal None (ContractVersion.tryParse "01.0.0")
                  Assert.isTrue (ContractVersion.isSupported { Major = 1; Minor = 9; Patch = 0 }) "minor rejected"
                  Assert.isTrue (not (ContractVersion.isSupported { Major = 2; Minor = 0; Patch = 0 })) "future major accepted" }

          { Name = "identity never carries credentials, in actors, reasons, or evidence"
            Run =
              fun () ->
                  let leaky = agent "anthropic/claude-code" "anthropic" "sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA" "claude-code"
                  Assert.isTrue (not (Actor.problems leaky).IsEmpty) "credential in actor accepted"

                  let reason = { contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Created ] "2026-09-26T08:00:00.000Z" claude with Reason = Some "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345" }
                  Assert.isTrue (not (Contribution.problems reason).IsEmpty) "credential in reason accepted"
                  Assert.isTrue (Contribution.problems { reason with Reason = Some "Tighten acceptance criteria" } |> List.isEmpty) "ordinary reason rejected"
                  Assert.isTrue (not (Credentials.looksLikeCredential "openai/codex")) "an actor id flagged as a credential" } ]
