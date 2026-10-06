namespace Ros.Infrastructure.Work

open System
open System.IO
open Ros.Domain.Work
open Ros.Infrastructure.Git

/// Reads the identity of a repository's durable Praxis state: which
/// repository, which commit, and the fingerprint of the records a
/// control-plane host derives its responses from. Reads only: it never
/// writes a file and takes no Git optional lock.
[<RequireQualifiedAccess>]
module FileStateIdentityRepository =
    let private relative (root: string) (path: string) =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    /// The repository-relative paths the fingerprint covers: `ros.json`,
    /// the configured metric registry, and every file under `.ros/` except
    /// transient locks. Paths that do not exist are left out.
    let inputPaths (root: string) : string list =
        let state = Path.Combine(root, ".ros")

        let stateFiles =
            if Directory.Exists state then
                Directory.EnumerateFiles(state, "*", SearchOption.AllDirectories)
                |> Seq.map (relative root)
                |> Seq.filter StateIdentity.coversStatePath
                |> Seq.toList
            else
                []

        let configured =
            [ "ros.json"; FileWorkConfigRepository.readTelemetryMetricRegistryPath root ]
            |> List.map (fun path -> path.Replace('\\', '/'))
            |> List.filter (fun path -> File.Exists(Path.Combine(root, path)))

        configured @ stateFiles |> List.distinct

    /// The fingerprint of the current records, or why they could not be read
    /// (a file removed between listing and reading counts as a change and is
    /// simply absent).
    let readFingerprint (root: string) : Result<string, string> =
        try
            inputPaths root
            |> List.choose (fun path ->
                let full = Path.Combine(root, path)

                try
                    Some
                        { Path = path
                          ContentHash = StateIdentity.contentHash (File.ReadAllBytes full) }
                with
                | :? FileNotFoundException
                | :? DirectoryNotFoundException -> None)
            |> StateIdentity.fingerprint
            |> Ok
        with error ->
            Error error.Message

    let read (root: string) : StateIdentity =
        let fullRoot = Path.GetFullPath root
        let branch, commit = ProcessGitRepository.readBranchAndCommit fullRoot

        { Repository = FileWorkConfigRepository.readRepositoryId fullRoot
          Commit = commit
          Branch = branch
          Fingerprint = readFingerprint fullRoot }
