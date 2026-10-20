namespace Praxis.Infrastructure.Work

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading

/// I8 (PRAXIS-REL-011): the out-of-tree transient store. Claims, validation
/// results, verifier receipts, claim-protocol events and the admitted-contract
/// cache live under `$(git rev-parse --git-common-dir)/praxis/`, which Git
/// never tracks, so claiming, validating and verifying never dirty the
/// worktree. Every worktree of one repository shares the store. A root that is
/// not (yet) inside a Git repository uses `<root>/.git/praxis/`, the same
/// directory `git init` there later adopts, so no claim is lost by initializing.
[<RequireQualifiedAccess>]
module TransientStore =
    let private resolved = ConcurrentDictionary<string, string>()

    let private resolve (root: string) =
        let full = Path.GetFullPath root
        let fallback = Path.Combine(full, ".git", "praxis")

        match ClaimProcess.run "git" full [] None (TimeSpan.FromSeconds 30.0) [ "rev-parse"; "--git-common-dir" ] with
        | Ok result when result.ExitCode = 0 && result.Stdout.Trim() <> "" ->
            Path.GetFullPath(Path.Combine(full, result.Stdout.Trim(), "praxis"))
        | _ -> fallback

    /// The store root for the repository at `root` (resolved once per root
    /// and process; a non-repository root's fallback is where `git init` puts
    /// the common directory, so the answer never changes for a toplevel).
    let directory (root: string) = resolved.GetOrAdd(Path.GetFullPath root, resolve)

    let claims (root: string) = Path.Combine(directory root, "claims")
    let validations (root: string) = Path.Combine(directory root, "validations")
    let receipts (root: string) = Path.Combine(directory root, "receipts")
    let events (root: string) = Path.Combine(directory root, "events.jsonl")
    let cache (root: string) = Path.Combine(directory root, "cache")

    /// The pre-I8 tracked claim store, read only to refuse and migrate.
    let legacyClaims (root: string) = Path.Combine(root, ".ros", "claims")

    /// Runs `action` while holding an exclusive lock file next to `path`.
    let withLock (path: string) (action: unit -> Result<'value, string>) : Result<'value, string> =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let lockPath = path + ".lock"
        let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 30.0

        let rec acquire () =
            try
                Ok(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            with :? IOException when DateTime.UtcNow < deadline ->
                Thread.Sleep 15
                acquire ()

        try
            match acquire () with
            | Error message -> Error message
            | Ok handle ->
                use _ = handle
                action ()
        with error ->
            Error $"cannot lock {lockPath}: {error.Message}"

    /// Appends one compact JSON line unless a line with the same `eventId`
    /// is already present (idempotent sink).
    let appendEvent (root: string) (eventId: string) (line: string) =
        let path = events root

        withLock path (fun () ->
            try
                let current = if File.Exists path then File.ReadAllText path else ""

                if current.Split('\n') |> Array.exists (fun existing -> existing.Contains($"\"eventId\":\"{eventId}\"", StringComparison.Ordinal)) then
                    Ok()
                else
                    let separator = if current.Length > 0 && not (current.EndsWith "\n") then "\n" else ""
                    File.AppendAllText(path, $"{separator}{line}\n", UTF8Encoding(false))
                    Ok()
            with error ->
                Error error.Message)

    /// Writes `bytes` to `path` through a temporary file; never overwrites.
    let publish (path: string) (bytes: byte array) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = $"{path}.{Guid.NewGuid():N}.tmp"
        File.WriteAllBytes(temporary, bytes)
        File.Move(temporary, path, false)
