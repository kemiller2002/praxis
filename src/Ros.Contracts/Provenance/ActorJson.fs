namespace Ros.Contracts.Provenance

open System.Text.Json.Nodes
open Ros.Domain.Provenance

/// The canonical JSON form of an `Actor`, shared by work events, backlog
/// items, reports, and anything exported across an integration boundary:
///
/// `{"kind":"agent","id":"openai/codex","provider":"openai","model":"unknown","runtime":"codex"}`
///
/// Keys always appear in this order (event IDs hash the serialized form).
/// `provider`/`model`/`runtime` are omitted when not applicable (a human)
/// and are the literal `"unknown"` when applicable but not known.
[<RequireQualifiedAccess>]
module ActorJson =
    let node (actor: Actor) : JsonObject =
        let result = JsonObject()
        result["kind"] <- JsonValue.Create(ActorKind.code actor.Kind)
        result["id"] <- JsonValue.Create actor.Id

        [ "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ]
        |> List.iter (fun (name, value) -> value |> Option.iter (fun text -> result[name] <- JsonValue.Create text))

        result

    let private stringProperty (item: JsonObject) (name: string) =
        match item[name] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, text -> Some text
            | _ -> None
        | _ -> None

    /// `Ok None` when the actor is absent (legacy records); an error when it
    /// is present but not a well-formed actor.
    let tryParse (value: JsonNode) : Result<Actor option, string> =
        match value with
        | null -> Ok None
        | :? JsonObject as item ->
            match stringProperty item "kind" with
            | None -> Error "actor.kind is required"
            | Some kindText ->
                match ActorKind.tryParse kindText with
                | None -> Error $"unknown actor kind '{kindText}'"
                | Some kind ->
                    Ok(
                        Some
                            { Kind = kind
                              Id = stringProperty item "id" |> Option.defaultValue ""
                              Provider = stringProperty item "provider"
                              Model = stringProperty item "model"
                              Runtime = stringProperty item "runtime" }
                    )
        | _ -> Error "actor must be an object"
