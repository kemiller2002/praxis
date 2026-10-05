namespace Praxis.Infrastructure.Pacing

open System
open System.Diagnostics
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

[<RequireQualifiedAccess>]
module PacingProviders =
    let private usageApi = "https://api.anthropic.com/api/oauth/usage"

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
                    with _ ->
                        ()

                    Error $"{fileName} timed out"
        with error ->
            Error $"{fileName} unavailable: {error.Message}"

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
                let send (text: string) =
                    proc.StandardInput.WriteLine text
                    proc.StandardInput.Flush()

                let deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds 20.0
                send """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"praxis_pacing","version":"1.0.0"}}}"""

                let mutable result: Result<string, string> option = None

                while result.IsNone && DateTimeOffset.UtcNow < deadline do
                    let remaining = deadline - DateTimeOffset.UtcNow

                    try
                        let line =
                            proc.StandardOutput.ReadLineAsync()
                                .WaitAsync(if remaining > TimeSpan.Zero then remaining else TimeSpan.FromMilliseconds 1.0)
                                .GetAwaiter()
                                .GetResult()

                        if isNull line then
                            result <- Some(Error "Codex app-server closed before returning quota")
                        else
                            use document = JsonDocument.Parse line
                            let root = document.RootElement

                            match PacingJson.tryProperty "id" root |> Option.bind PacingJson.tryInt with
                            | Some 1 ->
                                match PacingJson.tryProperty "error" root with
                                | Some error ->
                                    result <- Some(Error $"Codex initialize failed: {error.GetRawText()}")
                                | None ->
                                    send """{"method":"initialized","params":{}}"""
                                    send """{"id":2,"method":"account/rateLimits/read"}"""
                            | Some 2 ->
                                match PacingJson.tryProperty "error" root, PacingJson.tryProperty "result" root with
                                | Some error, _ ->
                                    result <- Some(Error $"Codex quota query failed: {error.GetRawText()}")
                                | None, Some value ->
                                    result <- Some(Ok(value.GetRawText()))
                                | _ ->
                                    result <- Some(Error "Codex quota query returned no result")
                            | _ -> ()
                    with :? TimeoutException ->
                        result <- Some(Error "Codex quota query timed out")

                try
                    proc.StandardInput.Close()

                    if not proc.HasExited then
                        proc.Kill(true)
                with _ ->
                    ()

                result |> Option.defaultValue (Error "Codex quota query timed out")
        with error ->
            Error $"Codex app-server unavailable: {error.Message}"

    let private claudeToken (now: DateTimeOffset) : Result<string, string> =
        if not (OperatingSystem.IsMacOS()) then
            Error "Claude pacing requires macOS Keychain access"
        else
            match
                processCapture
                    "security"
                    [ "find-generic-password"; "-s"; "Claude Code-credentials"; "-w" ]
                    (TimeSpan.FromSeconds 3.0)
            with
            | Error message -> Error message
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

                        let expires =
                            PacingJson.tryProperty "expiresAt" oauth
                            |> Option.bind PacingJson.tryDecimal
                            |> Option.map (fun value -> DateTimeOffset.FromUnixTimeMilliseconds(int64 value))

                        match token, expires with
                        | Some value, Some expiry when expiry > now + TimeSpan.FromMinutes 1.0 -> Ok value
                        | _ -> Error "Claude Code OAuth credentials are missing or expired"
                with error ->
                    Error $"Claude Code credentials could not be parsed: {error.Message}"

    let queryClaude (now: DateTimeOffset) : Result<string, string> =
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
