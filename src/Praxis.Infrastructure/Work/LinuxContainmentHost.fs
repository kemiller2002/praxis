namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// Host locations the containment boundary must keep out of a worker root.
type ContainmentHostPaths =
    { RepositoryRoot: string
      CheckoutRoot: string
      /// Host roots by name (policy, journal, spool, submissions, ...).
      HostRoots: (string * string) list
      /// Further sealed locations such as the host configuration directory.
      Extra: string list }

/// Host effects of Linux containment: tool resolution and digests, platform
/// facts, staging roots, evidence files and the confined process spec. The
/// pure construction and gate live in `LinuxContainment`.
[<RequireQualifiedAccess>]
module LinuxContainmentHost =
    let private sha256File (path: string) =
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        "sha256:" + (SHA256.HashData input |> Convert.ToHexString).ToLowerInvariant()

    let operatingSystem () =
        if OperatingSystem.IsLinux() then "linux"
        elif OperatingSystem.IsMacOS() then "macos"
        elif OperatingSystem.IsWindows() then "windows"
        else "unsupported"

    let kernel () =
        try
            if OperatingSystem.IsLinux() && File.Exists "/proc/sys/kernel/osrelease" then
                File.ReadAllText("/proc/sys/kernel/osrelease").Trim()
            else RuntimeInformation.OSDescription
        with _ -> "unknown"

    let architecture () = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()

    let private resolve name =
        LinuxContainment.searchPath
        |> List.map (fun directory -> directory + "/" + name)
        |> List.tryFind File.Exists
        |> function
            | Some path -> (try Ok { Name = name; Path = path; Digest = sha256File path } with e -> Error(name + ": " + e.Message))
            | None -> Error(name + " is not installed in " + String.concat ":" LinuxContainment.searchPath)

    /// The pinned tool closure as found on this host now.
    let resolveTools () =
        if not (OperatingSystem.IsLinux()) then Error("containment is unsupported on " + operatingSystem ())
        else
            let found = [ "unshare"; "sh"; "prlimit"; "setpriv" ] @ LinuxContainment.supportTools |> List.map resolve
            match found |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | [] ->
                let tools = found |> List.choose Result.toOption
                let named name = tools |> List.find (fun t -> t.Name = name)
                Ok { Unshare = named "unshare"; Shell = named "sh"; Prlimit = named "prlimit"; Setpriv = named "setpriv"
                     Support = tools |> List.filter (fun t -> List.contains t.Name LinuxContainment.supportTools) }
            | missing -> Error(String.concat "; " missing)

    let facts (settings: LinuxContainmentSettings) (tools: LinuxContainmentTools) probeScriptDigest : ContainmentHostFacts =
        { OperatingSystem = operatingSystem ()
          Kernel = kernel ()
          Architecture = architecture ()
          ProfileDigest = LinuxContainment.profileDigest settings tools probeScriptDigest
          Tools = LinuxContainment.allTools tools }

    let private normalize (path: string) = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)

    /// Sealed and protected paths for this host: its roots, repository and
    /// configuration, the fixed system list and the invoking user's home.
    let request (paths: ContainmentHostPaths) stage : ContainedLaunchRequest =
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        let homeCredentials =
            if String.IsNullOrWhiteSpace home then [] else LinuxContainment.homeCredentials |> List.map (fun item -> normalize (Path.Combine(home, item)))
        { Stage = normalize stage
          WorkerRoot = normalize paths.CheckoutRoot
          Sealed =
            [ normalize paths.RepositoryRoot; normalize paths.CheckoutRoot ]
            @ (paths.HostRoots |> List.map (snd >> normalize)) @ (paths.Extra |> List.map normalize)
            @ LinuxContainment.systemSealed @ homeCredentials
            |> List.distinct
          Protected = LinuxContainment.systemProtected @ (if String.IsNullOrWhiteSpace home then [] else [ normalize home ]) |> List.distinct }

    /// A fresh, empty staging directory for one launch's minimal root.
    let createStage (checkoutRoot: string) =
        try
            let path = Path.Combine(normalize checkoutRoot, ".praxis-stage-" + Guid.NewGuid().ToString("N").Substring(0, 16))
            Directory.CreateDirectory path |> ignore
            Ok path
        with e -> Error("containment staging root unavailable: " + e.Message)

    /// The stage is only a mount point inside a private mount namespace; on
    /// the host it stays empty. Removal never follows links.
    let removeStage (path: string) =
        try
            if Directory.Exists path && Seq.isEmpty (Directory.EnumerateFileSystemEntries path) then Directory.Delete path
        with _ -> ()

    /// Confines one host-built spec. The worker's own executable pin is
    /// re-verified here because the supervisor now verifies `unshare`.
    let confine (settings: LinuxContainmentSettings) (tools: LinuxContainmentTools) (paths: ContainmentHostPaths) stage (spec: LocalWorkerProcessSpec) =
        try
            if not (File.Exists spec.Executable) || sha256File spec.Executable <> spec.ExecutableDigest then Error "worker executable changed before confinement"
            elif not (LocalHostJournalFiles.noLinks spec.WorkingDirectory) then Error "worker directory path contains a link"
            else LinuxContainment.wrap settings tools (request paths stage) { spec with WorkingDirectory = normalize spec.WorkingDirectory }
        with e -> Error("confinement refused: " + e.Message)

    // ----------------------------------------------------------- evidence

    let readEvidence (path: string) =
        try
            if not (Path.IsPathFullyQualified path) then Error "containment evidence path must be absolute"
            elif not (File.Exists path) then Error "containment has not been qualified on this host (no evidence file)"
            elif not (LocalHostJournalFiles.noLinks path) then Error "containment evidence path contains a link"
            elif FileInfo(path).Length > 262144L then Error "containment evidence exceeds 256 KiB"
            else LocalContainmentJson.read (File.ReadAllText path)
        with e -> Error("containment evidence unavailable: " + e.Message)

    /// Replace the evidence file atomically: write a sibling, flush, rename.
    let writeEvidence (path: string) (qualification: ContainmentQualification) =
        try
            let full = Path.GetFullPath path
            let directory = Path.GetDirectoryName full
            if not (Directory.Exists directory) || not (LocalHostJournalFiles.noLinks directory) then Error "containment evidence directory must exist and be unlinked"
            else
                let temporary = Path.Combine(directory, "." + Path.GetFileName full + "." + Guid.NewGuid().ToString("N") + ".tmp")
                do
                    use output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    let bytes = Text.UTF8Encoding(false).GetBytes(LocalContainmentJson.render qualification)
                    output.Write bytes
                    output.Flush true
                File.Move(temporary, full, true)
                Ok full
        with e -> Error("containment evidence not written: " + e.Message)
