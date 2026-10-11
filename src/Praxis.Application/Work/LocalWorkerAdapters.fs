namespace Praxis.Application.Work

open System
open Praxis.Contracts.Work
open Praxis.Domain.Execution
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
type LocalWorkerOperatingSystem = Linux | MacOS | Windows

/// Declared functional support, not sandbox qualification. A worker bridge
/// consumes the canonical packet on stdin and emits the canonical result.
type LocalWorkerAdapter =
    { Id: string; WorkerId: string
      OperatingSystems: Set<LocalWorkerOperatingSystem>; Roles: Set<ExecutionRole>
      Prepare: CheckedLocalWorkerPacket -> Result<LocalWorkerProcessSpec, string> }

type LocalWorkerAdapterCatalog = private LocalWorkerAdapterCatalog of Map<string, LocalWorkerAdapter>

[<RequireQualifiedAccess>]
module LocalWorkerAdapters =
    let private clean (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 1024
        && value = value.Trim() && not (value |> Seq.exists Char.IsControl)

    /// One exact binding per worker identity; ambiguity is never resolved by
    /// registration order. Provision the catalog independently of model input.
    let catalog (adapters: LocalWorkerAdapter list) =
        let roles = Set.ofList [ ExecutionRole.Implementation; ExecutionRole.Verification; ExecutionRole.Review ]
        if adapters.IsEmpty || adapters.Length > 256
           || adapters |> List.exists (fun a -> not (clean a.Id && clean a.WorkerId)
                                               || a.OperatingSystems.IsEmpty || a.Roles.IsEmpty || not (Set.isSubset a.Roles roles)) then
            Error "invalid local worker adapter catalog"
        elif (adapters |> List.map _.Id |> Set.ofList |> Set.count) <> adapters.Length
             || (adapters |> List.map _.WorkerId |> Set.ofList |> Set.count) <> adapters.Length then
            Error "local worker adapter or worker identity is duplicated"
        else Ok(LocalWorkerAdapterCatalog(adapters |> List.map (fun a -> a.WorkerId, a) |> Map.ofList))

    /// Unknown workers, unsupported OSes and roles refuse before preparation.
    /// The journaled dispatcher independently checks exact stdin and budgets.
    let select operatingSystem (LocalWorkerAdapterCatalog adapters) admitted =
        let packet = LocalAgentHandoff.checkedPacket admitted
        match adapters |> Map.tryFind packet.WorkerId with
        | None -> Error "local worker is not registered by the host"
        | Some adapter when not (adapter.OperatingSystems.Contains operatingSystem) -> Error "local worker adapter does not support this operating system"
        | Some adapter when not (adapter.Roles.Contains packet.Role) -> Error "local worker adapter does not support this execution role"
        | Some adapter ->
            try adapter.Prepare admitted
            with _ -> Error "local worker adapter preparation failed"

    /// Platform-specific command paths belong in the host binding. This bridge
    /// changes neither the delegated packet nor the captured raw result format.
    let command id workerId operatingSystems roles (template: LocalWorkerProcessSpec) =
        { Id = id; WorkerId = workerId; OperatingSystems = operatingSystems; Roles = roles
          Prepare = fun admitted ->
              let packet = LocalAgentHandoff.checkedPacket admitted
              Ok { template with Input = LocalAgentHandoffJson.renderPacket packet
                                 TimeoutMilliseconds = min template.TimeoutMilliseconds (packet.TimeoutSeconds * 1000)
                                 MaxOutputBytes = min template.MaxOutputBytes packet.MaxOutputBytes } }
