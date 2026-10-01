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

    let tests =
        [ { Name = "production project references point inward"
            Run = fun () -> productionGraph () |> validateGraph |> Assert.empty }
          { Name = "architecture guard rejects an outward Domain dependency"
            Run =
              fun () ->
                  let invalid = productionGraph () |> Map.add "Praxis.Domain" (Set.singleton "Praxis.Infrastructure")
                  let findings = invalid |> validateGraph
                  Assert.equal 2 findings.Length

                  if findings |> List.exists (fun finding -> not (finding.Contains("Praxis.Domain", StringComparison.Ordinal))) then
                      failwith $"Unexpected findings: {findings}" } ]
