namespace Praxis.Site

open System
open System.IO

/// Locates the Praxis repository the site tools operate on: the nearest
/// directory, walking up, that holds both `release.json` and `site/`. The
/// search starts at the working directory and falls back to the tool's own
/// location, so the tools never depend on where their binary was built.
[<RequireQualifiedAccess>]
module Repository =
    let isRoot (directory: string) =
        File.Exists(Path.Combine(directory, "release.json"))
        && Directory.Exists(Path.Combine(directory, "site"))

    let rec private walkUp (directory: DirectoryInfo) =
        match directory with
        | null -> None
        | current when isRoot current.FullName -> Some(Path.TrimEndingDirectorySeparator current.FullName)
        | current -> walkUp current.Parent

    let tryLocateFrom (start: string) = walkUp (DirectoryInfo(Path.GetFullPath start))

    let tryLocate () =
        tryLocateFrom Environment.CurrentDirectory
        |> Option.orElseWith (fun () -> tryLocateFrom AppContext.BaseDirectory)

    let locate () =
        match tryLocate () with
        | Some root -> root
        | None ->
            failwith "cannot find the Praxis repository: no directory above the working directory holds both release.json and site/"

    /// `path.relative(from, to)` with forward slashes.
    let relative (from: string) (target: string) =
        Path.GetRelativePath(from, target).Replace(Path.DirectorySeparatorChar, '/')
        |> fun text -> if text = "." then "" else text
