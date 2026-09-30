namespace Ros.Cli

open System
open Ros.Contracts.Integration
open Ros.Domain.Git
open Ros.Domain.Integration
open Ros.Infrastructure.Integration

[<RequireQualifiedAccess>]
module MergeReadinessCommands =
    let usage = "merge readiness [--evidence FILE] [--json]"

    type private Parsed =
        { Evidence: string option
          Json: bool
          Errors: string list }

    let private parse arguments =
        let rec loop remaining parsed =
            match remaining with
            | [] -> parsed
            | "--json" :: rest -> loop rest { parsed with Json = true }
            | "--evidence" :: value :: rest when not (value.StartsWith("--", StringComparison.Ordinal)) ->
                match parsed.Evidence with
                | None -> loop rest { parsed with Evidence = Some value }
                | Some _ -> loop rest { parsed with Errors = parsed.Errors @ [ "--evidence may be passed once" ] }
            | "--evidence" :: _ ->
                loop [] { parsed with Errors = parsed.Errors @ [ "--evidence requires FILE" ] }
            | unexpected :: rest ->
                loop rest { parsed with Errors = parsed.Errors @ [ $"unexpected argument '{unexpected}'" ] }

        loop arguments { Evidence = None; Json = false; Errors = [] }

    let private printText policy observation decision =
        match decision with
        | MergeReadinessDecision.Disabled ->
            printfn "Merge readiness: DISABLED"
            printfn "This repository has not enabled the merge-readiness policy."
        | MergeReadinessDecision.Ready commit ->
            printfn "Merge readiness: READY"
            printfn ""
            printfn "Candidate: %s" (CommitId.value commit)
            printfn "Required checks: %s" (if policy.RequiredChecks.IsEmpty then "(none)" else String.concat ", " policy.RequiredChecks)
            printfn "Working tree: %s" (if observation.WorkingTreeClean = Some true then "clean" else "not established")
            printfn "Remote candidate: %s" (if observation.Evidence.RemoteCandidateCurrent = Some true then "current" else "not established")
            printfn ""
            printfn "This exact commit satisfies the configured merge-readiness policy."
        | MergeReadinessDecision.NotReady(candidate, blockers) ->
            printfn "Merge readiness: NOT READY"
            printfn ""

            match candidate with
            | Some commit -> printfn "Candidate: %s" (CommitId.value commit)
            | None -> printfn "Candidate: unknown"

            match observation.Head with
            | Some head -> printfn "HEAD: %s" (CommitId.value head)
            | None -> printfn "HEAD: unknown"

            printfn ""
            printfn "Blockers:"

            for blocker in blockers do
                printfn "  - [%s] %s" (MergeBlocker.code blocker) (MergeBlocker.message blocker)

            printfn ""
            printfn "Development may continue and durable checkpoints remain valid; this exact candidate must not be represented as merge-ready."

    let run root arguments =
        let parsed = parse arguments

        if not parsed.Errors.IsEmpty then
            parsed.Errors |> List.iter (eprintfn "ERROR %s")
            eprintfn "Usage: praxis %s" usage
            2
        else
            match FileMergeReadinessRepository.readPolicy root with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok policy ->
                match FileMergeReadinessRepository.readEvidence root parsed.Evidence with
                | Error message ->
                    eprintfn "ERROR %s" message
                    1
                | Ok evidence ->
                    let observation = FileMergeReadinessRepository.observe root evidence
                    let decision = MergeReadiness.decide policy observation

                    if parsed.Json then
                        printf "%s" (MergeReadinessJson.renderDecision policy observation decision)
                    else
                        printText policy observation decision

                    match decision with
                    | MergeReadinessDecision.Disabled
                    | MergeReadinessDecision.Ready _ -> 0
                    | MergeReadinessDecision.NotReady _ -> 1
