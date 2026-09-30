namespace Ros.Contracts.Work

open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Git
open Ros.Domain.Work

/// The persisted and machine-readable forms of group checkpoints
/// (PRX-GRP-044). A `work.group.checkpointed` event is its own record: it
/// references members' `work.checkpointed` events by ID and never rewrites or
/// replaces them, and it claims no paths (PRX-GRP-043).
[<RequireQualifiedAccess>]
module GroupCheckpointJson =
    let eventType = "work.group.checkpointed"

    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private child (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonObject as value -> Some value
        | _ -> None

    let private stringsOf (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as values ->
            values
            |> Seq.choose (function
                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private objectsOf (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as values -> values |> Seq.choose (function :? JsonObject as item -> Some item | _ -> None) |> Seq.toList
        | _ -> []

    let strings (values: string list) =
        values |> List.fold (fun (array: JsonArray) value -> array.Add(JsonValue.Create value: JsonNode); array) (JsonArray())

    let private stateCode state =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"
        | LiveWorkState.Abandoned -> "abandoned"

    let private declarationNode (declaration: GroupDeclarationSource) =
        let node = JsonObject()
        node["source"] <- JsonValue.Create(GroupDeclarationSource.code declaration)
        node["path"] <- JsonValue.Create(GroupDeclarationSource.path declaration)
        node

    let private referenceNode (reference: MemberCheckpointReference) =
        let node = JsonObject()
        node["id"] <- JsonValue.Create reference.CheckpointId
        node["executionId"] <- JsonValue.Create reference.ExecutionId
        node["commit"] <- JsonValue.Create reference.Commit.Value
        node["recordedAt"] <- JsonValue.Create reference.RecordedAt
        node

    let private memberNode (memberItem: GroupMember) =
        let node = JsonObject()
        node["workItem"] <- JsonValue.Create memberItem.WorkItemId
        node["standing"] <- JsonValue.Create(GroupMemberStanding.code memberItem.Standing)
        memberItem.State |> Option.iter (fun state -> node["state"] <- JsonValue.Create(stateCode state))
        memberItem.ExecutionId |> Option.iter (fun execution -> node["execution"] <- JsonValue.Create execution)
        node["checkpoint"] <- (memberItem.Checkpoint |> Option.map (referenceNode >> fun reference -> reference :> JsonNode) |> Option.toObj)
        node

    let private membersIn (checkpoint: GroupCheckpoint) standing = strings (checkpoint.WithStanding standing)

    /// The `groupCheckpoint` object carried by the event: the historical
    /// fact, in the order it is hashed.
    let body (checkpoint: GroupCheckpoint) : JsonObject =
        let git = checkpoint.Location
        let node = JsonObject()
        node["repository"] <- JsonValue.Create git.Repository
        node["branch"] <- JsonValue.Create git.Branch
        node["commit"] <- JsonValue.Create git.LocalCommit.Value
        let remote = JsonObject()
        remote["name"] <- JsonValue.Create git.Remote.Name
        git.Remote.Url |> Option.iter (fun url -> remote["url"] <- JsonValue.Create url)
        node["remote"] <- remote
        node["remoteBranch"] <- JsonValue.Create git.RemoteBranch
        node["remoteCommit"] <- JsonValue.Create git.RemoteCommit.Value
        node["summary"] <- JsonValue.Create checkpoint.Summary
        node["nextAction"] <- JsonValue.Create checkpoint.NextAction
        node["decisions"] <- strings checkpoint.Decisions
        node["declaration"] <- declarationNode checkpoint.Declaration
        node["members"] <- checkpoint.Members |> List.fold (fun (array: JsonArray) memberItem -> array.Add(memberNode memberItem: JsonNode); array) (JsonArray())
        node["active"] <- membersIn checkpoint GroupMemberStanding.Active
        node["completed"] <- membersIn checkpoint GroupMemberStanding.Completed
        node["remaining"] <- membersIn checkpoint GroupMemberStanding.Remaining
        node["abandoned"] <- membersIn checkpoint GroupMemberStanding.Abandoned
        let verification = JsonObject()
        verification["status"] <- JsonValue.Create "verified"
        verification["mechanism"] <- JsonValue.Create(DurabilityMechanism.code checkpoint.Verification.Mechanism)
        node["verification"] <- verification
        node

    let isGroupCheckpointEvent (node: JsonObject) = text node "type" = Some eventType

    let private storedMember (node: JsonObject) : StoredGroupMember =
        let reference = child node "checkpoint"

        { WorkItemId = text node "workItem" |> Option.defaultValue ""
          Standing = text node "standing" |> Option.defaultValue ""
          State = text node "state"
          ExecutionId = text node "execution"
          CheckpointId = reference |> Option.bind (fun value -> text value "id")
          CheckpointExecutionId = reference |> Option.bind (fun value -> text value "executionId")
          CheckpointCommit = reference |> Option.bind (fun value -> text value "commit")
          CheckpointRecordedAt = reference |> Option.bind (fun value -> text value "recordedAt") }

    /// A stored group checkpoint event's fields, before they are re-checked.
    let tryRead (node: JsonObject) : StoredGroupCheckpoint option =
        child node "groupCheckpoint"
        |> Option.map (fun body ->
            let remote = child body "remote"
            let verification = child body "verification"
            let declaration = child body "declaration"

            { GroupId = text node "group" |> Option.defaultValue ""
              DeclarationSource = declaration |> Option.bind (fun value -> text value "source") |> Option.defaultValue ""
              DeclarationPath = declaration |> Option.bind (fun value -> text value "path")
              Members = objectsOf body "members" |> List.map storedMember
              Decisions = stringsOf body "decisions"
              Summary = text body "summary" |> Option.defaultValue ""
              NextAction = text body "nextAction" |> Option.defaultValue ""
              RecordedAt = text node "occurredAt" |> Option.defaultValue ""
              Repository = text body "repository" |> Option.defaultValue ""
              Branch = text body "branch" |> Option.defaultValue ""
              Commit = text body "commit" |> Option.defaultValue ""
              RemoteName = remote |> Option.bind (fun value -> text value "name") |> Option.defaultValue ""
              RemoteUrl = remote |> Option.bind (fun value -> text value "url")
              RemoteBranch = text body "remoteBranch" |> Option.defaultValue ""
              RemoteCommit = text body "remoteCommit" |> Option.defaultValue ""
              VerificationStatus = verification |> Option.bind (fun value -> text value "status") |> Option.defaultValue ""
              Mechanism = verification |> Option.bind (fun value -> text value "mechanism") |> Option.defaultValue ""
              Paths = stringsOf node "paths" })

    let rejectionNode (rejection: GroupCheckpointRejection) =
        match rejection with
        | GroupCheckpointRejection.Checkpoint inner -> CheckpointJson.rejectionNode inner
        | _ ->
            let node = JsonObject()
            node["code"] <- JsonValue.Create(GroupCheckpointRejection.code rejection)
            node["message"] <- JsonValue.Create(GroupCheckpointRejection.message rejection)
            node["remedy"] <- JsonValue.Create(GroupCheckpointRejection.remedy rejection)

            match rejection with
            | GroupCheckpointRejection.DuplicateMember id
            | GroupCheckpointRejection.UnknownMember id -> node["workItem"] <- JsonValue.Create id
            | _ -> ()

            node

    let warningNode (warning: GroupCheckpointWarning) =
        let node = JsonObject()
        node["code"] <- JsonValue.Create(GroupCheckpointWarning.code warning)
        node["workItem"] <- JsonValue.Create(GroupCheckpointWarning.workItemId warning)
        node["message"] <- JsonValue.Create(GroupCheckpointWarning.message warning)
        node
