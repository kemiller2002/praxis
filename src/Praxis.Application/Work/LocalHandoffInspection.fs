namespace Praxis.Application.Work

open System
open Praxis.Contracts.Work
open Praxis.Domain.Work

type LocalHandoffInspectionPorts =
    { ReadPacket: string -> Result<string, string>; Now: unit -> DateTimeOffset }

/// Reads proposals through bounded infrastructure and delegates only to the
/// advisory domain projection. No mutable work or execution port exists here.
[<RequireQualifiedAccess>]
module LocalHandoffInspection =
    let explain (ports: LocalHandoffInspectionPorts) timestamp (paths: string list) =
        if paths.IsEmpty || paths.Length > LocalHandoffExplanation.MaxPackets then Error "local explanation requires 1..64 packet files"
        else
            let rec load acc remaining =
                match remaining with
                | [] -> Ok(List.rev acc)
                | path :: rest ->
                    match ports.ReadPacket path |> Result.bind LocalAgentHandoffJson.readPacket with
                    | Error error -> Error(path + ": " + error)
                    | Ok packet -> load (packet :: acc) rest
            load [] paths |> Result.bind (fun packets ->
                let asOf = timestamp |> Option.defaultWith ports.Now
                LocalHandoffExplanation.explain asOf packets)
