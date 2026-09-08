namespace Ros.Domain.Artifacts

open System
open System.Globalization

[<RequireQualifiedAccess>]
type ArtifactValue =
    | Text of string
    | Number of float
    | Boolean of bool
    | Sequence of ArtifactValue list
    | Mapping of Map<string, ArtifactValue>

[<RequireQualifiedAccess>]
module ArtifactValue =
    let rec display value =
        match value with
        | ArtifactValue.Text text -> text
        | ArtifactValue.Number number -> number.ToString("G17", CultureInfo.InvariantCulture)
        | ArtifactValue.Boolean boolean -> if boolean then "true" else "false"
        | ArtifactValue.Sequence values -> values |> List.map display |> String.concat ","
        | ArtifactValue.Mapping _ -> "[object Object]"

    let isJavaScriptTruthy value =
        match value with
        | ArtifactValue.Text text -> text.Length > 0
        | ArtifactValue.Number number -> number <> 0.0 && not (Double.IsNaN number)
        | ArtifactValue.Boolean boolean -> boolean
        | ArtifactValue.Sequence _
        | ArtifactValue.Mapping _ -> true

type ArtifactDocument =
    { RelativePath: string
      FileName: string
      Metadata: Map<string, ArtifactValue> }

type ArtifactFinding =
    { Path: string
      Field: string
      Message: string }

[<RequireQualifiedAccess>]
type ArtifactKind =
    | Decision
    | Evidence
    | Experiment
    | Hypothesis
    | Journal
    | Mission
    | ResearchPackage
    | Theory

type ArtifactKindConfiguration =
    { Kind: ArtifactKind
      Name: string
      SourceDirectory: string
      RegistryPath: string
      IdentifierPrefix: string }

[<RequireQualifiedAccess>]
module ArtifactKinds =
    let configurations =
        [ { Kind = ArtifactKind.Decision
            Name = "decisions"
            SourceDirectory = "research/decisions"
            RegistryPath = "registries/decisions.json"
            IdentifierPrefix = "DF" }
          { Kind = ArtifactKind.Evidence
            Name = "evidence"
            SourceDirectory = "research/evidence"
            RegistryPath = "registries/evidence.json"
            IdentifierPrefix = "EV" }
          { Kind = ArtifactKind.Experiment
            Name = "experiments"
            SourceDirectory = "research/experiments"
            RegistryPath = "registries/experiments.json"
            IdentifierPrefix = "EX" }
          { Kind = ArtifactKind.Hypothesis
            Name = "hypotheses"
            SourceDirectory = "research/hypotheses"
            RegistryPath = "registries/hypotheses.json"
            IdentifierPrefix = "HY" }
          { Kind = ArtifactKind.Journal
            Name = "journals"
            SourceDirectory = "research/journals"
            RegistryPath = "registries/journals.json"
            IdentifierPrefix = "JR" }
          { Kind = ArtifactKind.Mission
            Name = "missions"
            SourceDirectory = "missions"
            RegistryPath = "registries/missions.json"
            IdentifierPrefix = "MS" }
          { Kind = ArtifactKind.ResearchPackage
            Name = "research-packages"
            SourceDirectory = "research/packages"
            RegistryPath = "registries/research-packages.json"
            IdentifierPrefix = "RP" }
          { Kind = ArtifactKind.Theory
            Name = "theories"
            SourceDirectory = "research/theories"
            RegistryPath = "registries/theories.json"
            IdentifierPrefix = "TH" } ]

    let identifierPrefix (identifier: string) =
        let separator = identifier.IndexOf('-')
        if separator < 0 then "" else identifier[.. separator - 1]

    let tryFindByIdentifier identifier =
        let prefix = identifierPrefix identifier
        configurations |> List.tryFind (fun configuration -> configuration.IdentifierPrefix = prefix)

[<RequireQualifiedAccess>]
module ArtifactDocument =
    let tryMetadata field document = document.Metadata |> Map.tryFind field

    let identifier document =
        document.Metadata
        |> Map.tryFind "id"
        |> Option.orElseWith (fun () -> document.Metadata |> Map.tryFind "identifier")
        |> Option.map ArtifactValue.display
        |> Option.defaultValue ""

type RegistryProjection =
    { RegistryPath: string
      Documents: ArtifactDocument list }
