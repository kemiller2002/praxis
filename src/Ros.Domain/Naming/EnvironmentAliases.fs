namespace Ros.Domain.Naming

/// Environment variables are documented under the canonical `PRAXIS_`
/// prefix; the historical `ROS_` names keep working (DF-ROS-2026-A050).
/// Every reader in this CLI reads the `ROS_` name, so the composition root
/// applies these assignments once at start-up instead of teaching every
/// reader two names.
[<RequireQualifiedAccess>]
module EnvironmentAliases =
    [<Literal>]
    let CanonicalPrefix = "PRAXIS_"

    [<Literal>]
    let LegacyPrefix = "ROS_"

    /// The suffixes shared by both prefixes, e.g. `PRAXIS_ACTOR`/`ROS_ACTOR`.
    let suffixes =
        [ "ACTOR"
          "ACTOR_KIND"
          "TELEMETRY_PROVIDER"
          "TELEMETRY_RUNTIME"
          "TELEMETRY_MODEL"
          "TELEMETRY_MODEL_VERSION"
          "TELEMETRY_RUNTIME_VERSION"
          "TELEMETRY_SESSION_ID"
          "TELEMETRY_CONVERSATION_ID"
          "TELEMETRY_RUN_ID"
          "BASE_REF"
          "PACKAGE_ROOT" ]

    let canonicalNames = suffixes |> List.map (fun suffix -> CanonicalPrefix + suffix)

    /// Legacy variable assignments that make a set canonical variable
    /// visible to every `ROS_` reader. The canonical name wins when both are
    /// set; an empty canonical value is treated as unset.
    let legacyAssignments (lookup: string -> string option) : (string * string) list =
        suffixes
        |> List.choose (fun suffix ->
            match lookup (CanonicalPrefix + suffix) with
            | Some value when value <> "" -> Some(LegacyPrefix + suffix, value)
            | _ -> None)
