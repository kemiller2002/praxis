namespace Ros.Cli

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Application.Work
open Ros.Contracts.Work
open Ros.Domain.Git
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work

/// Group checkpoints on the command line (PRX-GRP-044, PRAXIS-GROUP-05):
/// `work group checkpoint` records a durable checkpoint of a grouped
/// execution over its members' own checkpoints, and `work group checkpoint
/// show` reads a group's checkpoint history. Durability is verified exactly
/// as `work checkpoint` verifies it; every decision is made by
/// `Ros.Domain.Work`.
[<RequireQualifiedAccess>]
module GroupCheckpointCommands =
    let usage =
        "work group checkpoint --id GROUP-ID --occurred-at TIMESTAMP --summary TEXT --next-action TEXT [--decision TEXT]* [--config FILE] [--json] [IDENTITY] | work group checkpoint show GROUP-ID [--json]"

    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private optionValues (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private optionValue (name: string) (arguments: string list) = optionValues name arguments |> List.tryHead

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    let private valueFlags =
        set
            [ "--id"
              "--occurred-at"
              "--summary"
              "--next-action"
              "--config"
              "--decision"
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

    let rec private unexpected (arguments: string list) =
        match arguments with
        | [] -> []
        | flag :: _ :: rest when valueFlags.Contains flag -> unexpected rest
        | "--json" :: rest -> unexpected rest
        | token :: rest -> token :: unexpected rest

    let private argumentErrors (arguments: string list) =
        [ match optionValues "--id" arguments with
          | [ _ ] -> ()
          | [] -> yield "work group checkpoint requires --id GROUP-ID"
          | _ -> yield "work group checkpoint names exactly one group; pass --id once"
          match optionValues "--occurred-at" arguments with
          | [ value ] when isTimestamp value -> ()
          | [ value ] -> yield $"--occurred-at '{value}' is not a timestamp"
          | _ -> yield "work group checkpoint requires exactly one --occurred-at TIMESTAMP (the real current time)"
          if (optionValues "--summary" arguments).Length <> 1 then
              yield "work group checkpoint requires one --summary TEXT describing the group milestone"
          if (optionValues "--next-action" arguments).Length <> 1 then
              yield "work group checkpoint requires one --next-action TEXT naming the group's next intended step"
          if (optionValues "--config" arguments).Length > 1 then
              yield "pass --config once"
          for token in unexpected arguments do
              yield $"unexpected argument '{token}'" ]

    let private resolve (root: string) (path: string) =
        if Path.IsPathRooted path then path else Path.GetFullPath(Path.Combine(root, path))

    /// The declared group's current members and where they are declared.
    let private membership root (arguments: string list) (groupId: string) : Result<string list * GroupDeclarationSource, string> =
        FileGroupCheckpointRepository.readDeclaredGroup root (optionValue "--config" arguments |> Option.map (fun path -> path, resolve root path)) groupId

    let private memberNode (memberItem: GroupMember) =
        let node = JsonObject()
        node["workItem"] <- JsonValue.Create memberItem.WorkItemId
        node["standing"] <- JsonValue.Create(GroupMemberStanding.code memberItem.Standing)
        memberItem.State |> Option.iter (fun state -> node["state"] <- JsonValue.Create(FileCheckpointRepository.stateCode state))
        memberItem.ExecutionId |> Option.iter (fun execution -> node["execution"] <- JsonValue.Create execution)

        node["checkpoint"] <-
            (memberItem.Checkpoint
             |> Option.map (fun reference ->
                 let checkpoint = JsonObject()
                 checkpoint["id"] <- JsonValue.Create reference.CheckpointId
                 checkpoint["commit"] <- JsonValue.Create reference.Commit.Value
                 checkpoint["executionId"] <- JsonValue.Create reference.ExecutionId
                 checkpoint["recordedAt"] <- JsonValue.Create reference.RecordedAt
                 checkpoint :> JsonNode)
             |> Option.toObj)

        node

    let private recordedNode (eventId: string) (checkpoint: GroupCheckpoint) =
        let node = GroupCheckpointJson.body checkpoint
        node["id"] <- JsonValue.Create eventId
        node["groupId"] <- JsonValue.Create checkpoint.GroupId
        node["recordedAt"] <- JsonValue.Create checkpoint.RecordedAt
        node["executionIds"] <- GroupCheckpointJson.strings checkpoint.ExecutionIds
        node["members"] <- checkpoint.Members |> List.fold (fun (array: JsonArray) memberItem -> array.Add(memberNode memberItem: JsonNode); array) (JsonArray())
        node

    let private document (groupId: string) (status: string) =
        let node = JsonObject()
        node["command"] <- JsonValue.Create "work group checkpoint"
        node["schemaVersion"] <- JsonValue.Create 1
        node["groupId"] <- JsonValue.Create groupId
        node["status"] <- JsonValue.Create status
        node

    let private renderRecorded asJson (eventId: string) (checkpoint: GroupCheckpoint) =
        let warnings = GroupCheckpoint.warnings checkpoint

        if asJson then
            let output = document checkpoint.GroupId "recorded"
            output["checkpoint"] <- recordedNode eventId checkpoint
            output["paths"] <- JsonArray()
            output["warnings"] <- warnings |> List.fold (fun (array: JsonArray) warning -> array.Add(GroupCheckpointJson.warningNode warning: JsonNode); array) (JsonArray())
            printf "%s" (output.ToJsonString jsonOptions)
        else
            let git = checkpoint.Location
            let listed standing = checkpoint.WithStanding standing |> function [] -> "(none)" | ids -> String.concat ", " ids
            printfn "durable group checkpoint recorded for %s (checkpoint %s)" checkpoint.GroupId eventId
            printfn "  commit:        %s on %s" git.LocalCommit.Value git.Branch
            printfn "  verified at:   %s/%s == local HEAD (read from the remote itself)" git.Remote.Name git.RemoteBranch
            printfn "  executions:    %s" (String.concat ", " checkpoint.ExecutionIds)
            printfn "  recorded:      %s" checkpoint.RecordedAt
            printfn "  active:        %s" (listed GroupMemberStanding.Active)
            printfn "  completed:     %s" (listed GroupMemberStanding.Completed)
            printfn "  remaining:     %s" (listed GroupMemberStanding.Remaining)

            if not (checkpoint.WithStanding GroupMemberStanding.Abandoned).IsEmpty then
                printfn "  abandoned:     %s" (listed GroupMemberStanding.Abandoned)

            checkpoint.Decisions |> List.iter (printfn "  decision:      %s")
            printfn "  completed:     %s" checkpoint.Summary
            printfn "  next action:   %s" checkpoint.NextAction

            for memberItem in checkpoint.Members do
                match memberItem.Checkpoint with
                | Some reference -> printfn "  member %s: own checkpoint %s at %s" memberItem.WorkItemId reference.CheckpointId (CommitId.short reference.Commit)
                | None -> printfn "  member %s: no checkpoint of its own" memberItem.WorkItemId

            printfn "  attributed:    0 paths (members' own checkpoints attribute every change)"

            for warning in warnings do
                eprintfn "WARNING [%s] %s" (GroupCheckpointWarning.code warning) (GroupCheckpointWarning.message warning)

            printfn "Praxis state changed under .ros/; commit and push it so another executor can find this checkpoint."

        0

    let private renderRejected asJson (groupId: string) (rejections: GroupCheckpointRejection list) =
        if asJson then
            let output = document groupId "rejected"
            output["rejections"] <- rejections |> List.fold (fun (array: JsonArray) rejection -> array.Add(GroupCheckpointJson.rejectionNode rejection: JsonNode); array) (JsonArray())
            printf "%s" (output.ToJsonString jsonOptions)
        else
            for rejection in rejections do
                eprintfn "ERROR [%s] %s" (GroupCheckpointRejection.code rejection) (GroupCheckpointRejection.message rejection)
                eprintfn "  REMEDY %s" (GroupCheckpointRejection.remedy rejection)

            eprintfn "group checkpoint rejected; nothing was recorded"

        if rejections |> List.forall GroupCheckpointRejection.isArgumentError then 2 else 1

    let private renderFailed asJson (groupId: string) (message: string) =
        if asJson then
            let output = document groupId "failed"
            let failure = JsonObject()
            failure["code"] <- JsonValue.Create "persistence-failed"
            failure["message"] <- JsonValue.Create message
            output["failure"] <- failure
            printf "%s" (output.ToJsonString jsonOptions)
        else
            eprintfn "ERROR [persistence-failed] %s" message

        1

    /// Verifies and records one group checkpoint under the `work-protocol`
    /// lock. The inner result is the domain decision; the outer one is a
    /// failure to observe or persist.
    let private checkpoint root (arguments: string list) (actor: Actor) (groupId: string) =
        let decide () =
            match membership root arguments groupId with
            | Error reason -> Ok(Error [ GroupCheckpointRejection.GroupNotDeclared reason ])
            | Ok(members, declaration) ->
                FileGroupCheckpointRepository.observeMembers root (ProvenanceCommands.identityOverridesFrom arguments) members
                |> Result.map (fun observed ->
                    let git = ProcessGitDurability.create root
                    let policy = FileCheckpointRepository.readPolicy root

                    let candidate =
                        { GroupId = groupId
                          Repository = FileWorkConfigRepository.readRepositoryId root
                          Members = members
                          Declaration = declaration
                          Decisions = optionValues "--decision" arguments
                          Summary = optionValue "--summary" arguments |> Option.defaultValue ""
                          NextAction = optionValue "--next-action" arguments |> Option.defaultValue ""
                          OccurredAt = (optionValue "--occurred-at" arguments).Value }

                    CheckpointObservation.candidate git policy None ExecutionObservation.NoneActive
                    |> GroupCheckpoint.verify candidate observed)
                |> Result.bind (fun decision ->
                    match decision with
                    | Error rejections -> Ok(Error rejections)
                    | Ok verified -> FileGroupCheckpointRepository.record root actor verified |> Result.map (fun eventId -> Ok(eventId, verified)))

        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    WorkStateTransaction.recover root
                    |> Result.mapError _.Message
                    |> Result.bind decide
                with error ->
                    Error $"state persistence failed: {error.Message}"

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value

    let run root (arguments: string list) (actor: Actor) =
        match argumentErrors arguments with
        | _ :: _ as errors ->
            errors |> List.iter (eprintfn "ERROR %s")
            eprintfn "Usage: ros %s" usage
            2
        | [] ->
            let groupId = (optionValue "--id" arguments).Value
            let asJson = List.contains "--json" arguments

            match checkpoint root arguments actor groupId with
            | Error message -> renderFailed asJson groupId message
            | Ok(Error rejections) -> renderRejected asJson groupId rejections
            | Ok(Ok(eventId, verified)) -> renderRecorded asJson eventId verified

    // ---- work group checkpoint show ----

    let private historyNode (read: GroupCheckpointRead) =
        let node = JsonObject()
        node["id"] <- JsonValue.Create read.EventId

        match read.Stored with
        | Some stored ->
            let byStanding standing =
                stored.Members |> List.filter (fun memberItem -> memberItem.Standing = standing) |> List.map _.WorkItemId |> GroupCheckpointJson.strings

            node["recordedAt"] <- JsonValue.Create stored.RecordedAt
            node["branch"] <- JsonValue.Create stored.Branch
            node["commit"] <- JsonValue.Create stored.Commit
            node["remote"] <- JsonValue.Create stored.RemoteName
            node["remoteBranch"] <- JsonValue.Create stored.RemoteBranch
            node["active"] <- byStanding "active"
            node["completed"] <- byStanding "completed"
            node["remaining"] <- byStanding "remaining"
            node["abandoned"] <- byStanding "abandoned"

            node["memberCheckpoints"] <-
                stored.Members
                |> List.fold
                    (fun (references: JsonObject) memberItem ->
                        references[memberItem.WorkItemId] <- (memberItem.CheckpointId |> Option.map (fun id -> JsonValue.Create id :> JsonNode) |> Option.toObj)
                        references)
                    (JsonObject())

            node["decisions"] <- GroupCheckpointJson.strings stored.Decisions
            node["summary"] <- JsonValue.Create stored.Summary
            node["nextAction"] <- JsonValue.Create stored.NextAction

            match StoredGroupCheckpoint.problems stored with
            | [] -> ()
            | problems -> node["invalid"] <- GroupCheckpointJson.strings problems
        | None -> node["invalid"] <- GroupCheckpointJson.strings [ "the event has no groupCheckpoint object" ]

        if not read.IdMatchesContent then
            node["invalid"] <- GroupCheckpointJson.strings [ "eventId does not match the event content" ]

        node

    let show root (arguments: string list) =
        match arguments |> List.filter (fun value -> value <> "--json") with
        | [ groupId ] when not (groupId.StartsWith "--") ->
            let history = FileGroupCheckpointRepository.readHistory root groupId

            if List.contains "--json" arguments then
                let output = JsonObject()
                output["command"] <- JsonValue.Create "work group checkpoint show"
                output["schemaVersion"] <- JsonValue.Create 1
                output["groupId"] <- JsonValue.Create groupId
                output["history"] <- history |> List.fold (fun (array: JsonArray) read -> array.Add(historyNode read: JsonNode); array) (JsonArray())
                printf "%s" (output.ToJsonString jsonOptions)
            else
                printfn "GROUP CHECKPOINT HISTORY for %s (%d, oldest first; never rewritten)" groupId history.Length

                for read in history do
                    match read.Stored with
                    | Some stored ->
                        let named standing =
                            stored.Members |> List.filter (fun memberItem -> memberItem.Standing = standing) |> List.length

                        printfn
                            "  %s  %s  %s  active %d, completed %d, remaining %d  %s"
                            stored.RecordedAt
                            read.EventId
                            (stored.Commit |> Seq.truncate 12 |> Seq.toArray |> String)
                            (named "active")
                            (named "completed")
                            (named "remaining")
                            stored.Summary

                        printfn "      next action: %s" stored.NextAction
                    | None -> printfn "  %s  INVALID: no groupCheckpoint object" read.EventId

            0
        | _ ->
            eprintfn "ERROR work group checkpoint show requires exactly one GROUP-ID"
            eprintfn "Usage: ros %s" usage
            2
