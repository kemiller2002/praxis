namespace Ros.Domain.Provenance

open System
open Ros.Domain.Artifacts

/// One structural problem found while reading a provenance record. `Field`
/// is a dotted path relative to the record (`contributions[1].actor.id`).
type ProvenanceIssue =
    { Field: string
      Code: string
      Message: string }

/// An insertion-ordered tree used to render the canonical serialization
/// identically to JSON (events, queue items, registries) and YAML front
/// matter (canonical artifacts).
[<RequireQualifiedAccess>]
type OrderedValue =
    | Text of string
    | List of OrderedValue list
    | Fields of (string * OrderedValue) list

/// Reads and writes the canonical provenance serialization over the
/// format-neutral `ArtifactValue` tree, which both the front-matter parser
/// and the JSON adapters produce. Reading is total: it always returns the
/// best-effort value together with every issue, so validation can grade
/// issues instead of stopping at the first one.
[<RequireQualifiedAccess>]
module ProvenanceCodec =
    let private issue field code message =
        { Field = field
          Code = code
          Message = message }

    let private join (prefix: string) (field: string) =
        if prefix.Length = 0 then field else $"{prefix}.{field}"

    let private textOf value =
        match value with
        | ArtifactValue.Text text -> Some text
        | ArtifactValue.Number _
        | ArtifactValue.Boolean _ -> Some(ArtifactValue.display value)
        | ArtifactValue.Sequence _
        | ArtifactValue.Mapping _ -> None

    let private textField (fields: Map<string, ArtifactValue>) name =
        fields |> Map.tryFind name |> Option.bind textOf |> Option.filter (fun text -> text.Trim().Length > 0)

    let private hasWhitespace (value: string) = value |> Seq.exists Char.IsWhiteSpace

    // ---- actor -----------------------------------------------------------

    let readActor (prefix: string) (value: ArtifactValue) : Actor * ProvenanceIssue list =
        match value with
        | ArtifactValue.Mapping fields ->
            let text = textField fields

            let kind, kindIssues =
                match text "kind" with
                | None -> ActorKind.Unknown, [ issue (join prefix "kind") "missing-field" "actor kind is required (agent, human, automation, or unknown)" ]
                | Some raw ->
                    match ActorKind.tryParse raw with
                    | Some kind -> kind, []
                    | None -> ActorKind.Unknown, [ issue (join prefix "kind") "invalid-kind" $"actor kind '{raw}' is not agent, human, automation, or unknown" ]

            let actor =
                { Kind = kind
                  Id = Attribute.ofOption (text "id")
                  Provider = Attribute.ofOption (text "provider")
                  Model = Attribute.ofOption (text "model")
                  ModelVersion = text "modelVersion"
                  Runtime = Attribute.ofOption (text "runtime")
                  RuntimeVersion = text "runtimeVersion"
                  ExecutionId = Attribute.ofOption (text "executionId")
                  SessionId = text "sessionId"
                  Assurance = text "assurance" |> Option.map Assurance.parse |> Option.defaultValue Assurance.SelfReported }

            let missing =
                ActorField.requiredFor kind
                |> List.filter (fun field -> field <> ActorField.Kind)
                |> List.map ActorField.name
                |> List.filter (fun name -> (text name).IsNone)
                |> List.map (fun name ->
                    issue (join prefix name) "missing-field" $"'{name}' is required for a {ActorKind.code kind} actor; record 'unknown' when it is not known")

            let malformed =
                [ "id"; "executionId"; "provider"; "runtime" ]
                |> List.choose (fun name ->
                    text name
                    |> Option.filter hasWhitespace
                    |> Option.map (fun raw -> issue (join prefix name) "invalid-token" $"'{name}' must be a single token without whitespace, found '{raw}'"))

            actor, kindIssues @ missing @ malformed
        | _ -> Actor.unknown, [ issue prefix "invalid-actor" "actor must be a mapping" ]

    // ---- contribution ----------------------------------------------------

    let private stringList (value: ArtifactValue option) =
        match value with
        | Some(ArtifactValue.Sequence items) -> items |> List.choose textOf
        | Some single -> textOf single |> Option.toList
        | None -> []

    let readContribution (prefix: string) (value: ArtifactValue) : Contribution option * ProvenanceIssue list =
        match value with
        | ArtifactValue.Mapping fields ->
            let text = textField fields

            let operation, operationIssues =
                match text "operation" with
                | None -> Operation.Modified, [ issue (join prefix "operation") "missing-field" "contribution operation is required" ]
                | Some raw ->
                    match Operation.tryParse raw with
                    | Some operation -> operation, []
                    | None ->
                        let allowed = Operation.all |> List.map Operation.code |> String.concat ", "
                        Operation.Modified, [ issue (join prefix "operation") "invalid-operation" $"operation '{raw}' is not one of {allowed}" ]

            let at, atIssues =
                match text "at" with
                | None -> "", [ issue (join prefix "at") "missing-field" "contribution time 'at' is required" ]
                | Some raw when (Timestamp.tryParse raw).IsNone ->
                    raw, [ issue (join prefix "at") "invalid-timestamp" $"'{raw}' is not an ISO-8601 date or date-time" ]
                | Some raw -> raw, []

            let actor, actorIssues =
                match fields |> Map.tryFind "actor" with
                | None -> Actor.unknown, [ issue (join prefix "actor") "missing-field" "contribution actor is required" ]
                | Some actorValue -> readActor (join prefix "actor") actorValue

            let contribution =
                { Operation = operation
                  At = at
                  Actor = actor
                  WorkItem = text "workItem"
                  Reason = text "reason"
                  Evidence = stringList (fields |> Map.tryFind "evidence")
                  Basis = text "basis" }

            Some contribution, operationIssues @ atIssues @ actorIssues
        | _ -> None, [ issue prefix "invalid-contribution" "contribution must be a mapping" ]

    // ---- provenance block ------------------------------------------------

    let readProvenance (value: ArtifactValue) : Provenance * ProvenanceIssue list =
        match value with
        | ArtifactValue.Mapping fields ->
            match fields |> Map.tryFind "contributions" with
            | Some(ArtifactValue.Sequence items) ->
                let read =
                    items |> List.mapi (fun index item -> readContribution $"contributions[{index}]" item)

                let contributions = read |> List.choose fst
                let issues = read |> List.collect snd

                let emptyIssue =
                    if items.IsEmpty then
                        [ issue "contributions" "empty-provenance" "provenance declares no contributions" ]
                    else
                        []

                { Contributions = contributions }, emptyIssue @ issues
            | Some _ -> Provenance.empty, [ issue "contributions" "invalid-contributions" "contributions must be a list" ]
            | None -> Provenance.empty, [ issue "contributions" "missing-field" "provenance requires a contributions list" ]
        | _ -> Provenance.empty, [ issue "" "invalid-provenance" "provenance must be a mapping" ]

    // ---- canonical rendering --------------------------------------------

    let actorValue (actor: Actor) =
        Actor.fields actor |> List.map (fun (name, value) -> name, OrderedValue.Text value) |> OrderedValue.Fields

    let contributionValue (contribution: Contribution) =
        [ yield "operation", OrderedValue.Text(Operation.code contribution.Operation)
          yield "at", OrderedValue.Text contribution.At
          yield "actor", actorValue contribution.Actor
          match contribution.WorkItem with
          | Some workItem -> yield "workItem", OrderedValue.Text workItem
          | None -> ()
          match contribution.Reason with
          | Some reason -> yield "reason", OrderedValue.Text reason
          | None -> ()
          if not contribution.Evidence.IsEmpty then
              yield "evidence", contribution.Evidence |> List.map OrderedValue.Text |> OrderedValue.List
          match contribution.Basis with
          | Some basis -> yield "basis", OrderedValue.Text basis
          | None -> () ]
        |> OrderedValue.Fields

    let provenanceValue (provenance: Provenance) =
        OrderedValue.Fields
            [ "contributions", provenance.Contributions |> List.map contributionValue |> OrderedValue.List ]

    /// The neutral (unordered) form of an ordered value, for re-reading a
    /// rendered record through the same reader validation uses.
    let rec toArtifactValue value =
        match value with
        | OrderedValue.Text text -> ArtifactValue.Text text
        | OrderedValue.List items -> items |> List.map toArtifactValue |> ArtifactValue.Sequence
        | OrderedValue.Fields fields -> fields |> List.map (fun (name, item) -> name, toArtifactValue item) |> Map.ofList |> ArtifactValue.Mapping
