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
    | Requirement

type ArtifactKindConfiguration =
    { Kind: ArtifactKind
      Name: string
      SourceDirectory: string
      RegistryPath: string
      IdentifierPrefix: string
      /// A kind introduced after repositories were already installed: with
      /// no documents its registry file is optional, so upgrading does not
      /// make every existing installation's registries "stale".
      OptionalRegistry: bool }

[<RequireQualifiedAccess>]
module ArtifactKinds =
    let configurations =
        [ { Kind = ArtifactKind.Decision
            Name = "decisions"
            SourceDirectory = "research/decisions"
            RegistryPath = "registries/decisions.json"
            IdentifierPrefix = "DF"
            OptionalRegistry = false }
          { Kind = ArtifactKind.Evidence
            Name = "evidence"
            SourceDirectory = "research/evidence"
            RegistryPath = "registries/evidence.json"
            IdentifierPrefix = "EV"
            OptionalRegistry = false }
          { Kind = ArtifactKind.Experiment
            Name = "experiments"
            SourceDirectory = "research/experiments"
            RegistryPath = "registries/experiments.json"
            IdentifierPrefix = "EX"
            OptionalRegistry = false }
          { Kind = ArtifactKind.Hypothesis
            Name = "hypotheses"
            SourceDirectory = "research/hypotheses"
            RegistryPath = "registries/hypotheses.json"
            IdentifierPrefix = "HY"
            OptionalRegistry = false }
          { Kind = ArtifactKind.Journal
            Name = "journals"
            SourceDirectory = "research/journals"
            RegistryPath = "registries/journals.json"
            IdentifierPrefix = "JR"
            OptionalRegistry = false }
          { Kind = ArtifactKind.Mission
            Name = "missions"
            SourceDirectory = "missions"
            RegistryPath = "registries/missions.json"
            IdentifierPrefix = "MS"
            OptionalRegistry = false }
          { Kind = ArtifactKind.ResearchPackage
            Name = "research-packages"
            SourceDirectory = "research/packages"
            RegistryPath = "registries/research-packages.json"
            IdentifierPrefix = "RP"
            OptionalRegistry = false }
          { Kind = ArtifactKind.Theory
            Name = "theories"
            SourceDirectory = "research/theories"
            RegistryPath = "registries/theories.json"
            IdentifierPrefix = "TH"
            OptionalRegistry = false }
          { Kind = ArtifactKind.Requirement
            Name = "requirements"
            SourceDirectory = "research/requirements"
            RegistryPath = "registries/requirements.json"
            IdentifierPrefix = "RQ"
            OptionalRegistry = true } ]

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
