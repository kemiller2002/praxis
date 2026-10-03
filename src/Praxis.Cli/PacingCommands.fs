namespace Praxis.Cli

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Praxis.Domain.Pacing

type ProviderSnapshot =
    { Provider: string
      ObservedAt: DateTimeOffset
      Windows: QuotaWindow list
      Freshness: ObservationFreshness }

[<RequireQualifiedAccess>]
module PacingCommands =
    let usage =
        "pacing status [--provider codex|claude] [--model MODEL] [--json] [--state-dir PATH] | pacing gate [--provider codex|claude] [--model MODEL] [--state-dir PATH] | pacing override {on|off} [--state-dir PATH]"

    let private usageApi = "https://api.anthropic.com/api/oauth/usage"
    let private refreshEvery = TimeSpan.FromSeconds 60.0

    let private tryProperty name (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            None
        else
            let mutable value = Unchecked.defaultof<JsonElement>
            if element.TryGetProperty(name, &value) then Some value else None

    let private tryString element =
        if element.ValueKind = JsonValueKind.String then
            element.GetString() |> Option.ofObj
        else
            None

    let private tryDecimal element =
        if element.ValueKind <> JsonValueKind.Number then
            None
        else
            let mutable value = 0m
            if element.TryGetDecimal(&value) then
                Some value
            else
                let mutable number = 0.0
                if element.TryGetDouble(&number) && Double.IsFinite number then Some(decimal number) else None

    let private tryInt element =
        if element.ValueKind <> JsonValueKind.Number then
            None
        else
            let mutable value = 0
            if element.TryGetInt32(&value) then Some value else None

    let private tryEpoch element =
        match tryDecimal element with
        | Some value when value > 0m ->
            try
                Some(DateTimeOffset.FromUnixTimeSeconds(int64 value))
            with _ ->
                None
        | _ -> None

    let private tryTimestamp element =
        match tryString element with
        | Some text ->
            let mutable value = DateTimeOffset.MinValue
            if DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, &value) then
                Some value
            else
                None
        | None -> tryEpoch element

    let private readNumber name element =
        tryProperty name element |> Option.bind tryDecimal

    let private readReset name element =
        tryProperty name element |> Option.bind tryTimestamp

    let private labelFor duration scope =
        if Math.Abs(duration.TotalMinutes - 300.0) < 0.5 then
            "5H session"
        elif Math.Abs(duration.TotalMinutes - 10080.0) < 0.5 then
            match scope with
            | QuotaScope.Global -> "7D weekly"
            | QuotaScope.Model model -> $"7D weekly {model}"
            | QuotaScope.Surface surface -> $"7D weekly {surface}"
        else
            $"{duration.TotalMinutes:0}m allowance"

    let normalizeCodexResult (bucket: string) (observedAt: DateTimeOffset) (json: string) : Result<QuotaWindow list * bool, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let bucketElement =
                match tryProperty "rateLimitsByLimitId" root with
                | Some buckets when buckets.ValueKind = JsonValueKind.Object ->
                    tryProperty bucket buckets
                | _ ->
                    match tryProperty "rateLimits" root with
                    | Some single when single.ValueKind = JsonValueKind.Object -> Some single
                    | _ -> None

            match bucketElement with
            | None -> Error $"Codex did not report quota bucket '{bucket}'"
            | Some bucketRow ->
                let mutable expected = 0

                let windows =
                    [ for slot in [ "primary"; "secondary" ] do
                          match tryProperty slot bucketRow with
                          | Some row when row.ValueKind = JsonValueKind.Object ->
                              expected <- expected + 1

                              match readNumber "usedPercent" row, readNumber "windowDurationMins" row, tryProperty "resetsAt" row |> Option.bind tryEpoch with
                              | Some used, Some minutes, Some reset when used >= 0m && used <= 100m && minutes > 0m ->
                                  let duration = TimeSpan.FromMinutes(float minutes)
                                  let key = $"{bucket}:{int (Math.Round duration.TotalMinutes)}m"

                                  yield
                                      { Provider = "codex"
                                        Key = key
                                        Label = labelFor duration QuotaScope.Global
                                        Scope = QuotaScope.Global
                                        UsedPercent = used
                                        Duration = duration
                                        ResetsAt = reset
                                        ObservedAt = observedAt }
                              | _ -> ()
                          | _ -> () ]

                if windows.IsEmpty then
                    Error $"Codex bucket '{bucket}' contained no usable quota windows"
                else
                    Ok(windows, windows.Length = expected)
        with error ->
            Error $"Codex quota response could not be parsed: {error.Message}"

    let private scopeName name (scope: JsonElement) =
        tryProperty name scope
        |> Option.bind (tryProperty "display_name")
        |> Option.bind tryString

    let normalizeClaudeUsage (observedAt: DateTimeOffset) (json: string) : Result<QuotaWindow list * bool, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            let mutable windows = Map.empty<string, QuotaWindow>

            let add key scope used reset duration =
                if used >= 0m && used <= 100m && reset > DateTimeOffset.UnixEpoch then
                    windows <-
                        windows.Add(
                            key,
                            { Provider = "claude"
                              Key = key
                              Label = labelFor duration scope
                              Scope = scope
                              UsedPercent = used
                              Duration = duration
                              ResetsAt = reset
                              ObservedAt = observedAt })

            let topLevel key duration =
                match tryProperty key root with
                | Some row when row.ValueKind = JsonValueKind.Object ->
                    let used =
                        match readNumber "utilization" row with
                        | Some value -> Some value
                        | None -> readNumber "used_percentage" row

                    match used, readReset "resets_at" row with
                    | Some percent, Some reset -> add key QuotaScope.Global percent reset duration
                    | _ -> ()
                | _ -> ()

            topLevel "five_hour" (TimeSpan.FromMinutes 300.0)
            topLevel "seven_day" (TimeSpan.FromMinutes 10080.0)

            match tryProperty "limits" root with
            | Some limits when limits.ValueKind = JsonValueKind.Array ->
                for row in limits.EnumerateArray() do
                    let kind = tryProperty "kind" row |> Option.bind tryString |> Option.defaultValue ""
                    let scope = tryProperty "scope" row
                    let model = scope |> Option.bind (scopeName "model")
                    let surface = scope |> Option.bind (scopeName "surface")
                    let hasScope = model.IsSome || surface.IsSome

                    let key, quotaScope, duration =
                        match kind, model, surface, hasScope with
                        | "session", None, None, false -> Some "five_hour", QuotaScope.Global, TimeSpan.FromMinutes 300.0
                        | "weekly_all", None, None, false -> Some "seven_day", QuotaScope.Global, TimeSpan.FromMinutes 10080.0
                        | weekly, Some name, _, _ when weekly.StartsWith("weekly", StringComparison.Ordinal) ->
                            Some($"seven_day:{name}"), QuotaScope.Model name, TimeSpan.FromMinutes 10080.0
                        | weekly, None, Some name, _ when weekly.StartsWith("weekly", StringComparison.Ordinal) ->
                            Some($"seven_day:surface:{name}"), QuotaScope.Surface name, TimeSpan.FromMinutes 10080.0
                        | _ -> None, QuotaScope.Global, TimeSpan.Zero

                    match key, readNumber "percent" row, readReset "resets_at" row with
                    | Some windowKey, Some used, Some reset -> add windowKey quotaScope used reset duration
                    | _ -> ()
            | _ -> ()

            let result = windows |> Map.toList |> List.map snd
            let keys = result |> List.map _.Key |> Set.ofList
            let complete = keys.Contains "five_hour" && keys.Contains "seven_day"

            if result.IsEmpty then Error "Claude returned no usable quota windows" else Ok(result, complete)
        with error ->
            Error $"Claude quota response could not be parsed: {error.Message}"

    let private processCapture fileName arguments timeout =
        try
            let start = ProcessStartInfo()
            start.FileName <- fileName
            start.UseShellExecute <- false
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            start.CreateNoWindow <- true

            for argument in arguments do
                start.ArgumentList.Add argument

            use process = new Process()
            process.StartInfo <- start

            if not (process.Start()) then
                Error $"{fileName} did not start"
            else
                let output = process.StandardOutput.ReadToEndAsync()
                let error = process.StandardError.ReadToEndAsync()

                if process.WaitForExit(int timeout.TotalMilliseconds) then
                    let stdout = output.GetAwaiter().GetResult()
                    let stderr = error.GetAwaiter().GetResult()

                    if process.ExitCode = 0 then Ok stdout
                    else Error $"{fileName} exited {process.ExitCode}: {stderr.Trim()}"
                else
                    try
                        process.Kill(true)
                    with _ ->
                        ()

                    Error $"{fileName} timed out"
        with error ->
            Error $"{fileName} unavailable: {error.Message}"

    let private codexRpc () =
        try
            let start = ProcessStartInfo()
            start.FileName <- "codex"
            start.UseShellExecute <- false
            start.RedirectStandardInput <- true
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            start.CreateNoWindow <- true
            start.ArgumentList.Add "app-server"
            start.ArgumentList.Add "--stdio"

            use process = new Process()
            process.StartInfo <- start

            if not (process.Start()) then
                Error "codex app-server did not start"
            else
                let send text =
                    process.StandardInput.WriteLine text
                    process.StandardInput.Flush()

                let deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds 20.0
                send """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"praxis_pacing","version":"1.0.0"}}}"""

                let mutable result: Result<string, string> option = None

                while result.IsNone && DateTimeOffset.UtcNow < deadline do
                    let remaining = deadline - DateTimeOffset.UtcNow

                    try
                        let line =
                            process.StandardOutput.ReadLineAsync()
                                .WaitAsync(if remaining > TimeSpan.Zero then remaining else TimeSpan.FromMilliseconds 1.0)
                                .GetAwaiter()
                                .GetResult()

                        if isNull line then
                            result <- Some(Error "Codex app-server closed before returning quota")
                        else
                            use document = JsonDocument.Parse line
                            let root = document.RootElement

                            match tryProperty "id" root |> Option.bind tryInt with
                            | Some 1 ->
                                match tryProperty "error" root with
                                | Some error ->
                                    result <- Some(Error $"Codex initialize failed: {error.GetRawText()}")
                                | None ->
                                    send """{"method":"initialized","params":{}}"""
                                    send """{"id":2,"method":"account/rateLimits/read"}"""
                            | Some 2 ->
                                match tryProperty "error" root, tryProperty "result" root with
                                | Some error, _ -> result <- Some(Error $"Codex quota query failed: {error.GetRawText()}")
                                | None, Some value -> result <- Some(Ok(value.GetRawText()))
                                | _ -> result <- Some(Error "Codex quota query returned no result")
                            | _ -> ()
                    with :? TimeoutException ->
                        result <- Some(Error "Codex quota query timed out")

                try
                    process.StandardInput.Close()
                    if not process.HasExited then process.Kill(true)
                with _ ->
                    ()

                result |> Option.defaultValue (Error "Codex quota query timed out")
        with error ->
            Error $"Codex app-server unavailable: {error.Message}"

    let private claudeToken now =
        if not (OperatingSystem.IsMacOS()) then
            Error "Claude pacing requires macOS Keychain access"
        else
            match processCapture "security" [ "find-generic-password"; "-s"; "Claude Code-credentials"; "-w" ] (TimeSpan.FromSeconds 3.0) with
            | Error message -> Error message
            | Ok output ->
                try
                    use document = JsonDocument.Parse output
                    let root = document.RootElement

                    match tryProperty "claudeAiOauth" root with
                    | None -> Error "Claude Code OAuth credentials were not found in Keychain"
                    | Some oauth ->
                        let token = tryProperty "accessToken" oauth |> Option.bind tryString
                        let expires =
                            tryProperty "expiresAt" oauth
                            |> Option.bind tryDecimal
                            |> Option.map (fun value -> DateTimeOffset.FromUnixTimeMilliseconds(int64 value))

                        match token, expires with
                        | Some value, Some expiry when expiry > now + TimeSpan.FromMinutes 1.0 -> Ok value
                        | _ -> Error "Claude Code OAuth credentials are missing or expired"
                with error ->
                    Error $"Claude Code credentials could not be parsed: {error.Message}"

    let private claudeUsage () =
        let now = DateTimeOffset.UtcNow

        match claudeToken now with
        | Error message -> Error message
        | Ok token ->
            try
                use handler = new HttpClientHandler()
                handler.AllowAutoRedirect <- false
                use client = new HttpClient(handler)
                client.Timeout <- TimeSpan.FromSeconds 10.0
                use request = new HttpRequestMessage(HttpMethod.Get, usageApi)
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20") |> ignore
                use response = client.Send request

                if int response.StatusCode >= 300 && int response.StatusCode < 400 then
                    Error "Claude usage endpoint refused redirect for credential safety"
                elif not response.IsSuccessStatusCode then
                    Error $"Claude usage endpoint returned HTTP {int response.StatusCode}"
                else
                    Ok(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            with error ->
                Error $"Claude usage query failed: {error.Message}"

    let private stateDirectory arguments =
        match arguments |> List.tryFindIndex ((=) "--state-dir") with
        | Some index ->
            arguments
            |> List.tryItem (index + 1)
            |> Option.map Path.GetFullPath
            |> Option.defaultValue (Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".praxis", "usage-pacing"))
        | None ->
            match Environment.GetEnvironmentVariable "PRAXIS_PACING_DIR" with
            | null
            | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".praxis", "usage-pacing")
            | value -> Path.GetFullPath value

    let private optionValue name arguments =
        arguments
        |> List.tryFindIndex ((=) name)
        |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

    let private scopeParts scope =
        match scope with
        | QuotaScope.Global -> "global", None
        | QuotaScope.Model name -> "model", Some name
        | QuotaScope.Surface name -> "surface", Some name

    let private scopeFromParts kind name =
        match kind, name with
        | "model", Some value -> QuotaScope.Model value
        | "surface", Some value -> QuotaScope.Surface value
        | _ -> QuotaScope.Global

    let private atomicWrite path content =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        File.WriteAllText(temporary, content)
        File.Move(temporary, path, true)

    let private snapshotPath directory provider = Path.Combine(directory, $"snapshot-{provider}.json")

    let private saveSnapshot directory snapshot =
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

    let private loadSnapshot directory provider =
        let path = snapshotPath directory provider

        if not (File.Exists path) then
            None
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement
                let observed =
                    tryProperty "observedAt" root
                    |> Option.bind tryTimestamp
                    |> Option.defaultValue DateTimeOffset.MinValue

                let windows =
                    match tryProperty "windows" root with
                    | Some rows when rows.ValueKind = JsonValueKind.Array ->
                        [ for row in rows.EnumerateArray() do
                              match
                                  tryProperty "key" row |> Option.bind tryString,
                                  tryProperty "label" row |> Option.bind tryString,
                                  readNumber "usedPercent" row,
                                  readNumber "durationMinutes" row,
                                  readReset "resetsAt" row
                              with
                              | Some key, Some label, Some used, Some minutes, Some reset ->
                                  let kind = tryProperty "scopeKind" row |> Option.bind tryString |> Option.defaultValue "global"
                                  let name = tryProperty "scopeName" row |> Option.bind tryString

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

    let private mergeMissingHard previous current now =
        let currentKeys = current |> List.map _.Key |> Set.ofList

        let retained =
            previous
            |> List.filter (fun window ->
                window.UsedPercent > PacingPolicy.defaults.HardUsagePercent
                && window.ResetsAt > now
                && not (currentKeys.Contains window.Key))

        current @ retained

    let private queryProvider directory provider model =
        Directory.CreateDirectory directory |> ignore
        let observedAt = DateTimeOffset.UtcNow
        let previous = loadSnapshot directory provider |> Option.map _.Windows |> Option.defaultValue []

        let result =
            match provider with
            | "codex" ->
                let bucket =
                    match model with
                    | Some value when value.Contains("spark", StringComparison.OrdinalIgnoreCase) -> "codex_bengalfox"
                    | _ -> "codex"

                codexRpc () |> Result.bind (normalizeCodexResult bucket observedAt)
            | "claude" -> claudeUsage () |> Result.bind (normalizeClaudeUsage observedAt)
            | other -> Error $"unknown pacing provider '{other}'"

        match result with
        | Ok(windows, true) ->
            let snapshot =
                { Provider = provider
                  ObservedAt = observedAt
                  Windows = windows
                  Freshness = ObservationFreshness.Fresh }

            saveSnapshot directory snapshot
            snapshot
        | Ok(windows, false) ->
            let combined = mergeMissingHard previous windows observedAt

            let snapshot =
                { Provider = provider
                  ObservedAt = observedAt
                  Windows = combined
                  Freshness = ObservationFreshness.Stale "provider quota response was incomplete" }

            if not combined.IsEmpty then saveSnapshot directory snapshot
            snapshot
        | Error message ->
            match loadSnapshot directory provider with
            | Some cached -> { cached with Freshness = ObservationFreshness.Stale message }
            | None ->
                { Provider = provider
                  ObservedAt = observedAt
                  Windows = []
                  Freshness = ObservationFreshness.Unavailable message }

    let private holdPath directory = Path.Combine(directory, "hold.json")

    let private loadState directory =
        let path = holdPath directory

        if not (File.Exists path) then
            PacingState.empty
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement

                let holds =
                    match tryProperty "holds" root with
                    | Some rows when rows.ValueKind = JsonValueKind.Array ->
                        [ for row in rows.EnumerateArray() do
                              match
                                  tryProperty "key" row |> Option.bind tryString,
                                  tryProperty "provider" row |> Option.bind tryString,
                                  tryProperty "since" row |> Option.bind tryTimestamp
                              with
                              | Some key, Some provider, Some since ->
                                  let kind = tryProperty "scopeKind" row |> Option.bind tryString |> Option.defaultValue "global"
                                  let name = tryProperty "scopeName" row |> Option.bind tryString
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

    let private saveState directory state =
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

    let private withState directory action =
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

    let private overridePath directory = Path.Combine(directory, "override")

    let private overrideEnabled directory = File.Exists(overridePath directory)

    let private freshnessText freshness =
        match freshness with
        | ObservationFreshness.Fresh -> "fresh"
        | ObservationFreshness.Stale reason -> $"stale: {reason}"
        | ObservationFreshness.Unavailable reason -> $"unavailable: {reason}"

    let private evaluate directory provider model snapshot =
        withState directory (fun state ->
            Pacing.evaluate
                PacingPolicy.defaults
                { Now = DateTimeOffset.UtcNow
                  Provider = provider
                  Model = model
                  Windows = snapshot.Windows
                  Freshness = snapshot.Freshness
                  Existing = state
                  Override = overrideEnabled directory })

    let private renderStatusText provider model snapshot decision =
        printfn "pacing %s%s: %s" provider (model |> Option.map (fun value -> $" ({value})") |> Option.defaultValue "") (freshnessText snapshot.Freshness)

        if snapshot.Windows.IsEmpty then
            printfn "quota: no usable windows"
        else
            for window in snapshot.Windows do
                let ideal = Pacing.idealPercent DateTimeOffset.UtcNow window

                if Pacing.isWeekly PacingPolicy.defaults window then
                    let lead = Pacing.lead DateTimeOffset.UtcNow window
                    printfn "%s: used %.2f%%, pace %.2f%%, lead %+.1fh, reset %s" window.Label (float window.UsedPercent) (float ideal) lead.TotalHours (window.ResetsAt.ToLocalTime().ToString("g"))
                else
                    printfn "%s: used %.2f%%, reset %s" window.Label (float window.UsedPercent) (window.ResetsAt.ToLocalTime().ToString("g"))

        match decision.BindingReason with
        | None -> printfn "gate: proceed%s" (if overrideEnabled (stateDirectory []) then " (override)" else "")
        | Some reason ->
            printfn "gate: hold (%s); %s; recheck/resume estimate %s" (PacingReasonKind.code reason.Kind) reason.Detail (reason.ResumeAt.ToLocalTime().ToString("g"))

    let private statusJson provider model snapshot decision =
        let root = JsonObject()
        root["provider"] <- JsonValue.Create(provider)
        model |> Option.iter (fun value -> root["model"] <- JsonValue.Create(value))
        root["freshness"] <- JsonValue.Create(freshnessText snapshot.Freshness)
        root["mayProceed"] <- JsonValue.Create(decision.MayProceed)
        root["override"] <- JsonValue.Create(false)
        let windows = JsonArray()

        for window in snapshot.Windows do
            let row = JsonObject()
            row["key"] <- JsonValue.Create(window.Key)
            row["label"] <- JsonValue.Create(window.Label)
            row["usedPercent"] <- JsonValue.Create(window.UsedPercent)
            row["durationMinutes"] <- JsonValue.Create(window.Duration.TotalMinutes)
            row["resetsAt"] <- JsonValue.Create(window.ResetsAt.ToString("O"))

            if Pacing.isWeekly PacingPolicy.defaults window then
                row["leadHours"] <- JsonValue.Create((Pacing.lead DateTimeOffset.UtcNow window).TotalHours)

            windows.Add row

        root["windows"] <- windows

        match decision.BindingReason with
        | Some reason ->
            let hold = JsonObject()
            hold["kind"] <- JsonValue.Create(PacingReasonKind.code reason.Kind)
            hold["detail"] <- JsonValue.Create(reason.Detail)
            hold["resumeAt"] <- JsonValue.Create(reason.ResumeAt.ToString("O"))
            root["hold"] <- hold
        | None -> ()

        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    let private payloadModel provider explicitModel payload =
        match explicitModel with
        | Some _ -> explicitModel
        | None ->
            try
                use document = JsonDocument.Parse payload
                let root = document.RootElement

                match tryProperty "model" root |> Option.bind tryString with
                | Some model -> Some model
                | None when provider = "claude" ->
                    let transcript = tryProperty "transcript_path" root |> Option.bind tryString
                    let agent = tryProperty "agent_id" root |> Option.bind tryString

                    let path =
                        match transcript, agent with
                        | Some file, Some id when file.EndsWith(".jsonl", StringComparison.Ordinal) ->
                            Path.Combine(Path.GetDirectoryName file, Path.GetFileNameWithoutExtension file, "subagents", $"agent-{id}.jsonl")
                        | Some file, _ -> file
                        | _ -> ""

                    if String.IsNullOrWhiteSpace path || not (File.Exists path) then
                        None
                    else
                        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        let length = min stream.Length (1L <<< 20)
                        stream.Seek(-length, SeekOrigin.End) |> ignore
                        use reader = new StreamReader(stream)
                        let lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)

                        lines
                        |> Array.rev
                        |> Array.tryPick (fun line ->
                            try
                                use row = JsonDocument.Parse line
                                let entry = row.RootElement
                                let kind = tryProperty "type" entry |> Option.bind tryString

                                if kind = Some "assistant" then
                                    tryProperty "message" entry
                                    |> Option.bind (tryProperty "model")
                                    |> Option.bind tryString
                                    |> Option.filter (fun value -> not (value.StartsWith("<", StringComparison.Ordinal)))
                                else
                                    None
                            with _ ->
                                None)
                | None -> None
            with _ ->
                None

    let private payloadEvent payload =
        try
            use document = JsonDocument.Parse payload
            tryProperty "hook_event_name" document.RootElement |> Option.bind tryString |> Option.defaultValue "PreToolUse"
        with _ ->
            "PreToolUse"

    let private writeLog directory text =
        try
            Directory.CreateDirectory directory |> ignore
            File.AppendAllText(Path.Combine(directory, "pace.log"), $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {text}{Environment.NewLine}")
        with _ ->
            ()

    let private deny provider event detail =
        let reason = $"Praxis usage pacing hold remains active: {detail}. Retry after quota refresh/reset."

        if event = "PreToolUse" then
            let root = JsonObject()
            let output = JsonObject()
            output["hookEventName"] <- JsonValue.Create(event)
            output["permissionDecision"] <- JsonValue.Create("deny")
            output["permissionDecisionReason"] <- JsonValue.Create(reason)
            root["hookSpecificOutput"] <- output
            printf "%s" (root.ToJsonString())
        elif provider = "codex" then
            let root = JsonObject()
            root["continue"] <- JsonValue.Create(false)
            root["stopReason"] <- JsonValue.Create(reason)
            printf "%s" (root.ToJsonString())
        else
            let root = JsonObject()
            root["decision"] <- JsonValue.Create("block")
            root["reason"] <- JsonValue.Create(reason)
            printf "%s" (root.ToJsonString())

    let private runGate directory provider explicitModel =
        let payload = if Console.IsInputRedirected then Console.In.ReadToEnd() else "{}"
        let model = payloadModel provider explicitModel payload
        let event = payloadEvent payload
        let started = DateTimeOffset.UtcNow
        let maximum =
            if provider = "claude" then TimeSpan.FromDays 6.0
            else TimeSpan.FromDays 7.0 + TimeSpan.FromMinutes 1.0

        let mutable lastRefresh = DateTimeOffset.MinValue
        let mutable snapshot: ProviderSnapshot option = None
        let mutable lastReason: string option = None
        let mutable finished = false
        let mutable exitCode = 0

        while not finished do
            if overrideEnabled directory then
                lastReason |> Option.iter (fun _ -> writeLog directory "release: override enabled")
                finished <- true
            else
                let now = DateTimeOffset.UtcNow

                if snapshot.IsNone || now - lastRefresh >= refreshEvery then
                    snapshot <- Some(queryProvider directory provider model)
                    lastRefresh <- now

                let current = snapshot.Value
                let decision = evaluate directory provider model current

                match decision.BindingReason with
                | None ->
                    lastReason |> Option.iter (fun _ -> writeLog directory "release: back within pacing rules")
                    finished <- true
                | Some reason ->
                    let identity = $"{PacingReasonKind.code reason.Kind}: {reason.Detail}"

                    if lastReason <> Some identity then
                        writeLog directory $"hold {provider}: {identity}; resume estimate {reason.ResumeAt:O}"
                        lastReason <- Some identity

                    if now - started >= maximum then
                        deny provider event reason.Detail
                        finished <- true
                    else
                        let untilEstimate = reason.ResumeAt - now
                        let sleep =
                            if untilEstimate <= TimeSpan.Zero then TimeSpan.FromSeconds 1.0
                            else min PacingPolicy.defaults.PollInterval untilEstimate

                        Thread.Sleep sleep

        exitCode

    let private runStatus directory provider model asJson =
        let snapshot = queryProvider directory provider model
        let decision = evaluate directory provider model snapshot

        if asJson then
            printf "%s" (statusJson provider model snapshot decision)
        else
            printfn "state directory: %s" directory
            printfn "override: %s" (if overrideEnabled directory then "on" else "off")
            printfn "pacing %s%s: %s" provider (model |> Option.map (fun value -> $" ({value})") |> Option.defaultValue "") (freshnessText snapshot.Freshness)

            if snapshot.Windows.IsEmpty then
                printfn "quota: no usable windows"
            else
                for window in snapshot.Windows do
                    let now = DateTimeOffset.UtcNow
                    let ideal = Pacing.idealPercent now window

                    if Pacing.isWeekly PacingPolicy.defaults window then
                        let lead = Pacing.lead now window
                        printfn "%s: used %.2f%%, pace %.2f%%, lead %+.1fh, reset %s" window.Label (float window.UsedPercent) (float ideal) lead.TotalHours (window.ResetsAt.ToLocalTime().ToString("g"))
                    else
                        printfn "%s: used %.2f%%, reset %s" window.Label (float window.UsedPercent) (window.ResetsAt.ToLocalTime().ToString("g"))

            match decision.BindingReason with
            | None -> printfn "gate: proceed"
            | Some reason ->
                printfn "gate: hold (%s); %s; recheck/resume estimate %s" (PacingReasonKind.code reason.Kind) reason.Detail (reason.ResumeAt.ToLocalTime().ToString("g"))

        match snapshot.Freshness with
        | ObservationFreshness.Unavailable _ -> 1
        | _ -> 0

    let run _root arguments =
        let directory = stateDirectory arguments
        let provider = optionValue "--provider" arguments |> Option.defaultValue "codex" |> fun value -> value.ToLowerInvariant()
        let model = optionValue "--model" arguments

        match arguments |> List.filter (fun value -> value <> "--json") with
        | "status" :: _ when provider = "codex" || provider = "claude" ->
            runStatus directory provider model (arguments |> List.contains "--json")
        | "gate" :: _ when provider = "codex" || provider = "claude" ->
            runGate directory provider model
        | [ "override"; "on" ]
        | [ "override"; "on"; "--state-dir"; _ ] ->
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(overridePath directory, "")
            printfn "Pacing override on"
            0
        | [ "override"; "off" ]
        | [ "override"; "off"; "--state-dir"; _ ] ->
            try
                File.Delete(overridePath directory)
            with _ ->
                ()

            printfn "Pacing override off"
            0
        | _ ->
            eprintfn "ERROR expected %s" usage
            2
