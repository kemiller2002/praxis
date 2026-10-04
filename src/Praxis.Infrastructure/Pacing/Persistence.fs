namespace Praxis.Infrastructure.Pacing

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Praxis.Application.Pacing
open Praxis.Domain.Pacing

[<RequireQualifiedAccess>]
module PacingPersistence =
    let private scopeParts (scope: QuotaScope) : string * string option =
        match scope with
        | QuotaScope.Global -> "global", None
        | QuotaScope.Model name -> "model", Some name
        | QuotaScope.Surface name -> "surface", Some name

    let private scopeFromParts (kind: string) (name: string option) : QuotaScope =
        match kind, name with
        | "model", Some value -> QuotaScope.Model value
        | "surface", Some value -> QuotaScope.Surface value
        | _ -> QuotaScope.Global

    let private atomicWrite (path: string) (content: string) : unit =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        File.WriteAllText(temporary, content)
        File.Move(temporary, path, true)

    let private snapshotPath (directory: string) (provider: string) : string =
        Path.Combine(directory, $"snapshot-{provider}.json")

    let saveSnapshot (directory: string) (snapshot: ProviderSnapshot) : unit =
        let root = JsonObject()
        root["provider"] <- JsonValue.Create(snapshot.Provider)
        root["observedAt"] <- JsonValue.Create(snapshot.ObservedAt.ToString("O"))
        let windows = JsonArray()

        for window in snapshot.Windows do
            let row = JsonObject()
            let scopeKind, scopeName = scopeParts window.Scope
            row["key"] <- JsonValue.Create(window.Key)
            row["label"] <- JsonValue.Create(window.Label)
            row["scopeKind"] <- JsonValue.Create(scopeKind)
            scopeName |> Option.iter (fun name -> row["scopeName"] <- JsonValue.Create(name))
            row["usedPercent"] <- JsonValue.Create(window.UsedPercent)
            row["durationMinutes"] <- JsonValue.Create(window.Duration.TotalMinutes)
            row["resetsAt"] <- JsonValue.Create(window.ResetsAt.ToString("O"))
            windows.Add row

        root["windows"] <- windows
        atomicWrite (snapshotPath directory snapshot.Provider) (root.ToJsonString())

    let loadSnapshot (directory: string) (provider: string) : ProviderSnapshot option =
        let path = snapshotPath directory provider

        if not (File.Exists path) then
            None
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement

                let observed =
                    PacingJson.tryProperty "observedAt" root
                    |> Option.bind PacingJson.tryTimestamp
                    |> Option.defaultValue DateTimeOffset.MinValue

                let windows =
                    match PacingJson.tryProperty "windows" root with
                    | Some rows when rows.ValueKind = JsonValueKind.Array ->
                        [ for row in rows.EnumerateArray() do
                              match
                                  PacingJson.tryProperty "key" row |> Option.bind PacingJson.tryString,
                                  PacingJson.tryProperty "label" row |> Option.bind PacingJson.tryString,
                                  PacingJson.readNumber "usedPercent" row,
                                  PacingJson.readNumber "durationMinutes" row,
                                  PacingJson.readReset "resetsAt" row
                              with
                              | Some key, Some label, Some used, Some minutes, Some reset ->
                                  let kind =
                                      PacingJson.tryProperty "scopeKind" row
                                      |> Option.bind PacingJson.tryString
                                      |> Option.defaultValue "global"

                                  let name =
                                      PacingJson.tryProperty "scopeName" row
                                      |> Option.bind PacingJson.tryString

                                  yield
                                      { Provider = provider
                                        Key = key
                                        Label = label
                                        Scope = scopeFromParts kind name
                                        UsedPercent = used
                                        Duration = TimeSpan.FromMinutes(float minutes)
                                        ResetsAt = reset
                                        ObservedAt = observed }
                              | _ -> () ]
                    | _ -> []

                Some
                    { Provider = provider
                      ObservedAt = observed
                      Windows = windows
                      Freshness = ObservationFreshness.Stale "cached quota reading" }
            with _ ->
                None

    let private holdPath (directory: string) : string =
        Path.Combine(directory, "hold.json")

    let private loadState (directory: string) : PacingState =
        let path = holdPath directory

        if not (File.Exists path) then
            PacingState.empty
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement

                let holds =
                    match PacingJson.tryProperty "holds" root with
                    | Some rows when rows.ValueKind = JsonValueKind.Array ->
                        [ for row in rows.EnumerateArray() do
                              match
                                  PacingJson.tryProperty "key" row |> Option.bind PacingJson.tryString,
                                  PacingJson.tryProperty "provider" row |> Option.bind PacingJson.tryString,
                                  PacingJson.tryProperty "since" row |> Option.bind PacingJson.tryTimestamp
                              with
                              | Some key, Some provider, Some since ->
                                  let kind =
                                      PacingJson.tryProperty "scopeKind" row
                                      |> Option.bind PacingJson.tryString
                                      |> Option.defaultValue "global"

                                  let name =
                                      PacingJson.tryProperty "scopeName" row
                                      |> Option.bind PacingJson.tryString

                                  yield
                                      key,
                                      { Key = key
                                        Provider = provider
                                        Scope = scopeFromParts kind name
                                        Since = since }
                              | _ -> () ]
                        |> Map.ofList
                    | _ -> Map.empty

                { Holds = holds }
            with _ ->
                PacingState.empty

    let private saveState (directory: string) (state: PacingState) : unit =
        let root = JsonObject()
        let rows = JsonArray()

        for hold in state.Holds.Values do
            let row = JsonObject()
            let scopeKind, scopeName = scopeParts hold.Scope
            row["key"] <- JsonValue.Create(hold.Key)
            row["provider"] <- JsonValue.Create(hold.Provider)
            row["scopeKind"] <- JsonValue.Create(scopeKind)
            scopeName |> Option.iter (fun name -> row["scopeName"] <- JsonValue.Create(name))
            row["since"] <- JsonValue.Create(hold.Since.ToString("O"))
            rows.Add row

        root["holds"] <- rows
        atomicWrite (holdPath directory) (root.ToJsonString())

    let private withState
        (directory: string)
        (action: PacingState -> PacingDecision)
        : PacingDecision =
        Directory.CreateDirectory directory |> ignore
        let lockPath = Path.Combine(directory, "hold.lock")

        let rec acquire attempts =
            try
                new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            with :? IOException when attempts < 100 ->
                Thread.Sleep 50
                acquire (attempts + 1)

        use _lock = acquire 0
        let state = loadState directory
        let decision = action state
        saveState directory decision.State
        decision

    let private overridePath (directory: string) : string =
        Path.Combine(directory, "override")

    let private overrideEnabled (directory: string) : bool =
        File.Exists(overridePath directory)

    let private setOverride (directory: string) enabled =
        Directory.CreateDirectory directory |> ignore

        if enabled then
            File.WriteAllText(overridePath directory, "")
        else
            try
                File.Delete(overridePath directory)
            with _ ->
                ()

    let stateStore directory : PacingStateStore =
        { WithState = withState directory }

    let overrideStore directory : PacingOverrideStore =
        { IsEnabled = fun () -> overrideEnabled directory
          SetEnabled = setOverride directory }
