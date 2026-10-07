namespace Ros.Contracts.Git

open System.Text.Json
open Ros.Contracts
open Ros.Domain.Git

[<RequireQualifiedAccess>]
module GitStatusContract =
    let private deltaName delta =
        match delta with
        | GitDelta.Unmodified -> "unmodified"
        | GitDelta.Added -> "added"
        | GitDelta.Modified -> "modified"
        | GitDelta.Deleted -> "deleted"
        | GitDelta.Renamed -> "renamed"
        | GitDelta.Copied -> "copied"
        | GitDelta.TypeChanged -> "type-changed"
        | GitDelta.Unmerged -> "unmerged"
        | GitDelta.Unknown value -> $"unknown:{value}"

    let private reasonName reason =
        match reason with
        | GitUnavailableReason.ToolUnavailable -> "tool-unavailable"
        | GitUnavailableReason.NotRepository -> "not-repository"
        | GitUnavailableReason.CommandFailed -> "command-failed"
        | GitUnavailableReason.MalformedOutput -> "malformed-output"

    let private writeChange (writer: Utf8JsonWriter) change =
        writer.WriteStartObject()
        writer.WriteString("code", GitStatus.code change.Status)

        match change.Status with
        | GitChangeStatus.Tracked(index, workTree) ->
            writer.WriteString("kind", "tracked")
            writer.WriteString("index", deltaName index)
            writer.WriteString("workTree", deltaName workTree)
        | GitChangeStatus.Untracked -> writer.WriteString("kind", "untracked")
        | GitChangeStatus.Ignored -> writer.WriteString("kind", "ignored")

        writer.WriteString("path", change.Path)

        match change.OriginalPath with
        | Some originalPath -> writer.WriteString("originalPath", originalPath)
        | None -> writer.WriteNull("originalPath")

        writer.WriteEndObject()

    let renderJson observation =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schemaVersion", "1.0.0")

            match observation with
            | GitStatusObservation.Clean ->
                writer.WriteString("outcome", "clean")
                writer.WriteStartArray("changes")
                writer.WriteEndArray()
                writer.WriteNull("failure")
            | GitStatusObservation.Changed changes ->
                writer.WriteString("outcome", "changed")
                writer.WriteStartArray("changes")
                changes |> List.iter (writeChange writer)
                writer.WriteEndArray()
                writer.WriteNull("failure")
            | GitStatusObservation.Unavailable failure ->
                writer.WriteString("outcome", "unavailable")
                writer.WriteStartArray("changes")
                writer.WriteEndArray()
                writer.WriteStartObject("failure")
                writer.WriteString("operation", failure.Operation)
                writer.WriteString("reason", reasonName failure.Reason)
                writer.WriteString("message", failure.Message)

                match failure.ExitCode with
                | Some exitCode -> writer.WriteNumber("exitCode", exitCode)
                | None -> writer.WriteNull("exitCode")

                writer.WriteEndObject()

            writer.WriteStartObject("source")
            writer.WriteString("tool", "git")
            writer.WriteString("command", "status")
            writer.WriteString("format", "porcelain-v1-z")
            writer.WriteEndObject()
            writer.WriteEndObject())
