namespace Ros.Integrations.GitHub

open System.Diagnostics
open System.IO

/// A real, disposable local git repository standing in for a target
/// application's actual GitHub-hosted datastore repository, until real
/// GitHub App credentials for that application exist (see
/// `GitHubDatastoreWriter`'s own doc comment for the full rationale).
/// Every operation here shells out to the real `git` binary and produces
/// real commits -- nothing about the git history itself is faked, only
/// the target (a scratch local repo, not `github.com/<chrona-org>/...`).
[<RequireQualifiedAccess>]
module LocalGitSimulation =
    let private run (workingDirectory: string) (arguments: string) =
        let info =
            ProcessStartInfo(
                "git",
                arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        use proc = Process.Start info
        let stdout = proc.StandardOutput.ReadToEnd()
        proc.WaitForExit()
        proc.ExitCode, stdout

    /// Initializes the scratch repository with a fixed, deterministic
    /// identity so tests never depend on the host machine's own git
    /// configuration.
    let init (repoRoot: string) =
        Directory.CreateDirectory repoRoot |> ignore
        run repoRoot "init -q" |> ignore
        run repoRoot "config user.email test@example.invalid" |> ignore
        run repoRoot "config user.name \"ROS Central simulation\"" |> ignore

    /// Commits the current working tree state if and only if it differs
    /// from HEAD, returning whether a commit was made. "Repeat delivery
    /// creates no new commit" is therefore a property of `git commit`'s
    /// own no-op-on-no-changes behavior, not something reimplemented
    /// here -- delivering the same candidate twice stages an identical
    /// file the second time, `git diff --cached --quiet` reports no
    /// change, and no commit happens.
    let commitIfChanged (repoRoot: string) (message: string) : bool =
        run repoRoot "add -A" |> ignore
        let exitCode, _ = run repoRoot "diff --cached --quiet"

        if exitCode = 0 then
            false
        else
            run repoRoot $"commit -q -m \"{message}\"" |> ignore
            true

    let commitCount (repoRoot: string) : int =
        let exitCode, stdout = run repoRoot "rev-list --count HEAD"

        if exitCode = 0 then
            int (stdout.Trim())
        else
            0
