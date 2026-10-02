namespace Ros.Cli

open System
open System.IO
open System.Text
open Ros.Contracts.Work

/// `praxis state merge-file` and `praxis state merge-driver`
/// (PRAXIS-STATE-MERGE-01): a Git merge driver, installed by Praxis, that
/// merges Praxis's single-document state files without hand edits. Append-
/// only histories keep every entry once; id-keyed items merge three-way; an
/// item both sides changed differently is a conflict that names the item.
[<RequireQualifiedAccess>]
module StateMergeCommands =
    let usage =
        "state merge-file --path PATH --base FILE --ours FILE --theirs FILE | state merge-driver install [--dry-run] | state merge-driver status"

    let driverName = "praxis-state"

    /// Git substitutes %P (the path), %O (common ancestor), %A (ours, which
    /// receives the result) and %B (theirs). Git runs it from the top of the
    /// working tree, where `./praxis` is the repository's own launcher.
    let driverCommand = "./praxis state merge-file --path %P --base %O --ours %A --theirs %B"

    let private attributesBegin = "# >>> praxis state merge driver (managed by 'praxis state merge-driver install')"
    let private attributesEnd = "# <<< praxis state merge driver"

    let attributeLines =
        StateMergeJson.rules |> List.map (fun (path, _) -> $"/{path} merge={driverName}")

    let private managedBlock = [ attributesBegin ] @ attributeLines @ [ attributesEnd ]

    // ---- merge-file -----------------------------------------------------------

    let private option name (arguments: string list) =
        arguments |> List.pairwise |> List.tryFind (fst >> (=) name) |> Option.map snd

    let private readOptional (file: string) =
        if File.Exists file then Some(File.ReadAllText(file, Encoding.UTF8)) else None

    /// Leaves Git's ordinary conflict markers in `ours`, so a refused merge
    /// can never be mistaken for a resolved one (the result is not even
    /// valid JSON, which `praxis validate` reports).
    let private markConflict root ours ancestor theirs =
        CliProcess.run root "git" [ "merge-file"; "-L"; "ours"; "-L"; "common ancestor"; "-L"; "theirs"; ours; ancestor; theirs ]
        |> ignore

    let mergeFile (root: string) (arguments: string list) =
        match option "--path" arguments, option "--base" arguments, option "--ours" arguments, option "--theirs" arguments with
        | Some path, Some ancestor, Some ours, Some theirs ->
            let resolve (file: string) = if Path.IsPathRooted file then file else Path.Combine(root, file)
            let ancestorFile, oursFile, theirsFile = resolve ancestor, resolve ours, resolve theirs

            match readOptional oursFile, readOptional theirsFile with
            | Some oursText, Some theirsText ->
                match StateMergeJson.merge path (readOptional ancestorFile) oursText theirsText with
                | StateMergeOutcome.Merged content ->
                    File.WriteAllText(oursFile, content, UTF8Encoding(false))
                    eprintfn "praxis: merged %s" path
                    0
                | StateMergeOutcome.Conflicted conflicts ->
                    conflicts |> List.iter (fun found -> eprintfn "CONFLICT %s: %s: %s" found.Path found.Item found.Reason)
                    eprintfn "praxis: %s was not merged; resolve the item(s) above with Praxis commands, never by editing the file" path
                    markConflict root oursFile ancestorFile theirsFile
                    1
                | StateMergeOutcome.Unsupported reason ->
                    eprintfn "ERROR %s: %s" path reason
                    markConflict root oursFile ancestorFile theirsFile
                    1
            | _ ->
                eprintfn "ERROR state merge-file: --ours and --theirs must name existing files"
                2
        | _ ->
            eprintfn "ERROR usage: %s" usage
            2

    // ---- merge-driver ---------------------------------------------------------

    /// `.gitattributes` with the managed block replaced (or appended).
    let private withManagedBlock (existing: string) =
        let lines = existing.Replace("\r\n", "\n").Split('\n') |> Array.toList
        let start = lines |> List.tryFindIndex ((=) attributesBegin)
        let finish = lines |> List.tryFindIndex ((=) attributesEnd)

        let kept =
            match start, finish with
            | Some first, Some last when last > first -> (lines |> List.take first) @ (lines |> List.skip (last + 1))
            | _ -> lines

        let body = kept |> List.rev |> List.skipWhile String.IsNullOrWhiteSpace |> List.rev
        let separator = if body.IsEmpty then [] else [ "" ]
        String.Join("\n", body @ separator @ managedBlock) + "\n"

    let private attributesPath root = Path.Combine(root, ".gitattributes")

    let private gitConfig root (key: string) =
        match CliProcess.run root "git" [ "config"; "--local"; "--get"; key ] with
        | Ok result when result.Exit = 0 -> Some(result.Out.Trim())
        | _ -> None

    let private isRepository root =
        match CliProcess.run root "git" [ "rev-parse"; "--git-dir" ] with
        | Ok result -> result.Exit = 0
        | Error _ -> false

    type private DriverState =
        { AttributesCurrent: bool
          DriverConfigured: bool }

    let private state root =
        let current = readOptional (attributesPath root) |> Option.defaultValue ""

        { AttributesCurrent = withManagedBlock current = (current.Replace("\r\n", "\n").TrimEnd('\n') + "\n")
          DriverConfigured = gitConfig root $"merge.{driverName}.driver" = Some driverCommand }

    let private install root dryRun =
        if not (isRepository root) then
            eprintfn "ERROR state merge-driver install: %s is not a Git working tree" root
            6
        else
            let before = state root

            if dryRun then
                printfn "%s .gitattributes" (if before.AttributesCurrent then "unchanged" else "WOULD WRITE")
                printfn "%s git config merge.%s.driver" (if before.DriverConfigured then "unchanged" else "WOULD SET") driverName
                0
            else
                if not before.AttributesCurrent then
                    let current = readOptional (attributesPath root) |> Option.defaultValue ""
                    File.WriteAllText(attributesPath root, withManagedBlock current, UTF8Encoding(false))

                let configured =
                    [ $"merge.{driverName}.name", "Praxis state merge"; $"merge.{driverName}.driver", driverCommand ]
                    |> List.map (fun (key, value) -> CliProcess.run root "git" [ "config"; "--local"; key; value ])
                    |> List.forall (function
                        | Ok result -> result.Exit = 0
                        | Error _ -> false)

                if not configured then
                    eprintfn "ERROR state merge-driver install: git config failed"
                    1
                else
                    printfn "installed the %s merge driver for %d Praxis state file(s); commit .gitattributes" driverName attributeLines.Length
                    0

    let private status root =
        let current = state root
        printfn ".gitattributes: %s" (if current.AttributesCurrent then "current" else "missing or out of date")
        printfn "git config merge.%s.driver: %s" driverName (if current.DriverConfigured then "configured" else "not configured")
        if current.AttributesCurrent && current.DriverConfigured then 0 else 3

    let run (root: string) (arguments: string list) =
        match arguments with
        | "merge-file" :: rest -> mergeFile root rest
        | [ "merge-driver"; "install" ] -> install root false
        | [ "merge-driver"; "install"; "--dry-run" ] -> install root true
        | [ "merge-driver"; "status" ] -> status root
        | _ ->
            eprintfn "ERROR usage: %s" usage
            2
