namespace Ros.Domain.Work

/// Pure decision logic for the external work-system adapter contract
/// (`DF-ROS-2026-A007`, `docs/work-adapter-contract.md`,
/// `schemas/work-adapter-request.schema.json`/
/// `work-adapter-result.schema.json`), mirroring production's
/// `validateAdapterRequest`/`callFileAdapter` in `tools/ros_cli.mjs`.
/// Work items and events themselves are caller-defined, open-ended JSON
/// (only a handful of fields are ever inspected), so this module decides
/// *what happened* from those few fields; the actual JSON store
/// read/mutate/write stays in `Ros.Infrastructure.Work.FileAdapterRepository`.
[<RequireQualifiedAccess>]
module AdapterContract =
    /// The three operations `schemas/work-adapter-request.schema.json` allows.
    let supportedOperations = set [ "getWorkItem"; "transitionWorkItem"; "publishRepositoryEvent" ]

    /// Production's `validateAdapterRequest`'s first loop: a raw thrown
    /// `Error`, not a structured result, and never touches the store file.
    let requiredFields = [ "protocolVersion"; "requestId"; "operation"; "repository"; "principal" ]

    type RequestFields =
        { ProtocolVersion: string option
          RequestId: string option
          Operation: string option
          Repository: string option
          Principal: string option }

    /// The first missing/empty required field, in production's own checked
    /// order, else `None`.
    let firstMissingField (fields: RequestFields) : string option =
        requiredFields
        |> List.tryFind (fun field ->
            let value =
                match field with
                | "protocolVersion" -> fields.ProtocolVersion
                | "requestId" -> fields.RequestId
                | "operation" -> fields.Operation
                | "repository" -> fields.Repository
                | "principal" -> fields.Principal
                | _ -> None

            match value with
            | None -> true
            | Some text -> text = "")

    /// What `callFileAdapter` decided to do, once a request has passed the
    /// missing-required-field check above (that check happens before the
    /// store is ever loaded, so it is not represented as a decision here).
    [<RequireQualifiedAccess>]
    type AdapterDecision =
        | ProtocolMismatch
        | UnsupportedOperation of operation: string
        | ReplayCached
        | RepositoryUnknown
        | RemoteOutcomeUnknown
        | ReadForbidden
        | WorkItemNotFound
        | ReadSuccess
        | TransitionForbidden
        | TransitionConflict of expected: string * actual: string
        | TransitionSuccess
        | PublishForbidden
        | InvalidEvent
        | PublishSuccess

    type DecisionInput =
        { ProtocolVersion: string
          Operation: string
          Repository: string
          AuthorizedRepositories: Set<string>
          Scopes: Set<string>
          SimulateOutcome: string option
          AlreadyRequested: bool
          WorkItemExists: bool
          CurrentState: string option
          ExpectedState: string option
          EventIdPresent: bool }

    /// Mirrors `callFileAdapter`'s exact branch order: production checks
    /// protocol version and operation support via `validateAdapterRequest`
    /// *before* the store is ever loaded (so a malformed request never
    /// creates or reads the store file); only once past that does it load
    /// the store, where a cached replay wins over everything else (no store
    /// mutation at all), then repository authorization, then fault injection
    /// (regardless of operation or scope), then each operation's own
    /// forbidden/not-found/conflict checks.
    let decide (input: DecisionInput) : AdapterDecision =
        if input.ProtocolVersion <> "1.0.0" then
            AdapterDecision.ProtocolMismatch
        elif not (supportedOperations.Contains input.Operation) then
            AdapterDecision.UnsupportedOperation input.Operation
        elif input.AlreadyRequested then
            AdapterDecision.ReplayCached
        elif not (input.AuthorizedRepositories.Contains input.Repository) then
            AdapterDecision.RepositoryUnknown
        elif input.SimulateOutcome = Some "unknown" then
            AdapterDecision.RemoteOutcomeUnknown
        else
            match input.Operation with
            | "getWorkItem" ->
                if not (input.Scopes.Contains "work:read") then AdapterDecision.ReadForbidden
                elif not input.WorkItemExists then AdapterDecision.WorkItemNotFound
                else AdapterDecision.ReadSuccess
            | "transitionWorkItem" ->
                if not (input.Scopes.Contains "work:transition") then
                    AdapterDecision.TransitionForbidden
                elif not input.WorkItemExists then
                    AdapterDecision.WorkItemNotFound
                else
                    match input.ExpectedState, input.CurrentState with
                    | Some expected, Some actual when expected <> actual -> AdapterDecision.TransitionConflict(expected, actual)
                    | _ -> AdapterDecision.TransitionSuccess
            | _ ->
                // publishRepositoryEvent: the only remaining supported operation.
                if not (input.Scopes.Contains "event:publish") then AdapterDecision.PublishForbidden
                elif not input.EventIdPresent then AdapterDecision.InvalidEvent
                else AdapterDecision.PublishSuccess
