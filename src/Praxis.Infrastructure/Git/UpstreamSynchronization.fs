namespace Praxis.Infrastructure.Git

open System
open System.ComponentModel
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Git

type private SyncProcessResult =
    { ExitCode: int
      Output: string
      Error: string
      TimedOut: bool }

type private StoredSyncState =
    { Remote: string
      Branch: string
      StartingUpstreamCommit: CommitId option
      LastAttemptAt: DateTimeOffset option
      LastAttempt: UpstreamSyncAttempt
      LastSuccessfulCheckAt: DateTimeOffset option }

/// Implements the bounded, fetch-only upstream check. State is stored
/// under Git's per-worktree metadata so a check never dirties the checkout.
[<RequireQualifiedAccess>]
module FileUpstreamSynchronization =
    let private unavailable operation reason message exitCode =
        { Operation = operation
          Reason = reason
          Message = message
          ExitCode = exitCode }

    let private failureOf operation (result: SyncProcessResult) =
        let reason =
            if result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) then
                GitUnavailableReason.NotRepository
            else
                GitUnavailableReason.CommandFailed

        unavailable
            operation
            reason
            (if result.Error.Length = 0 then $"git exited with code {result.ExitCode}" else result.Error)
            (if result.TimedOut then None else Some result.ExitCode)

    let private runGit executable root operation timeout arguments : Result<SyncProcessResult, GitFailure> =
        try
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- executable
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.RedirectStandardInput <- true
            startInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
            startInfo.Environment["GCM_INTERACTIVE"] <- "never"
            startInfo.Environment["GIT_ASKPASS"] <- ""
            startInfo.Environment["SSH_ASKPASS"] <- ""
            startInfo.ArgumentList.Add "-C"
            startInfo.ArgumentList.Add root
            arguments |> List.iter startInfo.ArgumentList.Add

            use child = new Process(StartInfo = startInfo)

            if not (child.Start()) then
                Error(unavailable operation GitUnavailableReason.ToolUnavailable "git process did not start" None)
            else
                child.StandardInput.Close()
                let output = child.StandardOutput.ReadToEndAsync()
                let error = child.StandardError.ReadToEndAsync()

                let exited =
                    match timeout with
                    | Some(limit: TimeSpan) -> child.WaitForExit(int limit.TotalMilliseconds)
                    | None ->
                        child.WaitForExit()
                        true

                if not exited then
                    try
                        child.Kill true
                    with _ ->
                        ()

                    Ok
                        { ExitCode = -1
                          Output = ""
                          Error = $"timed out after {timeout.Value.TotalSeconds:F0}s"
                          TimedOut = true }
                else
                    child.WaitForExit()

                    Ok
                        { ExitCode = child.ExitCode
                          Output = output.Result
                          Error = error.Result.Trim()
                          TimedOut = false }
        with
        | :? Win32Exception as error ->
            Error(unavailable operation GitUnavailableReason.ToolUnavailable error.Message None)
        | error -> Error(unavailable operation GitUnavailableReason.CommandFailed error.Message None)

    let private timeout () =
        let configured = Environment.GetEnvironmentVariable "PRAXIS_GIT_REMOTE_TIMEOUT_SECONDS"

        match Double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, seconds when seconds > 0.0 -> TimeSpan.FromSeconds seconds
        | _ -> TimeSpan.FromSeconds 30.0

    let private readConfig (root: string) : UpstreamSyncConfig =
        let fallback = UpstreamSync.defaultConfig
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            fallback
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)

                let sync =
                    match document.RootElement.TryGetProperty "workProtocol" with
                    | true, protocol when protocol.ValueKind = JsonValueKind.Object ->
                        match protocol.TryGetProperty "upstreamSync" with
                        | true, value when value.ValueKind = JsonValueKind.Object -> Some value
                        | _ -> None
                    | _ -> None

                match sync with
                | None -> fallback
                | Some value ->
                    let enabled =
                        match value.TryGetProperty "enabled" with
                        | true, field when field.ValueKind = JsonValueKind.False -> false
                        | _ -> true

                    let text (name: string) (fallbackValue: string) =
                        match value.TryGetProperty name with
                        | true, field when field.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(field.GetString())) ->
                            field.GetString()
                        | _ -> fallbackValue

                    let maxAge =
                        match value.TryGetProperty "maxAgeMinutes" with
                        | true, field when field.ValueKind = JsonValueKind.Number ->
                            match field.TryGetDouble() with
                            | true, minutes when minutes > 0.0 -> TimeSpan.FromMinutes minutes
                            | _ -> fallback.MaxAge
                        | _ -> fallback.MaxAge

                    { Enabled = enabled
                      Remote = text "remote" fallback.Remote
                      Branch = text "branch" fallback.Branch
                      MaxAge = maxAge }
            with _ ->
                fallback

    let private runText executable root operation arguments =
        match runGit executable root operation None arguments with
        | Error failure -> Error failure
        | Ok result when result.ExitCode <> 0 -> Error(failureOf operation result)
        | Ok result -> Ok(result.Output.Trim())

    let private statePath executable root =
        runText executable root "git rev-parse --git-path" [ "rev-parse"; "--git-path"; "praxis/upstream-sync.json" ]
        |> Result.map (fun path -> if Path.IsPathRooted path then path else Path.GetFullPath(Path.Combine(root, path)))

    let private parseTimestamp (node: JsonNode) =
        if isNull node then
            None
        else
            match DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, value -> Some value
            | _ -> None

    let private parseCommit (node: JsonNode) =
        if isNull node then None else CommitId.tryParse (node.GetValue<string>())

    let private storedFailure (node: JsonNode) =
        if isNull node then
            None
        else
            let obj = node.AsObject()

            let reason =
                match obj["reason"].GetValue<string>() with
                | "tool-unavailable" -> GitUnavailableReason.ToolUnavailable
                | "not-repository" -> GitUnavailableReason.NotRepository
                | "malformed-output" -> GitUnavailableReason.MalformedOutput
                | _ -> GitUnavailableReason.CommandFailed

            Some
                { Operation = obj["operation"].GetValue<string>()
                  Reason = reason
                  Message = "the previous upstream sync attempt failed"
                  ExitCode = if isNull obj["exitCode"] then None else Some(obj["exitCode"].GetValue<int>()) }

    let private readState (path: string) : Result<StoredSyncState option, GitFailure> =
        if not (File.Exists path) then
            Ok None
        else
            try
                let node = JsonNode.Parse(File.ReadAllText path).AsObject()
                let attempt =
                    match node["lastAttemptOutcome"] with
                    | null -> UpstreamSyncAttempt.Never
                    | value when value.GetValue<string>() = "succeeded" -> UpstreamSyncAttempt.Succeeded
                    | _ -> storedFailure node["lastFailure"] |> Option.map UpstreamSyncAttempt.Failed |> Option.defaultValue UpstreamSyncAttempt.Never

                Ok(
                    Some
                        { Remote = node["remote"].GetValue<string>()
                          Branch = node["branch"].GetValue<string>()
                          StartingUpstreamCommit = parseCommit node["startingUpstreamCommit"]
                          LastAttemptAt = parseTimestamp node["lastAttemptAt"]
                          LastAttempt = attempt
                          LastSuccessfulCheckAt = parseTimestamp node["lastSuccessfulCheckAt"] }
                )
            with error ->
                Error(unavailable "read upstream sync state" GitUnavailableReason.MalformedOutput error.Message None)

    let private addOptionalString (node: JsonObject) (name: string) (value: CommitId option) =
        node[name] <- value |> Option.map (fun commit -> JsonValue.Create(CommitId.value commit)) |> Option.defaultValue null

    let private addOptionalTimestamp (node: JsonObject) (name: string) (value: DateTimeOffset option) =
        node[name] <-
            value
            |> Option.map (fun timestamp -> JsonValue.Create(timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
            |> Option.defaultValue null

    let private failureNode (failure: GitFailure) =
        let node = JsonObject()
        node["operation"] <- JsonValue.Create failure.Operation
        node["reason"] <- JsonValue.Create(GitUnavailableReason.code failure.Reason)
        node["exitCode"] <- failure.ExitCode |> Option.map (fun value -> JsonValue.Create value) |> Option.defaultValue null
        node

    let private writeState (path: string) (state: StoredSyncState) : Result<unit, GitFailure> =
        try
            let node = JsonObject()
            node["schemaVersion"] <- JsonValue.Create 1
            node["remote"] <- JsonValue.Create state.Remote
            node["branch"] <- JsonValue.Create state.Branch
            addOptionalString node "startingUpstreamCommit" state.StartingUpstreamCommit
            addOptionalTimestamp node "lastAttemptAt" state.LastAttemptAt
            addOptionalTimestamp node "lastSuccessfulCheckAt" state.LastSuccessfulCheckAt

            match state.LastAttempt with
            | UpstreamSyncAttempt.Never ->
                node["lastAttemptOutcome"] <- null
                node["lastFailure"] <- null
            | UpstreamSyncAttempt.Succeeded ->
                node["lastAttemptOutcome"] <- JsonValue.Create "succeeded"
                node["lastFailure"] <- null
            | UpstreamSyncAttempt.Failed failure ->
                node["lastAttemptOutcome"] <- JsonValue.Create "failed"
                node["lastFailure"] <- failureNode failure

            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let temporary = path + $".{Guid.NewGuid():N}.tmp"
            File.WriteAllText(temporary, node.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
            File.Move(temporary, path, true)
            Ok()
        with error ->
            Error(unavailable "write upstream sync state" GitUnavailableReason.CommandFailed error.Message None)

    let private readCommit executable root operation reference =
        runText executable root operation [ "rev-parse"; "--verify"; $"{reference}^{{commit}}" ]
        |> Result.bind (fun value ->
            match CommitId.tryParse value with
            | Some commit -> Ok commit
            | None -> Error(unavailable operation GitUnavailableReason.MalformedOutput $"'{value}' is not a full commit ID" None))

    let private readPaths executable root left right =
        match runGit executable root "git diff" None [ "diff"; "--name-only"; "-z"; "--no-renames"; "--no-ext-diff"; left; right; "--" ] with
        | Error failure -> Error failure
        | Ok result when result.ExitCode <> 0 -> Error(failureOf "git diff" result)
        | Ok result -> Ok(GitDurabilityParser.parseNameList result.Output)

    let private readDirtyPaths executable root =
        match runGit executable root "git status" None [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=all" ] with
        | Error failure -> Error failure
        | Ok result when result.ExitCode <> 0 -> Error(failureOf "git status" result)
        | Ok result ->
            GitStatusParser.parse result.Output
            |> Result.map (List.collect GitChangePaths.touchedPaths)

    let private sortPaths (paths: string list) =
        paths |> List.distinct |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

    let private emptyReport (config: UpstreamSyncConfig) availability (state: StoredSyncState option) =
        let elapsed, dueAt, stale = UpstreamSync.freshness DateTimeOffset.UtcNow config.MaxAge (state |> Option.bind _.LastSuccessfulCheckAt)

        { Config = config
          Availability = availability
          LastAttemptAt = state |> Option.bind _.LastAttemptAt
          LastAttempt = state |> Option.map _.LastAttempt |> Option.defaultValue UpstreamSyncAttempt.Never
          LastSuccessfulCheckAt = state |> Option.bind _.LastSuccessfulCheckAt
          ElapsedSinceSuccessfulCheck = elapsed
          DueAt = dueAt
          Stale = stale
          HeadCommit = None
          StartingUpstreamCommit = state |> Option.bind _.StartingUpstreamCommit
          CurrentUpstreamCommit = None
          UpstreamChangedSinceStart = None
          Ahead = None
          Behind = None
          IncomingUpstreamPaths = []
          UpstreamPathsSinceStart = []
          LocalChangedPaths = []
          OverlapPaths = []
          IntegrationRequired = false
          SafeForFinalValidation = false }

    let private reportAt executable root now (config: UpstreamSyncConfig) (state: StoredSyncState option) stateFailure =
        let elapsed, dueAt, stale = UpstreamSync.freshness now config.MaxAge (state |> Option.bind _.LastSuccessfulCheckAt)
        let baseReport =
            { (emptyReport config UpstreamSyncAvailability.Available state) with
                ElapsedSinceSuccessfulCheck = elapsed
                DueAt = dueAt
                Stale = stale }

        if not config.Enabled then
            { baseReport with Availability = UpstreamSyncAvailability.Disabled; Stale = false }
        else
            match stateFailure with
            | Some failure -> { baseReport with Availability = UpstreamSyncAvailability.Unavailable failure }
            | None ->
                let upstreamRef = $"refs/remotes/{config.Remote}/{config.Branch}"

                readCommit executable root "git rev-parse HEAD" "HEAD"
                |> Result.bind (fun head ->
                    readCommit executable root "git rev-parse upstream" upstreamRef
                    |> Result.bind (fun upstream ->
                        runText executable root "git merge-base" [ "merge-base"; head.Value; upstream.Value ]
                        |> Result.bind (fun mergeBase ->
                            match CommitId.tryParse mergeBase with
                            | None -> Error(unavailable "git merge-base" GitUnavailableReason.MalformedOutput $"'{mergeBase}' is not a full commit ID" None)
                            | Some merge ->
                                match runGit executable root "git rev-list" None [ "rev-list"; "--left-right"; "--count"; $"{head.Value}...{upstream.Value}" ] with
                                | Error failure -> Error failure
                                | Ok relation when relation.ExitCode <> 0 -> Error(failureOf "git rev-list" relation)
                                | Ok relation ->
                                    GitDurabilityParser.parseLeftRightCount relation.Output
                                    |> Result.bind (fun parsedRelation ->
                                        readPaths executable root merge.Value upstream.Value
                                        |> Result.bind (fun incoming ->
                                            readPaths executable root merge.Value head.Value
                                            |> Result.bind (fun committedLocal ->
                                                readDirtyPaths executable root
                                                |> Result.bind (fun dirty ->
                                                    let sinceStart =
                                                        match state |> Option.bind _.StartingUpstreamCommit with
                                                        | None -> Ok []
                                                        | Some starting when starting = upstream -> Ok []
                                                        | Some starting -> readPaths executable root starting.Value upstream.Value

                                                    sinceStart
                                                    |> Result.map (fun upstreamSinceStart ->
                                                        let ahead, behind =
                                                            match parsedRelation with
                                                            | CommitRelation.Same -> 0, 0
                                                            | CommitRelation.Ahead count -> count, 0
                                                            | CommitRelation.Behind count -> 0, count
                                                            | CommitRelation.Diverged(ahead, behind) -> ahead, behind

                                                        let local = sortPaths (committedLocal @ dirty)
                                                        let incomingPaths = sortPaths incoming
                                                        let incomingSet = Set.ofList incomingPaths
                                                        let overlap = local |> List.filter incomingSet.Contains
                                                        let successfulAttempt =
                                                            match state |> Option.map _.LastAttempt with
                                                            | Some UpstreamSyncAttempt.Succeeded -> true
                                                            | _ -> false

                                                        { baseReport with
                                                            HeadCommit = Some head
                                                            CurrentUpstreamCommit = Some upstream
                                                            UpstreamChangedSinceStart = state |> Option.bind _.StartingUpstreamCommit |> Option.map ((<>) upstream)
                                                            Ahead = Some ahead
                                                            Behind = Some behind
                                                            IncomingUpstreamPaths = incomingPaths
                                                            UpstreamPathsSinceStart = sortPaths upstreamSinceStart
                                                            LocalChangedPaths = local
                                                            OverlapPaths = overlap
                                                            IntegrationRequired = behind > 0
                                                            SafeForFinalValidation = successfulAttempt && not stale && behind = 0 }))))))))
                |> function
                    | Ok report -> report
                    | Error failure -> { baseReport with Availability = UpstreamSyncAvailability.Unavailable failure }

    let private stateForConfig (config: UpstreamSyncConfig) (state: StoredSyncState option) =
        state |> Option.filter (fun saved -> saved.Remote = config.Remote && saved.Branch = config.Branch)

    let statusAtWithExecutable executable root now =
        let root = Path.GetFullPath root
        let config = readConfig root

        if not config.Enabled then
            reportAt executable root now config None None
        else
            match statePath executable root with
            | Error failure -> reportAt executable root now config None (Some failure)
            | Ok path ->
                match readState path with
                | Error failure -> reportAt executable root now config None (Some failure)
                | Ok state -> reportAt executable root now config (stateForConfig config state) None

    let statusAt root now = statusAtWithExecutable "git" root now
    let status root = statusAt root DateTimeOffset.UtcNow

    /// Fetches only the configured branch and records a successful check
    /// only after its remote-tracking ref resolves. `start=true` resets the
    /// session baseline; a routine check preserves it.
    let checkAtWithExecutable executable root now start =
        let root = Path.GetFullPath root
        let config = readConfig root

        if not config.Enabled then
            reportAt executable root now config None None, true
        else
            match statePath executable root with
            | Error failure -> reportAt executable root now config None (Some failure), false
            | Ok path ->
                match readState path with
                | Error failure -> reportAt executable root now config None (Some failure), false
                | Ok existing ->
                    let previous = stateForConfig config existing
                    let refspec = $"+refs/heads/{config.Branch}:refs/remotes/{config.Remote}/{config.Branch}"
                    let fetch = runGit executable root "git fetch" (Some(timeout ())) [ "fetch"; "--quiet"; "--prune"; "--"; config.Remote; refspec ]

                    let attemptedState, attemptSucceeded =
                        match fetch with
                        | Error failure ->
                            { Remote = config.Remote
                              Branch = config.Branch
                              StartingUpstreamCommit = previous |> Option.bind _.StartingUpstreamCommit
                              LastAttemptAt = Some now
                              LastAttempt = UpstreamSyncAttempt.Failed failure
                              LastSuccessfulCheckAt = previous |> Option.bind _.LastSuccessfulCheckAt }, false
                        | Ok result when result.ExitCode <> 0 ->
                            let failure = failureOf "git fetch" result
                            { Remote = config.Remote
                              Branch = config.Branch
                              StartingUpstreamCommit = previous |> Option.bind _.StartingUpstreamCommit
                              LastAttemptAt = Some now
                              LastAttempt = UpstreamSyncAttempt.Failed failure
                              LastSuccessfulCheckAt = previous |> Option.bind _.LastSuccessfulCheckAt }, false
                        | Ok _ ->
                            match readCommit executable root "git rev-parse upstream" $"refs/remotes/{config.Remote}/{config.Branch}" with
                            | Error failure ->
                                { Remote = config.Remote
                                  Branch = config.Branch
                                  StartingUpstreamCommit = previous |> Option.bind _.StartingUpstreamCommit
                                  LastAttemptAt = Some now
                                  LastAttempt = UpstreamSyncAttempt.Failed failure
                                  LastSuccessfulCheckAt = previous |> Option.bind _.LastSuccessfulCheckAt }, false
                            | Ok current ->
                                { Remote = config.Remote
                                  Branch = config.Branch
                                  StartingUpstreamCommit =
                                    if start then Some current
                                    else previous |> Option.bind _.StartingUpstreamCommit |> Option.orElse (Some current)
                                  LastAttemptAt = Some now
                                  LastAttempt = UpstreamSyncAttempt.Succeeded
                                  LastSuccessfulCheckAt = Some now }, true

                    match writeState path attemptedState with
                    | Ok() -> reportAt executable root now config (Some attemptedState) None, attemptSucceeded
                    | Error failure -> reportAt executable root now config (Some attemptedState) (Some failure), false

    let checkAt root now start = checkAtWithExecutable "git" root now start
    let check root start = checkAt root DateTimeOffset.UtcNow start
