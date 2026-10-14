namespace Praxis.Domain.Lifecycle

open System
open System.Text.RegularExpressions

/// Makes the Markdown Praxis ships resolve inside a consumer repository.
///
/// The shipped documents are the Praxis repository's own documents, so their
/// relative links are written against the Praxis source tree. A consumer
/// receives only part of that tree, often at a different destination. While
/// rendering the payload a relative link that already names an installed
/// file from the document's destination (as a starter template's links do)
/// is kept; every other relative link is resolved against the Praxis source
/// tree and rewritten:
///   - to the installed copy, relative to the installing document's
///     destination, when the payload installs the target unconditionally;
///   - otherwise to an absolute GitHub URL pinned to the installing release's
///     tag, so it still names exactly the text that release shipped.
/// External links, in-page anchors, and fenced code blocks are left alone.
[<RequireQualifiedAccess>]
module ConsumerLinks =
    type Context =
        {
            /// e.g. https://github.com/kemiller2002/praxis
            RepositoryUrl: string
            /// The release tag the absolute links are pinned to, e.g. v3.7.2.
            Tag: string
            /// Praxis source path -> the consumer destination it is always
            /// installed at.
            Installed: Map<string, string>
            /// Directories every installed repository has at the same path
            /// as Praxis itself (its own state, e.g. `.ros`), whose links
            /// name the consumer's copy, never Praxis's.
            RepositoryLocal: string list
        }

    let private segments (path: string) =
        path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    let private directoryOf (path: string) =
        match segments path |> List.rev with
        | _ :: parent -> List.rev parent
        | [] -> []

    /// Resolves `.` and `..`; `None` when the path escapes the root.
    let private normalize (parts: string list) =
        parts
        |> List.fold
            (fun state part ->
                match state, part with
                | None, _ -> None
                | Some resolved, "." -> Some resolved
                | Some [], ".." -> None
                | Some resolved, ".." -> Some(List.tail resolved)
                | Some resolved, part -> Some(part :: resolved))
            (Some [])
        |> Option.map List.rev

    let private relativeTo (fromDirectory: string list) (target: string list) =
        let rec common left right =
            match left, right with
            | l :: lefts, r :: rights when l = r -> common lefts rights
            | _ -> left, right

        let ups, downs = common fromDirectory target
        List.replicate ups.Length ".." @ downs |> String.concat "/"

    let private isRelative (target: string) =
        target <> ""
        && not (target.StartsWith "#")
        && not (target.StartsWith "/")
        && not (Regex.IsMatch(target, "^[a-zA-Z][a-zA-Z0-9+.-]*:"))

    /// The consumer-side form of one link target written in `sourcePath`
    /// (a Praxis source path) and installed at `destinationPath`.
    let rewriteTarget (context: Context) (sourcePath: string) (destinationPath: string) (target: string) =
        if not (isRelative target) then
            target
        else
            let path, fragment =
                match target.IndexOf '#' with
                | -1 -> target, ""
                | index -> target.Substring(0, index), target.Substring index

            let installedAtDestination =
                match normalize (directoryOf destinationPath @ segments path) with
                | Some resolved when not resolved.IsEmpty ->
                    let resolvedPath = String.concat "/" resolved

                    context.Installed
                    |> Map.exists (fun _ installedAt -> installedAt = resolvedPath || installedAt.StartsWith(resolvedPath + "/"))
                | _ -> false

            let repositoryLocal (resolvedPath: string) =
                context.RepositoryLocal
                |> List.exists (fun directory -> resolvedPath = directory || resolvedPath.StartsWith(directory + "/"))

            match normalize (directoryOf sourcePath @ segments path) with
            | _ when installedAtDestination -> target
            | Some resolved when repositoryLocal (String.concat "/" resolved) ->
                relativeTo (directoryOf destinationPath) resolved
                + (if path.EndsWith "/" then "/" else "")
                + fragment
            | None
            | Some [] -> target
            | Some resolved ->
                let resolvedPath = String.concat "/" resolved

                // A directory is installed where the files under it are,
                // provided they keep their layout beneath it.
                let installedDirectory () =
                    context.Installed
                    |> Map.toSeq
                    |> Seq.tryPick (fun (source, installedAt) ->
                        if source.StartsWith(resolvedPath + "/") && installedAt.EndsWith(source.Substring resolvedPath.Length) then
                            Some(installedAt.Substring(0, installedAt.Length - (source.Length - resolvedPath.Length)))
                        else
                            None)

                match Map.tryFind resolvedPath context.Installed |> Option.orElseWith installedDirectory with
                | Some installedAt ->
                    match relativeTo (directoryOf destinationPath) (segments installedAt) with
                    | "" -> "./" + fragment
                    | relative -> relative + (if path.EndsWith "/" then "/" else "") + fragment
                | None -> $"{context.RepositoryUrl.TrimEnd '/'}/blob/{context.Tag}/{resolvedPath}{fragment}"

    let private inlineLink = Regex(@"(\[[^\]]*\]\()([^)\s]+)", RegexOptions.Compiled)
    let private fence = Regex(@"^\s*(```|~~~)", RegexOptions.Compiled)

    /// Rewrites every inline link outside fenced code blocks in a Markdown
    /// document. Line endings are preserved byte for byte.
    let rewrite (context: Context) (sourcePath: string) (destinationPath: string) (markdown: string) =
        let lines = Regex.Split(markdown, "(?<=\n)")

        lines
        |> Array.fold
            (fun (fenced, output) line ->
                if fence.IsMatch line then
                    not fenced, line :: output
                elif fenced then
                    fenced, line :: output
                else
                    let rewritten =
                        inlineLink.Replace(
                            line,
                            fun (m: Match) ->
                                m.Groups[1].Value + rewriteTarget context sourcePath destinationPath m.Groups[2].Value
                        )

                    fenced, rewritten :: output)
            (false, [])
        |> snd
        |> List.rev
        |> String.concat ""
