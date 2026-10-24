namespace Praxis.Infrastructure.Requirements

open System
open System.IO
open Praxis.Application.Requirements

/// Filesystem implementation of the preservation gate's input port
/// (REQ-PRES-001). Read-only: the gate never writes an input, an approval or
/// a review. The only write is the optional `--output` evidence file.
[<RequireQualifiedAccess>]
module FilePreservationInputs =
    let private guard (action: unit -> 'T) =
        try
            Ok(action ())
        with
        | :? IOException as error -> Error error.Message
        | :? UnauthorizedAccessException as error -> Error error.Message
        | :? ArgumentException as error -> Error error.Message

    let private readFile (path: string) =
        if File.Exists path then guard (fun () -> File.ReadAllBytes path)
        else Error $"file '{path}' does not exist"

    let private readDerived (directory: string) =
        if not (Directory.Exists directory) then
            Error $"derived package directory '{directory}' does not exist"
        else
            guard (fun () ->
                Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories)
                |> Array.map (fun path -> Path.GetRelativePath(directory, path).Replace('\\', '/'), path)
                |> Array.filter (fun (relative, _) -> not (String.Equals(Path.GetFileName relative, "README.md", StringComparison.OrdinalIgnoreCase)))
                |> Array.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                |> Array.map (fun (relative, path) -> relative, File.ReadAllBytes path)
                |> Array.toList)

    let private readSource (root: string) (relative: string) =
        let full = Path.GetFullPath(Path.Combine(root, relative))
        let rootFull = Path.GetFullPath root

        if Path.IsPathRooted relative || not (full.StartsWith(rootFull, StringComparison.Ordinal)) then
            Error $"source document path '{relative}' must stay within the source root"
        elif File.Exists full then
            guard (fun () -> Some(File.ReadAllText full))
        else
            Ok None

    let create () : PreservationGate.Inputs =
        { ReadFile = readFile
          ReadDerived = readDerived
          ReadSource = readSource }

    let writeEvidence (path: string) (content: string) =
        guard (fun () ->
            match Path.GetDirectoryName(Path.GetFullPath path) with
            | null -> ()
            | directory -> Directory.CreateDirectory directory |> ignore

            File.WriteAllText(path, content))
