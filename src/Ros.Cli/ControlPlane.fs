namespace Ros.Cli

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.ControlPlane
open Ros.Contracts.Work
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Domain.Work
open Ros.Infrastructure.Execution
open Ros.Infrastructure.Work

/// Where a control-plane document comes from (PRX-CTL-008): the repository's
/// identity, its checked-out commit, and a digest of the durable Praxis
/// records. Computed on every request; nothing is remembered between them.
[<RequireQualifiedAccess>]
module ControlPlaneSource =
    /// `.ros/` directories that hold no work state: lock files (transient
    /// mutual exclusion) and a hub's own registration data.
    let excludedDirectories = [ "locks"; "hub" ]

    let private hex (bytes: byte array) = Convert.ToHexString(bytes).ToLowerInvariant()

    /// The digest of `(relative path, content digest)` pairs, in ordinal path
    /// order, so the same records always give the same fingerprint.
    let digest (entries: (string * byte array) list) : string =
        entries
        |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))
        |> List.map (fun (path, content) -> $"{path}\n{hex (SHA256.HashData content)}\n")
        |> String.concat ""
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> hex
        |> sprintf "sha256:%s"

    let private stateFiles (root: string) : (string * byte array) list =
        let stateRoot = Path.Combine(root, ".ros")

        if not (Directory.Exists stateRoot) then
            []
        else
            Directory.EnumerateFiles(stateRoot, "*", SearchOption.AllDirectories)
            |> Seq.map (fun path -> Path.GetRelativePath(stateRoot, path).Replace('\\', '/'), path)
            |> Seq.filter (fun (relative, _) -> excludedDirectories |> List.forall (fun excluded -> not (relative.StartsWith(excluded + "/", StringComparison.Ordinal))))
            |> Seq.map (fun (relative, path) -> relative, File.ReadAllBytes path)
            |> Seq.toList

    let fingerprint (root: string) = digest (stateFiles root)

    let private branch (root: string) =
        match CliProcess.run root "git" [ "symbolic-ref"; "--short"; "-q"; "HEAD" ] with
        | Ok result when result.Exit = 0 && result.Out.Trim() <> "" -> Some(result.Out.Trim())
        | _ -> None

    let read (root: string) : SourceIdentity =
        { Repository = FileWorkConfigRepository.readRepositoryId root
          Commit = GitWorkspace.head root |> Result.toOption
          Branch = branch root
          StateFingerprint = fingerprint root }

/// The live (context) record of one work item, as `work context` shows it.
type LiveRecord =
    { State: string
      SemanticState: LiveWorkState option
      SemanticCode: string
      WorkType: string option
      Evidence: (string * string) list
      RequiredEvidence: string list
      NextAction: string option
      TelemetryExecutionIds: string list
      Node: JsonObject }

/// Pure projection of recorded work state onto the control-plane contract.
/// Legality and refusal reasons are the kernel's own decisions
/// (`BacklogTransition.decide`, `WorkTransition.decide`); nothing here
/// restates a transition rule.
[<RequireQualifiedAccess>]
module ControlPlaneView =
    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private texts (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as values -> values |> Seq.choose (function :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>()) | _ -> None) |> Seq.toList
        | _ -> []

    let parseLiveState (code: string) =
        match code with
        | "ready" -> Some LiveWorkState.Ready
        | "active" -> Some LiveWorkState.Active
        | "blocked" -> Some LiveWorkState.Blocked
        | "complete" -> Some LiveWorkState.Complete
        | "abandoned" -> Some LiveWorkState.Abandoned
        | _ -> None

    /// One item of the `work context` view.
    let liveRecord (node: JsonObject) : LiveRecord =
        let semanticCode = text node "semanticState" |> Option.defaultValue ""

        { State = text node "state" |> Option.defaultValue semanticCode
          SemanticState = parseLiveState semanticCode
          SemanticCode = semanticCode
          WorkType = text node "type"
          Evidence =
            match node["evidence"] with
            | :? JsonArray as entries ->
                entries
                |> Seq.choose (function
                    | :? JsonObject as entry ->
                        match text entry "type", text entry "path" with
                        | Some evidenceType, Some path -> Some(evidenceType, path)
                        | _ -> None
                    | _ -> None)
                |> Seq.toList
            | _ -> []
          RequiredEvidence = texts node "requiredEvidenceForCompletion"
          NextAction =
            match node["latestCheckpoint"] with
            | :? JsonObject as checkpoint -> text checkpoint "nextAction"
            | _ -> None
          TelemetryExecutionIds = texts node "telemetryExecutionIds"
          Node = node }

    /// The `work context` view's items by id.
    let liveRecords (view: JsonObject) : Map<string, LiveRecord> =
        match view["workItems"] with
        | :? JsonArray as items ->
            items
            |> Seq.choose (function :? JsonObject as item -> text item "id" |> Option.map (fun id -> id, liveRecord item) | _ -> None)
            |> Map.ofSeq
        | _ -> Map.empty

    /// Arguments a transition request may carry, by name.
    type RequestArguments =
        { Reason: string option
          WorkType: string option
          Evidence: (string * string) list
          Conclusion: string option }

    let noArguments =
        { Reason = None
          WorkType = None
          Evidence = []
          Conclusion = None }

    /// The kernel action a requested action names for a backlog-only item.
    let private backlogAction (action: string) =
        match action with
        | "ready" -> Some BacklogAction.Ready
        | "block" -> Some BacklogAction.Block
        | "abandon" -> Some BacklogAction.Abandon
        | "start" -> Some BacklogAction.Start
        | _ -> None

    /// The kernel action a requested action names for live work.
    let private liveAction (action: string) =
        match action with
        | "start" -> Some WorkAction.Begin
        | "block" -> Some WorkAction.Block
        | "resume" -> Some WorkAction.Resume
        | "complete" -> Some WorkAction.Complete
        | "abandon" -> Some WorkAction.Abandon
        | _ -> None

    /// The kernel's decision for one action, as `(legal, requires,
    /// refusal)`: `requires` lists what a legal action still needs from the
    /// request; `refusal` is `(category, code, message)`.
    type Decision =
        | Permitted
        | NeedsArguments of category: RefusalCategory * code: string * message: string * requires: string list
        | Refused of category: RefusalCategory * code: string option * message: string

    let decide (id: string) (backlogStatus: string option) (live: LiveRecord option) (action: string) (arguments: RequestArguments) : Decision =
        match live, backlogStatus with
        | Some record, _ ->
            match record.SemanticState, liveAction action with
            | None, _ -> Refused(RefusalCategory.IllegalTransition, None, $"'{id}' has an unrecognized live state '{record.SemanticCode}'")
            | Some _, None -> Refused(RefusalCategory.IllegalTransition, None, $"'{action}' does not apply to live work item '{id}' (state '{record.SemanticCode}')")
            | Some state, Some kernelAction ->
                let request =
                    { State = state
                      Action = kernelAction
                      BlockReason = arguments.Reason
                      RequiredEvidence = Set.ofList record.RequiredEvidence
                      ProvidedEvidence = arguments.Evidence |> List.map fst |> Set.ofList }

                match WorkTransition.decide request with
                | TransitionDecision.Allowed _ -> Permitted
                | TransitionDecision.Rejected(TransitionRejection.IllegalTransition _) ->
                    Refused(RefusalCategory.IllegalTransition, Some "illegal-transition", $"cannot {action} '{id}' from '{record.SemanticCode}'")
                | TransitionDecision.Rejected TransitionRejection.BlockReasonRequired ->
                    NeedsArguments(RefusalCategory.MissingArgument, "block-reason-required", "block requires a reason", [ "reason" ])
                | TransitionDecision.Rejected TransitionRejection.AbandonReasonRequired ->
                    NeedsArguments(RefusalCategory.MissingArgument, "abandon-reason-required", "abandon requires a reason stating why the work is cancelled", [ "reason" ])
                | TransitionDecision.Rejected(TransitionRejection.MissingEvidence missing) ->
                    NeedsArguments(
                        RefusalCategory.MissingArgument,
                        "missing-evidence",
                        $"""completion evidence missing for '{id}': {String.Join(", ", missing)}""",
                        missing |> List.map (sprintf "evidence:%s")
                    )
        | None, Some status ->
            match BacklogState.parse status, backlogAction action with
            | None, _ -> Refused(RefusalCategory.IllegalTransition, None, $"'{id}' has an unrecognized backlog status '{status}'")
            | Some _, None -> Refused(RefusalCategory.IllegalTransition, None, $"'{action}' does not apply to backlog item '{id}' (status '{status}')")
            | Some state, Some kernelAction ->
                match BacklogTransition.decide { State = state; Action = kernelAction; Reason = arguments.Reason } with
                | BacklogTransitionDecision.Allowed _ -> Permitted
                | BacklogTransitionDecision.Rejected(BacklogTransitionRejection.IllegalTransition _) ->
                    Refused(RefusalCategory.IllegalTransition, Some "illegal-transition", $"cannot {action} backlog item '{id}' from '{status}'")
                | BacklogTransitionDecision.Rejected BacklogTransitionRejection.BlockReasonRequired ->
                    NeedsArguments(RefusalCategory.MissingArgument, "block-reason-required", "block requires a reason", [ "reason" ])
        | None, None -> Refused(RefusalCategory.NotFound, None, $"work item '{id}' was not found")

    /// Every action's view: legal (perhaps needing arguments) or refused,
    /// with the kernel's reason when it gives one.
    let actions (id: string) (backlogStatus: string option) (live: LiveRecord option) : ActionView list =
        ControlPlaneJson.actions
        |> List.map (fun action ->
            match decide id backlogStatus live action noArguments with
            | Permitted ->
                { Action = action
                  Legal = true
                  Requires = []
                  Refusal = None }
            | NeedsArguments(_, _, _, requires) ->
                { Action = action
                  Legal = true
                  Requires = requires
                  Refusal = None }
            | Refused(_, code, message) ->
                { Action = action
                  Legal = false
                  Requires = []
                  Refusal = code |> Option.map (fun value -> value, message) })

    let unknowns: Availability<string list> =
        Availability.Unavailable "Praxis records no unknowns for individual work items"

    let obligations (live: LiveRecord option) : Availability<Obligations> =
        match live with
        | None -> Availability.Unavailable "no live work record: obligations are recorded once the item is started"
        | Some record ->
            let recorded = record.Evidence |> List.map fst |> Set.ofList

            Availability.Available
                { RequiredEvidenceForCompletion = record.RequiredEvidence
                  MissingEvidenceForCompletion = record.RequiredEvidence |> List.filter (recorded.Contains >> not)
                  NextAction = record.NextAction }

    let workItem (row: WorkListRow) (backlogStatus: string option) (live: LiveRecord option) : WorkItemView =
        { Id = row.Id
          Title = row.Title
          Description = row.Description
          Tags = row.Tags
          Priority = row.Priority
          SemanticState = row.Status
          Backlog =
            backlogStatus
            |> Option.map (fun status ->
                { Status = status
                  BlockedReason = if live.IsNone then row.BlockedReason else None })
          Live =
            live
            |> Option.map (fun record ->
                { State = record.State
                  SemanticState = record.SemanticCode
                  WorkType = record.WorkType })
          BlockedReason = row.BlockedReason
          Actions = actions row.Id backlogStatus live
          Obligations = obligations live
          Unknowns = unknowns }

    /// The `execution show --json` / `execution list --json` envelope with
    /// provider, model and runtime presented as the host that ran the
    /// execution (PRX-CTL-012), never as the owner of the work.
    let execution (node: JsonObject) : JsonNode =
        let copy (name: string) = node[name] |> Option.ofObj |> Option.map (fun value -> value.DeepClone()) |> Option.toObj
        let actor = match node["actor"] with :? JsonObject as value -> value | _ -> JsonObject()
        let actorField (name: string) = actor[name] |> Option.ofObj |> Option.map (fun value -> value.DeepClone()) |> Option.toObj

        let result = JsonObject()
        result["executionId"] <- copy "executionId"
        result["workItem"] <- copy "workItem"
        result["role"] <- copy "role"
        result["status"] <- copy "state"
        result["statusReason"] <- copy "stateReason"
        result["startedAt"] <- copy "startedAt"
        let actorNode = JsonObject()
        actorNode["id"] <- actorField "id"
        actorNode["kind"] <- actorField "kind"
        result["actor"] <- actorNode
        let host = JsonObject()
        host["provider"] <- actorField "provider"
        host["model"] <- actorField "model"
        host["runtime"] <- actorField "runtime"
        result["executionHost"] <- host
        result["workStateOwner"] <- JsonValue.Create "repository"

        [ "baselineRevision"; "candidateRevision"; "workspaceBinding"; "containment"; "securitySandbox"; "steps"; "scopeEffects"; "verification"; "legalActions" ]
        |> List.filter (fun name -> node.ContainsKey name)
        |> List.iter (fun name -> result[name] <- copy name)

        result

/// `praxis control-plane ...`: the typed, versioned control-plane documents
/// (`praxis.control-plane` v1). Every read goes through the same functions
/// the corresponding CLI command uses; the one mutation runs the CLI's own
/// transition command. `web serve` and `hub serve` relay these documents.
[<RequireQualifiedAccess>]
module ControlPlaneCommands =
    let usage =
        "control-plane source | control-plane work [ID] [--tag T]* [--status S] | control-plane evidence ID | control-plane executions [--work-item ID] | control-plane execution EXE-ID | control-plane transition --id ID --action ready|block|abandon|start|resume|complete [--reason TEXT] [--type TYPE] [--evidence TYPE=PATH]* [--conclusion TEXT]"

    let private optionValue (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private optionValues (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private positional (arguments: string list) =
        let rec loop (remaining: string list) =
            match remaining with
            | [] -> None
            | flag :: _ :: rest when flag.StartsWith "--" && flag <> "--json" -> loop rest
            | flag :: rest when flag.StartsWith "--" -> loop rest
            | value :: _ -> Some value

        loop arguments

    let private print (node: JsonNode) =
        printf "%s" (ControlPlaneJson.render node)

    let private respond (kind: string) (source: SourceIdentity) (result: Result<JsonNode, Refusal>) =
        match result with
        | Ok data ->
            print (ControlPlaneJson.document kind source data)
            0
        | Error refusal ->
            print (ControlPlaneJson.refusalDocument kind (Some source) refusal)
            1

    let private refusal category message action workItemId : Refusal =
        { Category = category
          Code = None
          Message = message
          Action = action
          WorkItemId = workItemId }

    /// Everything a work-item view is projected from, read once.
    type private WorkState =
        { Rows: WorkListRow list
          Backlog: Map<string, string>
          Live: Map<string, LiveRecord> }

    let private readState (root: string) : Result<WorkState, string> =
        FileWorkListRepository.readListView root
        |> Result.bind (fun rows ->
            FileWorkContextRepository.readContextView root None
            |> Result.map (fun view ->
                { Rows = rows
                  Backlog = FileBacklogQueueRepository.readItems root |> List.map (fun item -> item.Id, item.Status) |> Map.ofList
                  Live = ControlPlaneView.liveRecords view }))

    let private viewOf (state: WorkState) (row: WorkListRow) =
        ControlPlaneView.workItem row (state.Backlog.TryFind row.Id) (state.Live.TryFind row.Id)

    let private findItem (state: WorkState) (id: string) =
        match state.Rows |> List.tryFind (fun row -> row.Id = id) with
        | Some row -> Ok(viewOf state row)
        | None -> Error(refusal RefusalCategory.NotFound $"work item '{id}' was not found" None (Some id))

    let private unreadable message = refusal RefusalCategory.Unavailable message None None

    let workItemDocument (root: string) (id: string) : Result<JsonNode, Refusal> =
        readState root
        |> Result.mapError unreadable
        |> Result.bind (fun state -> findItem state id)
        |> Result.map ControlPlaneJson.workItem

    let private workList (root: string) (arguments: string list) : Result<JsonNode, Refusal> =
        let tags = optionValues "--tag" arguments
        let status = optionValue "--status" arguments

        readState root
        |> Result.mapError unreadable
        |> Result.map (fun state ->
            state.Rows
            |> List.filter (fun row -> tags |> List.forall (fun tag -> List.contains tag row.Tags))
            |> List.filter (fun row -> status |> Option.forall ((=) row.Status))
            |> List.map (viewOf state)
            |> ControlPlaneJson.workList)

    let private usageOf (root: string) (id: string) : Availability<JsonNode> =
        let scope = FileTelemetryUsageRepository.read root (Some id) UsageDimension.Execution

        match Usage.aggregate UsageDimension.Execution scope.ExecutionsByKey scope.Measurements with
        | [] -> Availability.Unavailable $"no telemetry usage is recorded for '{id}'"
        | groups -> Availability.Available(TelemetryUsageJson.document (Some id) UsageDimension.Execution groups :> JsonNode)

    let private evidence (root: string) (id: string) : Result<JsonNode, Refusal> =
        readState root
        |> Result.mapError unreadable
        |> Result.bind (fun state ->
            findItem state id
            |> Result.map (fun _ ->
                let live = state.Live.TryFind id
                let unstarted = "no live work record: evidence and checkpoints are recorded once the item is started"

                let recorded =
                    match live with
                    | None -> Availability.Unavailable unstarted
                    | Some record ->
                        Availability.Available(
                            record.Evidence
                            |> List.map (fun (evidenceType, path) ->
                                let entry = JsonObject()
                                entry["type"] <- JsonValue.Create evidenceType
                                entry["path"] <- JsonValue.Create path
                                entry :> JsonNode)
                        )

                let checkpoints =
                    match live with
                    | None -> Availability.Unavailable unstarted
                    | Some _ ->
                        match CheckpointCommands.showDocument root true id with
                        | Ok document -> Availability.Available(document :> JsonNode)
                        | Error message -> Availability.Unavailable message

                let strings (values: string list) =
                    let array = JsonArray()
                    values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
                    array :> JsonNode

                let nodes (values: JsonNode list) =
                    let array = JsonArray()
                    values |> List.iter array.Add
                    array :> JsonNode

                let data = JsonObject()
                data["workItemId"] <- JsonValue.Create id
                data["evidence"] <- ControlPlaneJson.availability nodes recorded
                data["checkpoints"] <- ControlPlaneJson.availability (fun (node: JsonNode) -> node) checkpoints
                data["telemetryExecutionIds"] <- strings (live |> Option.map _.TelemetryExecutionIds |> Option.defaultValue [])
                data["usage"] <- ControlPlaneJson.availability (fun (node: JsonNode) -> node) (usageOf root id)
                data["executionIds"] <- strings (ExecutionCommands.listEnvelopes root (Some id) |> List.map _.ExecutionId)
                data :> JsonNode))

    let private executions (root: string) (arguments: string list) : Result<JsonNode, Refusal> =
        let array = JsonArray()

        ExecutionCommands.listEnvelopes root (optionValue "--work-item" arguments)
        |> List.iter (fun envelope ->
            match Ros.Contracts.Execution.ExecutionJson.envelope envelope with
            | :? JsonObject as node -> array.Add(ControlPlaneView.execution node)
            | _ -> ())

        Ok(array :> JsonNode)

    let private execution (root: string) (actor: Actor) (id: string) : Result<JsonNode, Refusal> =
        if not (ExecutionStore.list root |> List.contains id) then
            Error(refusal RefusalCategory.NotFound $"execution '{id}' was not found" None None)
        else
            match ExecutionCommands.snapshotDocument root actor id with
            | Ok(:? JsonObject as node) -> Ok(ControlPlaneView.execution node)
            | Ok _ -> Error(unreadable $"execution '{id}' has an unreadable record")
            | Error message -> Error(unreadable message)

    // ---- transition requests ----

    let private flag name value =
        match value with
        | Some text -> [ name; text ]
        | None -> []

    /// The CLI command line a permitted request runs: exactly the command an
    /// operator would type, so the CLI's own transition path, locking,
    /// guards, identity resolution and provenance apply unchanged.
    let commandLine (now: string) (id: string) (isLive: bool) (action: string) (arguments: ControlPlaneView.RequestArguments) : string list =
        match action, isLive with
        | "ready", _ -> [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now ]
        | "abandon", false -> [ "work"; "backlog-transition"; "--id"; id; "--action"; "abandon"; "--occurred-at"; now ] @ flag "--reason" arguments.Reason
        | "abandon", true -> [ "work"; "abandon"; "--id"; id; "--occurred-at"; now ] @ flag "--reason" arguments.Reason
        | "block", _ -> [ "work"; "block"; "--id"; id; "--occurred-at"; now ] @ flag "--reason" arguments.Reason
        | "start", _ -> [ "work"; "start"; "--id"; id; "--occurred-at"; now ] @ flag "--type" arguments.WorkType
        | "resume", _ -> [ "work"; "resume"; "--id"; id; "--occurred-at"; now ]
        | _ ->
            [ "work"; "complete"; "--id"; id; "--occurred-at"; now ]
            @ (arguments.Evidence |> List.collect (fun (evidenceType, path) -> [ "--evidence"; $"{evidenceType}={path}" ]))
            @ flag "--conclusion" arguments.Conclusion

    let private parseEvidence (values: string list) : Result<(string * string) list, string> =
        values
        |> List.map (fun value ->
            match value.IndexOf '=' with
            | index when index > 0 && index < value.Length - 1 -> Ok(value.Substring(0, index), value.Substring(index + 1))
            | _ -> Error $"--evidence expects TYPE=PATH, got '{value}'")
        |> List.fold (fun acc item -> acc |> Result.bind (fun items -> item |> Result.map (fun value -> items @ [ value ]))) (Ok [])

    let private timestamp () =
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Globalization.CultureInfo.InvariantCulture)

    let private transition (root: string) (arguments: string list) : Result<JsonNode, Refusal> =
        let id = optionValue "--id" arguments
        let action = optionValue "--action" arguments

        let invalid message = refusal RefusalCategory.InvalidRequest message action id

        match id, action, parseEvidence (optionValues "--evidence" arguments) with
        | None, _, _ -> Error(invalid "a transition request requires --id")
        | _, None, _ -> Error(invalid "a transition request requires --action")
        | _, Some requested, _ when not (List.contains requested ControlPlaneJson.actions) ->
            Error(invalid $"""unknown action '{requested}'; expected one of {String.Join(", ", ControlPlaneJson.actions)}""")
        | _, _, Error message -> Error(invalid message)
        | Some workItemId, Some requested, Ok evidence ->
            let request: ControlPlaneView.RequestArguments =
                { Reason = optionValue "--reason" arguments
                  WorkType = optionValue "--type" arguments
                  Evidence = evidence
                  Conclusion = optionValue "--conclusion" arguments }

            readState root
            |> Result.mapError unreadable
            |> Result.bind (fun state ->
                findItem state workItemId
                |> Result.mapError (fun refused -> { refused with Action = Some requested })
                |> Result.bind (fun _ ->
                    let live = state.Live.TryFind workItemId

                    let refuse category code message =
                        Error
                            { Category = category
                              Code = code
                              Message = message
                              Action = Some requested
                              WorkItemId = Some workItemId }

                    match ControlPlaneView.decide workItemId (state.Backlog.TryFind workItemId) live requested request with
                    | ControlPlaneView.Refused(category, code, message) -> refuse category code message
                    | ControlPlaneView.NeedsArguments(category, code, message, _) -> refuse category (Some code) message
                    | ControlPlaneView.Permitted ->
                        match CliProcess.runSelf root (commandLine (timestamp ()) workItemId live.IsSome requested request) with
                        | Error message -> refuse RefusalCategory.Unavailable None message
                        | Ok result when result.Exit <> 0 -> refuse RefusalCategory.Rejected None (CliProcess.failureMessage result)
                        | Ok _ ->
                            workItemDocument root workItemId
                            |> Result.map (fun item ->
                                let data = JsonObject()
                                data["action"] <- JsonValue.Create requested
                                data["workItem"] <- item
                                data :> JsonNode)))

    let run (root: string) (actor: Actor) (arguments: string list) : int =
        let source () = ControlPlaneSource.read root

        match arguments with
        | "source" :: _ -> respond "source" (source ()) (Ok(JsonObject() :> JsonNode))
        | "work" :: rest ->
            match positional rest with
            | Some id -> respond "work-item" (source ()) (workItemDocument root id)
            | None -> respond "work-list" (source ()) (workList root rest)
        | "evidence" :: rest ->
            match positional rest with
            | Some id -> respond "evidence" (source ()) (evidence root id)
            | None -> respond "evidence" (source ()) (Error(refusal RefusalCategory.InvalidRequest "evidence requires a work-item ID" None None))
        | "executions" :: rest -> respond "execution-list" (source ()) (executions root rest)
        | "execution" :: rest ->
            match positional rest with
            | Some id -> respond "execution" (source ()) (execution root actor id)
            | None -> respond "execution" (source ()) (Error(refusal RefusalCategory.InvalidRequest "execution requires an execution ID" None None))
        | "transition" :: rest ->
            let outcome = transition root rest
            // The source is read after the request, so a success reports the
            // state it produced and a refusal the (unchanged) state it judged.
            match respond "transition" (source ()) outcome, outcome with
            | _, Error { Category = RefusalCategory.InvalidRequest } -> 2
            | code, _ -> code
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2
