namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Provenance
open Praxis.Domain.Telemetry
open Praxis.Domain.Work
open Praxis.Infrastructure.Construction
open Praxis.Infrastructure.Work

/// VAC-002-P3-01 R-PRAX-1 (OD-6): the implementation brief carries the frozen
/// authority contract byte for byte, and every execution records the version
/// the brief carried, or its absence (never a digest it did not hold).
[<RequireQualifiedAccess>]
module AuthorityContractTests =
    /// The frozen P3 wording, restated independently of the production literal.
    let private frozen =
        "Implement only behavior supported by requirements or accepted decisions. "
        + "Do not invent exceptions, precedence rules, or business policies. "
        + "Record unsupported interpretations as unresolved questions. "
        + "Do not treat your own proposed decisions as authorized."

    let private frozenSha256 = "2af405073b299f562a8321feac875d2b9a9ae6d94171475a59a60a416784862d"

    let private sha256 (text: string) =
        System.Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes text)
        |> Convert.ToHexString
        |> fun hex -> hex.ToLowerInvariant()

    let private repositoryFile (path: string) =
        File.ReadAllText(Path.Combine(CliPort.repositoryRoot.Value, path))

    /// Creates one execution in a fresh repository whose AGENTS.md is
    /// `brief` (none when None) and returns its `authorityContract` node.
    let private recordedContract (brief: string option) =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-authority-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            brief |> Option.iter (fun text -> File.WriteAllText(Path.Combine(root, "AGENTS.md"), text))

            let request: FileTelemetryExecutionRepository.CreateExecutionRequest =
                { WorkItemId = "WI-0001"
                  WorkType = "task"
                  Classifications = []
                  ClassificationRationale = None
                  ExecutionId = None
                  IdentityOverrides = IdentityInputs.empty }

            match FileTelemetryExecutionRepository.createExecution root request with
            | Ok(Some executionId) ->
                let record =
                    JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json"))) :?> JsonObject

                record["authorityContract"] :?> JsonObject
            | Ok None -> failwith "expected an execution"
            | Error message -> failwith message
        finally
            Directory.Delete(root, true)

    let private text (node: JsonNode) =
        match node with
        | null -> None
        | value -> Some(value.GetValue<string>())

    let private briefWith (quote: string) =
        "# Agent Startup Guide\n\n## Authority\n\nA note.\n\n> " + quote + "\n\nApply, in descending order: ...\n\n## Core Rules\n\n> Not the contract.\n"

    let tests =
        [ { Name = "authority contract: the production literal is the frozen P3 wording and digest"
            Run =
              fun () ->
                  Assert.equal frozen AuthorityContract.Text
                  Assert.equal frozenSha256 (sha256 frozen)
                  Assert.equal frozenSha256 AuthorityContract.Sha256
                  Assert.equal frozenSha256 (AuthorityContract.digest AuthorityContract.Text) }

          { Name = "authority contract: AGENTS.md carries the frozen contract verbatim in its Authority section"
            Run =
              fun () ->
                  let brief = repositoryFile "AGENTS.md"
                  Assert.equal (Some frozen) (AuthorityContract.find brief)
                  Assert.equal (AuthorityContract.Present frozenSha256) (AuthorityContract.observe (Some brief))
                  Assert.isTrue (brief.Contains("> " + frozen + "\n")) "the contract is one quoted line, byte for byte"
                  Assert.isTrue (brief.Contains "not a demonstrated reliability control") "the preceding note labels it as existing policy" }

          { Name = "authority contract: the construction handoff brief renders the contract after the task and before the obligations"
            Run =
              fun () ->
                  let handoff: ConstructionHandoff =
                      { CohortId = "COHORT-1"; WorkItems = []; Requirements = []; Decisions = []; Interfaces = []
                        AcceptanceAdapters = []; GeneratedApi = []; PermittedFiles = []; Operations = [ "Do the task." ] }

                  let markdown = ConstructionHandoff.toMarkdown handoff
                  let at (s: string) = markdown.IndexOf(s, StringComparison.Ordinal)
                  Assert.isTrue (markdown.Contains("## Authority\n\n" + AuthorityContract.Note + "\n\n" + frozen + "\n")) "contract rendered verbatim"
                  Assert.isTrue (at "- Do the task." < at "## Authority" && at "## Authority" < at "## Obligations") "contract position" }

          { Name = "authority contract: a brief without the contract is absent, never a digest"
            Run =
              fun () ->
                  Assert.equal (AuthorityContract.Absent "brief-missing") (AuthorityContract.observe None)
                  Assert.equal (AuthorityContract.Absent "no-authority-contract") (AuthorityContract.observe (Some "# Guide\n\n## Authority\n\nPlain text only.\n"))
                  Assert.equal (AuthorityContract.Absent "no-authority-contract") (AuthorityContract.observe (Some ("# Guide\n\n## Rules\n\n> " + frozen + "\n"))) }

          { Name = "authority contract: telemetry records the frozen digest when the brief carries the contract"
            Run =
              fun () ->
                  let node = recordedContract (Some(briefWith frozen))
                  Assert.equal (Some "present") (text node["status"])
                  Assert.equal (Some "AGENTS.md") (text node["source"])
                  Assert.equal (Some frozenSha256) (text node["sha256"])
                  Assert.equal (Some "VAC-002-P3") (text node["frozenVersion"])
                  Assert.equal true (node["matchesFrozen"].GetValue<bool>()) }

          { Name = "authority contract: telemetry records a paraphrase under its own digest, not as the frozen version"
            Run =
              fun () ->
                  let paraphrase = "Only implement what requirements support."
                  let node = recordedContract (Some(briefWith paraphrase))
                  Assert.equal (Some "present") (text node["status"])
                  Assert.equal (Some(sha256 paraphrase)) (text node["sha256"])
                  Assert.equal false (node["matchesFrozen"].GetValue<bool>()) }

          { Name = "authority contract: telemetry records absence, with a null digest, when the brief has no contract"
            Run =
              fun () ->
                  for brief, reason in [ None, "brief-missing"; Some "# Guide\n\n## Authority\n\nNo quote.\n", "no-authority-contract" ] do
                      let node = recordedContract brief
                      Assert.equal (Some "absent") (text node["status"])
                      Assert.isTrue (node.ContainsKey "sha256" && isNull node["sha256"]) "absent is null, not a value"
                      Assert.equal (Some reason) (text node["reason"])
                      Assert.isTrue (not (node.ContainsKey "matchesFrozen")) "no frozen comparison without a contract" } ]
