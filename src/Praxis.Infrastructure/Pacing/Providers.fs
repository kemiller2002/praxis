namespace Praxis.Infrastructure.Pacing

open System
open System.Diagnostics
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

/// One line read from a provider process's protocol stream.
[<RequireQualifiedAccess>]
type ProtocolLine =
    | Line of string
    /// The stream ended (EOF) before the exchange completed.
    | Closed
    | TimedOut

/// A line-oriented request/response channel to a provider process. The real
/// channel wraps `codex app-server --stdio`; tests script one.
type ProtocolChannel =
    { Send: string -> unit
      ReadLine: TimeSpan -> ProtocolLine }

[<RequireQualifiedAccess>]
module PacingProviders =
    let private usageApi = "https://api.anthropic.com/api/oauth/usage"

    /// A provider error's diagnostic: its message or code, never the raw
    /// response payload.
    let private errorSummary (error: JsonElement) =
        match error.ValueKind with
        | JsonValueKind.Object ->
            PacingJson.tryProperty "message" error
            |> Option.bind PacingJson.tryString
            |> Option.orElse (PacingJson.tryProperty "code" error |> Option.map (fun code -> code.ToString()))
            |> Option.defaultValue "error without a message"
        | JsonValueKind.String -> error.GetString() |> Option.ofObj |> Option.defaultValue "error"
        | _ -> "error without a message"

    let private processCapture
        (fileName: string)
        (arguments: string list)
        (timeout: TimeSpan)
        : Result<string, string> =
        try
            let start = ProcessStartInfo()
            start.FileName <- fileName
            start.UseShellExecute <- false
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            start.CreateNoWindow <- true

            for argument in arguments do
                start.ArgumentList.Add argument

            use proc = new Process()
            proc.StartInfo <- start

            if not (proc.Start()) then
                Error $"{fileName} did not start"
            else
                let output = proc.StandardOutput.ReadToEndAsync()
                let error = proc.StandardError.ReadToEndAsync()

                if proc.WaitForExit(int timeout.TotalMilliseconds) then
                    let stdout = output.GetAwaiter().GetResult()
                    let stderr = error.GetAwaiter().GetResult()

                    if proc.ExitCode = 0 then
                        Ok stdout
                    else
                        Error $"{fileName} exited {proc.ExitCode}: {stderr.Trim()}"
                else
                    try
                        proc.Kill(true)
                    with
                    | :? InvalidOperationException -> ()
                    | :? ComponentModel.Win32Exception -> ()

                    Error $"{fileName} timed out"
        with
        | :? ComponentModel.Win32Exception as error -> Error $"{fileName} unavailable: {error.Message}"
        | :? InvalidOperationException as error -> Error $"{fileName} unavailable: {error.Message}"

    /// The Codex `account/rateLimits/read` exchange over a protocol channel:
    /// initialize, initialized, then the quota query. Timeout, EOF, an error
    /// reply and malformed JSON are explicit errors, never a reading.
    let converseCodex (channel: ProtocolChannel) (now: unit -> DateTimeOffset) (timeout: TimeSpan) : Result<string, string> =
        let deadline = now () + timeout
        channel.Send """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"praxis_pacing","version":"1.0.0"}}}"""

        let rec loop () =
            let remaining = deadline - now ()

            if remaining <= TimeSpan.Zero then
                Error "Codex quota query timed out"
            else
                match channel.ReadLine remaining with
                | ProtocolLine.TimedOut -> Error "Codex quota query timed out"
                | ProtocolLine.Closed -> Error "Codex app-server closed before returning quota"
                | ProtocolLine.Line line when String.IsNullOrWhiteSpace line -> loop ()
                | ProtocolLine.Line line ->
                    let parsed =
                        try
                            Ok(JsonDocument.Parse line)
                        with :? JsonException as error ->
                            Error $"Codex app-server returned malformed JSON: {error.Message}"

                    match parsed with
                    | Error message -> Error message
                    | Ok document ->
                        use document = document
                        let root = document.RootElement

                        match PacingJson.tryProperty "id" root |> Option.bind PacingJson.tryInt with
                        | Some 1 ->
                            match PacingJson.tryProperty "error" root with
                            | Some error -> Error $"Codex initialize failed: {errorSummary error}"
                            | None ->
                                channel.Send """{"method":"initialized","params":{}}"""
                                channel.Send """{"id":2,"method":"account/rateLimits/read"}"""
                                loop ()
                        | Some 2 ->
                            match PacingJson.tryProperty "error" root, PacingJson.tryProperty "result" root with
                            | Some error, _ -> Error $"Codex quota query failed: {errorSummary error}"
                            | None, Some value -> Ok(value.GetRawText())
                            | _ -> Error "Codex quota query returned no result"
                        | _ -> loop ()

        loop ()

    let queryCodex () : Result<string, string> =
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

            use proc = new Process()
            proc.StartInfo <- start

            if not (proc.Start()) then
                Error "codex app-server did not start"
            else
                let channel =
                    { Send =
                        fun text ->
                            proc.StandardInput.WriteLine text
                            proc.StandardInput.Flush()
                      ReadLine =
                        fun remaining ->
                            try
                                match proc.StandardOutput.ReadLineAsync().WaitAsync(remaining).GetAwaiter().GetResult() with
                                | null -> ProtocolLine.Closed
                                | line -> ProtocolLine.Line line
                            with :? TimeoutException ->
                                ProtocolLine.TimedOut }

                let result = converseCodex channel (fun () -> DateTimeOffset.UtcNow) (TimeSpan.FromSeconds 20.0)

                try
                    proc.StandardInput.Close()

                    if not proc.HasExited then
                        proc.Kill(true)
                with
                | :? InvalidOperationException -> ()
                | :? IO.IOException -> ()
                | :? ComponentModel.Win32Exception -> ()

                result
        with
        | :? ComponentModel.Win32Exception as error -> Error $"Codex app-server unavailable: {error.Message}"
        | :? InvalidOperationException as error -> Error $"Codex app-server unavailable: {error.Message}"
        | :? IO.IOException as error -> Error $"Codex app-server unavailable: {error.Message}"

    /// Decodes the Claude Code Keychain entry into an unexpired OAuth access
    /// token. An unavailable Keychain, a missing entry, an expired or
    /// malformed credential are explicit errors. Pure.
    let decodeClaudeCredential (now: DateTimeOffset) (keychain: Result<string, string>) : Result<string, string> =
        match keychain with
        | Error message -> Error $"Claude Code credentials are unavailable: {message}"
        | Ok output ->
            try
                use document = JsonDocument.Parse output
                let root = document.RootElement

                match PacingJson.tryProperty "claudeAiOauth" root with
                | None -> Error "Claude Code OAuth credentials were not found in Keychain"
                | Some oauth ->
                    let token =
                        PacingJson.tryProperty "accessToken" oauth
                        |> Option.bind PacingJson.tryString
                        |> Option.filter (fun value -> value <> "")

                    let expires =
                        PacingJson.tryProperty "expiresAt" oauth
                        |> Option.bind PacingJson.tryDecimal
                        |> Option.bind (fun value ->
                            try
                                Some(DateTimeOffset.FromUnixTimeMilliseconds(int64 value))
                            with :? ArgumentOutOfRangeException ->
                                None)

                    match token, expires with
                    | Some value, Some expiry when expiry > now + TimeSpan.FromMinutes 1.0 -> Ok value
                    | Some _, Some _ -> Error "Claude Code OAuth credentials are expired"
                    | _ -> Error "Claude Code OAuth credentials are missing an access token or expiry"
            with :? JsonException ->
                // The credential text is never echoed into a diagnostic.
                Error "Claude Code credentials could not be parsed"

    let private readKeychain () : Result<string, string> =
        if not (OperatingSystem.IsMacOS()) then
            Error "Claude pacing requires macOS Keychain access"
        else
            processCapture "security" [ "find-generic-password"; "-s"; "Claude Code-credentials"; "-w" ] (TimeSpan.FromSeconds 3.0)

    /// The Claude usage request through a supplied HTTP handler. Redirects
    /// are refused (the bearer token is never forwarded); any non-success
    /// status is an explicit error.
    let queryClaudeUsage (handler: HttpMessageHandler) (token: string) : Result<string, string> =
        try
            use client = new HttpClient(handler, false)
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
        with
        | :? HttpRequestException as error -> Error $"Claude usage query failed: {error.Message}"
        | :? OperationCanceledException -> Error "Claude usage query timed out"

    /// The Claude query composed from its seams: a credential source and an
    /// HTTP handler factory. Tests supply both; production uses the Keychain
    /// and a redirect-refusing `HttpClientHandler`.
    let queryClaudeWith (keychain: unit -> Result<string, string>) (handler: unit -> HttpMessageHandler) (now: DateTimeOffset) : Result<string, string> =
        decodeClaudeCredential now (keychain ())
        |> Result.bind (fun token ->
            use handler = handler ()
            queryClaudeUsage handler token)

    let private redirectRefusingHandler () : HttpMessageHandler =
        let handler = new HttpClientHandler()
        handler.AllowAutoRedirect <- false
        handler

    let queryClaude (now: DateTimeOffset) : Result<string, string> =
        queryClaudeWith readKeychain redirectRefusingHandler now
