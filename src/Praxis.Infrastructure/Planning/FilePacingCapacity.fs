namespace Praxis.Infrastructure.Planning

open System
open System.Globalization
open System.IO
open Praxis.Application.Pacing
open Praxis.Domain.Pacing
open Praxis.Domain.Planning
open Praxis.Infrastructure.Pacing

/// Provider capacity for the planner, read from usage-pacing state
/// (PRX-QUAL-009). This adapter is the only place pacing and planning meet:
/// it maps pacing holds and readings onto the planner's provider-neutral
/// capacity states and never queries a provider itself.
[<RequireQualifiedAccess>]
module FilePacingCapacity =
    /// A reading older than this is unknown capacity, not available capacity.
    let readingFreshFor = TimeSpan.FromMinutes 15.0

    let private iso (value: DateTimeOffset) = value.ToString("O", CultureInfo.InvariantCulture)

    let private percent (value: decimal) = value.ToString("0.##", CultureInfo.InvariantCulture)

    let private capacity (directory: string) (provider: ProviderId) (state: CapacityState) =
        { Provider = ProviderId.code provider
          State = state
          Provenance = Provenance.create EvidenceSource.Telemetry (Path.Combine(directory, "hold.json")) }

    /// Pure: capacity per provider from the persisted pacing state and each
    /// provider's last cached reading time.
    let fromState (directory: string) (now: DateTimeOffset) (read: PacingStateRead) (lastReading: ProviderId -> DateTimeOffset option) : ProviderCapacity list =
        let providers =
            ProviderId.all
            |> List.filter (fun provider ->
                (lastReading provider).IsSome
                || match read with
                   | PacingStateRead.Current(state, _)
                   | PacingStateRead.Migrated(state, _) -> state.Holds.Values |> Seq.exists (fun hold -> hold.Provider = provider)
                   | _ -> false)

        let integrity =
            match read with
            | PacingStateRead.Unreadable reason -> Some $"pacing state is unreadable ({reason})"
            | PacingStateRead.Unsupported version -> Some $"pacing state schema {version} is newer than this Praxis"
            | PacingStateRead.Absent
            | PacingStateRead.Current _
            | PacingStateRead.Migrated _ -> None

        let holds =
            match read with
            | PacingStateRead.Current(state, _)
            | PacingStateRead.Migrated(state, _) -> state.Holds.Values |> Seq.toList
            | _ -> []

        providers
        |> List.map (fun provider ->
            let own = holds |> List.filter (fun hold -> hold.Provider = provider)

            let hard =
                own
                |> List.choose (fun hold ->
                    match hold.Basis with
                    | HoldBasis.HardLimit(used, resetsAt) when resetsAt > now -> Some(hold.Key, used, resetsAt)
                    | _ -> None)
                |> List.sortByDescending (fun (_, _, resetsAt) -> resetsAt)

            let weekly =
                own
                |> List.choose (fun hold ->
                    match hold.Basis with
                    | HoldBasis.WeeklyLead resetsAt -> Some(hold.Key, resetsAt)
                    | HoldBasis.HardLimit _ -> None)

            let state =
                match integrity, hard, weekly, lastReading provider with
                | Some reason, _, _, _ -> CapacityState.Unknown reason
                | None, (key, used, resetsAt) :: _, _, _ -> CapacityState.Exhausted(Some(iso resetsAt), $"hard limit {key} at {percent used}%% used")
                | None, [], (key, resetsAt) :: _, _ -> CapacityState.Constrained(resetsAt |> Option.map iso, $"weekly pacing hold {key}")
                | None, [], [], Some observed when now - observed <= readingFreshFor -> CapacityState.Available
                | None, [], [], Some observed -> CapacityState.Unknown $"last pacing reading at {iso observed} is older than {int readingFreshFor.TotalMinutes} minutes"
                | None, [], [], None -> CapacityState.Unknown "no pacing reading"

            capacity directory provider state)

    /// Reads capacity from a pacing state directory; a missing directory
    /// yields no capacity (unknown), never available capacity.
    let read (directory: string) (now: DateTimeOffset) : ProviderCapacity list =
        if not (Directory.Exists directory) then
            []
        else
            let lastReading provider =
                match PacingPersistence.loadSnapshot directory provider (PacingAdapterRules.info provider None) with
                | Ok(Some snapshot) -> Some snapshot.ObservedAt
                | Ok None
                | Error _ -> None

            fromState directory now (PacingPersistence.readState directory) lastReading
