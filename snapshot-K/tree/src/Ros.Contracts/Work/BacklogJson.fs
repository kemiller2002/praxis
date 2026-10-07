namespace Ros.Contracts.Work

open Ros.Contracts
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module BacklogContract =
    let private writeFieldChange
        (writer: System.Text.Json.Utf8JsonWriter)
        (name: string)
        (change: BacklogFieldChange)
        =
        writer.WriteStartObject(name)

        match change with
        | BacklogFieldChange.Keep -> writer.WriteString("kind", "keep")
        | BacklogFieldChange.Clear -> writer.WriteString("kind", "clear")
        | BacklogFieldChange.Set value ->
            writer.WriteString("kind", "set")
            writer.WriteString("value", value)

        writer.WriteEndObject()

    let stateName state =
        match state with
        | BacklogState.Captured -> "captured"
        | BacklogState.Ready -> "ready"
        | BacklogState.Blocked -> "blocked"
        | BacklogState.Abandoned -> "abandoned"

    let actionName action =
        match action with
        | BacklogAction.Ready -> "ready"
        | BacklogAction.Block -> "block"
        | BacklogAction.Abandon -> "abandon"
        | BacklogAction.Start -> "start"

    let renderDecisionJson decision =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schemaVersion", "1.0.0")

            match decision with
            | BacklogTransitionDecision.Allowed effect ->
                writer.WriteString("outcome", "allowed")
                writer.WriteStartObject("effect")

                match effect with
                | BacklogTransitionEffect.PromoteToLiveWork ->
                    writer.WriteString("kind", "promote-to-live-work")
                | BacklogTransitionEffect.ChangeState(state, blockedReason, abandonedReason) ->
                    writer.WriteString("kind", "change-state")
                    writer.WriteString("state", stateName state)
                    writeFieldChange writer "blockedReason" blockedReason
                    writeFieldChange writer "abandonedReason" abandonedReason

                writer.WriteEndObject()
                writer.WriteNull("rejection")
            | BacklogTransitionDecision.Rejected rejection ->
                writer.WriteString("outcome", "rejected")
                writer.WriteNull("effect")
                writer.WriteStartObject("rejection")

                match rejection with
                | BacklogTransitionRejection.BlockReasonRequired ->
                    writer.WriteString("reason", "block-reason-required")
                | BacklogTransitionRejection.IllegalTransition(state, action) ->
                    writer.WriteString("reason", "illegal-transition")
                    writer.WriteString("state", stateName state)
                    writer.WriteString("action", actionName action)

                writer.WriteEndObject()

            writer.WriteEndObject())

    let renderPromotionJson outcome =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schemaVersion", "1.0.0")

            match outcome with
            | BacklogPromotionOutcome.Planned plan ->
                writer.WriteString("outcome", "planned")
                writer.WriteStartObject("plan")
                writer.WriteStartArray("workItems")
                plan.WorkItemIds |> List.iter writer.WriteStringValue
                writer.WriteEndArray()
                writer.WriteString("workType", plan.WorkType)
                writer.WriteEndObject()
                writer.WriteNull("rejection")
            | BacklogPromotionOutcome.Rejected rejection ->
                writer.WriteString("outcome", "rejected")
                writer.WriteNull("plan")
                writer.WriteStartObject("rejection")

                match rejection with
                | BacklogPromotionRejection.NoWorkItems -> writer.WriteString("reason", "no-work-items")
                | BacklogPromotionRejection.InvalidWorkItemId workItemId ->
                    writer.WriteString("reason", "invalid-work-item-id")
                    writer.WriteString("workItem", workItemId)
                | BacklogPromotionRejection.BacklogItemNotReady(workItemId, state) ->
                    writer.WriteString("reason", "backlog-item-not-ready")
                    writer.WriteString("workItem", workItemId)
                    writer.WriteString("state", stateName state)

                writer.WriteEndObject()

            writer.WriteEndObject())
