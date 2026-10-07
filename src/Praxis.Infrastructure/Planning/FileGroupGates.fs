namespace Praxis.Infrastructure.Planning

open System
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Git
open Praxis.Infrastructure.Provenance
open Praxis.Infrastructure.Work

/// The file and Git edge of the grouped-execution completion gates
/// (PRX-GRP-133..135). It finds whether a completing member runs in grouped
/// mode, reads the two evidence documents as committed at `HEAD`, and
/// observes the ordering and path facts; `GroupGates.judge` decides.
[<RequireQualifiedAccess>]
module FileGroupGates =
    let private lines (root: string) (arguments: string list) = ProcessGitRepository.readLines root arguments

    /// A document as committed at `HEAD`: evidence that exists only in the
    /// working tree was not committed and is malformed for the gate.
    let private committed (root: string) (path: string) (decode: string -> EvidenceReading<'T>) : EvidenceReading<'T> =
        match lines root [ "show"; $"HEAD:{path.Replace('\\', '/').TrimStart('.', '/')}" ] with
        | Ok content -> decode (String.concat "\n" content)
        | Error _ -> EvidenceReading.Malformed $"'{path}' is not committed at HEAD"

    let private observe evidenceType (decode: string -> EvidenceReading<'T>) (root: string) (provided: WorkEvidence list) =
        CompletionReadinessOperations.observe (fun path -> committed root path decode) evidenceType provided

    let private startCommit (root: string) (executionId: string) : string option =
        match FileTelemetryQueryRepository.readByExecutionId root executionId with
        | Ok record ->
            match record["repository"] with
            | :? JsonObject as repository ->
                match repository["start"] with
                | :? JsonObject as start ->
                    match start["commit"] with
                    | :? JsonValue as commit -> Some(commit.GetValue<string>())
                    | _ -> None
                | _ -> None
            | _ -> None
        | Error _ -> None

    let private normalize (path: string) = path.Replace('\\', '/').TrimStart('.', '/')

    /// PRX-GRP-133: the analysis was committed in an ancestor of the
    /// member's first attributed commit, the first commit since its
    /// execution started that changes anything but Praxis state and the two
    /// evidence documents. A member that changed nothing else passes.
    let analysisPrecedesChanges (root: string) (executionId: string) (analysisPath: string) (exempt: string list) : Result<bool, string> =
        let analysis = normalize analysisPath
        let exempt = exempt |> List.map normalize |> Set.ofList |> Set.add analysis

        match startCommit root executionId with
        | None -> Error $"execution {executionId} records no start commit"
        | Some start ->
            match lines root [ "log"; "--diff-filter=A"; "--format=%H"; "--reverse"; "HEAD"; "--"; analysis ] with
            | Error failure -> Error failure.Message
            | Ok [] -> Error $"'{analysis}' was never added in HEAD's history"
            | Ok(added :: _) ->
                match lines root [ "rev-list"; "--reverse"; $"{start}..HEAD" ] with
                | Error failure -> Error failure.Message
                | Ok commits ->
                    let meaningful commit =
                        match lines root [ "diff-tree"; "--no-commit-id"; "--name-only"; "-r"; commit ] with
                        | Ok paths -> paths |> List.exists (fun path -> not (path.StartsWith(".ros/", StringComparison.Ordinal)) && not (exempt.Contains path))
                        | Error _ -> true

                    match commits |> List.tryFind meaningful with
                    | None -> Ok true
                    | Some first ->
                        match lines root [ "merge-base"; added; first ] with
                        | Ok [ mergeBase ] -> Ok(mergeBase = added)
                        | Ok _ -> Ok false
                        | Error failure -> Error failure.Message

    let private pathExists (root: string) (path: string) =
        match lines root [ "cat-file"; "-e"; $"HEAD:{normalize path}" ] with
        | Ok _ -> true
        | Error _ -> false

    /// The `group-verified` facet for each completing item: `Some` only for
    /// an item whose active execution was begun in grouped mode by a group
    /// execution; an unreadable group store fails closed.
    let facet (root: string) (provided: WorkEvidence list) : string -> FacetStatus option =
        let groups = FileWorkGroupRepository.read root
        let executions = FileProvenanceRepository.readExecutions root
        let standing = FileWorkGroupFacts.standing root |> Result.defaultValue (fun _ -> MemberStanding.Unknown)
        let pathOf evidenceType = provided |> List.tryFind (fun evidence -> evidence.Type = evidenceType) |> Option.map (fun evidence -> evidence.Path)

        fun workItemId ->
            match groups with
            | Error message -> Some(FacetStatus.Unavailable [ $"the group store cannot be read, so the group gates cannot be judged: {message}" ])
            | Ok groups ->
                executions
                |> List.filter (fun view -> view.WorkItemId = workItemId && view.Status = "active")
                |> List.tryPick (fun view -> WorkGroups.executionOf groups workItemId view.ExecutionId)
                |> Option.bind (fun (group, execution, begun) ->
                    match begun.Mode with
                    | ExecutionMode.Independent -> None
                    | ExecutionMode.Grouped ->
                        let subject =
                            { WorkItemId = workItemId
                              GroupId = group.Declaration.Id
                              GroupExecutionId = execution.Id }

                        let facts =
                            { AnalysisPrecedesChanges =
                                match pathOf QualityEvidenceTypes.groupAnalysis with
                                | Some path -> analysisPrecedesChanges root begun.ExecutionId path (pathOf QualityEvidenceTypes.groupVerification |> Option.toList)
                                | None -> Error "no group analysis was supplied"
                              PathExists = pathExists root
                              Standing = standing }

                        Some(
                            GroupGates.judge
                                subject
                                facts
                                (observe QualityEvidenceTypes.groupAnalysis GroupEvidenceJson.decodeAnalysis root provided)
                                (observe QualityEvidenceTypes.groupVerification GroupEvidenceJson.decodeVerification root provided)
                        ))

    /// PRX-GRP-152: a member of a group execution completing without a
    /// `cost.execution_total` and without a recorded capability state saying
    /// why cost is unavailable gets a warning, never a refusal.
    let costWarnings (root: string) (ids: string list) : string list =
        match FileWorkGroupRepository.read root with
        | Error _ -> []
        | Ok groups ->
            let executions = FileProvenanceRepository.readExecutions root

            ids
            |> List.collect (fun workItemId ->
                executions
                |> List.filter (fun view -> view.WorkItemId = workItemId && view.Status = "active")
                |> List.choose (fun view -> WorkGroups.executionOf groups workItemId view.ExecutionId |> Option.map (fun (_, execution, _) -> view.ExecutionId, execution.Id)))
            |> List.choose (fun (executionId, groupExecution) ->
                match FileTelemetryQueryRepository.readByExecutionId root executionId with
                | Error _ -> None
                | Ok record ->
                    let has (field: string) (predicate: JsonObject -> bool) =
                        match record[field] with
                        | :? JsonArray as entries -> entries |> Seq.exists (function :? JsonObject as entry -> predicate entry | _ -> false)
                        | _ -> false

                    let named (entry: JsonObject) (key: string) (value: string) =
                        match entry[key] with
                        | :? JsonValue as found -> found.ToString() = value
                        | _ -> false

                    let costRecorded = has "metrics" (fun metric -> named metric "id" "cost.execution_total")

                    let explained =
                        has "capabilities" (fun capability ->
                            named capability "metricId" "cost.execution_total"
                            && List.exists (named capability "status") [ "unsupported"; "supported-unavailable"; "unavailable" ]
                            && not (named capability "reason" "runtime capability not reported or mapped"))

                    if costRecorded || explained then None
                    else
                        Some
                            $"{executionId} ({groupExecution}) records no cost.execution_total and no capability state explaining why; record it with `telemetry record {executionId} --metric cost.execution_total --value AMOUNT --currency USD`, or record why it is unavailable (PRX-GRP-152)")
