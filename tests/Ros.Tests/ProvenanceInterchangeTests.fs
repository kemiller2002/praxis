namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Artifacts
open Ros.Domain.Provenance
open Ros.Infrastructure.Artifacts

/// The cross-system provenance contract (RQ-ROS-2026-A013..A017,
/// DF-ROS-2026-A037), asserted against the same fixtures the JavaScript
/// reference library and every downstream Echelon codec use
/// (`tests/fixtures/provenance-interchange/`), so both sides of the shared
/// contract reach identical verdicts.
[<RequireQualifiedAccess>]
module ProvenanceInterchangeTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "package.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "provenance-interchange")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private fixture name =
        let root = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))
        JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "provenance-interchange", name)))

    let private verdictCode verdict =
        match verdict with
        | Supported(_, _, warnings) -> "supported", warnings.Length
        | Unsupported _ -> "unsupported", 0
        | Malformed _ -> "malformed", 0

    let private agent id provider model runtime =
        { Kind = ActorKind.Agent
          Id = id
          Provider = Some provider
          Model = Some model
          Runtime = Some runtime }

    let private automation id provider runtime =
        { Kind = ActorKind.Automation
          Id = id
          Provider = Some provider
          Model = Some "unknown"
          Runtime = Some runtime }

    let private contribution key operations at actor =
        { Key = key
          Operations = operations
          At = at
          Last = None
          Actor = actor
          Reason = None
          Evidence = [] }

    let private record item provenance =
        match ArtifactProvenance.record item provenance with
        | Ok updated -> updated
        | Error message -> failwith message

    let private build items =
        items |> List.fold (fun provenance item -> record item provenance) ArtifactProvenance.empty

    let private supported node =
        match ProvenanceInterchangeJson.classify node with
        | Supported(provenance, lineage, _) -> provenance, lineage
        | other -> failwith $"expected supported, got {other}"

    let private codex = agent "openai/codex" "openai" "gpt-5-codex" "codex"
    let private claude = agent "anthropic/claude-code" "anthropic" "unknown" "claude-code"
    let private dokimos = automation "echelon/dokimos" "echelon" "dokimos"

    let tests =
        [ { Name = "interchange conformance: every shared fixture reaches the same verdict as the reference library"
            Run =
              fun () ->
                  let cases = (fixture "cases.json").["cases"].AsArray()
                  Assert.isTrue (cases.Count >= 40) "expected the full conformance set"

                  for item in cases do
                      let name = item["name"].GetValue<string>()
                      let expected = item["expect"].GetValue<string>(), item["warnings"].GetValue<int>()
                      let verdict = ProvenanceInterchangeJson.classify item["block"]
                      let actual = verdictCode verdict

                      if actual <> expected then
                          failwith $"{name}: expected {expected}, got {actual} ({verdict})" }

          { Name = "interchange export round-trips: toNode then classify yields the same history and lineage"
            Run =
              fun () ->
                  let provenance =
                      build
                          [ contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Created ] "2026-09-26T08:00:00.000Z" codex
                            contribution "EXT-dokimos.snapshot-1" [ ContributionOperation.Measured ] "2026-09-26T08:10:00.000Z" dokimos
                            contribution "EXE-20260926T090000000Z-b1b1b1b1" [ ContributionOperation.Remediated ] "2026-09-26T09:00:00.000Z" claude ]

                  let node = ProvenanceInterchangeJson.toNode (Some(Some "RQ-APP-2026-A001", None)) [ "EV-APP-2026-A001"; "ordo:resolution/r-17" ] provenance
                  Assert.equal "praxis.provenance/1" (node["schema"].GetValue<string>())
                  let parsed, lineage = supported (JsonNode.Parse(node.ToJsonString()))
                  Assert.equal provenance parsed
                  Assert.equal [ "EV-APP-2026-A001"; "ordo:resolution/r-17" ] lineage }

          { Name = "role operations never transfer authorship, and measuring or validating is not modifying"
            Run =
              fun () ->
                  let provenance =
                      build
                          [ contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Created; ContributionOperation.Discovered ] "2026-09-26T08:00:00.000Z" codex
                            contribution "EXT-dokimos.snapshot-1" [ ContributionOperation.Measured ] "2026-09-26T08:10:00.000Z" dokimos
                            contribution "EXT-github-actions.run-7" [ ContributionOperation.Validated ] "2026-09-26T08:20:00.000Z" (automation "github/github-actions" "github" "github-actions") ]

                  let involvement = Involvement.describe provenance
                  Assert.equal (Some codex) involvement.Origin
                  Assert.empty involvement.Modifiers
                  Assert.equal "agent-created" involvement.Label

                  let remediated =
                      record (contribution "EXE-20260926T090000000Z-b1b1b1b1" [ ContributionOperation.Remediated ] "2026-09-26T09:00:00.000Z" claude) provenance

                  Assert.equal [ claude ] (Involvement.describe remediated).Modifiers
                  Assert.isTrue (Involvement.describe remediated).AgentToAgentRevision "a remediation by another agent is an agent-to-agent revision" }

          { Name = "foreign executions: an agent may be keyed by EXT-<system>.<run>, which is informational, never an error"
            Run =
              fun () ->
                  let key = "EXT-dokimos.snapshot-20260926-01"
                  Assert.isTrue (Contribution.isValidKey key) "EXT key must be valid"
                  Assert.equal (Some "dokimos") (Contribution.foreignSystem key)
                  Assert.isTrue (not (Contribution.isValidKey "EXT-dokimos")) "an EXT key must name a run"
                  Assert.isTrue (not (Contribution.isValidKey "EXT-Dokimos.run")) "system ids are lower-case registry ids"

                  let item = contribution key [ ContributionOperation.Created ] "2026-09-26T08:00:00.000Z" claude
                  Assert.empty (Contribution.problems item)
                  Assert.equal None (Contribution.execution item)
                  Assert.equal (Some key) (Contribution.anyExecution item)

                  let text =
                      "---\nid: RQ-TEST-2026-A001\ntitle: Imported\nstatus: draft\ncreated: 2026-09-26\nupdated: 2026-09-26\n---\n\n# Body\n"
                      |> ProvenanceFrontMatter.writeContribution item
                      |> function Ok value -> value | Error message -> failwith message

                  let document =
                      match FrontMatter.parse "research/requirements/RQ-TEST-2026-A001--r.md" text with
                      | Ok value -> value
                      | Error message -> failwith message

                  match ArtifactProvenance.parse document.Metadata with
                  | Ok(Some parsed) -> Assert.equal [ item ] parsed.Contributions
                  | other -> failwith $"expected the imported provenance to parse: {other}"

                  let findings =
                      ProvenanceValidation.findings
                          { Policy =
                              { Enforced = true
                                RequiredFrom = Some "2026-09-25"
                                RequireOriginator = ProvenancePolicy.defaultRequireOriginator }
                            Documents = [ document ]
                            Executions = Map.empty
                            KnownIdentifiers = Set.ofList [ "RQ-TEST-2026-A001" ] }

                  Assert.empty (findings |> List.filter (fun finding -> finding.Severity <> FindingSeverity.Info))

                  Assert.isTrue
                      (findings |> List.exists (fun finding -> finding.Severity = FindingSeverity.Info && finding.Message.Contains "Echelon system 'dokimos'"))
                      "foreign execution is reported for audit" }

          { Name = "a downstream role history written into front matter reads back identically (downstream -> Praxis artifact)"
            Run =
              fun () ->
                  let items =
                      [ contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Created; ContributionOperation.Discovered ] "2026-09-26T08:00:00.000Z" codex
                        contribution "EXT-vigila.op-7" [ ContributionOperation.Transformed ] "2026-09-26T08:05:00.000Z" (automation "echelon/vigila" "echelon" "vigila")
                        contribution "EXE-20260926T100000000Z-a2a2a2a2" [ ContributionOperation.Remediated; ContributionOperation.Resolved ] "2026-09-26T10:00:00.000Z" codex ]

                  let text =
                      items
                      |> List.fold
                          (fun text item -> match ProvenanceFrontMatter.writeContribution item text with Ok value -> value | Error message -> failwith message)
                          "---\nid: RQ-TEST-2026-A001\ntitle: Imported\nstatus: draft\ncreated: 2026-09-26\nupdated: 2026-09-26\n---\n\n# Body\n"

                  match FrontMatter.parse "research/requirements/RQ-TEST-2026-A001--r.md" text |> Result.map (fun document -> ArtifactProvenance.parse document.Metadata) with
                  | Ok(Ok(Some parsed)) -> Assert.equal (build items) parsed
                  | other -> failwith $"expected round trip: {other}" }

          { Name = "a later execution cannot merge 'created' into its own entry to become a second originator"
            Run =
              fun () ->
                  let history =
                      build
                          [ contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Created ] "2026-09-26T08:00:00.000Z" codex
                            contribution "EXE-20260926T090000000Z-b1b1b1b1" [ ContributionOperation.Modified ] "2026-09-26T09:00:00.000Z" claude ]

                  let forged =
                      ArtifactProvenance.record
                          (contribution "EXE-20260926T090000000Z-b1b1b1b1" [ ContributionOperation.Created ] "2026-09-26T09:30:00.000Z" claude)
                          history

                  Assert.isTrue (Result.isError forged) "a second originator must be refused"

                  let originless =
                      build
                          [ contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Modified ] "2026-09-26T08:00:00.000Z" codex
                            contribution "EXE-20260926T090000000Z-b1b1b1b1" [ ContributionOperation.Modified ] "2026-09-26T09:00:00.000Z" claude ]

                  let late =
                      ArtifactProvenance.record
                          (contribution "EXE-20260926T090000000Z-b1b1b1b1" [ ContributionOperation.Created ] "2026-09-26T09:30:00.000Z" claude)
                          originless

                  Assert.isTrue (Result.isError late) "a late originator must be refused"

                  let own =
                      ArtifactProvenance.record
                          (contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Created ] "2026-09-26T08:10:00.000Z" codex)
                          originless

                  Assert.isTrue (Result.isOk own) "the earliest entry may still record its own creation" }

          { Name = "an operation from a later contract-1 release is preserved and warned about, not rejected (contract 1.1)"
            Run =
              fun () ->
                  let text =
                      "---\nid: RQ-TEST-2026-A001\ntitle: Imported\nstatus: draft\ncreated: 2026-09-26\nupdated: 2026-09-26\nprovenance:\n  contributions:\n    CTB-20260926-aaaaaaaa:\n      operations: [created, quarantined]\n      at: 2026-09-26T08:00:00.000Z\n      actor:\n        kind: human\n        id: kevin\n---\n\n# Body\n"

                  let document =
                      match FrontMatter.parse "research/requirements/RQ-TEST-2026-A001--r.md" text with
                      | Ok value -> value
                      | Error message -> failwith message

                  match ArtifactProvenance.parse document.Metadata with
                  | Ok(Some parsed) ->
                      Assert.equal
                          [ ContributionOperation.Created; ContributionOperation.Extension "quarantined" ]
                          (Assert.single parsed.Contributions).Operations
                  | other -> failwith $"expected the newer operation to be carried: {other}"

                  let findings =
                      ProvenanceValidation.findings
                          { Policy = ProvenancePolicy.notConfigured
                            Documents = [ document ]
                            Executions = Map.empty
                            KnownIdentifiers = Set.ofList [ "RQ-TEST-2026-A001" ] }

                  Assert.empty (findings |> List.filter (fun finding -> finding.Severity = FindingSeverity.Error))
                  Assert.isTrue (findings |> List.exists (fun finding -> finding.Severity = FindingSeverity.Warning && finding.Message.Contains "quarantined")) "warned"

                  let newline = text.Replace("[created, quarantined]", "[created, \"Created!\"]")

                  match FrontMatter.parse "research/requirements/RQ-TEST-2026-A001--r.md" newline |> Result.map (fun document -> ArtifactProvenance.parse document.Metadata) with
                  | Ok(Error _) -> ()
                  | other -> failwith $"a code outside the grammar must stay malformed: {other}" }

          { Name = "timestamps are calendar-valid and compared at millisecond precision (contract 1.1)"
            Run =
              fun () ->
                  for invalid in [ "2026-02-30T00:00:00Z"; "2026-09-26T24:00:00Z"; "0000-01-01T00:00:00Z"; "2026-09-26T08:00:00Z\n" ] do
                      Assert.isTrue (not (Contribution.isTimestamp invalid)) $"expected {invalid} to be rejected"

                  Assert.equal (Contribution.parseTimestamp "2026-09-26T08:00:00.0009Z") (Contribution.parseTimestamp "2026-09-26T08:00:00.0001Z")
                  Assert.isTrue (Contribution.isTimestamp "9999-12-31T23:59:59.999999999Z") "maximum with nine fractional digits" }

          { Name = "credential-like values are refused wherever they appear in a provenance block"
            Run =
              fun () ->
                  for secret in [ "ghp_0123456789abcdefghijABCDEFGHIJ0123"; "github_pat_0123456789abcdefghij_KLMNOP"; "sk-ant-api03-abcdefghijklmnopqrstuv"; "AKIAABCDEFGHIJKLMNOP"; "Bearer abcdefghijklmnopqrstuvwxyz"; "-----BEGIN RSA PRIVATE KEY-----" ] do
                      Assert.isTrue (ProvenanceInterchangeJson.isCredentialLike secret) $"expected {secret} to be refused"

                  for benign in [ "openai/codex"; "EXE-20260926T080000000Z-a1a1a1a1"; "anthropic"; "Tighten acceptance criteria"; "sk-short" ] do
                      Assert.isTrue (not (ProvenanceInterchangeJson.isCredentialLike benign)) $"did not expect {benign} to be refused" }

          { Name = "end-to-end chain: replayed with the Praxis domain, every record keeps its own originator and classifies as supported"
            Run =
              fun () ->
                  let chain = fixture "echelon-chain.json"

                  let blocks =
                      chain.["steps"].AsArray()
                      |> Seq.fold
                          (fun (blocks: Map<string, JsonObject>) step ->
                              let recordId = step["record"].GetValue<string>()

                              let current =
                                  blocks
                                  |> Map.tryFind recordId
                                  |> Option.defaultWith (fun () ->
                                      let empty = JsonObject()
                                      empty["schema"] <- JsonValue.Create ProvenanceInterchangeJson.SchemaTag
                                      empty["contributions"] <- JsonObject()
                                      empty)

                              let next = current.DeepClone().AsObject()

                              match step["lineage"] with
                              | :? JsonArray as references ->
                                  let existing = match next["derivedFrom"] with :? JsonArray as values -> values | _ -> JsonArray()
                                  let merged = JsonArray()
                                  for value in existing do merged.Add(value.DeepClone())
                                  for value in references do merged.Add(value.DeepClone())
                                  next["derivedFrom"] <- merged
                              | _ ->
                                  let append = step["append"]
                                  let key = append["key"].GetValue<string>()
                                  let provenance, _ = supported next

                                  let incoming, _ =
                                      let single = JsonObject()
                                      single["schema"] <- JsonValue.Create ProvenanceInterchangeJson.SchemaTag
                                      let map = JsonObject()
                                      map[key] <- append["contribution"].DeepClone()
                                      single["contributions"] <- map
                                      supported single

                                  let updated = record (Assert.single incoming.Contributions) provenance
                                  let lineage = match next["derivedFrom"] with :? JsonArray as values -> values |> Seq.map _.GetValue<string>() |> Seq.toList | _ -> []
                                  let rebuilt = ProvenanceInterchangeJson.toNode None lineage updated
                                  next["contributions"] <- rebuilt["contributions"].DeepClone()

                              blocks |> Map.add recordId next)
                          Map.empty

                  let originators = chain.["expect"].["originators"].AsObject()

                  for pair in originators do
                      let provenance, _ = supported blocks[pair.Key]
                      let origin = ArtifactProvenance.originator provenance |> Option.get
                      Assert.equal (pair.Value["key"].GetValue<string>()) origin.Key
                      Assert.equal (pair.Value["actorId"].GetValue<string>()) origin.Actor.Id

                  let finding, _ = supported blocks["aegis:finding/SF-0001"]
                  let withOperation operation = finding.Contributions |> List.filter (Contribution.has operation) |> List.map _.Actor.Id
                  Assert.equal [ "google/gemini-cli" ] (withOperation ContributionOperation.Discovered)
                  Assert.equal [ "openai/codex" ] (withOperation ContributionOperation.Remediated)
                  Assert.equal [ "github/github-actions" ] (withOperation ContributionOperation.Validated)

                  let distinctOriginators =
                      blocks
                      |> Map.toList
                      |> List.choose (fun (_, block) -> supported block |> fst |> ArtifactProvenance.originator |> Option.map _.Actor.Id)
                      |> List.distinct

                  Assert.isTrue (distinctOriginators.Length > 1) "no single actor authored the whole chain" } ]
