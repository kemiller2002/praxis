namespace Praxis.Contracts.Work

open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Contracts.Provenance
open Praxis.Domain.Work
open Praxis.Domain.Verification

/// `praxis.verification-plan-handoff/1`: what Praxis admitted and hands to
/// Ordo. It names the admitted plan bytes, the digest Ordo must find in them
/// (`--expected-digest`), the input claim they came from and the executor
/// that admitted them. Ordo's schedule (`ordo.verification-schedule/1`) is
/// Ordo's output; Praxis does not produce or reinterpret it.
[<RequireQualifiedAccess>]
module VerificationPlanHandoffJson =
    let private options = JsonSerializerOptions(WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let node (handoff: VerificationPlanHandoff) =
        let input = JsonObject()
        input["path"] <- JsonValue.Create handoff.Input.Path
        input["sha256"] <- JsonValue.Create handoff.Input.Sha256
        input["size"] <- JsonValue.Create handoff.Input.Size

        let source = JsonObject()
        source["repository"] <- JsonValue.Create handoff.Plan.SourceRepository
        source["commit"] <- JsonValue.Create handoff.Plan.SourceCommit
        source["baselineId"] <- JsonValue.Create handoff.Plan.BaselineId
        source["baselineSha256"] <- JsonValue.Create handoff.Plan.BaselineSha256

        let plan = JsonObject()
        plan["schema"] <- JsonValue.Create VerificationPlanIntake.PlanSchema
        plan["planId"] <- JsonValue.Create handoff.Plan.PlanId
        plan["digest"] <- JsonValue.Create handoff.Plan.Digest
        plan["digestVerified"] <- JsonValue.Create "recomputed by Praxis over the producer's canonical serialization without planIdentity"
        plan["source"] <- source
        plan["requirements"] <- JsonValue.Create handoff.Plan.Requirements
        plan["obligations"] <- JsonValue.Create handoff.Plan.Obligations
        plan["missingTestObligations"] <- JsonValue.Create handoff.Plan.MissingTestObligations
        plan["admittedPath"] <- JsonValue.Create handoff.AdmittedPath

        let ordering = JsonObject()
        ordering["owner"] <- JsonValue.Create "ordo"
        ordering["schema"] <- JsonValue.Create VerificationPlanIntake.ScheduleSchema
        ordering["command"] <- JsonValue.Create(VerificationPlanIntake.orderingCommand handoff)

        let node = JsonObject()
        node["schema"] <- JsonValue.Create VerificationPlanIntake.HandoffSchema
        node["claimId"] <- JsonValue.Create handoff.ClaimId
        node["input"] <- input
        node["plan"] <- plan
        node["ordering"] <- ordering
        node["admittedAt"] <- JsonValue.Create handoff.AdmittedAt
        node["admittedBy"] <- ActorJson.node handoff.AdmittedBy
        node["executionId"] <- (match handoff.ExecutionId with Some id -> JsonValue.Create id :> JsonNode | None -> null)
        node["derived_from"] <- JsonArray(JsonValue.Create handoff.ClaimId)
        node

    let render (handoff: VerificationPlanHandoff) = (node handoff).ToJsonString(options) + "\n"
