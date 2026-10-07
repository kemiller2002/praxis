namespace Ros.Infrastructure.Artifacts

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open Ros.Application.Artifacts

type RegistryLockSettings =
    { RetryDelay: TimeSpan
      Timeout: TimeSpan
      StaleAfter: TimeSpan }

type RegistryLockLease =
    { Release: unit -> Result<unit, DependencyFailure> }

[<RequireQualifiedAccess>]
module RegistryLock =
    let defaultSettings =
        { RetryDelay = TimeSpan.FromMilliseconds 20.0
          Timeout = TimeSpan.FromSeconds 10.0
          StaleAfter = TimeSpan.FromMinutes 1.0 }

    let private lockFailure operation outcome message =
        { Operation = operation
          Path = None
          Message = message
          Outcome = outcome }

    let lockPath (root: string) (resource: string) =
        let bytes: byte array = Encoding.UTF8.GetBytes resource
        let hash: string = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
        Path.Combine(root, ".ros", "locks", $"{hash}.lock")

    let private ownerToken (text: string) =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            let token =
                match root.TryGetProperty "ownerToken" with
                | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                | _ -> null

            let processId =
                match root.TryGetProperty "pid" with
                | true, value when value.TryGetInt32() |> fst -> value.GetInt32() |> Some
                | _ -> None

            token, processId
        with _ ->
            null, None

    let private processIsAlive processId =
        match processId with
        | Some value when value > 0 ->
            try
                use runningProcess = Process.GetProcessById value
                Some true
            with
            | :? ArgumentException -> Some false
            | :? UnauthorizedAccessException -> Some true
            | _ -> None
        | _ -> None

    let private removeAbandoned file staleAfter =
        try
            let info = FileInfo file
            let observed = File.ReadAllText(file, Encoding.UTF8)
            let _, processId = ownerToken observed

            match processIsAlive processId with
            | Some true -> false
            | None when DateTime.UtcNow - info.LastWriteTimeUtc <= staleAfter -> false
            | _ ->
                if File.ReadAllText(file, Encoding.UTF8) <> observed then
                    false
                else
                    File.Delete file
                    true
        with
        | :? FileNotFoundException -> true
        | :? DirectoryNotFoundException -> true

    let private release file token =
        try
            if not (File.Exists file) then
                Ok()
            else
                let observed = File.ReadAllText(file, Encoding.UTF8)
                let observedToken, _ = ownerToken observed

                if String.Equals(observedToken, token, StringComparison.Ordinal) then
                    File.Delete file
                    Ok()
                else
                    Error(
                        lockFailure
                            "release artifact registry lock"
                            DependencyOutcome.Indeterminate
                            "registry lock ownership changed before release"
                    )
        with error ->
            Error(
                lockFailure
                    "release artifact registry lock"
                    DependencyOutcome.Indeterminate
                    error.Message
            )

    let acquire root resource settings: Result<RegistryLockLease, DependencyFailure> =
        let file = lockPath root resource
        let directory = Path.GetDirectoryName file
        let started = Stopwatch.StartNew()
        let token = Guid.NewGuid().ToString "N"
        Directory.CreateDirectory directory |> ignore

        let metadata =
            JsonSerializer.Serialize
                {| pid = Environment.ProcessId
                   ownerToken = token
                   resource = resource
                   acquiredAt = DateTime.UtcNow.ToString("O") |}
            + "\n"

        let rec tryAcquire () =
            try
                use stream = File.Open(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                let bytes = Encoding.UTF8.GetBytes metadata
                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(true)
                Ok { Release = fun () -> release file token }
            with
            | :? IOException ->
                if removeAbandoned file settings.StaleAfter then
                    tryAcquire ()
                elif started.Elapsed >= settings.Timeout then
                    Error(
                        lockFailure
                            "acquire artifact registry lock"
                            DependencyOutcome.Failed
                            $"timed out waiting for ROS lock '{resource}'"
                    )
                else
                    Thread.Sleep settings.RetryDelay
                    tryAcquire ()
            | error ->
                Error(lockFailure "acquire artifact registry lock" DependencyOutcome.Failed error.Message)

        tryAcquire ()
