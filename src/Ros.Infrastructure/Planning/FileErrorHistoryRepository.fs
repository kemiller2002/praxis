namespace Ros.Infrastructure.Planning

open System
open System.IO
open System.Text
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Infrastructure.Artifacts

/// The planner's estimate-error history, `.ros/planning/estimate-error.jsonl`
/// (PRX-PLAN-170..173): one measurement per line, appended only by
/// `praxis plan record-error`. JSON Lines with content-derived ids, so
/// parallel branches' measurements merge as an append-only log.
[<RequireQualifiedAccess>]
module FileErrorHistoryRepository =
    let relativePath = ".ros/planning/estimate-error.jsonl"

    let private path (root: string) = Path.Combine(root, ".ros", "planning", "estimate-error.jsonl")

    let private lines (root: string) =
        let file = path root

        if File.Exists file then
            File.ReadAllLines(file, Encoding.UTF8) |> Array.toList |> List.mapi (fun index line -> index + 1, line) |> List.filter (fun (_, line) -> line.Trim().Length > 0)
        else
            []

    /// Every stored measurement, plus a message for each line that is not one.
    let read (root: string) : ErrorMeasurement list * (int * string) list =
        lines root
        |> List.map (fun (number, line) -> number, PlanningJson.parseErrorMeasurement line)
        |> List.fold
            (fun (measurements, problems) (number, parsed) ->
                match parsed with
                | Ok measurement -> measurements @ [ measurement ], problems
                | Error message -> measurements, problems @ [ number, message ])
            ([], [])

    /// Validation findings for the stored history, as (path, field, message).
    let findings (root: string) : (string * string * string) list =
        let measurements, problems = read root

        (problems |> List.map (fun (number, message) -> relativePath, $"line {number}", message))
        @ (ErrorHistory.findings measurements |> List.map (fun (id, field, message) -> relativePath, $"{id}.{field}", message))

    /// Records `computed` unless the key already holds it (idempotent) or
    /// holds a different measurement (refused). Runs under a lock, so
    /// concurrent recordings never interleave.
    let record (root: string) (computed: ErrorMeasurement) : Result<ErrorRecordOutcome, string> =
        match RegistryLock.acquire (Path.GetFullPath root) "planning-error-history" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            try
                let measurements, problems = read root

                match problems with
                | (number, message) :: _ -> Error $"{relativePath} line {number}: {message}"
                | [] ->
                    let outcome = ErrorHistory.record measurements computed

                    match outcome with
                    | ErrorRecordOutcome.Recorded measurement ->
                        Directory.CreateDirectory(Path.GetDirectoryName(path root)) |> ignore
                        File.AppendAllText(path root, PlanningJson.errorMeasurementLine measurement + "\n", UTF8Encoding(false))
                    | _ -> ()

                    Ok outcome
            finally
                lease.Release() |> ignore
