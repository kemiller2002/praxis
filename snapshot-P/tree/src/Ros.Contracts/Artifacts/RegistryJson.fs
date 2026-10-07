namespace Ros.Contracts.Artifacts

open System
open System.Text.Json
open Ros.Contracts
open Ros.Domain.Artifacts

[<RequireQualifiedAccess>]
module RegistryJson =
    let rec private writeValue (writer: Utf8JsonWriter) value =
        match value with
        | ArtifactValue.Text text -> writer.WriteStringValue text
        | ArtifactValue.Number number -> writer.WriteNumberValue number
        | ArtifactValue.Boolean boolean -> writer.WriteBooleanValue boolean
        | ArtifactValue.Sequence values ->
            writer.WriteStartArray()
            for item in values do
                writeValue writer item
            writer.WriteEndArray()
        | ArtifactValue.Mapping mapping ->
            writer.WriteStartObject()

            mapping
            |> Map.toList
            |> List.sortWith (fun (left, _) (right, _) -> StringComparer.Ordinal.Compare(left, right))
            |> List.iter (fun (name, item) ->
                writer.WritePropertyName name
                writeValue writer item)

            writer.WriteEndObject()

    let private entry document =
        document.Metadata
        |> Map.remove "identifier"
        |> Map.add "id" (ArtifactValue.Text(ArtifactDocument.identifier document))
        |> Map.add "path" (ArtifactValue.Text document.RelativePath)

    let render documents =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartArray()

            for document in documents do
                writeValue writer (ArtifactValue.Mapping(entry document))

            writer.WriteEndArray())
