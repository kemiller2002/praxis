namespace Praxis.Tests

open System
open System.IO
open System.Xml.Linq

[<RequireQualifiedAccess>]
module ArchitectureTests =
    let private expectedReferences =
        Map.ofList
            [ "Praxis.Domain", Set.empty
              "Praxis.Contracts", Set.ofList [ "Praxis.Domain" ]
              "Praxis.Application", Set.ofList [ "Praxis.Contracts"; "Praxis.Domain" ]
              "Praxis.Infrastructure", Set.ofList [ "Praxis.Application"; "Praxis.Contracts"; "Praxis.Domain" ]
              "Praxis.Cli", Set.ofList [ "Praxis.Application"; "Praxis.Contracts"; "Praxis.Domain"; "Praxis.Infrastructure" ] ]

    let private validateGraph (graph: Map<string, Set<string>>) =
        expectedReferences
        |> Map.toList
        |> List.collect (fun (project, expected) ->
            let actual = graph |> Map.tryFind project |> Option.defaultValue Set.empty

            [ if actual <> expected then
                  yield $"{project} references {actual}; expected {expected}"

              if project = "Praxis.Domain" && not actual.IsEmpty then
                  yield "Praxis.Domain must not reference an outward project"

              if project = "Praxis.Application"
                 && (actual.Contains "Praxis.Infrastructure" || actual.Contains "Praxis.Cli") then
                  yield "Praxis.Application must not reference Infrastructure or CLI" ])

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "src", "Praxis.Domain")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private readReferences root project =
        let file = Path.Combine(root, "src", project, $"{project}.fsproj")
        let document = XDocument.Load(file)

        document.Descendants(XName.Get "ProjectReference")
        |> Seq.choose (fun element ->
            let attribute = element.Attribute(XName.Get "Include")
            if isNull attribute then None else Some(Path.GetFileNameWithoutExtension(attribute.Value)))
        |> Set.ofSeq

    let private productionGraph () =
        let root = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))

        expectedReferences
        |> Map.toList
        |> List.map (fun (project, _) -> project, readReferences root project)
        |> Map.ofList


    let private pacingBoundaryFindings () =
        let root = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))
        let read relative = File.ReadAllText(Path.Combine(root, relative))

        let cliCommands = read "src/Praxis.Cli/PacingCommands.fs"

        // Every Application pacing source, so a new file cannot escape the rule.
        let applicationSources =
            Directory.GetFiles(Path.Combine(root, "src", "Praxis.Application", "Pacing"), "*.fs")
            |> Array.sort
            |> Array.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText path)
            |> Array.toList

        let forbiddenCli =
            [ "System.Net.Http"
              "HttpClient"
              "ProcessStartInfo"
              "File."
              "Directory."
              "FileStream"
              "Thread.Sleep"
              "find-generic-password"
              "app-server"
              "normalizeCodexResult"
              "normalizeClaudeUsage" ]

        let forbiddenApplication =
            [ "System.IO"
              "System.Diagnostics"
              "System.Net.Http"
              "HttpClient"
              "ProcessStartInfo"
              "File."
              "Directory."
              "FileStream"
              "Thread.Sleep"
              "find-generic-password"
              "app-server" ]

        [ for marker in forbiddenCli do
              if cliCommands.Contains(marker, StringComparison.Ordinal) then
                  yield $"PacingCommands.fs owns forbidden adapter concern '{marker}'"

          for path, source in applicationSources do
              for marker in forbiddenApplication do
                  if source.Contains(marker, StringComparison.Ordinal) then
                      yield $"{path} owns forbidden adapter concern '{marker}'" ]

    let tests =
        [ { Name = "pacing adapters stay outside CLI and Application"
            Run = fun () -> pacingBoundaryFindings () |> Assert.empty }
          { Name = "production project references point inward"
            Run = fun () -> productionGraph () |> validateGraph |> Assert.empty }
          { Name = "architecture guard rejects an outward Domain dependency"
            Run =
              fun () ->
                  let invalid = productionGraph () |> Map.add "Praxis.Domain" (Set.singleton "Praxis.Infrastructure")
                  let findings = invalid |> validateGraph
                  Assert.equal 2 findings.Length

                  if findings |> List.exists (fun finding -> not (finding.Contains("Praxis.Domain", StringComparison.Ordinal))) then
                      failwith $"Unexpected findings: {findings}" } ]
