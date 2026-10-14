namespace Praxis.Cli

open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Work

/// Backlog-queue validation and repair: `work backlog-validate`, the
/// backlog contributor to `validate`, and `work reidentify` (PRAXIS-HYG-02).
/// Every decision is made by `BacklogQueueValidation` and
/// `BacklogReidentification`; this module gathers files and renders.
[<RequireQualifiedAccess>]
module BacklogQueueCommands =
    let reidentifyUsage = "work reidentify --id ID --new-id NEW-ID --reason TEXT --occurred-at TIMESTAMP"

    let private optionValue (name: string) (arguments: string list) =
        arguments
        |> List.tryFindIndex ((=) name)
        |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

    let private liveStates (contextItems: LiveWorkItem list) =
        contextItems |> List.map (fun item -> item.Id, item.SemanticState) |> Map.ofList

    /// The raw backlog rows' own findings plus their agreement with the live
    /// context (`BacklogQueueValidation.contextDisagreements`).
    let findings root : Result<BacklogQueueFinding list, string> =
        FileWorkListRepository.readContextItems root
        |> Result.map (fun contextItems ->
            let items = FileBacklogQueueRepository.readItems root
            BacklogQueueValidation.findings items @ BacklogQueueValidation.contextDisagreements items (liveStates contextItems))

    /// Mirrors production `queueFindings` (`tools/ros_cli.mjs`): duplicate
    /// ids, invalid ids, invalid status, and invalid priority over the raw
    /// backlog rows in `.ros/work/queue.json`, plus backlog/live-context
    /// status disagreements.
    let validate (usage: string) root (arguments: string list) =
        if not (arguments |> List.forall ((=) "--json")) then
            eprintfn "%s" usage
            2
        else
            match findings root with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok found ->
                printf "%s" (BacklogQueueValidationContract.renderJson found)
                if found.IsEmpty then 0 else 1

    let private rejectionMessage (rejection: BacklogReidentificationRejection) =
        match rejection with
        | BacklogReidentificationRejection.InvalidNewId newId -> $"invalid work-item ID '{newId}'"
        | BacklogReidentificationRejection.ReasonRequired -> "reidentify requires --reason stating why the backlog row is a different obligation"
        | BacklogReidentificationRejection.NotInBacklog id -> $"'{id}' is not a captured local work item"
        | BacklogReidentificationRejection.NoDisagreement id ->
            $"backlog item '{id}' agrees with its live work item (or has none); only a row whose terminal status contradicts the live item with the same ID can be reidentified"
        | BacklogReidentificationRejection.NewIdInUse newId -> $"work item '{newId}' already exists"

    let private planAndApply root id newId reason timestamp =
        match FileWorkListRepository.readContextItems root with
        | Error message -> Error message
        | Ok contextItems ->
            let queueItems = FileBacklogQueueRepository.readItems root

            let request: BacklogReidentificationRequest =
                { Id = id
                  NewId = newId
                  Reason = reason
                  Row = queueItems |> List.tryFind (fun item -> item.Id = id)
                  LiveStates = liveStates contextItems
                  KnownIds =
                    Set.unionMany
                        [ queueItems |> List.map (fun item -> item.Id) |> Set.ofList
                          contextItems |> List.map (fun item -> item.Id) |> Set.ofList
                          FileEventLogRepository.readWorkItemIds root ] }

            match BacklogReidentification.plan request with
            | BacklogReidentificationOutcome.Rejected rejection -> Error(rejectionMessage rejection)
            | BacklogReidentificationOutcome.Planned plan ->
                FileBacklogQueueRepository.applyReidentification root plan timestamp contextItems

    /// `work reidentify`: renames a backlog row whose ID collides with a
    /// different, live work item, under the same "work-protocol" lock,
    /// recovery and `backlog-state` journal every backlog effect uses. The
    /// live item, its events and its telemetry are never touched.
    let reidentify root (arguments: string list) =
        match optionValue "--id" arguments, optionValue "--new-id" arguments, optionValue "--occurred-at" arguments with
        | Some id, Some newId, Some timestamp ->
            match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
            | Error failure ->
                eprintfn "ERROR %s" failure.Message
                1
            | Ok lease ->
                let result =
                    try
                        match WorkStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match BacklogStateTransaction.recover root with
                            | Error failure -> Error failure.Message
                            | Ok() -> planAndApply root id newId (optionValue "--reason" arguments) timestamp
                    with error ->
                        lease.Release() |> ignore
                        reraise ()

                match lease.Release(), result with
                | Error releaseFailure, Ok _ ->
                    eprintfn "ERROR %s" releaseFailure.Message
                    1
                | _, Error message ->
                    eprintfn "ERROR %s" message
                    1
                | Ok(), Ok row ->
                    printf "%s" (BacklogTransitionEffectContract.renderJson row)
                    0
        | _ ->
            eprintfn "ERROR work reidentify requires --id, --new-id and --occurred-at"
            eprintfn "Usage: %s" reidentifyUsage
            2
