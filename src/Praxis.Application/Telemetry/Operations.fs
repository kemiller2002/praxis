namespace Praxis.Application.Telemetry

open Praxis.Domain.Telemetry

[<RequireQualifiedAccess>]
module TelemetryOperations =
    let decideExecutionLink request = ExecutionLinkRecovery.decide request
