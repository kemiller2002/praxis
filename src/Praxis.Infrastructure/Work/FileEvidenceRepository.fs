namespace Praxis.Infrastructure.Work

open System
open System.IO
open Praxis.Application.Work
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module FileEvidenceRepository =
    let private comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

    /// Whether `candidate` (a full path) is `root` or lies beneath it.
    let private within (root: string) (candidate: string) =
        let root = Path.TrimEndingDirectorySeparator root
        String.Equals(candidate, root, comparison)
        || candidate.StartsWith(root + string Path.DirectorySeparatorChar, comparison)

    /// The physical path a full path designates: every component that is a
    /// symbolic link is followed (so a linked parent directory cannot carry
    /// a citation out of the repository), and links inside a target are
    /// followed in turn.
    let rec private realPath (path: string) : string =
        let root = string (Path.GetPathRoot path)
        let parts = path.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)

        let step (current: string) (part: string) =
            let next = Path.Combine(current, part)
            let info: FileSystemInfo = if Directory.Exists next then DirectoryInfo next else FileInfo next

            match info.LinkTarget with
            | null -> next
            | _ ->
                match info.ResolveLinkTarget true with
                | null -> next
                | target -> realPath (Path.GetFullPath target.FullName)

        parts |> Array.fold step root

    /// PRX-VER-ADV-001: evidence must resolve inside the repository. An
    /// absolute path, a `..` escape, or a link out of the tree names bytes the
    /// candidate commit does not contain and a reviewer cannot see, so it is
    /// refused rather than counted as present.
    let private observe root (evidence: WorkEvidence) =
        let physicalRoot = realPath root

        let outside () =
            EvidencePathObservation.Unavailable
                $"evidence '{evidence.Path}' resolves outside the repository; cite a repository-relative path"

        try
            let candidate = Path.GetFullPath(Path.Combine(root, evidence.Path))

            if not (within root candidate) then
                outside ()
            else
                File.GetAttributes candidate |> ignore

                if within physicalRoot (realPath candidate) then EvidencePathObservation.Present else outside ()
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> EvidencePathObservation.Missing
        | error -> EvidencePathObservation.Unavailable error.Message

    let create root : WorkEvidenceRepository =
        { Observe = observe (Path.GetFullPath root) }
