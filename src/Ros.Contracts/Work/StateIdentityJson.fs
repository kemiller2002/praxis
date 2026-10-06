namespace Ros.Contracts.Work

open System.Text.Json.Nodes
open Ros.Domain.Work

/// The source identity a control-plane response carries (PRX-CTL-004,
/// PRX-CTL-008): the `source` object of a versioned document, the
/// `praxis.state-identity` document `praxis state identity --json` prints,
/// and the `Praxis-*` response headers. Adding fields is non-breaking.
[<RequireQualifiedAccess>]
module StateIdentityJson =
    let contract = "praxis.state-identity"
    let version = 1

    let unknownCommit = "unknown"

    let private text (value: string) : JsonNode = JsonValue.Create value
    let private optionalText (value: string option) : JsonNode = value |> Option.map text |> Option.toObj

    let private fields (identity: StateIdentity) : (string * JsonNode) list =
        [ "repository", text identity.Repository
          "commit", optionalText identity.Commit
          "branch", optionalText identity.Branch
          "stateFingerprint", text (StateIdentity.fingerprintText identity) ]
        @ (match identity.Fingerprint with
           | Error reason -> [ "stateFingerprintError", text reason ]
           | Ok _ -> [])

    let private record (entries: (string * JsonNode) list) : JsonObject =
        let result = JsonObject()
        entries |> List.iter (fun (name, value) -> result[name] <- value)
        result

    /// `{repository, commit, branch, stateFingerprint, stable}`.
    let sourceNode (identity: StateIdentity) (stable: bool) : JsonObject =
        record (fields identity @ [ "stable", JsonValue.Create stable ])

    let document (identity: StateIdentity) : JsonNode =
        record ([ "contract", text contract; "version", JsonValue.Create version ] @ fields identity)

    /// The identity a `praxis.state-identity` document describes, if it is one.
    let parse (node: JsonNode) : Result<StateIdentity, string> =
        let stringOf (name: string) (item: JsonObject) =
            match item[name] with
            | :? JsonValue as value ->
                match value.TryGetValue<string>() with
                | true, found -> Some found
                | _ -> None
            | _ -> None

        match node with
        | :? JsonObject as item when stringOf "contract" item = Some contract ->
            match stringOf "repository" item, stringOf "stateFingerprint" item with
            | Some repository, Some fingerprint ->
                Ok
                    { Repository = repository
                      Commit = stringOf "commit" item
                      Branch = stringOf "branch" item
                      Fingerprint =
                        match stringOf "stateFingerprintError" item with
                        | Some reason -> Error reason
                        | None -> Ok fingerprint }
            | _ -> Error "the state identity document has no repository or stateFingerprint"
        | _ -> Error $"not a {contract} document"

    /// The `Praxis-*` headers every control-plane response carries.
    let headers (identity: StateIdentity) (stable: bool) : (string * string) list =
        [ "Praxis-State-Fingerprint", StateIdentity.fingerprintText identity
          "Praxis-Repository", identity.Repository
          "Praxis-Commit", identity.Commit |> Option.defaultValue unknownCommit
          "Praxis-State-Stable", (if stable then "true" else "false") ]
        @ (match identity.Fingerprint with
           | Error reason -> [ "Praxis-State-Error", reason.Replace('\r', ' ').Replace('\n', ' ') ]
           | Ok _ -> [])
