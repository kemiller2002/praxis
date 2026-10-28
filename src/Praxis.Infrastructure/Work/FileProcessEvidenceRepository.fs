namespace Praxis.Infrastructure.Work

open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Contracts.Provenance
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Json

/// What Praxis itself saw when a process statement was recorded.
type ProcessObservation =
    { Commit: string option
      Branch: string option
      ExecutionId: string option
      ExecutionRole: string option
      Identity: IdentityBasis }

/// Process evidence (WI-0103): `work.evidence.recorded` events appended to the
/// same `.ros/events/events.jsonl` ledger as every other work event, through
/// the `work-state` journal. There is no second evidence store; a verifier
/// reads this log together with `work.started`, `work.checkpointed` and the
/// execution envelopes. Callers hold the `work-protocol` lock.
[<RequireQualifiedAccess>]
module FileProcessEvidenceRepository =
    let private eventsRelativePath = ".ros/events/events.jsonl"
    let private contextRelativePath = ".ros/context/current.json"
    let private compact = JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    let private optional (node: JsonObject) (name: string) (value: string option) =
        value |> Option.iter (fun text -> node[name] <- JsonValue.Create text)

    /// How this process's identity is established: the discovery mechanism, and
    /// whether the actor kind came from a flag, the environment or discovery.
    let identityBasis (overrides: Praxis.Domain.Telemetry.IdentityInputs) (kindFlagged: bool) : Result<IdentityBasis, string> =
        match FileTelemetryExecutionRepository.resolveIdentity overrides with
        | Error message -> Error message
        | Ok(_, identity, source) ->
            let kindSource =
                if kindFlagged then "flag"
                elif not (System.String.IsNullOrEmpty(System.Environment.GetEnvironmentVariable "ROS_ACTOR_KIND")) then "environment"
                else "discovery-mechanism"

            Ok
                { Mechanism = source.Mechanism
                  ActorKindSource = kindSource
                  RunId = identity.RunId }

    /// The role recorded in an execution's envelope, if the envelope says one.
    let readExecutionRole (root: string) (executionId: string) =
        let path = Path.Combine(root, ".ros", "executions", executionId, "envelope.json")

        if not (File.Exists path) then
            None
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as envelope ->
                    match envelope["role"] with
                    | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                    | _ -> None
                | _ -> None
            with _ ->
                None

    /// The content-addressed event: `statement` is what the caller asserted,
    /// `observed` is what Praxis saw, and `provenance` says which of the two a
    /// verifier may rely on. The hash covers every field before `eventId`.
    let eventNode
        (repositoryId: string)
        (protocolVersion: string)
        (workItemId: string)
        (occurredAt: string)
        (actor: Actor)
        (statement: ProcessStatement)
        (assessment: ProvenanceAssessment)
        (observation: ProcessObservation)
        : JsonObject =
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "1.0.0"
        node["type"] <- JsonValue.Create ProcessEvidence.eventType
        node["workItem"] <- JsonValue.Create workItemId
        node["repository"] <- JsonValue.Create repositoryId
        node["protocolVersion"] <- JsonValue.Create protocolVersion
        node["occurredAt"] <- JsonValue.Create occurredAt
        node["kind"] <- JsonValue.Create(ProcessEvidenceKind.code statement.Kind)

        let provenance = JsonObject()
        provenance["class"] <- JsonValue.Create(EvidenceProvenance.code assessment.Class)
        provenance["basis"] <- JsonValue.Create assessment.Basis
        optional provenance "authenticity" assessment.Authenticity
        node["provenance"] <- provenance

        let asserted = JsonObject()
        asserted["summary"] <- JsonValue.Create statement.Summary
        asserted["subjects"] <- strings statement.Subjects
        optional asserted "candidate" statement.Candidate
        asserted["references"] <- strings statement.References
        optional asserted "decision" statement.Decision
        optional asserted "outcome" statement.Outcome
        optional asserted "attestation" (statement.Attestation |> Option.map Attestation.render)
        node["statement"] <- asserted

        let observed = JsonObject()
        optional observed "commit" observation.Commit
        optional observed "branch" observation.Branch
        optional observed "execution" observation.ExecutionId
        optional observed "executionRole" observation.ExecutionRole
        let identity = JsonObject()
        identity["mechanism"] <- JsonValue.Create observation.Identity.Mechanism
        identity["actorKindSource"] <- JsonValue.Create observation.Identity.ActorKindSource
        // Every Praxis identity is self-reported, CI discovery included (an environment variable can be set by anyone);
        // a verifier binds CI records to the run it trusts through runId.
        identity["assurance"] <- JsonValue.Create "self-reported"
        optional identity "runId" observation.Identity.RunId
        observed["identity"] <- identity
        node["observed"] <- observed

        let executions = JsonArray()
        observation.ExecutionId |> Option.iter (fun id -> executions.Add(JsonValue.Create id: JsonNode))
        node["telemetryExecutions"] <- executions
        node["actor"] <- ActorJson.node actor
        let publication = JsonObject()
        publication["status"] <- JsonValue.Create "pending"
        node["publication"] <- publication
        node["eventId"] <- JsonValue.Create(CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact node))
        node

    /// Appends the event atomically with the unchanged work context, so the
    /// journal's recovery covers it exactly like a checkpoint.
    let record (root: string) (event: JsonObject) : Result<string, string> =
        try
            let eventsPath = Path.Combine(root, eventsRelativePath)
            let contextPath = Path.Combine(root, contextRelativePath)

            if not (File.Exists contextPath) then
                Error "there is no work context; run 'praxis init' and 'praxis work begin' first"
            else
                let current = if File.Exists eventsPath then File.ReadAllText eventsPath else ""
                let separator = if current.Length > 0 && not (current.EndsWith "\n") then "\n" else ""
                let eventId = event["eventId"].GetValue<string>()

                let writes: WorkStateWrite list =
                    [ { Path = eventsRelativePath; Content = $"{current}{separator}{event.ToJsonString compact}\n" }
                      { Path = contextRelativePath; Content = File.ReadAllText contextPath } ]

                match WorkStateTransaction.prepare root writes with
                | Error failure -> Error $"state persistence failed: {failure.Message}"
                | Ok() ->
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error $"state persistence failed: {failure.Message}"
                    | Ok() -> Ok eventId
        with error ->
            Error $"state persistence failed: {error.Message}"
