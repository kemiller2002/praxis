namespace Ros.Tests

open Ros.Domain.Provenance

/// Shared provenance values for effect tests whose subject is not
/// provenance itself: every writer now requires an explicit actor.
[<RequireQualifiedAccess>]
module ProvenanceFixtures =
    let agent =
        { Actor.unknown with
            Kind = ActorKind.Agent
            Id = Attribute.Known "test-agent"
            Provider = Attribute.Known "test-provider"
            Runtime = Attribute.Known "test-runtime"
            ExecutionId = Attribute.Known "EXE-20260101T000000000Z-00000000" }

    let contributionBy actor operation at =
        { Operation = operation
          At = at
          Actor = actor
          WorkItem = None
          Reason = None
          Evidence = []
          Basis = None }

    let contribution = contributionBy agent Operation.Modified "2026-01-01T00:00:00.000Z"
