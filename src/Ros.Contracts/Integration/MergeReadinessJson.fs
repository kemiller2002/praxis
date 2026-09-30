namespace Ros.Contracts.Integration

open System
open System.Text.Json.Nodes
open Ros.Domain.Git
open Ros.Domain.Integration

[<RequireQualifiedAccess>]
module MergeReadinessJson =
    [<Literal>]
    let Schema = "praxis.merge-readiness/1"

    let private text value : JsonNode = JsonValue.Create value
    let private boolean value : JsonNode = JsonValue.Create value

    let private objectNode fields =
        let node = JsonObject()
        fields |> List.iter (fun (key, value: JsonNode) -> node[key] <- value)
        node

    let private array values =
        let node = JsonArray()
        values |> List.iter node.Add
        node :> JsonNode

    let private optionalText value =
        value |> Option.map (CommitId.value >> text) |> Option.toObj

    let private memberNode name (node: JsonObject) =
        let mutable value: JsonNode = null
        if node.TryGetPropertyValue(name, &value) then Option.ofObj value else None

    let private stringValue name (node: JsonObject) =
        match memberNode name node with
        | Some(:? JsonValue as value) ->
            match value.TryGetValue<string>() with
            | true, text -> Some text
            | _ -> None
        | _ -> None

    let private boolValue name (node: JsonObject) =
        match memberNode name node with
        | Some(:? JsonValue as value) ->
            match value.TryGetValue<bool>() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None

    let private parseCommit field value =
        match CommitId.tryParse value with
        | Some commit -> Ok commit
        | None -> Error $"{field} must be a full lowercase Git SHA-1 or SHA-256"

    let private parseCheck (node: JsonNode) =
        match node with
        | :? JsonObject as item ->
            match stringValue "id" item, stringValue "state" item with
            | Some id, Some state when not (String.IsNullOrWhiteSpace id) ->
                match CheckState.tryParse state with
                | None -> Error $"check '{id}' has an invalid state"
                | Some parsedState ->
                    match stringValue "commit" item with
                    | None ->
                        Ok
                            { Id = id
                              State = parsedState
                              Commit = None }
                    | Some rawCommit ->
                        parseCommit $"checks[{id}].commit" rawCommit
                        |> Result.map (fun commit ->
                            { Id = id
                              State = parsedState
                              Commit = Some commit })
            | _ -> Error "each check requires non-blank string id and state"
        | _ -> Error "checks entries must be objects"

    let private traverse parser values =
        values
        |> List.fold
            (fun state value ->
                state
                |> Result.bind (fun parsed ->
                    parser value |> Result.map (fun item -> parsed @ [ item ])))
            (Ok [])

    let parseEvidence (raw: string) : Result<MergeReadinessEvidence, string> =
        try
            match JsonNode.Parse raw with
            | :? JsonObject as root ->
                let schema = stringValue "schema" root

                if schema <> Some Schema then
                    Error $"merge-readiness evidence schema must be '{Schema}'"
                else
                    let candidate =
                        match stringValue "candidateCommit" root with
                        | None -> Ok None
                        | Some rawCommit -> parseCommit "candidateCommit" rawCommit |> Result.map Some

                    let checks =
                        match memberNode "checks" root with
                        | None -> Ok []
                        | Some(:? JsonArray as values) ->
                            values |> Seq.map (fun value -> value) |> Seq.toList |> traverse parseCheck
                        | _ -> Error "checks must be an array"

                    candidate
                    |> Result.bind (fun candidateCommit ->
                        checks
                        |> Result.map (fun parsedChecks ->
                            { CandidateCommit = candidateCommit
                              RemoteCandidateCurrent = boolValue "remoteCandidateCurrent" root
                              Checks = parsedChecks }))
            | _ -> Error "merge-readiness evidence must be a JSON object"
        with error ->
            Error $"invalid merge-readiness evidence JSON: {error.Message}"

    let private checkNode check =
        objectNode
            [ "id", text check.Id
              "state", text (CheckState.code check.State)
              "commit", optionalText check.Commit ]
        :> JsonNode

    let private blockerNode blocker =
        objectNode
            [ "code", text (MergeBlocker.code blocker)
              "message", text (MergeBlocker.message blocker) ]
        :> JsonNode

    let renderDecision
        (policy: MergeReadinessPolicy)
        (observation: MergeReadinessObservation)
        (decision: MergeReadinessDecision)
        =
        let root = JsonObject()
        root["schema"] <- text Schema

        root["status"] <-
            match decision with
            | MergeReadinessDecision.Disabled -> text "disabled"
            | MergeReadinessDecision.NotReady _ -> text "not-ready"
            | MergeReadinessDecision.Ready _ -> text "ready"

        root["candidateCommit"] <- optionalText observation.Evidence.CandidateCommit
        root["head"] <- optionalText observation.Head

        root["workingTreeClean"] <-
            observation.WorkingTreeClean
            |> Option.map boolean
            |> Option.toObj

        root["remoteCandidateCurrent"] <-
            observation.Evidence.RemoteCandidateCurrent
            |> Option.map boolean
            |> Option.toObj

        root["requiredChecks"] <- policy.RequiredChecks |> List.map text |> array
        root["optionalChecks"] <- policy.OptionalChecks |> List.map text |> array
        root["checks"] <- observation.Evidence.Checks |> List.map checkNode |> array

        root["blockers"] <-
            match decision with
            | MergeReadinessDecision.NotReady(_, blockers) -> blockers |> List.map blockerNode |> array
            | _ -> array []

        root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true, IndentSize = 2))
