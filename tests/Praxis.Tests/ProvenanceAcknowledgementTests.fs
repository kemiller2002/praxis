namespace Praxis.Tests

open System
open System.IO
open Praxis.Cli
open Praxis.Contracts.Provenance
open Praxis.Domain.Artifacts
open Praxis.Domain.Provenance
open Praxis.Infrastructure.Artifacts

/// DF-ROS-2026-A055: the `origin-unrecorded` acknowledgement. It never names
/// or infers a creator, records who acknowledged the gap and when with a
/// required reason, is refused once a creation is recorded, and satisfies
/// the missing-creation rule while `validate` reports it as itself.
[<RequireQualifiedAccess>]
module ProvenanceAcknowledgementTests =
    let private t name run = { Name = $"provenance origin-unrecorded: {name}"; Run = run }

    let private owner =
        { Kind = ActorKind.Human
          Id = "kevin"
          Provider = None
          Model = None
          Runtime = None }

    let private creator =
        { Kind = ActorKind.Agent
          Id = "anthropic/claude-code"
          Provider = Some "anthropic"
          Model = Some "unknown"
          Runtime = Some "claude-code" }

    let private acknowledgement reason : Contribution =
        { Key = "CTB-20261007T120000000Z-ack"
          Operations = [ ContributionOperation.OriginUnrecorded ]
          At = "2026-10-07T12:00:00.000Z"
          Last = None
          Actor = owner
          Reason = reason
          Evidence = [] }

    let private creation : Contribution =
        { Key = "EXE-20261007T110000000Z-aaaaaaaa"
          Operations = [ ContributionOperation.Created ]
          At = "2026-10-07T11:00:00.000Z"
          Last = None
          Actor = creator
          Reason = None
          Evidence = [] }

    let private path = "research/hypotheses/HY-TEST-2026-A001--h.md"

    let private hypothesis =
        "---\nid: HY-TEST-2026-A001\ntitle: Hypothesis\nstatus: proposed\ncreated: 2026-10-07\nupdated: 2026-10-07\n---\n\n# Body\n"

    let private document (text: string) =
        match FrontMatter.parse path text with
        | Ok parsed -> parsed
        | Error message -> failwith message

    let private withContribution (item: Contribution) (text: string) =
        match ProvenanceFrontMatter.writeContribution item text with
        | Ok updated -> updated
        | Error message -> failwith message

    let private validate (text: string) =
        let parsed = document text

        ProvenanceValidation.findings
            { Policy =
                { Enforced = true
                  RequiredFrom = Some "2026-09-25"
                  RequireOriginator = ProvenancePolicy.defaultRequireOriginator }
              Documents = [ parsed ]
              Executions = Map.empty
              KnownIdentifiers = Set.ofList [ ArtifactDocument.identifier parsed ] }

    let tests =
        [ t "an acknowledgement with a reason is recorded under the acknowledger and names no creator" (fun () ->
              match ArtifactProvenance.record (acknowledgement (Some "pushed directly with no Praxis execution")) ArtifactProvenance.empty with
              | Error message -> failwith message
              | Ok provenance ->
                  Assert.empty (ArtifactProvenance.problems provenance)
                  Assert.equal None (ArtifactProvenance.originator provenance)
                  let recorded = (ArtifactProvenance.originAcknowledgement provenance).Value
                  Assert.equal (owner, [ ContributionOperation.OriginUnrecorded ]) (recorded.Actor, recorded.Operations)
                  Assert.equal (Some ContributionOperation.OriginUnrecorded) (ContributionOperation.tryParse "origin-unrecorded")
                  Assert.equal "origin-unrecorded" (ContributionOperation.code ContributionOperation.OriginUnrecorded))

          t "it is refused when the artifact already records its creation, and a creation cannot follow it" (fun () ->
              let created = ArtifactProvenance.empty |> ArtifactProvenance.record creation |> Result.defaultWith failwith

              match ArtifactProvenance.record (acknowledgement (Some "unknown origin")) created with
              | Error message -> Assert.isTrue (message.Contains "already records its creation") message
              | Ok _ -> failwith "an acknowledgement was accepted over a recorded creation"

              let acknowledged = ArtifactProvenance.empty |> ArtifactProvenance.record (acknowledgement (Some "unknown origin")) |> Result.defaultWith failwith
              Assert.isTrue (ArtifactProvenance.record { creation with At = "2026-10-07T13:00:00.000Z" } acknowledged |> Result.isError) "a creation followed an acknowledgement"

              let both = { Contributions = [ creation; acknowledgement (Some "unknown origin") ] }
              Assert.isTrue (ArtifactProvenance.problems both |> List.exists (fun problem -> problem.Message.Contains "both 'created' and 'origin-unrecorded'")) "a hand-written conflict went unreported")

          t "a reason is required and the operation stands alone" (fun () ->
              let problemsOf item = Contribution.problems item |> List.map fst
              Assert.isTrue (problemsOf (acknowledgement None) |> List.contains "reason") "no reason was accepted"
              Assert.isTrue (problemsOf (acknowledgement (Some "   ")) |> List.contains "reason") "a blank reason was accepted"
              Assert.isTrue (problemsOf { acknowledgement (Some "r") with Operations = [ ContributionOperation.OriginUnrecorded; ContributionOperation.Modified ] } |> List.contains "operations") "a combined operation was accepted"
              Assert.equal 2 (ProvenanceAcknowledgeCommands.run (Path.GetTempPath()) [ "--path"; path ])
              Assert.equal 2 (ProvenanceAcknowledgeCommands.run (Path.GetTempPath()) [ "--path"; path; "--reason"; " " ])
              Assert.equal 2 (ProvenanceAcknowledgeCommands.run (Path.GetTempPath()) [ "--reason"; "why" ]))

          t "validate treats an acknowledged origin as satisfying the creation rule and reports it distinctly" (fun () ->
              let missing = validate hypothesis
              Assert.isTrue (missing |> List.exists (fun finding -> finding.Severity = FindingSeverity.Error && finding.Message.Contains "records no provenance")) "a new record without provenance passed"

              let acknowledged = validate (withContribution (acknowledgement (Some "pushed directly to main with no Praxis execution")) hypothesis)
              Assert.empty (acknowledged |> List.filter (fun finding -> finding.Severity <> FindingSeverity.Info))
              let note = acknowledged |> List.filter (fun finding -> finding.Field = "provenance.origin") |> Assert.single
              Assert.isTrue (note.Message.StartsWith "origin unrecorded:" && note.Message.Contains "no creator is named") note.Message
              Assert.isTrue (note.Message.Contains "human:kevin" || note.Message.Contains "kevin") note.Message
              Assert.isTrue (note.Message.Contains "pushed directly to main with no Praxis execution") note.Message

              let created = validate (withContribution creation hypothesis)
              Assert.empty (created |> List.filter (fun finding -> finding.Field = "provenance.origin")))

          t "cli acknowledge-unrecorded writes the acknowledgement; a later creation and an acknowledgement over a creation are refused" (fun () ->
              let root = Path.Combine(Path.GetTempPath(), $"praxis-origin-{Guid.NewGuid():N}")
              Directory.CreateDirectory(Path.Combine(root, "research", "hypotheses")) |> ignore
              Directory.CreateDirectory(Path.Combine(root, ".ros", "telemetry", "executions")) |> ignore
              File.WriteAllText(Path.Combine(root, "ros.json"), """{"repository":{"id":"origin-test"}}""")
              File.WriteAllText(Path.Combine(root, path), hypothesis)
              let other = "research/hypotheses/HY-TEST-2026-A002--h.md"
              File.WriteAllText(Path.Combine(root, other), hypothesis.Replace("HY-TEST-2026-A001", "HY-TEST-2026-A002"))
              let asOwner = [ "--actor-kind"; "human"; "--actor"; "kevin"; "--occurred-at"; "2026-10-07T12:00:00.000Z" ]

              try
                  Assert.equal 0 (ProvenanceAcknowledgeCommands.run root ([ "--path"; path; "--reason"; "authored outside any Praxis execution" ] @ asOwner))
                  let written = File.ReadAllText(Path.Combine(root, path))
                  Assert.isTrue (written.Contains "origin-unrecorded" && written.Contains "authored outside any Praxis execution") written
                  Assert.equal 1 (ProvenanceCommands.run root ([ "record"; "--path"; path; "--operation"; "created" ] @ asOwner))

                  Assert.equal 0 (ProvenanceCommands.run root ([ "record"; "--path"; other; "--operation"; "created" ] @ asOwner))
                  Assert.equal 1 (ProvenanceAcknowledgeCommands.run root ([ "--path"; other; "--reason"; "unknown" ] @ asOwner))
                  Assert.isTrue (not (File.ReadAllText(Path.Combine(root, other)).Contains "origin-unrecorded")) "the refused acknowledgement was written"
              finally
                  Directory.Delete(root, true)) ]
