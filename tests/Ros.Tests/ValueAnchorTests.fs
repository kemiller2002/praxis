namespace Ros.Tests

open Ros.Domain.Architecture
open Ros.Domain.Artifacts
open Ros.Domain.Provenance
open Ros.Domain.Work

/// Whole-value validators anchor with `\z`: a .NET `$` also matches before a
/// final line feed, so `^...$` accepted a value followed by a newline. Each
/// corrected validator rejects LF, CR and CRLF suffixes and still accepts the
/// values it accepted before.
[<RequireQualifiedAccess>]
module ValueAnchorTests =
    let private terminators = [ "\n"; "\r"; "\r\n" ]

    /// `accepts` holds for every valid value and for none of its
    /// line-terminated variants.
    let private anchored (name: string) (accepts: string -> bool) (valid: string list) =
        valid
        |> List.iter (fun value ->
            Assert.isTrue (accepts value) $"{name}: valid value '{value}' was rejected"

            terminators
            |> List.iter (fun terminator ->
                Assert.isTrue (not (accepts (value + terminator))) $"{name}: '{value}' followed by {terminator.Length} terminator char(s) was accepted"))

    let private document identifier fileName =
        { RelativePath = $"research/packages/{fileName}"
          FileName = fileName
          Metadata = Map.ofList [ "id", ArtifactValue.Text identifier; "title", ArtifactValue.Text "Title"; "status", ArtifactValue.Text "draft" ] }

    let tests =
        [ { Name = "actor kind rejects an extension with a trailing newline or carriage return"
            Run =
              fun () ->
                  Assert.equal None (ActorKind.tryParse "x-agent\n")
                  Assert.equal None (ActorKind.tryParse "x-agent\r")
                  Assert.equal (Some(ActorKind.Extension "x-agent")) (ActorKind.tryParse "x-agent")
                  anchored "actor kind" (ActorKind.tryParse >> Option.isSome) [ "x-agent"; "x-a1-b2"; "agent"; "human" ] }

          { Name = "contribution operation, execution id, contribution key and timestamp reject a trailing line terminator"
            Run =
              fun () ->
                  anchored "contribution operation" (ContributionOperation.tryParse >> Option.isSome) [ "created"; "x-ported" ]
                  anchored "execution id" Contribution.isExecutionId [ "EXE-20261002T145826078Z-d36d4a90" ]
                  anchored "contribution key" Contribution.isValidKey [ "EXE-1"; "CTB-legacy.1" ]
                  anchored "timestamp" Contribution.isTimestamp [ "2026-10-02T14:58:22Z"; "2026-10-02T14:58:22.000Z" ] }

          { Name = "artifact identifiers and legacy research-package filenames reject a trailing line terminator"
            Run =
              fun () ->
                  anchored
                      "artifact identifier"
                      ArtifactPolicy.isValidIdentifier
                      [ "EV-ROS-2026-A7F2"; "CN-ROS-2026-0001"; "RP-2026-08-16-ROS"; "REP-ROS-2026-001" ]

                  // A legacy package may be named `<ID>.md`; a terminated ID never qualifies.
                  Assert.empty (ArtifactPolicy.validate [] [ document "REP-ROS-2026-001" "REP-ROS-2026-001.md" ])

                  for terminator in terminators do
                      let identifier = "REP-ROS-2026-001" + terminator

                      Assert.isTrue
                          (ArtifactPolicy.validate [] [ document identifier $"{identifier}.md" ]
                           |> List.exists (fun finding -> finding.Message.StartsWith "filename must start"))
                          "a terminated legacy identifier was accepted as a legacy filename" }

          { Name = "implementation-policy exception decisions reject a trailing line terminator"
            Run =
              fun () ->
                  let accepts decision =
                      ImplementationLanguagePolicy.configurationFindings
                          { ProhibitNodeArtifacts = true
                            Exceptions = [ { Path = "web/app.js"; Decision = decision } ] }
                      |> List.isEmpty

                  anchored "decision" accepts [ "DF-ROS-2026-A030" ] }

          { Name = "work-group, work-item ids and glob matches reject a trailing line terminator"
            Run =
              fun () ->
                  anchored "group id" WorkGroups.isValidGroupId [ "GROUP-A1"; "GROUP-PLAN-2" ]
                  anchored "work item id" WorkItemId.isValid [ "WI-0064"; "ACTOR-KIND-ANCHOR" ]
                  anchored "glob" (fun value -> PathFilter.globMatch "src/*.fs" value) [ "src/Program.fs" ]
                  // `**` matches any character but a line feed, so only the LF forms are refused.
                  Assert.isTrue (PathFilter.globMatch ".ros/**" ".ros/context/current.json") "double-star rejected a valid path"
                  Assert.isTrue (not (PathFilter.globMatch ".ros/**" ".ros/context/current.json\n")) "double-star accepted a trailing LF" } ]
