namespace Ros.Infrastructure.Remote

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Ros.Contracts.Remote
open Ros.Domain.Remote

/// Effects of the remote execution boundary (PRAXIS-REMOTE-03): the request
/// journal, the repository's remote policy, the working-tree observations
/// that bind a request to a commit, and the child process that runs the
/// same command implementation the local CLI runs.
[<RequireQualifiedAccess>]
module FileRemoteRepository =
    let private fullPath (root: string) (relative: string) =
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))

    /// `Ok NotRecorded` when no entry exists; an unreadable entry is an
    /// error rather than "absent", so a damaged journal can never turn a
    /// duplicate into a second execution.
    let lookup (root: string) (requestId: string) : Result<JournalLookup * string option, string> =
        let file = fullPath root (RemotePersistence.journalPath requestId)

        if not (File.Exists file) then
            Ok(JournalLookup.NotRecorded, None)
        else
            match RemoteJournal.read (File.ReadAllText file) with
            | Ok(fingerprint, response) -> Ok(JournalLookup.Recorded fingerprint, Some response)
            | Error message -> Error $"the journal entry for this request ID is unreadable ({message})"

    let write (root: string) (entry: RemoteJournal.Entry) : Result<string, string> =
        let relative = RemotePersistence.journalPath entry.Request.RequestId
        let file = fullPath root relative

        try
            Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
            let temporary = file + ".tmp"
            File.WriteAllText(temporary, RemoteJournal.render entry)
            File.Move(temporary, file, false)
            Ok relative
        with error ->
            Error error.Message

    /// The repository's opt-in: `ros.json` `remote.capabilities`. Absent,
    /// unreadable, or malformed configuration allows remote reads only.
    let readRepositoryCapabilities (root: string) : Set<Capability> =
        let path = Path.Combine(root, "ros.json")

        try
            if not (File.Exists path) then
                RemotePolicy.defaultRepositoryCapabilities
            else
                use document = JsonDocument.Parse(File.ReadAllText path)

                match document.RootElement.TryGetProperty "remote" with
                | true, remote when remote.ValueKind = JsonValueKind.Object ->
                    match remote.TryGetProperty "capabilities" with
                    | true, values when values.ValueKind = JsonValueKind.Array ->
                        values.EnumerateArray()
                        |> Seq.choose (fun value ->
                            if value.ValueKind = JsonValueKind.String then Capability.tryParse (value.GetString()) else None)
                        |> Set.ofSeq
                    | _ -> RemotePolicy.defaultRepositoryCapabilities
                | _ -> RemotePolicy.defaultRepositoryCapabilities
        with _ ->
            RemotePolicy.defaultRepositoryCapabilities

    type ProcessOutcome =
        { ExitCode: int
          Stdout: string
          Stderr: string
          TimedOut: bool }

    /// Runs one program with an explicit argument list and an explicit,
    /// complete environment. Arguments are passed as discrete values
    /// (`ArgumentList`), never through a shell, and nothing from the
    /// parent environment survives unless it is in `environment`.
    let run (workingDirectory: string) (program: string) (arguments: string list) (environment: Map<string, string>) (timeout: TimeSpan) : ProcessOutcome =
        let info = ProcessStartInfo(program)
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.RedirectStandardInput <- true
        arguments |> List.iter info.ArgumentList.Add
        info.Environment.Clear()
        environment |> Map.iter (fun name value -> info.Environment[name] <- value)

        use child = new Process(StartInfo = info)
        child.Start() |> ignore
        child.StandardInput.Close()
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()

        if child.WaitForExit(int timeout.TotalMilliseconds) then
            child.WaitForExit()

            { ExitCode = child.ExitCode
              Stdout = stdout.Result
              Stderr = stderr.Result
              TimedOut = false }
        else
            (try child.Kill(true) with _ -> ())
            child.WaitForExit()

            { ExitCode = -1
              Stdout = stdout.Result
              Stderr = stderr.Result
              TimedOut = true }

    let private git (root: string) (arguments: string list) =
        let environment =
            Environment.GetEnvironmentVariables()
            |> Seq.cast<Collections.DictionaryEntry>
            |> Seq.map (fun entry -> string entry.Key, string entry.Value)
            |> Map.ofSeq

        run root "git" arguments environment (TimeSpan.FromMinutes 2.0)

    /// The ref and commit the executor is actually on. A detached HEAD has
    /// no ref, so a mutation naming one is refused as stale rather than
    /// applied to an unnamed commit.
    let observeHead (root: string) : string option * string option =
        let text (outcome: ProcessOutcome) =
            if outcome.ExitCode = 0 && outcome.Stdout.Trim().Length > 0 then Some(outcome.Stdout.Trim()) else None

        let branch = git root [ "symbolic-ref"; "--quiet"; "HEAD" ] |> text
        let commit = git root [ "rev-parse"; "--verify"; "HEAD" ] |> text
        branch, commit

    /// Paths that differ from `HEAD` (tracked changes and untracked files),
    /// repository-relative with `/` separators. `Error` when Git cannot say,
    /// which the boundary treats as "cannot bind to repository state".
    let changedPaths (root: string) : Result<string list, string> =
        let outcome = git root [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=all" ]

        if outcome.ExitCode <> 0 then
            Error(outcome.Stderr.Trim())
        else
            let entries = outcome.Stdout.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

            let rec paths (remaining: string list) =
                match remaining with
                | [] -> []
                | entry :: rest when entry.Length > 3 ->
                    let status = entry.Substring(0, 2)
                    let path = entry.Substring 3

                    // A rename or copy is followed by its source path.
                    match status.Contains 'R' || status.Contains 'C', rest with
                    | true, source :: afterSource -> path :: source :: paths afterSource
                    | _ -> path :: paths rest
                | _ :: rest -> paths rest

            Ok(paths entries |> List.distinct)

    /// Undoes a refused or failed mutation. The boundary only mutates a
    /// clean tree, so restoring every changed path to `HEAD` (and removing
    /// new files) returns the repository exactly to where it started.
    let restore (root: string) (paths: string list) : Result<unit, string> =
        let tracked, untracked =
            paths
            |> List.partition (fun path -> (git root [ "cat-file"; "-e"; $"HEAD:{path}" ]).ExitCode = 0)

        let restored =
            if tracked.IsEmpty then
                Ok()
            else
                let outcome = git root ([ "checkout"; "HEAD"; "--" ] @ tracked)
                if outcome.ExitCode = 0 then Ok() else Error(outcome.Stderr.Trim())

        restored
        |> Result.bind (fun () ->
            try
                untracked
                |> List.iter (fun path ->
                    let file = fullPath root path
                    if File.Exists file then File.Delete file)

                Ok()
            with error ->
                Error error.Message)

    /// The current bytes of each path that exists, so a later refusal can
    /// put exactly these back (used when earlier constituents of a batch
    /// have already changed the working tree).
    let snapshot (root: string) (paths: string list) : Map<string, byte[]> =
        paths
        |> List.choose (fun path ->
            let file = fullPath root path
            if File.Exists file then Some(path, File.ReadAllBytes file) else None)
        |> Map.ofList

    /// Returns every changed path to its state before the refused command:
    /// its snapshot when it had one, otherwise `HEAD` (or absent).
    let restoreTo (root: string) (before: Map<string, byte[]>) (changed: string list) : Result<unit, string> =
        let fromSnapshot, fromHead = changed |> List.partition before.ContainsKey

        try
            fromSnapshot |> List.iter (fun path -> File.WriteAllBytes(fullPath root path, before[path]))
            restore root fromHead
        with error ->
            Error error.Message
