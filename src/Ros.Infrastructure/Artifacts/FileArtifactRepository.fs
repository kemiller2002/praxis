namespace Ros.Infrastructure.Artifacts

open System
open System.IO
open System.Text
open Ros.Application.Artifacts
open Ros.Domain.Artifacts

[<RequireQualifiedAccess>]
module FileArtifactRepository =
    let private normalizePath (value: string) = value.Replace('\\', '/')

    let private dependencyFailure operation path outcome (error: exn) =
        { Operation = operation
          Path = path
          Message = error.Message
          Outcome = outcome }

    let private enumerateArtifacts root =
        try
            ArtifactKinds.configurations
            |> List.collect (fun configuration ->
                let directory = Path.Combine(root, configuration.SourceDirectory)

                if not (Directory.Exists directory) then
                    []
                else
                    Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    |> Seq.filter (fun file ->
                        Path.GetExtension(file) = ".md"
                        && not (Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal)))
                    |> Seq.map (fun file -> Path.GetRelativePath(root, file) |> normalizePath)
                    |> Seq.toList)
            |> Set.ofList
            |> Set.toList
            |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
            |> Ok
        with error ->
            dependencyFailure "enumerate artifact files" None DependencyOutcome.Failed error
            |> Error

    let private load root =
        match enumerateArtifacts root with
        | Error failure -> Error failure
        | Ok paths ->
            let documents = ResizeArray<ArtifactDocument>()
            let findings = ResizeArray<ArtifactFinding>()

            for relativePath in paths do
                try
                    let text = File.ReadAllText(Path.Combine(root, relativePath), Encoding.UTF8)

                    match FrontMatter.parse relativePath text with
                    | Ok document -> documents.Add document
                    | Error message ->
                        findings.Add(
                            { Path = relativePath
                              Field = "front_matter"
                              Message = message }
                        )
                with error ->
                    findings.Add(
                        { Path = relativePath
                          Field = "front_matter"
                          Message = error.Message }
                    )

            Ok
                { Documents = documents |> Seq.toList
                  ParseFindings = findings |> Seq.toList }

    let private readRegistry root relativePath =
        try
            let file = Path.Combine(root, relativePath)
            if File.Exists file then Ok(Some(File.ReadAllText(file, Encoding.UTF8))) else Ok None
        with error ->
            dependencyFailure "read registry" (Some relativePath) DependencyOutcome.Failed error
            |> Error

    let private writeRegistry root relativePath (content: string) =
        RegistryTransaction.writeAtomic (Path.Combine(root, relativePath)) content

    let create root =
        let repositoryRoot = Path.GetFullPath root

        { Load = fun () -> load repositoryRoot
          ReadRegistry = readRegistry repositoryRoot
          WriteRegistry = writeRegistry repositoryRoot
          AcquireRegistryWriteLease =
            fun () ->
                match RegistryLock.acquire repositoryRoot "artifact-registries" RegistryLock.defaultSettings with
                | Error failure -> Error failure
                | Ok lock ->
                    Ok
                        { Recover = fun () -> RegistryTransaction.recover repositoryRoot
                          Prepare = RegistryTransaction.prepare repositoryRoot
                          Complete = fun () -> RegistryTransaction.complete repositoryRoot
                          Release = lock.Release } }
