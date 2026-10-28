namespace Praxis.Cli

open System
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Git
open Praxis.Infrastructure.Work

/// `work evidence`: records one statement about how work was carried out
/// (assignment, dependency decision, scope change, owner approval,
/// independent review, release selection, test result, acceptance reference,
/// final disposition) as a `work.evidence.recorded` event (WI-0103). Parses,
/// delegates classification to `Praxis.Domain.Work.ProcessEvidence` and
/// persistence to `FileProcessEvidenceRepository`, and renders.
[<RequireQualifiedAccess>]
module ProcessEvidenceCommands =
    let usage =
        "work evidence --id ID --kind {assignment|dependency-decision|scope-change|owner-approval|independent-review|release-selection|test-result|acceptance-reference|final-disposition} --summary TEXT --occurred-at TIMESTAMP [--subject REF]* [--candidate SHA] [--reference REF]* [--decision ID] [--outcome VALUE] [--attestation TYPE:REF] [--execution EXE-ID] [--json] [IDENTITY]"

    let private optionValue (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private optionValues (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private compact = JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private request (arguments: string list) : ProcessEvidenceRequest =
        { WorkItemId = optionValue "--id" arguments |> Option.defaultValue ""
          OccurredAt = optionValue "--occurred-at" arguments |> Option.defaultValue ""
          Kind = optionValue "--kind" arguments
          Summary = optionValue "--summary" arguments
          Subjects = optionValues "--subject" arguments
          Candidate = optionValue "--candidate" arguments
          References = optionValues "--reference" arguments
          Decision = optionValue "--decision" arguments
          Outcome = optionValue "--outcome" arguments
          Attestation = optionValue "--attestation" arguments }

    let private identityBasis (arguments: string list) =
        match FileTelemetryExecutionRepository.resolveIdentity (ProvenanceCommands.identityOverridesFrom arguments) with
        | Error message -> Error message
        | Ok(_, _, source) ->
            let kindSource =
                if (optionValue "--actor-kind" arguments).IsSome then "flag"
                elif not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "ROS_ACTOR_KIND")) then "environment"
                else "discovery-mechanism"

            Ok { Mechanism = source.Mechanism; ActorKindSource = kindSource }

    /// The execution the statement belongs to. An execution that belongs to a
    /// different actor is refused, never borrowed; no execution is recorded as
    /// none rather than guessed.
    let private execution root (arguments: string list) (workItemId: string) =
        match FileCheckpointRepository.resolveExecution root workItemId (ProvenanceCommands.identityOverridesFrom arguments) (optionValue "--execution" arguments) with
        | ExecutionObservation.Resolved(executionId, _) -> Ok(Some executionId)
        | ExecutionObservation.NoneActive -> Ok None
        | ExecutionObservation.Ambiguous ids -> Error $"""several active executions could own this statement ({String.Join(", ", ids)}); pass --execution EXE-ID"""
        | ExecutionObservation.Refused reason -> Error reason

    let private record root (arguments: string list) (actor: Actor) (statement: ProcessStatement) (occurredAt: string) (workItemId: string) =
        match identityBasis arguments with
        | Error message -> Error message
        | Ok basis ->
            match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
            | Error failure -> Error failure.Message
            | Ok lease ->
                let result =
                    try
                        match WorkStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match execution root arguments workItemId with
                            | Error message -> Error message
                            | Ok executionId ->
                                let branch, commit = ProcessGitRepository.readBranchAndCommit root

                                let observation =
                                    { Commit = commit
                                      Branch = branch
                                      ExecutionId = executionId
                                      ExecutionRole = executionId |> Option.bind (FileProcessEvidenceRepository.readExecutionRole root)
                                      Identity = basis }

                                let assessment = ProcessEvidence.classify actor basis statement

                                let event =
                                    FileProcessEvidenceRepository.eventNode
                                        (FileWorkConfigRepository.readRepositoryId root)
                                        (FileWorkConfigRepository.readProtocolVersion root)
                                        workItemId
                                        occurredAt
                                        actor
                                        statement
                                        assessment
                                        observation

                                FileProcessEvidenceRepository.record root event |> Result.map (fun eventId -> eventId, assessment, event)
                    with error ->
                        Error $"state persistence failed: {error.Message}"

                match lease.Release(), result with
                | Error failure, Ok _ -> Error failure.Message
                | _, value -> value

    let run root (arguments: string list) (actor: Actor) =
        let parsed = request arguments

        match ProcessEvidence.validate parsed with
        | Error errors ->
            for error in errors do
                eprintfn "ERROR %s" error

            eprintfn "Usage: praxis %s" usage
            2
        | Ok statement ->
            match record root arguments actor statement parsed.OccurredAt parsed.WorkItemId with
            | Error message ->
                eprintfn "ERROR %s" message
                eprintfn "process evidence rejected; nothing was recorded"
                1
            | Ok(eventId, assessment, event) ->
                if List.contains "--json" arguments then
                    printf "%s" (event.ToJsonString compact)
                else
                    printfn
                        "recorded %s for %s as %s (event %s)"
                        (ProcessEvidenceKind.code statement.Kind)
                        parsed.WorkItemId
                        (EvidenceProvenance.code assessment.Class)
                        eventId

                    printfn "basis: %s" assessment.Basis

                    assessment.Authenticity
                    |> Option.iter (fun authenticity -> printfn "authenticity: %s" authenticity)

                0
