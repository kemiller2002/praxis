namespace Ros.Application.Telemetry

open Ros.Domain.Telemetry

[<RequireQualifiedAccess>]
module TelemetryOperations =
    let decideExecutionLink request = ExecutionLinkRecovery.decide request
