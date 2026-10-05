namespace Praxis.Infrastructure.Pacing

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Praxis.Application.Pacing
open Praxis.Domain.Pacing

/// Schema-versioned pacing safety state (`hold.json`).
///
/// Schema 2: `{ "schemaVersion": 2, "revision": n, "holds": [ ... ] }` where each
/// hold records its basis (`weekly-lead` with the window's `resetsAt`, or
/// `hard-limit` with `usedPercent` and `resetsAt`). Schema 1 (no
/// `schemaVersion`; weekly holds only, no reset identity) is migrated on read.
/// Any malformed document or row is `Unreadable` — partial state is never
/// silently accepted — and a newer schema is `Unsupported`.
[<RequireQualifiedAccess>]
module PacingStateDocument =
    [<Literal>]
    let CurrentSchema = 2

    let private scopeParts (scope: QuotaScope) : string * string option =
        match scope with
        | QuotaScope.Global -> "global", None
        | QuotaScope.Model name -> "model", Some name
        | QuotaScope.Surface name -> "surface", Some name

    let private scopeFromParts (kind: string) (name: string option) : Result<QuotaScope, string> =
        match kind, name with
        | "global", _ -> Ok QuotaScope.Global
        | "model", Some value -> Ok(QuotaScope.Model value)
        | "surface", Some value -> Ok(QuotaScope.Surface value)
        | other, _ -> Error $"unknown scope '{other}'"

    let private text name row = PacingJson.tryProperty name row |> Option.bind PacingJson.tryString

    let private required name row parse =
        PacingJson.tryProperty name row
        |> Option.bind parse
        |> function
            | Some value -> Ok value
            | None -> Error $"hold is missing a valid '{name}'"

    let private parseScope row =
        scopeFromParts (text "scopeKind" row |> Option.defaultValue "global") (text "scopeName" row)

    let private parseBasis (schema: int) row : Result<HoldBasis, string> =
        if schema = 1 then
            Ok(HoldBasis.WeeklyLead None)
        else
            match text "basis" row with
            | Some "weekly-lead" ->
                // `resetsAt` is absent only for a latch migrated from schema 1.
                match PacingJson.tryProperty "resetsAt" row with
                | None -> Ok(HoldBasis.WeeklyLead None)
                | Some element ->
                    match PacingJson.tryTimestamp element with
                    | Some reset -> Ok(HoldBasis.WeeklyLead(Some reset))
                    | None -> Error "hold has an invalid 'resetsAt'"
            | Some "hard-limit" ->
                required "usedPercent" row PacingJson.tryDecimal
                |> Result.bind (fun used ->
                    required "resetsAt" row PacingJson.tryTimestamp
                    |> Result.map (fun reset -> HoldBasis.HardLimit(used, reset)))
            | Some other -> Error $"unknown hold basis '{other}'"
            | None -> Error "hold is missing its basis"

    let private parseHold (schema: int) (row: JsonElement) : Result<PacingHold, string> =
        required "key" row PacingJson.tryString
        |> Result.bind (fun key ->
            required "provider" row PacingJson.tryString
            |> Result.bind (fun provider ->
                required "since" row PacingJson.tryTimestamp
                |> Result.bind (fun since ->
                    parseScope row
                    |> Result.bind (fun scope ->
                        parseBasis schema row
                        |> Result.map (fun basis ->
                            { Key = key
                              Provider = provider
                              Scope = scope
                              Since = since
                              Basis = basis })))))

    let private parseHolds (schema: int) (root: JsonElement) : Result<PacingState, string> =
        match PacingJson.tryProperty "holds" root with
        | Some rows when rows.ValueKind = JsonValueKind.Array ->
            rows.EnumerateArray()
            |> Seq.toList
            |> List.fold
                (fun acc row ->
                    acc |> Result.bind (fun holds -> parseHold schema row |> Result.map (fun hold -> hold :: holds)))
                (Ok [])
            |> Result.map (List.rev >> PacingState.ofHolds)
        | _ -> Error "document has no 'holds' array"

    let private schemaOf (root: JsonElement) : Result<int, string> =
        match PacingJson.tryProperty "schemaVersion" root with
        | None -> Ok 1
        | Some element ->
            PacingJson.tryInt element
            |> Option.filter (fun version -> version >= 1)
            |> function
                | Some version -> Ok version
                | None -> Error "schemaVersion is not a positive integer"

    /// Pure: classify the text of a persisted state document.
    let parse (content: string) : PacingStateRead =
        try
            use document = JsonDocument.Parse content
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                PacingStateRead.Unreadable "document is not a JSON object"
            else
                match schemaOf root with
                | Error reason -> PacingStateRead.Unreadable reason
                | Ok version when version > CurrentSchema -> PacingStateRead.Unsupported version
                | Ok 1 ->
                    match parseHolds 1 root with
                    | Ok state -> PacingStateRead.Migrated(state, 1)
                    | Error reason -> PacingStateRead.Unreadable reason
                | Ok version ->
                    let revision =
                        PacingJson.tryProperty "revision" root
                        |> Option.bind PacingJson.tryDecimal
                        |> Option.map int64

                    match revision, parseHolds version root with
                    | Some value, Ok state -> PacingStateRead.Current(state, value)
                    | None, Ok _ -> PacingStateRead.Unreadable "document has no revision"
                    | _, Error reason -> PacingStateRead.Unreadable reason
        with :? JsonException as error ->
            PacingStateRead.Unreadable $"invalid JSON: {error.Message}"

    let private holdRow (hold: PacingHold) =
        let row = JsonObject()
        let scopeKind, scopeName = scopeParts hold.Scope
        row["key"] <- JsonValue.Create(hold.Key)
        row["provider"] <- JsonValue.Create(hold.Provider)
        row["scopeKind"] <- JsonValue.Create(scopeKind)
        scopeName |> Option.iter (fun name -> row["scopeName"] <- JsonValue.Create(name))
        row["since"] <- JsonValue.Create(hold.Since.ToString("O"))
        row["basis"] <- JsonValue.Create(PacingHold.basisCode hold)

        match hold.Basis with
        | HoldBasis.WeeklyLead(Some resetsAt) -> row["resetsAt"] <- JsonValue.Create(resetsAt.ToString("O"))
        | HoldBasis.WeeklyLead None ->
            // A migrated legacy latch has no window identity yet; the next
            // fresh reading of its window adopts one.
            ()
        | HoldBasis.HardLimit(used, resetsAt) ->
            row["usedPercent"] <- JsonValue.Create(used)
            row["resetsAt"] <- JsonValue.Create(resetsAt.ToString("O"))

        row

    /// Pure: render state at a revision as a schema-2 document.
    let serialize (state: PacingState) (revision: int64) : string =
        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create(CurrentSchema)
        root["revision"] <- JsonValue.Create(revision)
        let rows = JsonArray()
        state.Holds.Values |> Seq.iter (holdRow >> rows.Add)
        root["holds"] <- rows
        root.ToJsonString()

[<RequireQualifiedAccess>]
module PacingPersistence =
    let private lockAttempts = 100
    let private lockRetry = TimeSpan.FromMilliseconds 50.0

    let private atomicWrite (path: string) (content: string) : unit =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        File.WriteAllText(temporary, content)
        File.Move(temporary, path, true)

    let private snapshotPath (directory: string) (provider: string) : string =
        Path.Combine(directory, $"snapshot-{provider}.json")

    /// The snapshot is a display/merge cache, not safety state: hard holds are
    /// persisted in `hold.json`. A failed cache write is reported, not fatal.
    let saveSnapshot (directory: string) (snapshot: ProviderSnapshot) : Result<unit, string> =
        let root = JsonObject()
        root["provider"] <- JsonValue.Create(snapshot.Provider)
        root["observedAt"] <- JsonValue.Create(snapshot.ObservedAt.ToString("O"))
        let windows = JsonArray()

        for window in snapshot.Windows do
            let row = JsonObject()

            let scopeKind, scopeName =
                match window.Scope with
                | QuotaScope.Global -> "global", None
                | QuotaScope.Model name -> "model", Some name
                | QuotaScope.Surface name -> "surface", Some name

            row["key"] <- JsonValue.Create(window.Key)
            row["label"] <- JsonValue.Create(window.Label)
            row["scopeKind"] <- JsonValue.Create(scopeKind)
            scopeName |> Option.iter (fun name -> row["scopeName"] <- JsonValue.Create(name))
            row["usedPercent"] <- JsonValue.Create(window.UsedPercent)
            row["durationMinutes"] <- JsonValue.Create(window.Duration.TotalMinutes)
            row["resetsAt"] <- JsonValue.Create(window.ResetsAt.ToString("O"))
            windows.Add row

        root["windows"] <- windows

        try
            atomicWrite (snapshotPath directory snapshot.Provider) (root.ToJsonString()) |> Ok
        with
        | :? IOException as error -> Error error.Message
        | :? UnauthorizedAccessException as error -> Error error.Message

    let loadSnapshot (directory: string) (provider: string) : Result<ProviderSnapshot option, string> =
        let path = snapshotPath directory provider

        if not (File.Exists path) then
            Ok None
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement

                let observed =
                    PacingJson.tryProperty "observedAt" root
                    |> Option.bind PacingJson.tryTimestamp
                    |> Option.defaultValue DateTimeOffset.MinValue

                let scopeOf kind name =
                    match kind, name with
                    | "model", Some value -> QuotaScope.Model value
                    | "surface", Some value -> QuotaScope.Surface value
                    | _ -> QuotaScope.Global

                let windows =
                    match PacingJson.tryProperty "windows" root with
                    | Some rows when rows.ValueKind = JsonValueKind.Array ->
                        rows.EnumerateArray()
                        |> Seq.choose (fun row ->
                            match
                                PacingJson.tryProperty "key" row |> Option.bind PacingJson.tryString,
                                PacingJson.tryProperty "label" row |> Option.bind PacingJson.tryString,
                                PacingJson.readNumber "usedPercent" row,
                                PacingJson.readNumber "durationMinutes" row,
                                PacingJson.readReset "resetsAt" row
                            with
                            | Some key, Some label, Some used, Some minutes, Some reset ->
                                Some
                                    { Provider = provider
                                      Key = key
                                      Label = label
                                      Scope =
                                        scopeOf
                                            (PacingJson.tryProperty "scopeKind" row
                                             |> Option.bind PacingJson.tryString
                                             |> Option.defaultValue "global")
                                            (PacingJson.tryProperty "scopeName" row |> Option.bind PacingJson.tryString)
                                      UsedPercent = used
                                      Duration = TimeSpan.FromMinutes(float minutes)
                                      ResetsAt = reset
                                      ObservedAt = observed }
                            | _ -> None)
                        |> Seq.toList
                    | _ -> []

                Ok(
                    Some
                        { Provider = provider
                          ObservedAt = observed
                          Windows = windows
                          Coverage = []
                          Freshness = ObservationFreshness.Stale "cached quota reading" }
                )
            with
            | :? JsonException as error -> Error $"cached snapshot is unreadable: {error.Message}"
            | :? IOException as error -> Error $"cached snapshot could not be read: {error.Message}"
            | :? UnauthorizedAccessException as error -> Error $"cached snapshot could not be read: {error.Message}"

    let holdPath (directory: string) : string = Path.Combine(directory, "hold.json")

    let readState (directory: string) : PacingStateRead =
        let path = holdPath directory

        if not (File.Exists path) then
            PacingStateRead.Absent
        else
            try
                PacingStateDocument.parse (File.ReadAllText path)
            with
            | :? IOException as error -> PacingStateRead.Unreadable $"state could not be read: {error.Message}"
            | :? UnauthorizedAccessException as error ->
                PacingStateRead.Unreadable $"state could not be read: {error.Message}"

    let private nextRevision (read: PacingStateRead) =
        match read with
        | PacingStateRead.Current(_, revision) -> revision + 1L
        | PacingStateRead.Absent
        | PacingStateRead.Migrated _
        | PacingStateRead.Unreadable _
        | PacingStateRead.Unsupported _ -> 1L

    let private acquireLock (directory: string) : Result<FileStream, PacingStoreFault> =
        let lockPath = Path.Combine(directory, "hold.lock")

        let rec attempt remaining =
            try
                Directory.CreateDirectory directory |> ignore
                Ok(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            with
            | :? IOException when remaining > 1 ->
                Thread.Sleep lockRetry
                attempt (remaining - 1)
            | :? IOException as error ->
                Error(PacingStoreFault.LockUnavailable $"{lockPath} is held by another process: {error.Message}")
            | :? UnauthorizedAccessException as error -> Error(PacingStoreFault.LockUnavailable error.Message)

        attempt lockAttempts

    let private write (directory: string) (read: PacingStateRead) (state: PacingState) : Result<unit, PacingStoreFault> =
        try
            atomicWrite (holdPath directory) (PacingStateDocument.serialize state (nextRevision read)) |> Ok
        with
        | :? IOException as error -> Error(PacingStoreFault.WriteFailed error.Message)
        | :? UnauthorizedAccessException as error -> Error(PacingStoreFault.WriteFailed error.Message)

    let private transact
        (directory: string)
        (action: PacingStateRead -> PacingTransaction)
        : Result<PacingTransaction, PacingStoreFault> =
        acquireLock directory
        |> Result.bind (fun lockHandle ->
            use _ = lockHandle
            let read = readState directory
            let transaction = action read

            match transaction.Write with
            | None -> Ok transaction
            | Some state -> write directory read state |> Result.map (fun () -> transaction))

    /// Explicit operator recovery: move unreadable or unsupported state aside.
    /// Readable state is never quarantined.
    let private quarantine (directory: string) (now: DateTimeOffset) : Result<string option, string> =
        acquireLock directory
        |> Result.mapError PacingStoreFault.message
        |> Result.bind (fun lockHandle ->
            use _ = lockHandle

            match readState directory with
            | PacingStateRead.Absent -> Ok None
            | PacingStateRead.Current _
            | PacingStateRead.Migrated _ -> Error "pacing state is readable; refusing to quarantine it"
            | PacingStateRead.Unreadable _
            | PacingStateRead.Unsupported _ ->
                let target = holdPath directory + ".quarantined-" + now.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ")

                try
                    File.Move(holdPath directory, target)
                    Ok(Some target)
                with
                | :? IOException as error -> Error error.Message
                | :? UnauthorizedAccessException as error -> Error error.Message)

    let private overridePath (directory: string) : string = Path.Combine(directory, "override")

    let private overrideEnabled (directory: string) : bool = File.Exists(overridePath directory)

    let private setOverride (directory: string) enabled : Result<unit, string> =
        try
            Directory.CreateDirectory directory |> ignore

            if enabled then
                File.WriteAllText(overridePath directory, "")
            else
                File.Delete(overridePath directory)

            if overrideEnabled directory = enabled then
                Ok()
            else
                Error $"override marker at {overridePath directory} did not reach the requested state"
        with
        | :? IOException as error -> Error error.Message
        | :? UnauthorizedAccessException as error -> Error error.Message

    let stateStore directory : PacingStateStore =
        { Transact = transact directory
          Quarantine = quarantine directory }

    let overrideStore directory : PacingOverrideStore =
        { IsEnabled = fun () -> overrideEnabled directory
          SetEnabled = setOverride directory }
