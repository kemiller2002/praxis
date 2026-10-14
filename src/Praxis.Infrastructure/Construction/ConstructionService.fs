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
          "planDigest", b.PlanDigest; "planSourceManifestDigest", b.PlanSourceManifestDigest
          "planBlueprintDigest", b.PlanBlueprintDigest; "generatorVersion", b.GeneratorVersion
          "ordoCoreVersion", b.OrdoCoreVersion; "ordoConstructionVersion", b.OrdoConstructionVersion ]
        |> List.iter (fun (name, value) -> binding[name] <- JsonValue.Create value)
        binding["requirementKeys"] <- strings b.RequirementKeys
        binding["decisionIds"] <- strings b.DecisionIds
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "praxis.construction-preview/1"
        node["binding"] <- binding
        // The exact admitted plan (canonical form), so a verifier never has to
        // rebuild a host-stamped binding from the agent's operations.
        node["plan"] <- JsonNode.Parse b.PlanCanonicalJson
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

    let private applicationNode (report: ConstructionReport) =
        let node = previewNode report.Preview
        node["schemaVersion"] <- JsonValue.Create "praxis.construction-application/1"
        node["constructionId"] <- JsonValue.Create report.ConstructionId
        match report.Outcome with
        | ConstructionOutcome.Applied candidate ->
            node["outcome"] <- JsonValue.Create "applied"
            node["candidateCommit"] <- JsonValue.Create candidate
        | ConstructionOutcome.AlreadyCurrent -> node["outcome"] <- JsonValue.Create "already-current"
        node

    /// Read-only: nothing is written anywhere.
    let preview root hostConfig groupId planPath targetRef =
        admitted root hostConfig groupId planPath |> Result.bind (fun (configuration, admitted) ->
            ConstructionApplication.preview configuration admitted targetRef |> Result.map previewNode)

    let apply root hostConfig groupId planPath targetRef =
        admitted root hostConfig groupId planPath |> Result.bind (fun (configuration, admitted) ->
            ConstructionApplication.apply configuration admitted targetRef DateTimeOffset.UtcNow |> Result.map applicationNode)

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

    let private extensionNode (report: ExtensionReport) =
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
        node

    let extend root hostConfig groupId planPath targetRef filesDirectory role =
        implementationRole role |> Result.bind (fun () ->
            admitted root hostConfig groupId planPath |> Result.bind (fun (configuration, admitted) ->
                ConstructionExtension.extend configuration admitted targetRef filesDirectory DateTimeOffset.UtcNow |> Result.map extensionNode))

    // ---------------------------------------- mechanical-first (CONSTRUCT-008)

    let private hostState root hostConfig =
        LocalRuntimeService.configuration root hostConfig
        |> Result.mapError (function LocalCommandFailure.Invalid reason | LocalCommandFailure.Refused reason -> reason)
        |> Result.bind (fun configuration -> (FileLocalRuntime.hostStore configuration).Load() |> Result.map (fun policy -> configuration, policy))

    let private configurationOnly root hostConfig = hostState root hostConfig |> Result.toOption |> Option.map fst

    /// Write an agent-side output file; a link is refused, a regular file replaced.
    let private writeOutput (path: string) (content: string) =
        try
            let full = Path.GetFullPath path
            let info = FileInfo full
            if info.Exists && not (isNull info.LinkTarget) then Error("output is a symbolic link: " + path)
            elif Directory.Exists full then Error("output is a directory: " + path)
            else
                File.WriteAllText(full, content, Text.UTF8Encoding(false))
                Ok full
        with error -> Error("cannot write " + path + ": " + error.Message)

    let private twin (out: string) (suffix: string) =
        let full = Path.GetFullPath out
        let stem = if full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) then full.Substring(0, full.Length - 5) else full
        stem + suffix

    let private notice = "Generated structure only. Not evidence of behavioral verification; obligations remain open."

    /// The host artifacts `prepare` writes for `out`: never inside a workspace.
    let private hostArtifacts (out: string) = [ Path.GetFullPath out; twin out ".handoff.json"; twin out ".handoff.md" ]

    /// `construct prepare`, with the default-plan generator injected (the
    /// CLI passes `OrdoDefaultPlan.generator ()`). With `workspace`, the
    /// worker workspace (HANDOFF.md and an empty outbox) is created after the
    /// prepared artifact; its layout is checked before anything is written.
    let prepareInWith (generator: DefaultPlanGenerator) root hostConfig groupId (cohort: string option) (vocabulary: string option) (decisions: string option) (out: string) (workspace: string option) (telemetryTarget: string option) =
        let configuration = configurationOnly root hostConfig
        let layout =
            match workspace, configuration with
            | None, _ -> Ok()
            | Some _, None -> Error "the host configuration is unreadable, so the workspace layout cannot be checked"
            | Some w, Some c ->
                match ConstructionWorkspace.layoutProblems w (ConstructionWorkspace.hostLocations c (ConstructionWorkspace.recordPath out :: hostArtifacts out)) with
                | [] -> Ok()
                | problems -> Error("workspace refused: " + String.concat "; " problems)
        let written (report: PreparedReport) =
            writeOutput out report.Text |> Result.bind (fun preparedPath ->
                writeOutput (twin out ".handoff.json") (report.HandoffJson.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n") |> Result.bind (fun handoffPath ->
                    writeOutput (twin out ".handoff.md") report.HandoffMarkdown |> Result.bind (fun markdownPath ->
                        match workspace, configuration with
                        | None, _ -> Ok(report, preparedPath, handoffPath, markdownPath, None)
                        | Some w, Some c ->
                            ConstructionWorkspace.create c out (hostArtifacts out) w report.CacheKey report.HandoffMarkdown
                            |> Result.map (fun created -> report, preparedPath, handoffPath, markdownPath, Some created)
                        | Some _, None -> Error "the host configuration is unreadable")))
        // "prepared" is reported only once --out, its twins and any workspace are written.
        let result, telemetry =
            ConstructionTelemetry.measure root telemetryTarget configuration "prepare" groupId (fun () ->
                match layout
                      |> Result.bind (fun () -> hostState root hostConfig)
                      |> Result.bind (fun (configuration, policy) -> ConstructionPrepared.prepare generator configuration policy DateTimeOffset.UtcNow groupId cohort vocabulary decisions)
                      |> Result.bind written with
                | Ok((report, _, _, _, _) as outputs) -> Ok outputs, Some report.CacheKey, (if report.Reused then ConstructionTelemetryOutcome.Reused else ConstructionTelemetryOutcome.Prepared)
                | Error reason ->
                    configuration |> Option.iter (fun c -> ConstructionPrepared.recordRefusal c "prepare" groupId None [ reason ] |> ignore)
                    Error reason, None, ConstructionTelemetryOutcome.Refused)
        result |> Result.map (fun (report, preparedPath, handoffPath, markdownPath, created) ->
            let node = JsonObject()
            node["schemaVersion"] <- JsonValue.Create "praxis.construction-prepare/1"
            node["cacheKey"] <- JsonValue.Create report.CacheKey
            node["reused"] <- JsonValue.Create report.Reused
            node["prepared"] <- JsonValue.Create preparedPath
            node["handoff"] <- JsonValue.Create handoffPath
            node["handoffMarkdown"] <- JsonValue.Create markdownPath
            match created with
            | Some(record, recordPath) ->
                node["workspace"] <- JsonValue.Create record.Workspace
                node["workspaceRecord"] <- JsonValue.Create recordPath
                node["workerFiles"] <- strings [ ConstructionWorkspace.HandoffName; ConstructionWorkspace.OutboxName + "/" ]
            | None -> ()
            node["obligations"] <- JsonValue.Create report.Obligations
            node["permittedFiles"] <- strings report.PermittedFiles
            node["telemetry"] <- telemetry
            node["notice"] <- JsonValue.Create notice
            node)
        |> Result.mapError (fun reason -> reason + " [telemetry: " + telemetry.ToJsonString() + "]")

    let prepareWith (generator: DefaultPlanGenerator) root hostConfig groupId (cohort: string option) (vocabulary: string option) (decisions: string option) (out: string) (telemetryTarget: string option) =
        prepareInWith generator root hostConfig groupId cohort vocabulary decisions out None telemetryTarget

    /// `construct apply --prepared`, with the generator injected.
    /// `dokimos.host-observations/1`: every handwritten operation the host
    /// denied, for independent verification of rejected operations. The
    /// field names are Dokimos's established contract (`schema`, `detail`).
    let private observationsText (denied: (string * string) list) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create "dokimos.host-observations/1"
        node["observations"] <-
            JsonArray(
                denied
                |> List.mapi (fun index (path, reason) ->
                    let item = JsonObject()
                    item["id"] <- JsonValue.Create(sprintf "denied-%d" (index + 1))
                    item["kind"] <- JsonValue.Create "write"
                    item["recordedBy"] <- JsonValue.Create "host"
                    item["outcome"] <- JsonValue.Create "denied"
                    item["path"] <- JsonValue.Create path
                    item["detail"] <- JsonValue.Create reason
                    item :> JsonNode)
                |> List.toArray)
        node.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n"

    /// The cache key a prepared artifact states, if it states one.
    let private preparedCacheKey (preparedPath: string) =
        try
            match JsonNode.Parse(File.ReadAllText preparedPath) with
            | :? JsonObject as node -> (match node["cacheKey"] with :? JsonValue as v -> (try Some(v.GetValue<string>()) with _ -> None) | _ -> None)
            | _ -> None
        with _ -> None

    /// The handwritten files to apply: `--extensions` as given, or the
    /// workspace outbox staged host-side (dispositions kept, never applied).
    let private submitted (configuration: LocalHostConfiguration) preparedPath (extensions: string option) (workspace: string option) =
        match extensions, workspace with
        | Some _, Some _ -> Error([ "give --extensions or --workspace, not both" ], [])
        | _, None -> Ok(extensions, None)
        | None, Some w ->
            match ConstructionWorkspace.readRecord preparedPath with
            | Error reason -> Error([ reason ], [])
            | Ok record when Some record.CacheKey <> preparedCacheKey preparedPath -> Error([ "the workspace was prepared for a different prepared artifact" ], [])
            | Ok record ->
                // Host scratch beside the prepared artifact: outside the workspace
                // (the layout rule) and outside every journaled host root.
                let staging = twin preparedPath (".staged-" + Guid.NewGuid().ToString("N"))
                let discard () = try Directory.Delete(staging, true) with _ -> ()
                match ConstructionWorkspace.submission record w staging with
                | Error reason -> discard (); Error([ reason ], [ w, reason ])
                | Ok submission when not submission.Denied.IsEmpty ->
                    discard ()
                    Error([ "extension refused: " + String.concat "; " (submission.Denied |> List.map snd) ], submission.Denied)
                | Ok submission -> Ok(Some submission.Staged, submission.Dispositions)

    let applyPreparedInWith (generator: DefaultPlanGenerator) root hostConfig groupId preparedPath targetRef (extensions: string option) (workspace: string option) (observations: string option) (telemetryTarget: string option) =
        let configuration = configurationOnly root hostConfig
        let mutable denied = []
        let mutable dispositions = None
        let result, telemetry =
            ConstructionTelemetry.measure root telemetryTarget configuration "apply" groupId (fun () ->
                match hostState root hostConfig with
                | Error reason -> Error reason, None, ConstructionTelemetryOutcome.Refused
                | Ok(configuration, policy) ->
                    match submitted configuration preparedPath extensions workspace with
                    | Error(reasons, refusedWrites) ->
                        denied <- refusedWrites
                        let recorded =
                            match ConstructionPrepared.recordRefusal configuration "apply" groupId None reasons with
                            | Some path -> " (refusal recorded at " + path + ")"
                            | None -> ""
                        Error(String.concat "; " reasons + recorded), None, ConstructionTelemetryOutcome.Refused
                    | Ok(files, returned) ->
                        dispositions <- returned
                        let applied = ConstructionPrepared.applyPrepared generator configuration policy DateTimeOffset.UtcNow groupId preparedPath targetRef files
                        // The staged copy of a workspace outbox is host scratch; the
                        // extension report keeps every applied file's digest.
                        if workspace.IsSome then files |> Option.iter (fun staged -> try Directory.Delete(Path.GetDirectoryName staged, true) with _ -> ())
                        match applied with
                        | Ok applied ->
                            let outcome =
                                match applied.Construction.Outcome, applied.Extension |> Option.map _.Outcome with
                                | _, Some(ExtensionOutcome.Applied _) | ConstructionOutcome.Applied _, _ -> ConstructionTelemetryOutcome.Applied
                                | _ -> ConstructionTelemetryOutcome.AlreadyCurrent
                            Ok applied, Some applied.CacheKey, outcome
                        | Error refusal ->
                            denied <- refusal.Denied
                            let recorded = match refusal.RecordPath with Some path -> " (refusal recorded at " + path + ")" | None -> ""
                            Error(String.concat "; " refusal.Reasons + recorded), None, ConstructionTelemetryOutcome.Refused)
        result
        |> Result.bind (fun applied ->
            let node = applicationNode applied.Construction
            node["schemaVersion"] <- JsonValue.Create "praxis.construction-prepared-application/1"
            node["cacheKey"] <- JsonValue.Create applied.CacheKey
            node["extension"] <- (match applied.Extension with Some report -> extensionNode report :> JsonNode | None -> null)
            node["telemetry"] <- telemetry
            // Dispositions are the worker's record, kept host-side, never applied.
            match dispositions with
            | None -> Ok node
            | Some text ->
                writeOutput (twin preparedPath ".dispositions.json") text |> Result.map (fun path ->
                    node["dispositions"] <- JsonValue.Create path
                    node))
        |> fun outcome ->
            match observations with
            | None -> outcome
            | Some path ->
                match writeOutput path (observationsText denied), outcome with
                | Ok written, Ok node ->
                    node["observations"] <- JsonValue.Create written
                    Ok node
                | Ok written, Error reason -> Error(reason + " (denied operations recorded at " + written + ")")
                | Error reason, Ok _ -> Error("applied, but host observations not written: " + reason)
                | Error _, Error reason -> Error reason
        |> Result.mapError (fun reason -> reason + " [telemetry: " + telemetry.ToJsonString() + "]")

    let applyPreparedWith (generator: DefaultPlanGenerator) root hostConfig groupId preparedPath targetRef (extensions: string option) (observations: string option) (telemetryTarget: string option) =
        applyPreparedInWith generator root hostConfig groupId preparedPath targetRef extensions None observations telemetryTarget

    /// `construct check`, with the runner injected (the CLI passes the
    /// qualified-containment runner, which refuses on an unqualified host).
    let checkWith (runner: LocalHostConfiguration -> Result<ConstructionCheckRunner, string>) root hostConfig preparedPath candidateRef checkId =
        hostState root hostConfig |> Result.bind (fun (configuration, _) ->
            runner configuration |> Result.bind (fun chosen -> ConstructionCheck.run chosen configuration preparedPath candidateRef checkId))

    let check root hostConfig preparedPath candidateRef checkId =
        checkWith (fun configuration -> ConstructionCheck.containedRunner configuration DateTimeOffset.UtcNow) root hostConfig preparedPath candidateRef checkId

    let prepare root hostConfig groupId cohort vocabulary decisions out workspace telemetryTarget =
        prepareInWith (OrdoDefaultPlan.generator ()) root hostConfig groupId cohort vocabulary decisions out workspace telemetryTarget

    let applyPrepared root hostConfig groupId preparedPath targetRef extensions workspace observations telemetryTarget =
        applyPreparedInWith (OrdoDefaultPlan.generator ()) root hostConfig groupId preparedPath targetRef extensions workspace observations telemetryTarget

