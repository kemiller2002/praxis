namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Execution
open Praxis.Infrastructure.Planning
open Ordo.Construction

/// File, clock and policy effects behind `praxis construct`. The CLI parses
/// arguments and prints; each operation returns a JSON document.
[<RequireQualifiedAccess>]
module ConstructionService =
    let private maxPlanBytes = 1048576

    let private readPlan (path: string) =
        try
            let info = FileInfo(Path.GetFullPath path)
            if not info.Exists then Error "construction plan file does not exist"
            elif info.Length > int64 maxPlanBytes then Error "construction plan exceeds 1 MiB"
            else Ok(File.ReadAllText info.FullName)
        with error -> Error("construction plan unreadable: " + error.Message)

    let private strings (values: string list) = JsonArray(values |> List.map (fun value -> JsonValue.Create value :> JsonNode) |> List.toArray)

    let private kindName = function
        | ConstructionWriteKind.Create -> "create"
        | ConstructionWriteKind.Replace -> "replace"
        | ConstructionWriteKind.Unchanged -> "unchanged"
        | ConstructionWriteKind.RemoveStale -> "remove-stale"

    let private obligationNode (obligation: Artifacts.ConstructionObligation) =
        let node = JsonObject()
        node["id"] <- JsonValue.Create obligation.Id
        match obligation.Kind with
        | Artifacts.Implement contract ->
            node["kind"] <- JsonValue.Create "implement"
            node["contract"] <- JsonValue.Create contract
        | Artifacts.ResolveUnresolved(subject, question) ->
            node["kind"] <- JsonValue.Create "resolve"
            node["subject"] <- JsonValue.Create subject
            node["question"] <- JsonValue.Create question
        | Artifacts.ResolveEffectFailure(effect, subject, question) ->
            node["kind"] <- JsonValue.Create "resolve-effect-failure"
            node["effect"] <- JsonValue.Create effect
            node["subject"] <- JsonValue.Create subject
            node["question"] <- JsonValue.Create question
        | Artifacts.VerifyIndependently what ->
            node["kind"] <- JsonValue.Create "verify-independently"
            node["what"] <- JsonValue.Create what
        node["requirementKeys"] <- strings obligation.RequirementKeys
        node["status"] <- JsonValue.Create "open"
        node

    let previewNode (preview: ConstructionPreview) =
        let b = preview.Binding
        let binding = JsonObject()
        [ "policyRevision", b.PolicyRevision; "groupId", b.GroupId; "cohortId", b.CohortId; "sourceCommit", b.SourceCommit
          "manifestPath", b.ManifestPath; "manifestDigest", b.ManifestDigest; "blueprintPath", b.BlueprintPath
          "blueprintDigest", b.BlueprintDigest; "approver", b.Approver; "approvalExpiresAt", b.ApprovalExpiresAt
          "planDigest", b.PlanDigest; "generatorVersion", b.GeneratorVersion
          "ordoCoreVersion", b.OrdoCoreVersion; "ordoConstructionVersion", b.OrdoConstructionVersion ]
        |> List.iter (fun (name, value) -> binding[name] <- JsonValue.Create value)
        binding["requirementKeys"] <- strings b.RequirementKeys
        binding["decisionIds"] <- strings b.DecisionIds
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "praxis.construction-preview/1"
        node["binding"] <- binding
        node["targetRef"] <- JsonValue.Create preview.TargetRef
        node["targetCommit"] <- JsonValue.Create preview.TargetCommit
        node["ownershipRoot"] <- JsonValue.Create preview.OwnershipRoot
        node["writes"] <-
            JsonArray(
                preview.Writes
                |> List.map (fun write ->
                    let item = JsonObject()
                    item["kind"] <- JsonValue.Create(kindName write.Kind)
                    item["path"] <- JsonValue.Create write.Path
                    write.Digest |> Option.iter (fun digest -> item["sha256"] <- JsonValue.Create digest)
                    item :> JsonNode)
                |> List.toArray)
        node["extensionContracts"] <- strings (preview.ExtensionContracts |> List.map _.Name)
        node["obligations"] <- JsonArray(preview.Obligations |> List.map (fun o -> obligationNode o :> JsonNode) |> List.toArray)
        node["notice"] <- JsonValue.Create "Generated structure only. Not evidence of behavioral verification; obligations remain open."
        node

    let private admitted root hostConfig groupId planPath =
        LocalRuntimeService.configuration root hostConfig
        |> Result.mapError (function LocalCommandFailure.Invalid reason | LocalCommandFailure.Refused reason -> reason)
        |> Result.bind (fun configuration ->
            readPlan planPath |> Result.bind (fun plan ->
                (FileLocalRuntime.hostStore configuration).Load() |> Result.bind (fun policy ->
                    ConstructionAdmission.admit configuration policy DateTimeOffset.UtcNow groupId plan
                    |> Result.map (fun admitted -> configuration, admitted))))

    /// Read-only: nothing is written anywhere.
    let preview root hostConfig groupId planPath targetRef =
        admitted root hostConfig groupId planPath |> Result.bind (fun (configuration, admitted) ->
            ConstructionApplication.preview configuration admitted targetRef |> Result.map previewNode)

    let apply root hostConfig groupId planPath targetRef =
        admitted root hostConfig groupId planPath |> Result.bind (fun (configuration, admitted) ->
            ConstructionApplication.apply configuration admitted targetRef DateTimeOffset.UtcNow |> Result.map (fun report ->
                let node = previewNode report.Preview
                node["schemaVersion"] <- JsonValue.Create "praxis.construction-application/1"
                node["constructionId"] <- JsonValue.Create report.ConstructionId
                match report.Outcome with
                | ConstructionOutcome.Applied candidate ->
                    node["outcome"] <- JsonValue.Create "applied"
                    node["candidateCommit"] <- JsonValue.Create candidate
                | ConstructionOutcome.AlreadyCurrent -> node["outcome"] <- JsonValue.Create "already-current"
                node))

    /// Classify the admitted construction's work and persist the routing
    /// record in the host construction root. Never touches the repository.
    let route root hostConfig groupId planPath =
        admitted root hostConfig groupId planPath |> Result.bind (fun (configuration, admitted) ->
            let routing = ConstructionRouting.classify configuration admitted
            ConstructionRouting.record configuration routing |> Result.map (fun path ->
                let node = ConstructionRouting.render routing
                node["recordPath"] <- JsonValue.Create path
                node))

    /// Only the implementation role may fill slots: verification and review
    /// stay independent of the code they judge. The role is a routing
    /// constraint on this request, not an authenticated identity.
    let private implementationRole (role: string) =
        match ExecutionRole.tryParse role with
        | Some ExecutionRole.Implementation -> Ok()
        | Some other -> Error(sprintf "role %s may not submit handwritten extensions; only the implementation role fills slots" (ExecutionRole.toWire other))
        | None -> Error("unknown role: " + role)

    let private kindName' = function
        | ExtensionWriteKind.Create -> "create"
        | ExtensionWriteKind.Replace -> "replace"
        | ExtensionWriteKind.Unchanged -> "unchanged"

    let extend root hostConfig groupId planPath targetRef filesDirectory role =
        implementationRole role |> Result.bind (fun () ->
            admitted root hostConfig groupId planPath |> Result.bind (fun (configuration, admitted) ->
                ConstructionExtension.extend configuration admitted targetRef filesDirectory DateTimeOffset.UtcNow |> Result.map (fun report ->
                    let node = JsonObject()
                    node["schemaVersion"] <- JsonValue.Create "praxis.construction-extension/1"
                    node["extensionId"] <- JsonValue.Create report.ExtensionId
                    node["constructionId"] <- JsonValue.Create report.ConstructionId
                    node["constructionCommit"] <- JsonValue.Create report.ConstructionCommit
                    node["planDigest"] <- JsonValue.Create report.Routing.Binding.PlanDigest
                    node["routeId"] <- JsonValue.Create(ConstructionRouting.routeId report.Routing)
                    node["routeRecord"] <- JsonValue.Create report.RouteRecord
                    node["targetRef"] <- JsonValue.Create report.TargetRef
                    node["targetCommit"] <- JsonValue.Create report.TargetCommit
                    node["files"] <-
                        JsonArray(
                            report.Files
                            |> List.map (fun (file, kind) ->
                                let item = JsonObject()
                                item["kind"] <- JsonValue.Create(kindName' kind)
                                item["path"] <- JsonValue.Create file.Path
                                item["sha256"] <- JsonValue.Create file.Digest
                                item["contracts"] <- strings file.Contracts
                                item :> JsonNode)
                            |> List.toArray)
                    let routed = ConstructionRouting.render report.Routing
                    node["exceptions"] <- routed["exceptions"].DeepClone()
                    node["measurement"] <- routed["measurement"].DeepClone()
                    match report.Outcome with
                    | ExtensionOutcome.Applied candidate ->
                        node["outcome"] <- JsonValue.Create "applied"
                        node["candidateCommit"] <- JsonValue.Create candidate
                    | ExtensionOutcome.AlreadyCurrent -> node["outcome"] <- JsonValue.Create "already-current"
                    node["notice"] <- JsonValue.Create "Handwritten files committed into host-declared slots. No worker or submitted code ran; obligations remain open until independently verified."
                    node)))
