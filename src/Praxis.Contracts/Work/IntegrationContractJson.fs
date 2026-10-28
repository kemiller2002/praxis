namespace Praxis.Contracts.Work

open System
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Work

/// `praxis.integration-contract/1` (schemas/praxis-integration-contract.schema.json)
/// and the `praxis.streams-check/1` report.
[<RequireQualifiedAccess>]
module IntegrationContractJson =
    exception private Malformed of string

    let ReportSchema = "praxis.streams-check/1"

    let private fail message = raise (Malformed message)

    let private field (node: JsonObject) (name: string) : JsonNode =
        let mutable value: JsonNode = null
        if node.TryGetPropertyValue(name, &value) then value else null

    let private text (node: JsonObject) (name: string) (where: string) =
        match field node name with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String && value.GetValue<string>().Trim().Length > 0 -> value.GetValue<string>()
        | null -> fail $"{where}.{name} is required"
        | _ -> fail $"{where}.{name} must be a non-empty string"

    let private optionalText (node: JsonObject) (name: string) (where: string) =
        match field node name with
        | null -> None
        | _ -> Some(text node name where)

    let private texts (node: JsonObject) (name: string) (where: string) =
        match field node name with
        | null -> []
        | :? JsonArray as array ->
            array
            |> Seq.mapi (fun index item ->
                match item with
                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String && value.GetValue<string>().Trim().Length > 0 -> value.GetValue<string>()
                | _ -> fail $"{where}.{name}[{index}] must be a non-empty string")
            |> Seq.toList
        | _ -> fail $"{where}.{name} must be an array of strings"

    let private objects (node: JsonObject) (name: string) (where: string) =
        match field node name with
        | null -> []
        | :? JsonArray as array ->
            array
            |> Seq.mapi (fun index item ->
                match item with
                | :? JsonObject as entry -> entry, $"{where}.{name}[{index}]"
                | _ -> fail $"{where}.{name}[{index}] must be an object")
            |> Seq.toList
        | _ -> fail $"{where}.{name} must be an array"

    let private approval (node: JsonObject, where: string) : InterfaceApproval =
        let authority =
            match optionalText node "decision" where, optionalText node "approvedBy" where with
            | Some decision, None -> ApprovalAuthority.Decision decision
            | None, Some approver -> ApprovalAuthority.Recorded approver
            | Some _, Some _ -> fail $"{where} names both decision and approvedBy; use one"
            | None, None -> fail $"{where} needs a decision (Decision Record ID) or approvedBy"

        { Stream = text node "stream" where
          Paths = texts node "paths" where
          Authority = authority
          Reason = optionalText node "reason" where }

    let private sharedInterface (node: JsonObject, where: string) : SharedInterface =
        let paths = texts node "paths" where
        if paths.IsEmpty then fail $"{where}.paths must name at least one path"

        { Id = text node "id" where
          Owner = text node "owner" where
          Paths = paths
          Approvals = objects node "approvals" where |> List.map approval }

    let private stream (node: JsonObject, where: string) : StreamContract =
        let owns =
            match field node "owns", field node "ownsPlannerArea" with
            | null, (:? JsonValue as flag) when flag.GetValueKind() = JsonValueKind.True -> OwnershipSource.PlannerArea
            | null, _ -> fail $"{where} needs owns (globs) or ownsPlannerArea: true"
            | _, null -> OwnershipSource.Declared(texts node "owns" where)
            | _ -> fail $"{where} names both owns and ownsPlannerArea; use one"

        let readiness =
            match field node "readiness" with
            | null -> ReadinessCondition.all
            | _ ->
                texts node "readiness" where
                |> List.map (fun code ->
                    match ReadinessCondition.tryParse code with
                    | Some condition -> condition
                    | None -> fail $"{where}.readiness: unknown condition '{code}'")

        { Id = text node "id" where
          WorkItem = text node "workItem" where
          Ref = text node "ref" where
          Owns = owns
          DependsOn = texts node "dependsOn" where
          RequiredTests = texts node "requiredTests" where
          Readiness = readiness
          Artifacts = texts node "artifacts" where }

    /// The contract object; a document whose root holds `integration` (a
    /// `ros.json`) is read through that member.
    let parse (json: string) : Result<IntegrationContract, string> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as root ->
                let contract =
                    match field root "integration" with
                    | :? JsonObject as nested -> nested
                    | null -> root
                    | _ -> fail "integration must be an object"

                let schema = text contract "schema" "integration"
                if schema <> IntegrationContract.Schema then fail $"integration.schema is '{schema}', expected '{IntegrationContract.Schema}'"

                Ok
                    { Target = optionalText contract "target" "integration"
                      ManagedPaths = texts contract "managedPaths" "integration"
                      CommonPaths = texts contract "commonPaths" "integration"
                      Streams = objects contract "streams" "integration" |> List.map stream
                      Interfaces = objects contract "interfaces" "integration" |> List.map sharedInterface }
            | _ -> Error "malformed integration contract: the document is not a JSON object"
        with
        | Malformed message -> Error $"malformed integration contract: {message}"
        | :? JsonException as error -> Error $"malformed integration contract: {error.Message}"

    /// Whether a `ros.json` document declares an integration contract.
    let declares (json: string) =
        try
            match JsonNode.Parse json with
            | :? JsonObject as root -> not (isNull (field root "integration"))
            | _ -> false
        with _ ->
            false

    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    let private optionalString (value: string option) : JsonNode =
        value |> Option.map (fun value -> JsonValue.Create value :> JsonNode) |> Option.toObj

    let private findingNode (finding: PathFinding) =
        let node = JsonObject()
        node["code"] <- JsonValue.Create(PathFinding.code finding)
        node["path"] <- JsonValue.Create(PathFinding.path finding)

        match finding with
        | PathFinding.OutsideOwnership(_, owners) -> node["ownedBy"] <- strings owners
        | PathFinding.InterfaceUnapproved(_, id, owner) ->
            node["interface"] <- JsonValue.Create id
            node["interfaceOwner"] <- JsonValue.Create owner
        | PathFinding.DecisionNotFound(_, id, decision) ->
            node["interface"] <- JsonValue.Create id
            node["decision"] <- JsonValue.Create decision

        node

    let private streamNode (report: StreamReport) =
        let node = JsonObject()
        node["id"] <- JsonValue.Create report.Stream
        node["workItem"] <- JsonValue.Create report.WorkItem
        node["ref"] <- JsonValue.Create report.Ref
        node["tip"] <- optionalString report.Tip
        node["ownership"] <- JsonValue.Create report.OwnershipOrigin
        node["integrated"] <- JsonValue.Create report.Integrated
        node["readiness"] <- JsonValue.Create(Readiness.code report.Readiness)
        let findings = JsonArray()
        report.Findings |> List.iter (fun finding -> findings.Add(findingNode finding :> JsonNode))
        node["findings"] <- findings
        let covered = JsonArray()

        report.Covered
        |> List.iter (fun entry ->
            let item = JsonObject()
            item["path"] <- JsonValue.Create entry.Path
            item["basis"] <- JsonValue.Create entry.Basis
            covered.Add(item :> JsonNode))

        node["covered"] <- covered
        node["managedPaths"] <- strings report.ManagedPaths
        node["targetOverlap"] <- strings report.TargetOverlap
        let tests = JsonArray()

        report.RequiredTests
        |> List.iter (fun test ->
            let item = JsonObject()
            item["test"] <- JsonValue.Create test.Test
            item["status"] <- JsonValue.Create test.Status
            item["detail"] <- JsonValue.Create test.Detail
            tests.Add(item :> JsonNode))

        node["requiredTests"] <- tests
        let conditions = JsonArray()

        report.Conditions
        |> List.iter (fun condition ->
            let item = JsonObject()
            item["condition"] <- JsonValue.Create(ReadinessCondition.code condition.Condition)

            item["status"] <-
                JsonValue.Create(
                    match condition.Met with
                    | Some true -> "met"
                    | Some false -> "unmet"
                    | None -> "unverified"
                )

            item["detail"] <- JsonValue.Create condition.Detail
            conditions.Add(item :> JsonNode))

        node["conditions"] <- conditions
        node

    let toNode (report: IntegrationReport) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create ReportSchema
        node["passed"] <- JsonValue.Create(IntegrationReport.passes report)
        node["target"] <- JsonValue.Create report.Target
        node["targetCommit"] <- optionalString report.TargetCommit
        node["contractFindings"] <- strings report.ContractFindings
        let streams = JsonArray()
        report.Streams |> List.iter (fun stream -> streams.Add(streamNode stream :> JsonNode))
        node["streams"] <- streams
        let overlaps = JsonArray()

        report.SiblingOverlaps
        |> List.iter (fun overlap ->
            let item = JsonObject()
            item["left"] <- JsonValue.Create overlap.Left
            item["right"] <- JsonValue.Create overlap.Right
            item["paths"] <- strings overlap.Paths
            item["managedPaths"] <- strings overlap.ManagedPaths
            overlaps.Add(item :> JsonNode))

        node["siblingOverlaps"] <- overlaps
        node["sequence"] <- strings report.Sequence
        let cycles = JsonArray()
        report.Cycles |> List.iter (fun cycle -> cycles.Add(strings cycle :> JsonNode))
        node["cycles"] <- cycles
        node

    let renderJson report =
        (toNode report).ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2))
