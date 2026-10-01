namespace Praxis.Site

open System
open System.IO

/// An assembled artifact and every problem found when re-checking it.
type Assembled = { Target: string; Problems: string list }

/// Assembles the deployable public-site artifact (docs/public-site.md).
///
/// The artifact is exactly the files under site/: nothing is generated, bundled
/// or fetched at this stage. The copy is checked again with the same rules as
/// the source, so what is uploaded is what was verified.
[<RequireQualifiedAccess>]
module Assemble =
    /// The absolute target for `out`, or an error when it is not strictly
    /// inside the repository.
    let target (root: string) (out: string) =
        let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, out)))

        if full.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
            Ok full
        else
            Error $"refusing to assemble outside the repository: {out}"

    let rec private copy (source: string) (destination: string) =
        Directory.CreateDirectory destination |> ignore

        for file in Directory.GetFiles source do
            File.Copy(file, Path.Combine(destination, Path.GetFileName file), true)

        for directory in Directory.GetDirectories source do
            copy directory (Path.Combine(destination, Path.GetFileName directory))

    let assemble (root: string) (out: string) =
        target root out
        |> Result.map (fun full ->
            if Directory.Exists full then Directory.Delete(full, true)
            elif File.Exists full then File.Delete full

            copy (Path.Combine(root, "site")) full

            let missing =
                Site.requiredFiles
                |> List.filter (fun file ->
                    let path = Path.Combine(full, file)
                    not (File.Exists path || Directory.Exists path))
                |> List.map (fun file -> $"artifact is missing {file}")

            { Target = full
              Problems = Site.check full @ missing })
