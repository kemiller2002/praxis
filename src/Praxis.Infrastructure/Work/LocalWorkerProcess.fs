namespace Praxis.Infrastructure.Work

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Praxis.Application.Work
open Praxis.Domain.Work

/// A bounded process mechanism, NOT an isolation or authority adapter.
/// No CLI/runtime route calls this module. A qualifying controller must add
/// actual sandbox/network/credential protection and journal intent first.
[<RequireQualifiedAccess>]
module LocalWorkerProcess =
    let private errorText (value: string) = if value.Length > 1024 then value.Substring(0, 1024) else value
    let private safeText limit (value: string) = not (isNull value) && value.Length <= limit && not (value.Contains '\u0000')
    let private preflight (spec: LocalWorkerProcessSpec) =
        if not (Path.IsPathFullyQualified spec.Executable && File.Exists spec.Executable)
           || not (Path.IsPathFullyQualified spec.WorkingDirectory && Directory.Exists spec.WorkingDirectory) then Error "host executable and worktree must be existing absolute paths"
        elif not (LocalAgentHandoff.isDigest spec.ExecutableDigest) then Error "invalid host executable pin"
        elif spec.Arguments.Length > 64 || not (spec.Arguments |> List.forall (safeText 4096)) then Error "invalid bounded host argv"
        elif spec.Environment.Count > 32 || spec.Environment |> Map.exists (fun key value ->
            String.IsNullOrWhiteSpace key || key.Length > 128 || key.Contains '=' || not (safeText 128 key && safeText 4096 value)) then Error "invalid explicit environment"
        elif spec.TimeoutMilliseconds <= 0 || spec.TimeoutMilliseconds > 86400000 || spec.MaxOutputBytes <= 0 || spec.MaxOutputBytes > 67108864 then Error "invalid worker budget"
        elif isNull spec.Input || Encoding.UTF8.GetByteCount spec.Input > 65536 then Error "oversized worker input"
        else
            use executable = new FileStream(spec.Executable, FileMode.Open, FileAccess.Read, FileShare.Read)
            let actual = "sha256:" + (SHA256.HashData executable |> Convert.ToHexString).ToLowerInvariant()
            if actual <> spec.ExecutableDigest then Error "host executable changed before start" else Ok()

    /// Explicit observer dependency permits mechanism fixtures; it does not confer host trust.
    let runWithOutputObservation (observeIdentity: Process -> Result<string, string>) onOutput spec onStarted (cancellation: CancellationToken) = task {
        let rejected outcome started =
            { ProcessIdentity = None; ProcessStarted = started; RootExitObserved = false; Outcome = outcome
              StandardOutput = ""; StandardError = ""; CapturedBytes = 0 }
        let checkedSpec = try preflight spec with e -> Error(errorText e.Message)
        match checkedSpec with
        | Error reason -> return rejected (LocalWorkerProcessOutcome.PreflightRefused reason) (Some false)
        | Ok() when cancellation.IsCancellationRequested -> return rejected LocalWorkerProcessOutcome.Cancelled (Some false)
        | Ok() ->
            use child = new Process()
            let info = ProcessStartInfo(spec.Executable, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = spec.WorkingDirectory)
            spec.Arguments |> List.iter info.ArgumentList.Add
            info.Environment.Clear()
            spec.Environment |> Map.iter (fun key value -> info.Environment[key] <- value)
            child.StartInfo <- info
            let start = try if child.Start() then Ok() else Error "process start returned no handle" with e -> Error(errorText e.Message)
            match start with
            | Error reason -> return rejected (LocalWorkerProcessOutcome.StartUncertain reason) None
            | Ok() ->
                use lifetime = CancellationTokenSource.CreateLinkedTokenSource cancellation
                lifetime.CancelAfter spec.TimeoutMilliseconds
                use stdout = new MemoryStream()
                use stderr = new MemoryStream()
                let gate = obj()
                let mutable captured = 0
                let mutable limitExceeded = false
                let mutable streamError = None
                let pump stream (source: Stream) (sink: MemoryStream) = task {
                    let buffer = Array.zeroCreate<byte> 4096
                    let mutable reading = true
                    try
                        while reading do
                            let! count = source.ReadAsync(buffer.AsMemory(), lifetime.Token)
                            if count = 0 then reading <- false
                            else
                                lock gate (fun () ->
                                    let retained = min count (spec.MaxOutputBytes - captured)
                                    let persisted =
                                        try onOutput stream (buffer.AsSpan(0, retained).ToArray())
                                        with e -> Error(errorText e.Message)
                                    match persisted with
                                    | Ok() -> sink.Write(buffer, 0, retained); captured <- captured + retained
                                    | Error reason -> streamError <- Some reason; lifetime.Cancel()
                                    if retained < count then limitExceeded <- true; lifetime.Cancel())
                    with
                    | :? OperationCanceledException -> ()
                    | e -> lock gate (fun () -> streamError <- Some(errorText e.Message); lifetime.Cancel()) }
                let outPump = pump LocalWorkerOutputStream.StandardOutput child.StandardOutput.BaseStream stdout
                let errPump = pump LocalWorkerOutputStream.StandardError child.StandardError.BaseStream stderr
                let identity =
                    try
                        observeIdentity child |> Result.bind (fun value ->
                            if String.IsNullOrWhiteSpace value || not (safeText 1024 value) || Seq.exists Char.IsControl value then Error "invalid observed process incarnation"
                            else Ok value)
                    with e -> Error(errorText e.Message)
                let recorded = identity |> Result.bind (fun id -> try onStarted id with e -> Error(errorText e.Message))
                let writeInput = task {
                    try
                        match recorded with
                        | Error _ -> ()
                        | Ok() ->
                            let bytes = Encoding.UTF8.GetBytes spec.Input
                            do! child.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(), lifetime.Token)
                            child.StandardInput.Close()
                    with
                    | :? OperationCanceledException -> ()
                    | e -> lock gate (fun () -> streamError <- Some(errorText e.Message); lifetime.Cancel()) }
                let mutable outcome =
                    match recorded with Error reason -> LocalWorkerProcessOutcome.ObservationFailed reason | Ok() -> LocalWorkerProcessOutcome.Exited 0
                try
                    match recorded with
                    | Error _ -> lifetime.Cancel()
                    | Ok() ->
                        do! child.WaitForExitAsync(lifetime.Token)
                        outcome <- LocalWorkerProcessOutcome.Exited child.ExitCode
                with
                | :? OperationCanceledException ->
                    outcome <-
                        if limitExceeded then LocalWorkerProcessOutcome.OutputLimitExceeded
                        elif cancellation.IsCancellationRequested then LocalWorkerProcessOutcome.Cancelled
                        elif streamError.IsSome then LocalWorkerProcessOutcome.StreamFailed streamError.Value
                        else LocalWorkerProcessOutcome.TimedOut
                | e -> outcome <- LocalWorkerProcessOutcome.ObservationFailed("process wait failed: " + errorText e.Message)
                let mutable rootExit = false
                try
                    if not child.HasExited then child.Kill(true)
                    use cleanup = new CancellationTokenSource(5000)
                    do! child.WaitForExitAsync(cleanup.Token)
                    rootExit <- true
                with e -> outcome <- LocalWorkerProcessOutcome.ObservationFailed("root exit unconfirmed: " + errorText e.Message)
                // Exit does not guarantee descendants closed inherited pipes.
                // Cancellation bounds stream/input waits independently of EOF.
                let streams = Task.WhenAll [| outPump; errPump; writeInput |]
                if (match outcome with LocalWorkerProcessOutcome.Exited _ -> true | _ -> false) then
                    let! completed = Task.WhenAny(streams :> Task, Task.Delay(500))
                    if not (Object.ReferenceEquals(completed, streams)) then
                        outcome <- LocalWorkerProcessOutcome.ObservationFailed "worker streams did not close after root exit"
                lifetime.Cancel()
                let! _ = streams
                if limitExceeded && rootExit && Result.isOk recorded then outcome <- LocalWorkerProcessOutcome.OutputLimitExceeded
                elif streamError.IsSome && (match outcome with LocalWorkerProcessOutcome.Exited _ -> true | _ -> false) then outcome <- LocalWorkerProcessOutcome.StreamFailed streamError.Value
                let decode (bytes: byte array) =
                    try UTF8Encoding(false, true).GetString bytes
                    with :? DecoderFallbackException ->
                        match outcome with
                        | LocalWorkerProcessOutcome.Exited _ -> outcome <- LocalWorkerProcessOutcome.StreamFailed "worker output is not UTF-8"
                        | _ -> ()
                        Encoding.UTF8.GetString bytes
                let output, error = decode (stdout.ToArray()), decode (stderr.ToArray())
                return { ProcessIdentity = identity |> Result.toOption; ProcessStarted = Some true; RootExitObserved = rootExit
                         Outcome = outcome; StandardOutput = output; StandardError = error; CapturedBytes = captured } }

    let private observeIdentity (child: Process) =
            try Ok(sprintf "pid:%d/start-utc-ticks:%d" child.Id (child.StartTime.ToUniversalTime().Ticks))
            with e -> Error(sprintf "process incarnation unavailable (pid:%d): %s" child.Id (errorText e.Message))

    let runWithIdentityObservation observer spec onStarted cancellation =
        runWithOutputObservation observer (fun _ _ -> Ok()) spec onStarted cancellation

    let run spec onStarted cancellation = runWithIdentityObservation observeIdentity spec onStarted cancellation

    let createStreaming () : LocalWorkerStreamingProcessPort =
        { RunStreaming = fun spec onStarted onOutput cancellation -> runWithOutputObservation observeIdentity onOutput spec onStarted cancellation }

    let create () : LocalWorkerProcessPort = { Run = run }
