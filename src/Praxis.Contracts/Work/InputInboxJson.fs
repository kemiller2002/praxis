namespace Praxis.Contracts.Work

open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Contracts.Provenance
open Praxis.Domain.Work

/// `praxis.inbox-claim/1`: the durable claim record of one input document,
/// `claim.json` inside its claim directory. One parser, one renderer.
[<RequireQualifiedAccess>]
module InputInboxJson =
    [<Literal>]
    let Schema = "praxis.inbox-claim/1"

    let private options = JsonSerializerOptions(WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private text (value: string option) : JsonNode =
        match value with
        | Some value -> JsonValue.Create value
        | None -> null

    let private derivationNode (derivation: Derivation) =
        let node = JsonObject()
        node["kind"] <- JsonValue.Create(DerivationKind.code derivation.Kind)

        node["targetKind"] <-
            JsonValue.Create(
                match derivation.Target with
                | DerivationTarget.Artifact _ -> "artifact"
                | DerivationTarget.RepositoryPath _ -> "path"
            )

        node["target"] <- JsonValue.Create(DerivationTarget.value derivation.Target)
        node["summary"] <- JsonValue.Create derivation.Summary
        node["locator"] <- text derivation.Locator
        node["recordedAt"] <- JsonValue.Create derivation.RecordedAt
        node["actor"] <- ActorJson.node derivation.Actor
        node["executionId"] <- text derivation.ExecutionId
        node

    let node (claim: InputClaim) =
        let source = JsonObject()
        source["path"] <- JsonValue.Create claim.Source.Path
        source["fileName"] <- JsonValue.Create claim.Source.FileName
        source["sha256"] <- JsonValue.Create claim.Source.Sha256
        source["size"] <- JsonValue.Create claim.Source.Size

        let node = JsonObject()
        node["schema"] <- JsonValue.Create Schema
        node["claimId"] <- JsonValue.Create claim.ClaimId
        node["state"] <- JsonValue.Create(InputClaimState.code claim.State)
        node["source"] <- source
        node["claimedAt"] <- JsonValue.Create claim.ClaimedAt
        node["claimant"] <- ActorJson.node claim.Claimant
        node["executionId"] <- text claim.ExecutionId
        let derivations = JsonArray()
        claim.Derivations |> List.iter (fun derivation -> derivations.Add(derivationNode derivation))
        node["derivations"] <- derivations
        node["noDerivationReason"] <- text claim.NoDerivationReason
        node["closedAt"] <- text claim.ClosedAt
        node["releaseReason"] <- text claim.ReleaseReason
        node["rejectionReason"] <- text claim.RejectionReason
        node

    let render (claim: InputClaim) = (node claim).ToJsonString options + "\n"

    let private stringOf (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, text -> Some text
            | _ -> None
        | _ -> None

    let private required (node: JsonObject) (name: string) =
        match stringOf node name with
        | Some value when value.Length > 0 -> Ok value
        | _ -> Error $"{name} is required"

    let private actorOf (node: JsonObject) (name: string) =
        match ActorJson.tryParse node[name] with
        | Ok(Some actor) -> Ok actor
        | Ok None -> Error $"{name} is required"
        | Error message -> Error $"{name}: {message}"

    let private sequence (results: Result<'a, string> list) =
        List.foldBack (fun item state -> Result.bind (fun items -> item |> Result.map (fun value -> value :: items)) state) results (Ok [])

    let private parseDerivation (node: JsonNode) : Result<Derivation, string> =
        match node with
        | :? JsonObject as item ->
            let kind =
                required item "kind"
                |> Result.bind (fun code -> DerivationKind.tryParse code |> Option.map Ok |> Option.defaultValue (Error $"unknown derivation kind '{code}'"))

            let target =
                match required item "targetKind", required item "target" with
                | Ok "artifact", Ok value -> Ok(DerivationTarget.Artifact value)
                | Ok "path", Ok value -> Ok(DerivationTarget.RepositoryPath value)
                | Ok other, Ok _ -> Error $"unknown targetKind '{other}'"
                | Error message, _
                | _, Error message -> Error message

            match kind, target, required item "summary", required item "recordedAt", actorOf item "actor" with
            | Ok kind, Ok target, Ok summary, Ok recordedAt, Ok actor ->
                Ok
                    { Kind = kind
                      Target = target
                      Summary = summary
                      Locator = stringOf item "locator"
                      RecordedAt = recordedAt
                      Actor = actor
                      ExecutionId = stringOf item "executionId" }
            | Error message, _, _, _, _
            | _, Error message, _, _, _
            | _, _, Error message, _, _
            | _, _, _, Error message, _
            | _, _, _, _, Error message -> Error $"derivation: {message}"
        | _ -> Error "derivation must be an object"

    let parse (json: string) : Result<InputClaim, string> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as root when stringOf root "schema" = Some Schema ->
                let source =
                    match root["source"] with
                    | :? JsonObject as source ->
                        let size =
                            match source["size"] with
                            | :? JsonValue as value ->
                                match value.TryGetValue<int64>() with
                                | true, size -> Ok size
                                | _ -> Error "source.size must be an integer"
                            | _ -> Error "source.size is required"

                        match required source "path", required source "fileName", required source "sha256", size with
                        | Ok path, Ok fileName, Ok sha256, Ok size -> Ok { Path = path; FileName = fileName; Sha256 = sha256; Size = size }
                        | Error message, _, _, _
                        | _, Error message, _, _
                        | _, _, Error message, _
                        | _, _, _, Error message -> Error $"source.{message}"
                    | _ -> Error "source is required"

                let state =
                    required root "state"
                    |> Result.bind (fun code -> InputClaimState.tryParse code |> Option.map Ok |> Option.defaultValue (Error $"unknown state '{code}'"))

                let derivations =
                    match root["derivations"] with
                    | :? JsonArray as items -> items |> Seq.map parseDerivation |> List.ofSeq |> sequence
                    | _ -> Error "derivations must be an array"

                match required root "claimId", state, source, required root "claimedAt", actorOf root "claimant", derivations with
                | Ok claimId, Ok state, Ok source, Ok claimedAt, Ok claimant, Ok derivations ->
                    Ok
                        { ClaimId = claimId
                          Source = source
                          State = state
                          ClaimedAt = claimedAt
                          Claimant = claimant
                          ExecutionId = stringOf root "executionId"
                          Derivations = derivations
                          NoDerivationReason = stringOf root "noDerivationReason"
                          ClosedAt = stringOf root "closedAt"
                          ReleaseReason = stringOf root "releaseReason"
                          RejectionReason = stringOf root "rejectionReason" }
                | Error message, _, _, _, _, _
                | _, Error message, _, _, _, _
                | _, _, Error message, _, _, _
                | _, _, _, Error message, _, _
                | _, _, _, _, Error message, _
                | _, _, _, _, _, Error message -> Error message
            | :? JsonObject -> Error $"not a {Schema} document"
            | _ -> Error "a claim record must be a JSON object"
        with :? JsonException as error ->
            Error $"invalid JSON: {error.Message}"
