namespace Praxis.Domain.Lifecycle

open System.Text.RegularExpressions

/// Keeps lines Praxis depends on present in a repository's own Git pattern
/// files (`.gitignore`, `.gitattributes`) without touching anything else in
/// them.
[<RequireQualifiedAccess>]
module RequiredLines =
    /// `/.ros/locks`, `.ros/locks/` and `.ros/locks` ignore the same thing for
    /// Praxis's purposes, and runs of whitespace separate attributes equally.
    let private canonical (line: string) =
        Regex.Replace(line.Trim(), @"\s+", " ").Trim('/')

    let private linesOf (content: string) =
        content.Split '\n'
        |> Array.map (fun line -> line.TrimEnd '\r')
        |> Array.filter (fun line -> not (line.TrimStart().StartsWith "#" || line.TrimStart().StartsWith "!"))
        |> Array.map canonical
        |> Set.ofArray

    /// The file with every missing line appended once, in the file's own
    /// line-ending style; `None` when every line is already present.
    let ensure (required: string list) (existing: string) : string option =
        let present = linesOf existing

        match required |> List.filter (fun line -> not (present.Contains(canonical line))) |> List.distinct with
        | [] -> None
        | missing ->
            let newline = if existing.Contains "\r\n" then "\r\n" else "\n"
            let separator = if existing = "" || existing.EndsWith "\n" then "" else newline
            Some(existing + separator + (missing |> List.map (fun line -> line + newline) |> String.concat ""))
