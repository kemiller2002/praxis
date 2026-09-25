module Ros.Cli.ProvenanceCommands

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Provenance
open Ros.Infrastructure.Provenance

/// The `identity` and `provenance` command family (`DF-ROS-2026-A036`),
/// kept out of the composition root per `DF-ROS-2026-A035`: this module
/// parses syntax, calls Infrastructure/Domain, and renders; the provenance
/// rules live in `Ros.Domain.Provenance`.

let usage =
    "identity [--json] [--actor NAME] | provenance record PATH [PATH]* [--operation {created|modified|reviewed|approved}] [--reason TEXT] [--evidence REF]* [--work-item ID] [--basis REF] [--at TIMESTAMP] [--actor NAME] | provenance show TARGET [--json] | provenance validate [--json] | provenance summary [--json]"

let private flagsWithValues =
    set [ "--operation"; "--reason"; "--evidence"; "--work-item"; "--basis"; "--at"; "--actor" ]

let private optionValue name (arguments: string list) =
    arguments
    |> List.pairwise
    |> List.tryFind (fun (flag, _) -> flag = name)
    |> Option.map snd

let private optionValues name (arguments: string list) =
    arguments
    |> List.pairwise
    |> List.filter (fun (flag, _) -> flag = name)
    |> List.map snd

let rec private positional (arguments: string list) =
    match arguments with
    | [] -> []
    | flag :: _ :: rest when flagsWithValues.Contains flag -> positional rest
    | flag :: rest when flag.StartsWith("--", StringComparison.Ordinal) -> positional rest
    | value :: rest -> value :: positional rest

let private nowIso () = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

// ---- identity -------------------------------------------------------------

let runIdentity root (arguments: string list) =
    let current = FileProvenanceRepository.currentActor root (optionValue "--actor" arguments)

    if arguments |> List.contains "--json" then
        printf "%s" (ProvenanceJson.renderIdentity current.Actor current.Binding current.Mechanism)
    else
        printfn "%s" (Actor.describe current.Actor)

        for name, value in Actor.fields current.Actor do
            printfn "  %s: %s" name value

        match current.Binding with
        | ExecutionBinding.Explicit id -> printfn "  execution bound explicitly (ROS_EXECUTION_ID=%s)" id
        | ExecutionBinding.Matched id -> printfn "  execution bound to active execution %s" id
        | ExecutionBinding.Unbound -> printfn "  no active execution bound; run 'work begin' (or set ROS_EXECUTION_ID) before recording work"

        printfn "  discovery: %s; assurance: self-reported (provenance, not authentication)" current.Mechanism

    0

// ---- provenance record ------------------------------------------------------

/// Fallback when no execution is bound: the single active work item, when
/// there is exactly one. (A bound execution's own work item wins.)
let private activeWorkItem root =
    let path = Path.Combine(root, ".ros", "context", "current.json")

    if not (File.Exists path) then
        None
    else
        try
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as context ->
                match context["workItems"] with
                | :? JsonArray as items ->
                    items
                    |> Seq.choose (function
                        | :? JsonObject as item ->
                            match item["semanticState"], item["id"] with
                            | :? JsonValue as state, (:? JsonValue as id) when state.ToString() = "active" -> Some(id.ToString())
                            | _ -> None
                        | _ -> None)
                    |> Seq.toList
                    |> function
                        | [ single ] -> Some single
                        | _ -> None
                | _ -> None
            | _ -> None
        with _ ->
            None

let runRecord root (arguments: string list) =
    let paths = positional arguments

    let operation =
        match optionValue "--operation" arguments with
        | None -> Ok None
        | Some raw ->
            match Operation.tryParse raw with
            | Some operation -> Ok(Some operation)
            | None -> Error $"unknown operation '{raw}'; use created, modified, reviewed, or approved"

    let at = optionValue "--at" arguments |> Option.defaultValue (nowIso ())

    match paths, operation with
    | [], _ ->
        eprintfn "ERROR provenance record requires at least one artifact PATH"
        2
    | _, Error message ->
        eprintfn "ERROR %s" message
        2
    | _, Ok _ when (Timestamp.tryParse at).IsNone ->
        eprintfn "ERROR --at '%s' is not an ISO-8601 timestamp" at
        2
    | _, Ok explicitOperation ->
        let current = FileProvenanceRepository.currentActor root (optionValue "--actor" arguments)
        let workItem =
            optionValue "--work-item" arguments
            |> Option.orElse current.WorkItem
            |> Option.orElse (activeWorkItem root)

        let build operation =
            FileProvenanceRepository.contribution
                current
                operation
                at
                workItem
                (optionValue "--reason" arguments)
                (optionValues "--evidence" arguments)
                (optionValue "--basis" arguments)

        let results =
            paths
            |> List.map (fun path -> path, FileProvenanceRepository.recordArtifactContribution root path explicitOperation build)

        for path, result in results do
            match result with
            | Ok(contribution, true) ->
                printfn "recorded %s on %s by %s" (Operation.code contribution.Operation) path (Actor.describe contribution.Actor)
            | Ok(contribution, false) ->
                printfn "unchanged %s: %s by this actor at %s is already recorded" path (Operation.code contribution.Operation) contribution.At
            | Error message -> eprintfn "ERROR %s" message

        if current.Binding = ExecutionBinding.Unbound && current.Actor.Kind <> ActorKind.Human then
            eprintfn "WARN no active Praxis execution is bound; contributions record executionId 'unknown'"

        if results |> List.exists (snd >> Result.isError) then 1 else 0

// ---- provenance show / validate / summary ---------------------------------

let runShow root (arguments: string list) =
    match positional arguments with
    | [ target ] ->
        let normalized = target.Replace('\\', '/')

        let matches =
            FileProvenanceRepository.attributedRecords root
            |> List.filter (fun record ->
                record.Kind <> RecordKind.Event && (record.RecordId = target || record.Path = normalized))

        match matches with
        | [] ->
            eprintfn "ERROR no artifact or backlog item '%s'" target
            1
        | record :: _ when arguments |> List.contains "--json" ->
            printf "%s" (ProvenanceJson.renderRecord record)
            0
        | record :: _ ->
            printfn "%s %s (%s)" (RecordKind.code record.Kind) record.RecordId record.Path

            match Provenance.originalCreator record.Provenance with
            | Some creator -> printfn "  creator: %s at %s" (Actor.describe creator.Actor) creator.At
            | None -> printfn "  creator: unknown (no 'created' contribution recorded)"

            printfn "  involvement: %s" (Provenance.involvementLabels record.Provenance |> String.concat ", ")

            if not record.DerivedFrom.IsEmpty then
                printfn "  derived from: %s" (record.DerivedFrom |> String.concat ", ")

            for contribution in record.Provenance.Contributions do
                let reason = contribution.Reason |> Option.map (sprintf " -- %s") |> Option.defaultValue ""
                printfn "  %s %s by %s%s" contribution.At (Operation.code contribution.Operation) (Actor.describe contribution.Actor) reason

            0
    | _ ->
        eprintfn "ERROR provenance show requires exactly one TARGET (artifact id, backlog id, or artifact path)"
        2

/// Provenance errors are also part of `validate`; this command adds the
/// warnings and informational legacy findings.
let runValidate root (arguments: string list) =
    let findings = FileProvenanceRepository.findings root
    let errors = findings |> List.filter (fun finding -> finding.Severity = Severity.Error)

    if arguments |> List.contains "--json" then
        printf "%s" (ProvenanceJson.renderFindings findings)
    else
        let count severity = findings |> List.filter (fun finding -> finding.Severity = severity) |> List.length

        for finding in findings |> List.filter (fun finding -> finding.Severity <> Severity.Info) do
            eprintfn "%s %s:%s [%s] %s" ((Severity.code finding.Severity).ToUpperInvariant()) finding.Path finding.Field finding.Code finding.Message

        printfn
            "provenance: %d error(s), %d warning(s), %d legacy/informational record(s)"
            errors.Length
            (count Severity.Warning)
            (count Severity.Info)

    if errors.IsEmpty then 0 else 1

let runSummary root (arguments: string list) =
    let summary = FileProvenanceRepository.attributedRecords root |> ProvenanceSummary.summarize

    if arguments |> List.contains "--json" then
        printf "%s" (ProvenanceJson.renderSummary summary)
    else
        printfn "%d record(s): %d attributed, %d unattributed" summary.Records summary.AttributedRecords summary.UnattributedRecords

        for totals in summary.Actors do
            printfn
                "  %s %s: %d created, %d modified, %d execution(s)"
                totals.Kind
                totals.Id
                totals.RecordsCreated
                totals.RecordsModified
                totals.Executions.Length

        if not summary.HumanCorrectionsOfAgentWork.IsEmpty then
            printfn "  human corrections of agent work: %s" (summary.HumanCorrectionsOfAgentWork |> String.concat ", ")

        if not summary.AgentToAgentRevisions.IsEmpty then
            printfn "  agent-to-agent revisions: %s" (summary.AgentToAgentRevisions |> String.concat ", ")

    0

/// Provenance errors contributed to the unified `validate` (warnings and
/// informational legacy findings stay in `provenance validate`).
let unifiedErrors root =
    FileProvenanceRepository.findings root
    |> List.filter (fun finding -> finding.Severity = Severity.Error)
    |> List.map (fun finding -> finding.Path, finding.Field, $"[{finding.Code}] {finding.Message}")

let dispatch root (arguments: string list) : int option =
    match arguments with
    | "identity" :: rest -> Some(runIdentity root rest)
    | "provenance" :: "record" :: rest -> Some(runRecord root rest)
    | "provenance" :: "show" :: rest -> Some(runShow root rest)
    | "provenance" :: "validate" :: rest -> Some(runValidate root rest)
    | "provenance" :: "summary" :: rest -> Some(runSummary root rest)
    | _ -> None
