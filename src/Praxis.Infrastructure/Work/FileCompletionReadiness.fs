namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// The file-system edge of completion readiness (PRX-QUAL-023): reads the
/// `workProtocol.qualityEvidence` policy from `ros.json` and the supplied
/// evidence documents, then hands them to the pure application gate.
/// Praxis never spawns Dokimos or Ordo here; it consumes the files a caller
/// supplied through `work complete --evidence TYPE=PATH`.
[<RequireQualifiedAccess>]
module FileCompletionReadiness =
    let private readText (root: string) (relativePath: string) (decode: string -> EvidenceReading<'T>) : EvidenceReading<'T> =
        let path = Path.Combine(root, relativePath)

        try
            if File.Exists path then decode (File.ReadAllText path)
            else EvidenceReading.Malformed "the evidence file does not exist"
        with
        | :? IOException as error -> EvidenceReading.Malformed $"the evidence file could not be read: {error.Message}"
        | :? System.UnauthorizedAccessException as error -> EvidenceReading.Malformed $"the evidence file could not be read: {error.Message}"

    /// SHA-256 of an evidence file's bytes (PRX-QUAL-023), `None` when the
    /// file cannot be read; the readiness record then shows the gap.
    let digest (root: string) (relativePath: string) : string option =
        let path = Path.Combine(root, relativePath)

        try
            if File.Exists path then
                Some("sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant())
            else
                None
        with
        | :? IOException
        | :? UnauthorizedAccessException -> None

    /// A `path` or `path:line` reference exists in the repository (and, with
    /// a line, the file has that many lines).
    let locationExists (root: string) (reference: string) : bool =
        let path, line =
            match reference.LastIndexOf ':' with
            | index when index > 0 && index < reference.Length - 1 && Seq.forall Char.IsAsciiDigit (reference.Substring(index + 1)) ->
                reference.Substring(0, index), Some(int (reference.Substring(index + 1)))
            | _ -> reference, None

        let full = Path.GetFullPath(Path.Combine(root, path))
        let inside = full.StartsWith(Path.GetFullPath root, StringComparison.Ordinal)

        try
            match line with
            | _ when not inside -> false
            | None -> File.Exists full || Directory.Exists full
            | Some number -> File.Exists full && number >= 1 && File.ReadLines full |> Seq.length >= number
        with
        | :? IOException
        | :? UnauthorizedAccessException -> false

    let sources (root: string) : QualityEvidenceSources =
        { ReadDokimos = fun path -> readText root path QualityEvidenceJson.decodeDokimos
          ReadOrdo = fun path -> readText root path QualityEvidenceJson.decodeOrdo
          ReadDesignDebt = fun path -> readText root path QualityEvidenceJson.decodeDesignDebt
          ReadVerificationMatrix = fun path -> readText root path QualityEvidenceJson.decodeVerificationMatrix
          ReadReleaseReadiness = fun path -> readText root path QualityEvidenceJson.decodeReleaseReadiness
          Digest = digest root
          LocationExists = locationExists root }

    let private terminalStates = set [ "complete"; "abandoned" ]

    /// Risk metadata by work item, from the queue (`risk` members). A present
    /// but invalid declaration is an error so completion fails closed.
    let readRisks (root: string) : Result<Map<string, WorkRisk>, string> =
        let path = Path.Combine(root, ".ros", "work", "queue.json")

        if not (File.Exists path) then
            Ok Map.empty
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)

                match document.RootElement.TryGetProperty "items" with
                | true, items when items.ValueKind = JsonValueKind.Array ->
                    items.EnumerateArray()
                    |> Seq.fold
                        (fun acc item ->
                            acc
                            |> Result.bind (fun risks ->
                                match item.TryGetProperty "id", item.TryGetProperty "risk" with
                                | (true, id), (true, risk) when risk.ValueKind <> JsonValueKind.Null ->
                                    QualityEvidenceJson.decodeRisk risk
                                    |> Result.map (fun decoded -> risks |> Map.add (id.GetString()) decoded)
                                    |> Result.mapError (fun reason -> $"work item '{id.GetString()}' has invalid risk metadata: {reason}")
                                | _ -> Ok risks))
                        (Ok Map.empty)
                | _ -> Ok Map.empty
            with :? JsonException as error ->
                Error $"queue.json is not valid JSON: {error.Message}"

    /// Recorded work items that are not complete or abandoned, in the queue
    /// or the live context.
    let openItems (root: string) (context: WorkContextPlanningView option) : Set<string> =
        let queueOpen =
            FileBacklogQueueRepository.readItems root
            |> List.filter (fun item -> not (terminalStates.Contains item.Status))
            |> List.map _.Id
            |> Set.ofList

        let closed, live =
            match context with
            | Some context ->
                context.WorkItems
                |> List.partition (fun item -> item.SemanticState = LiveWorkState.Complete || item.SemanticState = LiveWorkState.Abandoned)
                |> fun (closed, live) -> closed |> List.map _.Id |> Set.ofList, live |> List.map _.Id |> Set.ofList
            | None -> Set.empty, Set.empty

        Set.union queueOpen live - closed

    /// The repository policy. Absent: the legacy default (no gate). Present
    /// but unreadable or invalid: an error, so completion fails closed.
    let readPolicy (root: string) : Result<QualityEvidencePolicy, string> =
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            Ok QualityEvidencePolicies.legacyDefault
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)

                match document.RootElement.TryGetProperty "workProtocol" with
                | true, protocol when protocol.ValueKind = JsonValueKind.Object ->
                    match protocol.TryGetProperty "qualityEvidence" with
                    | true, policy -> QualityEvidenceJson.decodePolicy policy
                    | _ -> Ok QualityEvidencePolicies.legacyDefault
                | _ -> Ok QualityEvidencePolicies.legacyDefault
            with :? JsonException as error ->
                Error $"ros.json is not valid JSON: {error.Message}"

    let private readContext (root: string) =
        let contextPath = Path.Combine(root, ".ros", "context", "current.json")

        if File.Exists contextPath then
            WorkContextPlanContract.parseJson (File.ReadAllText contextPath) |> Result.toOption
        else
            None

    /// Gate for explicitly typed items (the envelope path already holds
    /// them). `group` gives a grouped-mode member's `group-verified` facet.
    let evaluateItems (root: string) (items: (string * string) list) (provided: WorkEvidence list) (group: string -> FacetStatus option) : CompletionGateOutcome =
        match readRisks root with
        | Error reason -> CompletionGateOutcome.PolicyInvalid reason
        | Ok risks ->
            let facts =
                { Risk = fun id -> risks |> Map.tryFind id
                  OpenItems = openItems root (readContext root) }

            CompletionReadinessOperations.gate (sources root) facts (readPolicy root) items provided group

    /// For a path without the native group gates (the runtime-free envelope):
    /// every item begun in grouped mode by any group execution is refused,
    /// because its gates cannot be checked there (fail closed, PRX-GRP-135).
    let groupedMembersUnverifiable (root: string) : string -> FacetStatus option =
        let path = Path.Combine(root, ".ros", "work", "groups.json")

        let grouped =
            try
                if File.Exists path then
                    match WorkGroupJson.readStore (File.ReadAllText path) with
                    | Ok groups ->
                        groups
                        |> List.collect (fun group -> group.Executions)
                        |> List.collect (fun execution -> execution.Members)
                        |> List.filter (fun begun -> begun.Mode = ExecutionMode.Grouped)
                        |> List.map (fun begun -> begun.WorkItemId)
                        |> Set.ofList
                        |> Ok
                    | Error message -> Error message
                else
                    Ok Set.empty
            with :? IOException as error ->
                Error error.Message

        fun id ->
            match grouped with
            | Ok members when members.Contains id ->
                Some(FacetStatus.Unavailable [ $"{id} executes in grouped mode; its group gates are checked only by the native `work complete`" ])
            | Ok _ -> None
            | Error message -> Some(FacetStatus.Unavailable [ $"the group store cannot be read, so the group gates cannot be judged: {message}" ])

    /// Gate for `work complete`: item types come from the work context. Only
    /// active items can complete; any other id is left to the transition
    /// planner to reject with its usual message.
    let evaluate (root: string) (ids: string list) (provided: WorkEvidence list) (group: string -> FacetStatus option) : CompletionGateOutcome =
        let types =
            match readContext root with
            | Some context -> context.WorkItems |> List.filter (fun item -> item.SemanticState = LiveWorkState.Active) |> List.map (fun item -> item.Id, item.WorkType) |> Map.ofList
            | None -> Map.empty

        evaluateItems root (ids |> List.choose (fun id -> types |> Map.tryFind id |> Option.map (fun workType -> id, workType))) provided group

    /// The additive per-item record written onto the completion event and the
    /// completed work item. Empty when the gate did not apply.
    let extensions (outcome: CompletionGateOutcome) : Map<string, JsonObject> =
        match outcome with
        | CompletionGateOutcome.Ready items
        | CompletionGateOutcome.Refused items -> items |> List.map (fun item -> item.WorkItemId, QualityEvidenceJson.extension item) |> Map.ofList
        | CompletionGateOutcome.NotApplicable
        | CompletionGateOutcome.PolicyInvalid _ -> Map.empty

    /// Machine-readable refusal document (stdout of a refused `work complete`).
    let refusalDocument (items: ItemReadiness list) =
        let node = JsonObject()
        node["outcome"] <- JsonValue.Create "refused"
        let readiness = JsonArray()
        items |> List.iter (fun item -> readiness.Add(QualityEvidenceJson.readinessNode item: JsonNode))
        node["completionReadiness"] <- readiness
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    /// What `work context` shows for an item still to be completed: the
    /// policy, the item's risk obligations and the evidence they require.
    let contextRequirement (policy: QualityEvidencePolicy) (risk: WorkRisk option) =
        let obligations = WorkRisk.obligations risk
        let node = QualityEvidenceJson.policyNode policy
        let required = JsonArray()

        QualityEvidencePolicies.requiredEvidenceTypes policy obligations
        |> List.iter (fun evidenceType -> required.Add(JsonValue.Create evidenceType: JsonNode))

        node["requiredEvidenceTypes"] <- required
        risk |> Option.iter (fun declared -> node["risk"] <- QualityEvidenceJson.riskNode declared)
        node["obligations"] <- QualityEvidenceJson.obligationsNode obligations
        node
