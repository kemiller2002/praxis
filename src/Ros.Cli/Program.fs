module Ros.Cli.Program

open System
open System.Collections.Generic
open System.IO
open Ros.Application.Artifacts
open Ros.Application.Git
open Ros.Application.Work
open Ros.Contracts.Cli
open Ros.Contracts.Git
open Ros.Contracts.Work
open Ros.Domain.Artifacts
open Ros.Domain.Git
open Ros.Domain.Work
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work

[<Literal>]
let Version = "0.2.0-shadow"

let private usage =
    "Usage: ros-fs [--root PATH] version | artifacts validate [--json] | registry build [--dry-run] | registry check | git status [--json] | work decide --state STATE --action ACTION [--reason TEXT] [--required TYPE] [--provided TYPE] [--json] | work plan --id ID --type TYPE --state STATE --action ACTION --occurred-at TIME [options]"

let private parseRoot (arguments: string array) =
    let values = ResizeArray<string>(arguments)
    let rootIndex = values.IndexOf("--root")

    if rootIndex < 0 then
        Ok(Path.GetFullPath(Directory.GetCurrentDirectory()), values |> Seq.toList)
    elif rootIndex + 1 >= values.Count || values[rootIndex + 1].StartsWith("--", StringComparison.Ordinal) then
        Error "--root requires a value"
    else
        let root = Path.GetFullPath values[rootIndex + 1]
        values.RemoveAt(rootIndex + 1)
        values.RemoveAt(rootIndex)
        Ok(root, values |> Seq.toList)

let private renderFinding (finding: ArtifactFinding) =
    let location =
        if String.IsNullOrEmpty finding.Field then finding.Path else $"{finding.Path}:{finding.Field}"

    $"{location}: {finding.Message}"

let private reportDependencyFailure (failure: DependencyFailure) =
    let location = failure.Path |> Option.map (fun path -> $" '{path}'") |> Option.defaultValue ""

    let outcome =
        match failure.Outcome with
        | DependencyOutcome.Failed -> "failed"
        | DependencyOutcome.Indeterminate -> "indeterminate"

    eprintfn "ERROR %s%s %s: %s" failure.Operation location outcome failure.Message
    1

let private runValidation asJson repository =
    match ArtifactOperations.validate repository with
    | ValidationOutcome.DependencyFailure failure -> reportDependencyFailure failure
    | ValidationOutcome.Completed findings when asJson ->
        printf "%s" (FindingContract.renderJson findings)
        if findings.IsEmpty then 0 else 1
    | ValidationOutcome.Completed [] ->
        printfn "validation passed"
        0
    | ValidationOutcome.Completed findings ->
        for finding in findings do
            eprintfn "ERROR %s\n  REPAIR %s" (renderFinding finding) (FindingContract.repair finding)

        eprintfn "validation failed with %d error(s)" findings.Length
        1

let private runRegistryCheck repository =
    match ArtifactOperations.checkRegistries repository with
    | RegistryCheckOutcome.DependencyFailure failure -> reportDependencyFailure failure
    | RegistryCheckOutcome.Completed [] ->
        printfn "registries are current"
        0
    | RegistryCheckOutcome.Completed findings ->
        for finding in findings do
            eprintfn "ERROR %s" (renderFinding finding)
        1

let private runRegistryBuild dryRun repository =
    let reportChange (change: RegistryChange) =
        printfn "%s %s" (if dryRun then "WOULD WRITE" else "WROTE") change.Path

    match ArtifactOperations.buildRegistries dryRun repository with
    | RegistryBuildOutcome.DependencyFailure failure -> reportDependencyFailure failure
    | RegistryBuildOutcome.Rejected findings ->
        for finding in findings do
            eprintfn "ERROR %s" (renderFinding finding)
        1
    | RegistryBuildOutcome.Completed changes ->
        changes |> List.iter reportChange
        printfn "%d registry file(s) %s" changes.Length (if dryRun then "would change" else "changed")
        0
    | RegistryBuildOutcome.Incomplete(written, pending, failure) ->
        written |> List.iter reportChange
        reportDependencyFailure failure |> ignore
        eprintfn "registry build incomplete: %d written; %d pending" written.Length pending.Length
        1

let private renderGitChange change =
    match change.OriginalPath with
    | Some originalPath -> $"{GitStatus.code change.Status} {originalPath} -> {change.Path}"
    | None -> $"{GitStatus.code change.Status} {change.Path}"

let private runGitStatus asJson repository =
    let observation = GitOperations.observe repository

    if asJson then
        printf "%s" (GitStatusContract.renderJson observation)

    match observation with
    | GitStatusObservation.Clean ->
        if not asJson then printfn "working tree clean"
        0
    | GitStatusObservation.Changed changes ->
        if not asJson then changes |> List.iter (renderGitChange >> printfn "%s")
        0
    | GitStatusObservation.Unavailable failure ->
        if not asJson then eprintfn "ERROR %s unavailable: %s" failure.Operation failure.Message
        1

let private optionValue name arguments =
    arguments
    |> List.tryFindIndex ((=) name)
    |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

let private optionValues name arguments =
    arguments
    |> List.mapi (fun index value -> index, value)
    |> List.choose (fun (index, value) -> if value = name then arguments |> List.tryItem (index + 1) else None)

let private parseWorkState value =
    match value with
    | "ready" -> Some LiveWorkState.Ready
    | "active" -> Some LiveWorkState.Active
    | "blocked" -> Some LiveWorkState.Blocked
    | "complete" -> Some LiveWorkState.Complete
    | _ -> None

let private parseWorkAction value =
    match value with
    | "begin" -> Some WorkAction.Begin
    | "block" -> Some WorkAction.Block
    | "resume" -> Some WorkAction.Resume
    | "complete" -> Some WorkAction.Complete
    | _ -> None

let private runWorkDecision arguments =
    let state = optionValue "--state" arguments |> Option.bind parseWorkState
    let action = optionValue "--action" arguments |> Option.bind parseWorkAction

    match state, action with
    | Some current, Some requested ->
        let request =
            { State = current
              Action = requested
              BlockReason = optionValue "--reason" arguments
              RequiredEvidence = optionValues "--required" arguments |> Set.ofList
              ProvidedEvidence = optionValues "--provided" arguments |> Set.ofList }

        let decision = WorkOperations.decideTransition request
        printf "%s" (WorkDecisionContract.renderJson decision)

        match decision with
        | TransitionDecision.Allowed _ -> 0
        | TransitionDecision.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work decide requires a valid --state and --action"
        2

let private parseEvidence (value: string) : WorkEvidence option =
    match value.Split('=', 2) with
    | [| evidenceType; path |] when evidenceType.Length > 0 && path.Length > 0 ->
        Some
            { Type = evidenceType
              Path = path }
    | _ -> None

let private defaultLocalState (action: WorkAction) =
    match action with
    | WorkAction.Begin
    | WorkAction.Resume -> "active"
    | WorkAction.Block -> "blocked"
    | WorkAction.Complete -> "complete"

let private runWorkPlan root arguments =
    let state = optionValue "--state" arguments |> Option.bind parseWorkState
    let action = optionValue "--action" arguments |> Option.bind parseWorkAction
    let workItem = optionValue "--id" arguments
    let workType = optionValue "--type" arguments
    let occurredAt = optionValue "--occurred-at" arguments
    let currentEvidence = optionValues "--current-evidence" arguments |> List.map parseEvidence
    let providedEvidence = optionValues "--evidence" arguments |> List.map parseEvidence

    match state, action, workItem, workType, occurredAt with
    | Some current, Some requested, Some workItemId, Some itemType, Some timestamp
        when currentEvidence |> List.forall Option.isSome
             && providedEvidence |> List.forall Option.isSome ->
        let request =
            { Item =
                { Id = workItemId
                  WorkType = itemType
                  LocalState = optionValue "--local-state" arguments |> Option.defaultValue (WorkDecisionContract.stateName current)
                  SemanticState = current
                  Evidence = currentEvidence |> List.choose id
                  BlockReason = optionValue "--current-block-reason" arguments
                  UpdatedAt = optionValue "--updated-at" arguments
                  CompletedAt = optionValue "--completed-at" arguments
                  TelemetryExecutionIds = optionValues "--telemetry-id" arguments }
              Action = requested
              TargetLocalState = optionValue "--target-local-state" arguments |> Option.defaultValue (defaultLocalState requested)
              BlockReason = optionValue "--reason" arguments
              RequiredEvidence = optionValues "--required" arguments |> Set.ofList
              ProvidedEvidence = providedEvidence |> List.choose id
              Repository = optionValue "--repository" arguments |> Option.defaultValue "repository"
              ProtocolVersion = optionValue "--protocol-version" arguments |> Option.defaultValue "1.0.0"
              OccurredAt = timestamp
              ChangedPaths = optionValues "--path" arguments
              TelemetryEnabled = arguments |> List.contains "--telemetry-enabled" }

        if arguments |> List.contains "--verify-evidence" then
            let outcome = WorkOperations.planVerifiedTransition (FileEvidenceRepository.create root) request
            printf "%s" (WorkPlanContract.renderVerifiedJson outcome)

            match outcome with
            | VerifiedWorkPlanOutcome.Planned _ -> 0
            | VerifiedWorkPlanOutcome.TransitionRejected _
            | VerifiedWorkPlanOutcome.EvidenceRejected _ -> 1
        else
            let outcome = WorkOperations.planTransition request
            printf "%s" (WorkPlanContract.renderJson outcome)

            match outcome with
            | WorkPlanOutcome.Planned _ -> 0
            | WorkPlanOutcome.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work plan requires valid --id, --type, --state, --action, --occurred-at, and TYPE=PATH evidence"
        2

let private dispatch root arguments =
    let repository = FileArtifactRepository.create root
    let gitRepository = ProcessGitRepository.create root

    match arguments with
    | [ "version" ]
    | [ "--version" ] ->
        printfn "ros-fs %s" Version
        0
    | []
    | [ "help" ]
    | [ "--help" ]
    | [ "-h" ] ->
        printfn "%s" usage
        0
    | "artifacts" :: "validate" :: rest when rest |> List.forall ((=) "--json") ->
        runValidation (rest |> List.contains "--json") repository
    | "registry" :: "build" :: rest when rest |> List.forall ((=) "--dry-run") ->
        runRegistryBuild (rest |> List.contains "--dry-run") repository
    | [ "registry"; "check" ] -> runRegistryCheck repository
    | "git" :: "status" :: rest when rest |> List.forall ((=) "--json") ->
        runGitStatus (rest |> List.contains "--json") gitRepository
    | "work" :: "decide" :: rest -> runWorkDecision rest
    | "work" :: "plan" :: rest -> runWorkPlan root rest
    | _ ->
        eprintfn "%s" usage
        2

[<EntryPoint>]
let main arguments =
    try
        match parseRoot arguments with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok(root, remaining) -> dispatch root remaining
    with error ->
        eprintfn "ERROR %s" error.Message
        1
