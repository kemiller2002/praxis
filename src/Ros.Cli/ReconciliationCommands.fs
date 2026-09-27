namespace Ros.Cli

open System
open System.Globalization
open Ros.Contracts.Work
open Ros.Domain.Git
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work

/// `work reconcile`: post-hoc, Git-evidenced attribution reconciliation
/// (issue #80). Extracted from the composition root as `DF-ROS-2026-A035`
/// requires for a new command family: this module parses, delegates to
/// `FileReconciliationRepository` (which composes the Application planner
/// and the Domain decision), and renders. It holds no reconciliation policy.
[<RequireQualifiedAccess>]
module ReconciliationCommands =
    let usage =
        "work reconcile --id ID --occurred-at TIMESTAMP --reason TEXT (--commit REV | --range BASE..HEAD) [--commit REV]* [--range BASE..HEAD]* [--path PATH]* [--dry-run] [--json] [IDENTITY]"

    let private flagsWithValues =
        set
            [ "--id"
              "--occurred-at"
              "--reason"
              "--commit"
              "--range"
              "--path"
              "--actor-kind"
              "--agent"
              "--actor"
              "--provider"
              "--model"
              "--model-version"
              "--runtime"
              "--runtime-version"
              "--session"
              "--conversation"
              "--run"
              "--subagent" ]

    let private switches = set [ "--dry-run"; "--json" ]

    type private Parsed =
        { Ids: string list
          OccurredAt: string list
          Reasons: string list
          Selectors: Result<GitEvidenceSelector, string> list
          Paths: string list
          Unexpected: string list }

    let rec private parse (parsed: Parsed) (arguments: string list) =
        match arguments with
        | [] -> parsed
        | flag :: value :: rest when flagsWithValues.Contains flag && not (value.StartsWith "--") ->
            let next =
                match flag with
                | "--id" -> { parsed with Ids = parsed.Ids @ [ value ] }
                | "--occurred-at" -> { parsed with OccurredAt = parsed.OccurredAt @ [ value ] }
                | "--reason" -> { parsed with Reasons = parsed.Reasons @ [ value ] }
                | "--commit" -> { parsed with Selectors = parsed.Selectors @ [ GitEvidenceSelector.tryParseCommit value ] }
                | "--range" -> { parsed with Selectors = parsed.Selectors @ [ GitEvidenceSelector.tryParseRange value ] }
                | "--path" -> { parsed with Paths = parsed.Paths @ [ value ] }
                | _ -> parsed

            parse next rest
        | switch :: rest when switches.Contains switch -> parse parsed rest
        | token :: rest -> parse { parsed with Unexpected = parsed.Unexpected @ [ token ] } rest

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    let private argumentErrors (parsed: Parsed) =
        [ match parsed.Ids with
          | [ id ] when WorkItemId.isValid id -> ()
          | [ id ] -> yield $"'{id}' is not a valid work-item ID"
          | [] -> yield "work reconcile requires --id ID"
          | _ -> yield "work reconcile attributes to exactly one work item; pass --id once"
          match parsed.OccurredAt with
          | [ value ] when isTimestamp value -> ()
          | [ value ] -> yield $"--occurred-at '{value}' is not a timestamp"
          | _ -> yield "work reconcile requires exactly one --occurred-at TIMESTAMP (the real current time)"
          match parsed.Reasons with
          | [ value ] when not (String.IsNullOrWhiteSpace value) -> ()
          | _ -> yield "work reconcile requires one non-empty --reason explaining why attribution was missing"
          if parsed.Selectors.IsEmpty then
              yield "work reconcile requires Git evidence: at least one --commit REV or --range BASE..HEAD"
          for selector in parsed.Selectors do
              match selector with
              | Error message -> yield message
              | Ok _ -> ()
          for token in parsed.Unexpected do
              yield $"unexpected argument '{token}'" ]

    let private short (sha: string) = if sha.Length > 12 then sha.Substring(0, 12) else sha

    let private dispositionLabel disposition =
        match disposition with
        | ReconciliationDisposition.Reconciled -> "reconciled"
        | ReconciliationDisposition.NotMeaningful -> "not meaningful"
        | ReconciliationDisposition.NotSelected -> "not selected"
        | ReconciliationDisposition.AlreadyAttributed -> "already attributed"
        | ReconciliationDisposition.AlreadyReconciled eventId -> $"already reconciled (event {eventId})"
        | ReconciliationDisposition.Conflict(workItem, eventId) -> $"conflict: {workItem} (event {eventId})"

    let private changeLabel (assessment: ReconciliationAssessment) =
        let kind = GitCommitChangeKind.code assessment.Change.Kind

        match assessment.Change.OriginalPath with
        | Some original when assessment.Path = original -> $"{kind} -> {assessment.Change.Path}"
        | Some original -> $"{kind} <- {original}"
        | None -> kind

    let private renderEvidence (evidence: GitReconciliationEvidence) (assessments: ReconciliationAssessment list) =
        printfn "evidence: HEAD %s" evidence.Head

        for commit in evidence.Commits do
            printfn
                "  commit %s  author %s <%s> %s  \"%s\""
                (short commit.Sha)
                commit.Author.Name
                commit.Author.Email
                commit.Author.Date
                commit.Subject

            for assessment in assessments |> List.filter (fun assessment -> assessment.Commit = commit.Sha) do
                printfn "    %-20s %s  (%s)" (dispositionLabel assessment.Disposition) assessment.Path (changeLabel assessment)

    let private render asJson (outcome: ReconciliationOutcome) (workItemId: string) =
        match outcome with
        | ReconciliationOutcome.Rejected rejections ->
            for rejection in rejections do
                eprintfn "ERROR %s" (ReconciliationRejection.message rejection)

            eprintfn "reconciliation rejected; nothing was recorded"
            1
        | ReconciliationOutcome.Recorded(plan, eventId) ->
            if asJson then
                printf "%s" (ReconciliationOutputContract.renderJson "reconciled" workItemId (Some eventId) (Some plan.Evidence) plan.ReconciledPaths plan.Assessments)
            else
                printfn "reconciled %d path(s) to %s as post-hoc attribution (event %s)" plan.ReconciledPaths.Length workItemId eventId
                printfn "this records recovery after the fact; the original changes remain unattributed at the time they were made"
                renderEvidence plan.Evidence plan.Assessments

            0
        | ReconciliationOutcome.Planned plan ->
            if asJson then
                printf "%s" (ReconciliationOutputContract.renderJson "planned" workItemId None (Some plan.Evidence) plan.ReconciledPaths plan.Assessments)
            else
                printfn "dry run: would reconcile %d path(s) to %s as post-hoc attribution; nothing was recorded" plan.ReconciledPaths.Length workItemId
                renderEvidence plan.Evidence plan.Assessments

            0
        | ReconciliationOutcome.NothingToReconcile(evidence, assessments) ->
            if asJson then
                printf "%s" (ReconciliationOutputContract.renderJson "nothing-to-reconcile" workItemId None (Some evidence) [] assessments)
            else
                printfn "nothing to reconcile for %s: every meaningful path the selected commits changed is already attributed or not selected" workItemId
                renderEvidence evidence assessments

            0

    let run root (arguments: string list) (actor: Actor) =
        let parsed =
            parse
                { Ids = []
                  OccurredAt = []
                  Reasons = []
                  Selectors = []
                  Paths = []
                  Unexpected = [] }
                arguments

        match argumentErrors parsed with
        | _ :: _ as errors ->
            for error in errors do
                eprintfn "ERROR %s" error

            eprintfn "Usage: ros %s" usage
            2
        | [] ->
            let command: ReconciliationCommand =
                { WorkItemId = List.head parsed.Ids
                  Reason = List.head parsed.Reasons
                  Selectors = parsed.Selectors |> List.choose Result.toOption
                  PathRestriction = parsed.Paths
                  OccurredAt = List.head parsed.OccurredAt
                  Actor = actor
                  DryRun = List.contains "--dry-run" arguments }

            match FileReconciliationRepository.reconcile root (ProcessGitRepository.createHistory root) command with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok outcome -> render (List.contains "--json" arguments) outcome command.WorkItemId
