namespace Ros.Persistence

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ros.ProjectAdministration

/// A provisional file-backed store for the outbound-delivery outbox,
/// mirroring `ActivityStore`'s own justification for staying file-based
/// until real load demonstrates otherwise (see that module's doc
/// comment and
/// docs/migrations/central-integration/COMPATIBILITY-MATRIX.md).
[<RequireQualifiedAccess>]
module OutboxStore =
    let private outboxPath (root: string) = Path.Combine(root, "outbox.json")

    let private targetTag =
        function
        | Chrona -> "chrona"
        | Summa -> "summa"
        | Other name -> $"other:{name}"

    let private parseTarget (tag: string) : IntegrationTarget =
        match tag with
        | "chrona" -> Chrona
        | "summa" -> Summa
        | other when other.StartsWith "other:" -> Other(other.Substring 6)
        | other -> Other other

    let private statusTag =
        function
        | NotReady -> "not_ready"
        | Pending -> "pending"
        | Sending -> "sending"
        | Delivered -> "delivered"
        | Failed _ -> "failed"

    let private failedAttempts =
        function
        | Failed(attempts, _) -> Some attempts
        | _ -> None

    let private failedError =
        function
        | Failed(_, error) -> Some error
        | _ -> None

    let private parseStatus (tag: string) (attempts: int option) (error: string option) : OutboundDeliveryState option =
        match tag, attempts, error with
        | "not_ready", _, _ -> Some NotReady
        | "pending", _, _ -> Some Pending
        | "sending", _, _ -> Some Sending
        | "delivered", _, _ -> Some Delivered
        | "failed", Some attempts, Some error -> Some(Failed(attempts, error))
        | _ -> None

    let private optionalString (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value -> Some(value.GetValue<string>())
        | _ -> None

    let private optionalInstant (node: JsonObject) (name: string) : DateTimeOffset option =
        optionalString node name
        |> Option.bind (fun raw ->
            match DateTimeOffset.TryParse raw with
            | true, value -> Some value
            | false, _ -> None)

    let private toNode (entry: OutboundIntegration) : JsonObject =
        let node = JsonObject()
        node["id"] <- JsonValue.Create entry.Id
        node["target"] <- JsonValue.Create(targetTag entry.Target)
        node["sourceRecordId"] <- JsonValue.Create entry.SourceRecordId
        node["contractVersion"] <- JsonValue.Create entry.ContractVersion
        node["status"] <- JsonValue.Create(statusTag entry.Status)

        failedAttempts entry.Status |> Option.iter (fun attempts -> node["failedAttempts"] <- JsonValue.Create attempts)
        failedError entry.Status |> Option.iter (fun error -> node["failedError"] <- JsonValue.Create error)

        node["attemptCount"] <- JsonValue.Create entry.AttemptCount
        node["createdAt"] <- JsonValue.Create(entry.CreatedAt.ToString "o")
        entry.LastAttemptAt |> Option.iter (fun v -> node["lastAttemptAt"] <- JsonValue.Create(v.ToString "o"))
        entry.CompletedAt |> Option.iter (fun v -> node["completedAt"] <- JsonValue.Create(v.ToString "o"))
        entry.LastError |> Option.iter (fun v -> node["lastError"] <- JsonValue.Create v)
        node

    let private ofNode (node: JsonObject) : OutboundIntegration option =
        match optionalString node "id", optionalString node "target", optionalString node "sourceRecordId", optionalString node "contractVersion", optionalString node "status", optionalInstant node "createdAt" with
        | Some id, Some target, Some sourceRecordId, Some contractVersion, Some statusTag, Some createdAt ->
            let attempts =
                match node["failedAttempts"] with
                | :? JsonValue as value -> Some(value.GetValue<int>())
                | _ -> None

            match parseStatus statusTag attempts (optionalString node "failedError") with
            | None -> None
            | Some status ->
                Some
                    { Id = id
                      Target = parseTarget target
                      SourceRecordId = sourceRecordId
                      ContractVersion = contractVersion
                      Status = status
                      AttemptCount = (match node["attemptCount"] with
                                      | :? JsonValue as value -> value.GetValue<int>()
                                      | _ -> 0)
                      CreatedAt = createdAt
                      LastAttemptAt = optionalInstant node "lastAttemptAt"
                      CompletedAt = optionalInstant node "completedAt"
                      LastError = optionalString node "lastError" }
        | _ -> None

    let private readAll (root: string) : OutboundIntegration list =
        let path = outboxPath root

        if not (File.Exists path) then
            []
        else
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonArray as items ->
                items
                |> Seq.choose (fun item ->
                    match item with
                    | :? JsonObject as obj -> ofNode obj
                    | _ -> None)
                |> Seq.toList
            | _ -> []

    let private writeAll (root: string) (entries: OutboundIntegration list) =
        Directory.CreateDirectory root |> ignore
        let array = JsonArray()
        entries |> List.iter (fun entry -> array.Add(toNode entry: JsonNode))
        let options = JsonSerializerOptions(WriteIndented = true)
        File.WriteAllText(outboxPath root, array.ToJsonString options + "\n")

    let tryFind (root: string) (id: string) : OutboundIntegration option =
        readAll root |> List.tryFind (fun entry -> entry.Id = id)

    /// Inserts a brand-new outbox entry. Fails on a duplicate id rather
    /// than overwriting it -- use `update` to persist a state change to
    /// an entry already in the outbox.
    let insert (root: string) (entry: OutboundIntegration) : Result<unit, string> =
        let existing = readAll root

        if existing |> List.exists (fun item -> item.Id = entry.Id) then
            Error $"outbox entry '{entry.Id}' already exists"
        else
            writeAll root (entry :: existing)
            Ok()

    /// Replaces an already-inserted entry by id -- the write path every
    /// `OutboundIntegration` state transition (`beginSending`,
    /// `markDelivered`, `markFailed`, `markReady`) uses to persist its
    /// result.
    let update (root: string) (entry: OutboundIntegration) : Result<unit, string> =
        let existing = readAll root

        if existing |> List.exists (fun item -> item.Id = entry.Id) |> not then
            Error $"outbox entry '{entry.Id}' does not exist"
        else
            writeAll root (entry :: (existing |> List.filter (fun item -> item.Id <> entry.Id)))
            Ok()

    /// Entries a processing loop should attempt to send next -- ready
    /// (`Pending`) or eligible for retry (`Failed`), never `NotReady`,
    /// `Sending`, or `Delivered`.
    let listDeliverable (root: string) : OutboundIntegration list =
        readAll root
        |> List.filter (fun entry ->
            match entry.Status with
            | Pending
            | Failed _ -> true
            | NotReady
            | Sending
            | Delivered -> false)
