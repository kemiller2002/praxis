namespace Praxis.Contracts.Work

open System
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Work

/// Decoders of the two grouped-execution evidence documents
/// (PRX-GRP-133, PRX-GRP-134): `praxis.group-analysis/1` and
/// `praxis.group-verification/1`. Schemas: `schemas/praxis-group-analysis.schema.json`
/// and `schemas/praxis-group-verification.schema.json`. Every field problem
/// is reported; nothing is guessed.
[<RequireQualifiedAccess>]
module GroupEvidenceJson =
    let private field (node: JsonObject) (name: string) : JsonNode option = Option.ofObj node[name]

    let private text (node: JsonObject) (name: string) : Result<string, string> =
        match field node name with
        | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetValue<string>())) ->
            Ok(value.GetValue<string>())
        | _ -> Error $"{name} must be a non-empty string"

    let private optionalText (node: JsonObject) (name: string) : Result<string option, string> =
        match field node name with
        | None -> Ok None
        | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.String -> Ok(Some(value.GetValue<string>()))
        | Some _ -> Error $"{name} must be a string or null"

    let private objects (node: JsonObject) (name: string) : Result<JsonObject list, string> =
        match field node name with
        | None -> Ok []
        | Some(:? JsonArray as values) when values |> Seq.forall (fun item -> item :? JsonObject) -> Ok(values |> Seq.map (fun item -> item :?> JsonObject) |> Seq.toList)
        | Some _ -> Error $"{name} must be an array of objects"

    let private texts (node: JsonObject) (name: string) : Result<string list, string> =
        match field node name with
        | Some(:? JsonArray as values) ->
            let parsed =
                values
                |> Seq.map (function
                    | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                    | _ -> None)
                |> Seq.toList

            if parsed |> List.forall Option.isSome then Ok(parsed |> List.choose id) else Error $"{name} must be an array of strings"
        | _ -> Error $"{name} must be an array of strings"

    /// Applies `read` to every entry, collecting every problem with its index.
    let private each (name: string) (read: JsonObject -> Result<'T, string list>) (entries: Result<JsonObject list, string>) : Result<'T list, string list> =
        match entries with
        | Error message -> Error [ message ]
        | Ok entries ->
            let results = entries |> List.mapi (fun index entry -> index, read entry)
            let errors = results |> List.collect (fun (index, result) -> match result with Error problems -> problems |> List.map (fun problem -> $"{name}[{index}]: {problem}") | Ok _ -> [])
            if errors.IsEmpty then Ok(results |> List.choose (fun (_, result) -> match result with Ok value -> Some value | Error _ -> None)) else Error errors

    let private errorsOf (results: Result<obj, string> list) = results |> List.choose (function Error message -> Some message | Ok _ -> None)
    let private boxed (result: Result<'a, string>) = result |> Result.map box
    let private boxedAll (result: Result<'a, string list>) = result |> Result.mapError (String.concat "; ") |> Result.map box

    let private value (result: Result<'a, 'e>) =
        match result with
        | Ok value -> value
        | Error _ -> invalidOp "unreachable: checked above"

    let private root (schema: string) (json: string) (decode: JsonObject -> Result<'T, string list>) : EvidenceReading<'T> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as document ->
                match text document "schema" with
                | Ok name when name = schema ->
                    match decode document with
                    | Ok parsed -> EvidenceReading.Parsed parsed
                    | Error problems -> EvidenceReading.Malformed(String.concat "; " problems)
                | Ok name -> EvidenceReading.Unsupported $"schema '{name}' is not '{schema}'"
                | Error _ -> EvidenceReading.Malformed $"schema must be '{schema}'"
            | _ -> EvidenceReading.Malformed "the document must be a JSON object"
        with :? JsonException as error ->
            EvidenceReading.Malformed $"not valid JSON: {error.Message}"

    let private member' (node: JsonObject) : Result<AnalysedMember, string list> =
        let id = text node "workItemId"
        let repository = optionalText node "repository"
        let criteria = texts node "acceptanceCriteria"

        match errorsOf [ boxed id; boxed repository; boxed criteria ] with
        | [] -> Ok { WorkItemId = value id; Repository = value repository; AcceptanceCriteria = value criteria }
        | errors -> Error errors

    let private reuse (node: JsonObject) : Result<ReuseEntry, string list> =
        let element = text node "element"
        let location = text node "location"

        let disposition =
            text node "disposition"
            |> Result.bind (fun raw ->
                if List.contains raw [ "reused"; "extended"; "not-reused" ] then Ok raw
                else Error $"disposition '{raw}' is not reused, extended or not-reused")

        let reason = text node "reason"

        match errorsOf [ boxed element; boxed location; boxed disposition; boxed reason ] with
        | [] -> Ok { Element = value element; Location = value location; Disposition = value disposition; Reason = value reason }
        | errors -> Error errors

    let private search (node: JsonObject) : Result<SearchEntry, string list> =
        match text node "command", text node "finding" with
        | Ok command, Ok finding -> Ok { Command = command; Finding = finding }
        | command, finding -> Error(errorsOf [ boxed command; boxed finding ])

    let private abstraction (node: JsonObject) : Result<NewAbstraction, string list> =
        let name = text node "name"
        // Present but possibly empty: the gate reports an empty one by name.
        let considered = optionalText node "consideredExisting" |> Result.map (Option.defaultValue "")
        let why = optionalText node "whyNotReused" |> Result.map (Option.defaultValue "")

        match errorsOf [ boxed name; boxed considered; boxed why ] with
        | [] -> Ok { Name = value name; ConsideredExisting = value considered; WhyNotReused = value why }
        | errors -> Error errors

    /// `praxis.group-analysis/1` (PRX-GRP-133).
    let decodeAnalysis (json: string) : EvidenceReading<GroupAnalysis> =
        root GroupGates.analysisSchema json (fun document ->
            let groupId = text document "groupId"
            let execution = optionalText document "groupExecutionId"
            let members = each "members" member' (objects document "members")
            let inventory = each "reuseInventory" reuse (objects document "reuseInventory")
            let searches = each "searches" search (objects document "searches")
            let abstractions = each "newAbstractions" abstraction (objects document "newAbstractions")

            match errorsOf [ boxed groupId; boxed execution; boxedAll members; boxedAll inventory; boxedAll searches; boxedAll abstractions ] with
            | [] ->
                Ok
                    { GroupId = value groupId
                      GroupExecutionId = value execution
                      Members = value members
                      ReuseInventory = value inventory
                      Searches = value searches
                      NewAbstractions = value abstractions }
            | errors -> Error errors)

    let private row (node: JsonObject) : Result<CriterionVerificationRow, string list> =
        let memberId = text node "member"
        let criterion = text node "criterion"

        let status =
            text node "status"
            |> Result.bind (fun raw -> CriterionStatus.tryParse raw |> Option.map Ok |> Option.defaultValue (Error $"status '{raw}' is not met, partially-met, not-met or unknown"))

        let evidence =
            match field node "evidence" with
            | Some(:? JsonObject as evidence) ->
                let kind =
                    text evidence "kind"
                    |> Result.bind (fun raw ->
                        if List.contains raw [ "test"; "command"; "location" ] then Ok raw
                        else Error $"evidence kind '{raw}' is not test, command or location")

                match kind, text evidence "reference", optionalText evidence "result" with
                | Ok kind, Ok reference, Ok result -> Ok({ Kind = kind; Reference = reference; Result = result |> Option.defaultValue "" }: CriterionEvidence)
                | kind, reference, result -> Error(String.concat "; " (errorsOf [ boxed kind; boxed reference; boxed result ]))
            | _ -> Error "evidence must be an object {kind, reference, result}"

        let deferred = optionalText node "deferredTo"

        match errorsOf [ boxed memberId; boxed criterion; boxed status; boxed evidence; boxed deferred ] with
        | [] ->
            Ok
                { Member = value memberId
                  Criterion = value criterion
                  Status = value status
                  Evidence = value evidence
                  DeferredTo = value deferred }
        | errors -> Error errors

    /// `praxis.group-verification/1` (PRX-GRP-134).
    let decodeVerification (json: string) : EvidenceReading<GroupVerification> =
        root GroupGates.verificationSchema json (fun document ->
            let groupId = text document "groupId"
            let execution = optionalText document "groupExecutionId"
            let rows = each "rows" row (objects document "rows")

            match errorsOf [ boxed groupId; boxed execution; boxedAll rows ] with
            | [] -> Ok { GroupId = value groupId; GroupExecutionId = value execution; Rows = value rows }
            | errors -> Error errors)
