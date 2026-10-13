namespace Praxis.Infrastructure.Planning

open System
open Praxis.Contracts.Work
open Praxis.Domain.Execution
open Praxis.Domain.Work

/// What `praxis local containment` prints: human lines, the JSON document
/// and whether the profile is qualified (exit 0) or refused (exit 3).
type LocalContainmentReport = { Qualified: bool; Lines: string list; Json: string }

/// Effects behind `praxis local containment`; the CLI only parses and prints.
[<RequireQualifiedAccess>]
module LocalContainmentService =
    let private probeLines (qualification: ContainmentQualification) =
        [ yield sprintf "profile: %s (%s)" qualification.Profile qualification.ProfileDigest
          yield sprintf "platform: %s %s %s" qualification.OperatingSystem qualification.Kernel qualification.Architecture
          for restriction in LocalContainment.restrictions qualification do
              yield sprintf "  %-12s %s" restriction.Dimension (RestrictionStatus.toWire restriction.Status)
          for probe in qualification.Probes do
              yield sprintf "  [%s] %s (%s): expected %s, observed %s; %s"
                        (ContainmentProbeOutcome.toWire probe.Outcome) probe.Id probe.Dimension probe.Expected probe.Observed probe.Detail
          for control in qualification.Unsupported do
              yield "  unsupported: " + control ]

    /// `local containment qualify`: run the real probes and record evidence.
    let qualify root hostConfig =
        LocalRuntimeService.configuration root hostConfig
        |> Result.bind (fun configuration ->
            LocalHostContainment.qualify configuration (fun () -> DateTimeOffset.UtcNow)
            |> Result.mapError LocalCommandFailure.Refused)
        |> Result.map (fun (qualification, failure, written) ->
            let qualified = LocalContainment.isQualified qualification
            { Qualified = qualified
              Lines =
                [ yield (if qualified then "QUALIFIED " else "UNQUALIFIED ") + written
                  match failure with
                  | Some reason -> yield "  reason: " + reason
                  | None -> ()
                  yield! probeLines qualification ]
              Json = LocalContainmentJson.render qualification })

    /// `local containment status`: the launch gate's decision, no launch.
    let status root hostConfig =
        LocalRuntimeService.configuration root hostConfig
        |> Result.bind (fun configuration ->
            LocalHostContainment.status configuration DateTimeOffset.UtcNow |> Result.mapError LocalCommandFailure.Refused)
        |> Result.map (fun (containment, evidence, admission) ->
            let admitted = Result.isOk admission
            let reason = match admission with Ok _ -> "admitted" | Error reason -> reason
            let node = Text.Json.Nodes.JsonObject()
            node["schema"] <- Text.Json.Nodes.JsonValue.Create "praxis.containment-status/1"
            node["evidence"] <- Text.Json.Nodes.JsonValue.Create containment.Evidence
            node["admitted"] <- Text.Json.Nodes.JsonValue.Create admitted
            node["reason"] <- Text.Json.Nodes.JsonValue.Create reason
            { Qualified = admitted
              Lines =
                [ yield (if admitted then "ADMITTED " else "REFUSED ") + reason
                  match evidence with
                  | Ok qualification -> yield! probeLines qualification
                  | Error error -> yield "  evidence: " + error ]
              Json = node.ToJsonString() })
