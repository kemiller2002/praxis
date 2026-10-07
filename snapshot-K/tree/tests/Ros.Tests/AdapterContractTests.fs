namespace Ros.Tests

open Ros.Domain.Work

[<RequireQualifiedAccess>]
module AdapterContractTests =
    let private baseInput : AdapterContract.DecisionInput =
        { ProtocolVersion = "1.0.0"
          Operation = "transitionWorkItem"
          Repository = "protocol-consumer"
          AuthorizedRepositories = Set.singleton "protocol-consumer"
          Scopes = Set.singleton "work:transition"
          SimulateOutcome = None
          AlreadyRequested = false
          WorkItemExists = true
          CurrentState = Some "ready"
          ExpectedState = Some "ready"
          EventIdPresent = false }

    let private fields protocolVersion requestId operation repository principal : AdapterContract.RequestFields =
        { ProtocolVersion = protocolVersion
          RequestId = requestId
          Operation = operation
          Repository = repository
          Principal = principal }

    let tests =
        [ { Name = "firstMissingField finds the first missing required field in production's own checked order"
            Run =
              fun () ->
                  Assert.equal (Some "protocolVersion") (AdapterContract.firstMissingField(fields None (Some "r") (Some "op") (Some "repo") (Some "p")))
                  Assert.equal (Some "requestId") (AdapterContract.firstMissingField(fields (Some "1.0.0") None (Some "op") (Some "repo") (Some "p")))
                  Assert.equal (Some "principal") (AdapterContract.firstMissingField(fields (Some "1.0.0") (Some "r") (Some "op") (Some "repo") (Some ""))) }

          { Name = "firstMissingField is None when every required field is present and non-empty"
            Run =
              fun () ->
                  Assert.equal None (AdapterContract.firstMissingField(fields (Some "1.0.0") (Some "r") (Some "op") (Some "repo") (Some "p"))) }

          { Name = "decide rejects a protocol-version mismatch before checking anything else"
            Run =
              fun () ->
                  let input = { baseInput with ProtocolVersion = "2.0.0"; AlreadyRequested = true }
                  Assert.equal AdapterContract.AdapterDecision.ProtocolMismatch (AdapterContract.decide input) }

          { Name = "decide rejects an unsupported operation before checking anything else"
            Run =
              fun () ->
                  let input = { baseInput with Operation = "deleteWorkItem"; AlreadyRequested = true }
                  Assert.equal (AdapterContract.AdapterDecision.UnsupportedOperation "deleteWorkItem") (AdapterContract.decide input) }

          { Name = "decide replays a cached request before checking repository authorization"
            Run =
              fun () ->
                  let input = { baseInput with AlreadyRequested = true; AuthorizedRepositories = Set.empty }
                  Assert.equal AdapterContract.AdapterDecision.ReplayCached (AdapterContract.decide input) }

          { Name = "decide rejects an unauthorized repository before fault injection or operation checks"
            Run =
              fun () ->
                  let input = { baseInput with AuthorizedRepositories = Set.empty; SimulateOutcome = Some "unknown" }
                  Assert.equal AdapterContract.AdapterDecision.RepositoryUnknown (AdapterContract.decide input) }

          { Name = "decide honors simulateOutcome=unknown regardless of operation or scope"
            Run =
              fun () ->
                  let input = { baseInput with SimulateOutcome = Some "unknown"; Scopes = Set.empty }
                  Assert.equal AdapterContract.AdapterDecision.RemoteOutcomeUnknown (AdapterContract.decide input) }

          { Name = "decide rejects getWorkItem without work:read scope"
            Run =
              fun () ->
                  let input = { baseInput with Operation = "getWorkItem"; Scopes = Set.empty }
                  Assert.equal AdapterContract.AdapterDecision.ReadForbidden (AdapterContract.decide input) }

          { Name = "decide reports a missing work item for getWorkItem"
            Run =
              fun () ->
                  let input =
                      { baseInput with
                          Operation = "getWorkItem"
                          Scopes = Set.singleton "work:read"
                          WorkItemExists = false }

                  Assert.equal AdapterContract.AdapterDecision.WorkItemNotFound (AdapterContract.decide input) }

          { Name = "decide succeeds reading an existing work item with the read scope"
            Run =
              fun () ->
                  let input = { baseInput with Operation = "getWorkItem"; Scopes = Set.singleton "work:read" }
                  Assert.equal AdapterContract.AdapterDecision.ReadSuccess (AdapterContract.decide input) }

          { Name = "decide rejects transitionWorkItem without work:transition scope, even before checking the work item exists"
            Run =
              fun () ->
                  let input = { baseInput with Scopes = Set.empty; WorkItemExists = false }
                  Assert.equal AdapterContract.AdapterDecision.TransitionForbidden (AdapterContract.decide input) }

          { Name = "decide reports a missing work item for transitionWorkItem"
            Run =
              fun () ->
                  let input = { baseInput with WorkItemExists = false }
                  Assert.equal AdapterContract.AdapterDecision.WorkItemNotFound (AdapterContract.decide input) }

          { Name = "decide rejects an expected-state precondition mismatch as a state conflict"
            Run =
              fun () ->
                  let input = { baseInput with ExpectedState = Some "ready"; CurrentState = Some "active" }
                  Assert.equal (AdapterContract.AdapterDecision.TransitionConflict("ready", "active")) (AdapterContract.decide input) }

          { Name = "decide succeeds transitioning a work item whose current state matches the expected precondition"
            Run =
              fun () ->
                  Assert.equal AdapterContract.AdapterDecision.TransitionSuccess (AdapterContract.decide baseInput) }

          { Name = "decide succeeds transitioning a work item with no expected-state precondition at all"
            Run =
              fun () ->
                  let input = { baseInput with ExpectedState = None; CurrentState = Some "anything" }
                  Assert.equal AdapterContract.AdapterDecision.TransitionSuccess (AdapterContract.decide input) }

          { Name = "decide rejects publishRepositoryEvent without event:publish scope, before checking the event's shape"
            Run =
              fun () ->
                  let input = { baseInput with Operation = "publishRepositoryEvent"; Scopes = Set.empty; EventIdPresent = false }
                  Assert.equal AdapterContract.AdapterDecision.PublishForbidden (AdapterContract.decide input) }

          { Name = "decide rejects an event with no eventId"
            Run =
              fun () ->
                  let input =
                      { baseInput with
                          Operation = "publishRepositoryEvent"
                          Scopes = Set.singleton "event:publish"
                          EventIdPresent = false }

                  Assert.equal AdapterContract.AdapterDecision.InvalidEvent (AdapterContract.decide input) }

          { Name = "decide succeeds publishing an event that carries an eventId"
            Run =
              fun () ->
                  let input =
                      { baseInput with
                          Operation = "publishRepositoryEvent"
                          Scopes = Set.singleton "event:publish"
                          EventIdPresent = true }

                  Assert.equal AdapterContract.AdapterDecision.PublishSuccess (AdapterContract.decide input) } ]
