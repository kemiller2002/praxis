namespace Ros.Contracts.Execution

open System.Text.Json.Nodes
open Ros.Contracts.Work
open Ros.Domain.Execution

/// The versioned control-plane execution contract (`praxis.execution-state`,
/// PRX-CTL-005/011/012) served by `GET /api/v1/executions` and
/// `GET /api/v1/executions/ID`; documented in `docs/web-interface.md`. The
/// provider, model and runtime are execution-host information: they appear
/// only under `executionHost`, never under `actor`, and `workStateOwner`
/// names the repository as the owner of the work state. Receipts keep the
/// `ordo.execution/1` shapes of `ExecutionJson`.
[<RequireQualifiedAccess>]
module ExecutionStateJson =
    let contract = "praxis.execution-state"
    let version = 1

    let private text (value: string) : JsonNode = JsonValue.Create value
    let private optionalText (value: string option) : JsonNode = value |> Option.map text |> Option.toObj
    let private integer (value: int) : JsonNode = JsonValue.Create value
    let private boolean (value: bool) : JsonNode = JsonValue.Create value

    let private array (values: JsonNode list) : JsonNode =
        let result = JsonArray()
        values |> List.iter result.Add
        result

    let private record (fields: (string * JsonNode) list) : JsonObject =
        let result = JsonObject()
        fields |> List.iter (fun (name, value) -> result[name] <- value)
        result

    let private stateReason (state: ExecutionState) =
        match state with
        | ExecutionState.Blocked reason
        | ExecutionState.Failed reason
        | ExecutionState.Abandoned reason -> Some reason
        | _ -> None

    let private hostNode (envelope: ExecutionEnvelope) : JsonNode =
        let host = ExecutionHost.ofActor envelope.Actor

        record
            [ "provider", text host.Provider
              "model", text host.Model
              "runtime", text host.Runtime
              "workspace",
              (envelope.Workspace
               |> Option.map (fun workspace ->
                   record [ "id", text workspace.Id; "mechanism", text workspace.Mechanism; "branch", optionalText workspace.Branch ] :> JsonNode)
               |> Option.toObj)
              "containment", text (Containment.toWire envelope.Containment)
              "securitySandbox", boolean (Containment.isSecuritySandbox envelope.Containment) ]

    /// The fields every execution presentation carries.
    let summary (envelope: ExecutionEnvelope) : JsonObject =
        record
            [ "executionId", text envelope.ExecutionId
              "workItem", text envelope.WorkItem
              "role", text (ExecutionRole.toWire envelope.Authority.Role)
              "status", text (ExecutionState.toWire envelope.State)
              "statusReason", optionalText (stateReason envelope.State)
              "startedAt", text (ExecutionJson.timestamp envelope.StartedAt)
              "parentExecution", optionalText envelope.Parent
              "baselineRevision", text envelope.BaselineRevision
              "candidateRevision", optionalText envelope.CandidateRevision
              "actor", record [ "id", text envelope.Actor.Id; "kind", text envelope.Actor.Kind ]
              "executionHost", hostNode envelope
              "workStateOwner", record [ "kind", text "repository"; "workItem", text envelope.WorkItem ] ]

    let private reconciliationNode (reconciliation: StepReconciliation) : JsonNode =
        let finding, detail =
            match reconciliation.Finding with
            | Reconciliation.Occurred evidence -> "occurred", evidence
            | Reconciliation.DidNotOccur evidence -> "did-not-occur", evidence
            | Reconciliation.StillUnknown reason -> "unknown", reason

        record
            [ "attempt", integer reconciliation.Attempt
              "finding", text finding
              "detail", text detail
              "at", text (ExecutionJson.timestamp reconciliation.At) ]

    let private receiptNode (receipt: StepReceipt) : JsonNode =
        let observation = receipt.Observation

        record
            [ "state", text (ReceiptState.toWire receipt.State)
              "attempt", integer receipt.Attempt
              "expected", ExecutionJson.expected receipt.Expected
              "observed", observation |> Option.map (fun o -> ExecutionJson.observed o.Observed) |> Option.toObj
              "comparison", observation |> Option.map (fun o -> ExecutionJson.result o.Comparison) |> Option.toObj
              "observedAt", observation |> Option.map (fun o -> text (ExecutionJson.timestamp o.At)) |> Option.toObj
              "reason", observation |> Option.bind (fun o -> o.Comparison.Reason) |> optionalText ]

    let step (view: StepView) : JsonNode =
        record
            [ "stepId", text view.StepId
              "sequence", integer view.Sequence
              "name", text view.Name
              "dependsOn", view.DependsOn |> List.map text |> array
              "attempts", integer view.Attempts
              "status", text (StepStatus.toWire view.Status)
              "receipt", receiptNode (StepReceipt.current view)
              "reconciliation", StepReceipt.reconciliation view |> Option.map reconciliationNode |> Option.toObj ]

    let private envelope (kind: string) (fields: (string * JsonNode) list) : JsonNode =
        record ([ "contract", text contract; "version", integer version; "kind", text kind ] @ fields)

    /// `unreadable` names stored executions whose envelope could not be read.
    let listDocument (workItem: string option) (executions: ExecutionEnvelope list) (unreadable: (string * string) list) : JsonNode =
        envelope
            "execution-list"
            [ "workItem", optionalText workItem
              "items", executions |> List.map (fun execution -> summary execution :> JsonNode) |> array
              "unreadable",
              unreadable
              |> List.map (fun (executionId, error) -> record [ "executionId", text executionId; "error", text error ] :> JsonNode)
              |> array ]

    let executionDocument
        (execution: ExecutionEnvelope)
        (steps: StepView list)
        (effects: ScopeEffect list)
        (verification: EvaluationOutcome option)
        : JsonNode =
        let node = summary execution
        node["steps"] <- steps |> List.map step |> array
        node["scopeEffects"] <- effects |> List.map ExecutionJson.scopeEffect |> array
        node["verification"] <- verification |> Option.map (EvaluationOutcome.toWire >> text) |> Option.toObj
        envelope "execution" [ "execution", node ]

    let executionNotFound (executionId: string) : JsonNode =
        WorkStateJson.errorDocument "execution-not-found" $"execution '{executionId}' was not found" [ "executionId", text executionId ]

    let executionUnreadable (executionId: string) (reason: string) : JsonNode =
        WorkStateJson.errorDocument "execution-unreadable" $"execution '{executionId}' could not be read: {reason}" [ "executionId", text executionId ]
