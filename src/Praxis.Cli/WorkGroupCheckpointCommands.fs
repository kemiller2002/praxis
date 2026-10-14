namespace Praxis.Cli

open Praxis.Contracts.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Application.Work
open Praxis.Infrastructure.Git
open Praxis.Infrastructure.Json
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// `work group checkpoint` (PRX-GRP-044, PRX-GRP-107): a durable group
/// checkpoint over members' own checkpoints, verified by the same Git rule
/// as `work checkpoint`. Members of other repositories are recorded as
/// dated observations, never verifications.
[<RequireQualifiedAccess>]
module WorkGroupCheckpointCommands =

    /// Each member's own latest checkpoint that re-verifies as durable, as
    /// the planner reads it from the live context.
    let private memberCheckpoints (root: string) : Result<string -> MemberCheckpointReference option, string> =
        FilePlanningRepository.readLive root
        |> Result.map (fun live ->
            let byId =
                live
                |> List.choose (fun item ->
                    item.Checkpoint
                    |> Option.map (fun checkpoint ->
                        item.Id,
                        { WorkItemId = item.Id
                          CheckpointId = checkpoint.CheckpointId
                          Commit = checkpoint.Commit }))
                |> Map.ofList

            byId.TryFind)

    /// The caller's own execution for each active member, resolved exactly as
    /// `work checkpoint` resolves it; any other member has none.
    let private ownExecution (root: string) (rawArguments: string list) : Result<string -> ExecutionObservation, string> =
        FileCheckpointRepository.readItems root
        |> Result.map (fun items ->
            let overrides = ProvenanceCommands.identityOverridesFrom rawArguments

            let active =
                items |> List.filter (fun item -> item.State = LiveWorkState.Active) |> List.map (fun item -> item.WorkItemId) |> Set.ofList

            fun id ->
                if active.Contains id then FileCheckpointRepository.resolveExecution root id overrides None
                else ExecutionObservation.NoneActive)

    /// The same Git durability rule as `work checkpoint`, observed now.
    let private durableLocation (root: string) =
        let git = ProcessGitDurability.create root
        let policy = FileCheckpointRepository.readPolicy root

        CheckpointObservation.candidate git policy None ExecutionObservation.NoneActive
        |> CheckpointVerification.verifyLocation (FileWorkConfigRepository.readRepositoryId root)

    let checkpoint (root: string) (rawArguments: string list) (actor: Actor) =
        let command = "work group checkpoint"
        let arguments = WorkGroupCommands.parse [ "--group"; "--occurred-at"; "--summary"; "--next-action"; "--decision" ] [ "--dry-run" ] rawArguments
        let asJson = arguments.Switches.Contains "--json"
        let dryRun = arguments.Switches.Contains "--dry-run"

        let errors =
            [ yield! WorkGroupCommands.commonErrors command arguments [ "--summary"; "--next-action" ]
              yield! arguments.Positional |> List.map (fun token -> $"unexpected argument '{token}'")
              yield! WorkGroupCommands.groupErrors command arguments
              yield! WorkGroupCommands.occurredAtErrors command arguments
              if (WorkGroupCommands.all arguments "--summary").Length <> 1 then
                  yield $"{command} requires one --summary TEXT describing the milestone"
              if (WorkGroupCommands.all arguments "--next-action").Length <> 1 then
                  yield $"{command} requires one --next-action TEXT naming the next intended step" ]

        match errors with
        | _ :: _ -> WorkGroupCommands.reportArgumentErrors command WorkGroupCommands.checkpointUsage errors
        | [] ->
            let groupId = (WorkGroupCommands.single arguments "--group").Value
            let occurredAt = (WorkGroupCommands.single arguments "--occurred-at").Value
            let location = durableLocation root

            let checkpointId =
                let commit = location |> Result.map (fun git -> git.LocalCommit.Value) |> Result.defaultValue ""
                "gcp-" + CanonicalJson.sha256HexPrefix 24 (String.concat "\u0000" [ groupId; occurredAt; commit; (WorkGroupCommands.single arguments "--summary").Value ])

            let request =
                { GroupId = groupId
                  CheckpointId = checkpointId
                  Summary = (WorkGroupCommands.single arguments "--summary").Value
                  NextAction = (WorkGroupCommands.single arguments "--next-action").Value
                  Decisions = WorkGroupCommands.all arguments "--decision"
                  OccurredAt = occurredAt
                  Actor = actor }

            let outcome =
                FileWorkGroupRepository.transact root dryRun (fun groups ->
                    match WorkGroupCommands.contextFor root None groupId groups, memberCheckpoints root, ownExecution root rawArguments with
                    | Error message, _, _
                    | _, Error message, _
                    | _, _, Error message -> Error(Choice1Of2 message)
                    | Ok(_, context), Ok references, Ok execution ->
                        WorkGroups.checkpoint context request (MemberFacts.ofStanding context.Standing) execution references location
                        |> Result.mapError Choice2Of2
                        |> Result.map (fun (group, recorded) -> WorkGroups.upsert groups group, (group, recorded)))

            match outcome with
            | Error message
            | Ok(Error(Choice1Of2 message)) -> WorkGroupCommands.reportFailure asJson command message
            | Ok(Error(Choice2Of2 rejections)) ->
                if asJson then
                    WorkGroupCommands.printJson (WorkGroupCommands.envelope command "rejected" [ "groupId", WorkGroupJson.text groupId; "rejections", rejections |> List.map WorkGroupJson.checkpointRejectionNode |> WorkGroupJson.array ])
                else
                    for rejection in rejections do
                        eprintfn "ERROR [%s] %s" (GroupCheckpointRejection.code rejection) (GroupCheckpointRejection.message rejection)

                    eprintfn "group checkpoint refused; nothing was recorded"

                if rejections |> List.forall GroupCheckpointRejection.isArgumentError then 2 else 1
            | Ok(Ok(group, recorded)) ->
                let status = if dryRun then "dry-run" else "recorded"

                if asJson then
                    WorkGroupCommands.printJson (WorkGroupCommands.envelope command status [ "dryRun", WorkGroupJson.boolean dryRun; "groupId", WorkGroupJson.text groupId; "checkpoint", WorkGroupJson.checkpointNode recorded ])
                else
                    let listed (values: string list) = match values with [] -> "(none)" | values -> String.concat ", " values
                    let git = recorded.Location
                    printfn "durable group checkpoint %s for %s" recorded.CheckpointId group.Declaration.Id
                    printfn "  commit:        %s on %s" git.LocalCommit.Value git.Branch
                    printfn "  verified at:   %s/%s == local HEAD (read from the remote itself)" git.Remote.Name git.RemoteBranch
                    printfn "  completed:     %s" (listed recorded.Completed)
                    printfn "  active:        %s" (listed recorded.Active)
                    printfn "  blocked:       %s" (listed recorded.Blocked)
                    printfn "  remaining:     %s" (listed recorded.Remaining)

                    if not recorded.Abandoned.IsEmpty then
                        printfn "  abandoned:     %s" (listed recorded.Abandoned)

                    recorded.Decisions |> List.iter (printfn "  decision:      %s")

                    recorded.MemberCheckpoints
                    |> List.iter (fun reference -> printfn "  member checkpoint: %s %s @ %s" reference.WorkItemId reference.CheckpointId reference.Commit)

                    printfn "  summary:       %s" recorded.Summary
                    printfn "  next action:   %s" recorded.NextAction
                    printfn "Members' own checkpoints are referenced, not replaced; no paths or executions are claimed."

                    if dryRun then printfn "dry run: nothing was written"
                    else printfn "Praxis state changed in %s; commit and push it." FileWorkGroupRepository.relativePath

                0

    // ---- work group show ----

