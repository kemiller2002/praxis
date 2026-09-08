namespace Ros.Contracts.Work

open Ros.Contracts
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkDecisionContract =
    let stateName state =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"

    let actionName action =
        match action with
        | WorkAction.Begin -> "begin"
        | WorkAction.Block -> "block"
        | WorkAction.Resume -> "resume"
        | WorkAction.Complete -> "complete"

    let writeRejection (writer: System.Text.Json.Utf8JsonWriter) rejection =
        match rejection with
        | TransitionRejection.IllegalTransition(state, action) ->
            writer.WriteString("reason", "illegal-transition")
            writer.WriteString("state", stateName state)
            writer.WriteString("action", actionName action)
            writer.WriteStartArray("missingEvidence")
            writer.WriteEndArray()
        | TransitionRejection.BlockReasonRequired ->
            writer.WriteString("reason", "block-reason-required")
            writer.WriteStartArray("missingEvidence")
            writer.WriteEndArray()
        | TransitionRejection.MissingEvidence missing ->
            writer.WriteString("reason", "missing-evidence")
            writer.WriteStartArray("missingEvidence")
            missing |> List.iter writer.WriteStringValue
            writer.WriteEndArray()

    let renderJson decision =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schemaVersion", "1.0.0")

            match decision with
            | TransitionDecision.Allowed target ->
                writer.WriteString("outcome", "allowed")
                writer.WriteString("targetState", stateName target)
                writer.WriteNull("rejection")
            | TransitionDecision.Rejected rejection ->
                writer.WriteString("outcome", "rejected")
                writer.WriteNull("targetState")
                writer.WriteStartObject("rejection")
                writeRejection writer rejection
                writer.WriteEndObject()

            writer.WriteEndObject())
