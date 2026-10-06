namespace Ros.Infrastructure.Planning

open System
open System.IO
open System.Text
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Infrastructure.Artifacts

/// The planner's estimate-error history at `.ros/planning/error-history.json`
/// (PRX-PLAN-170..173). Only `plan record-error` writes it, and it writes
/// this file and nothing else; every other planner command stays read-only
/// (PRX-PLAN-001).
[<RequireQualifiedAccess>]
module FileErrorHistoryRepository =
    let relativePath = ".ros/planning/error-history.json"

    let path (root: string) = Path.Combine(root, ".ros", "planning", "error-history.json")

    let private content (root: string) = File.ReadAllText(path root, Encoding.UTF8)

    /// The raw store for validation; `None` when nothing was ever recorded.
    let parse (root: string) : Result<(int * Result<ErrorMeasurement, string>) list, string> option =
        if File.Exists(path root) then
            Some(
                try
                    PlanningJson.parseErrorHistoryStore (content root)
                with error ->
                    Error $"cannot read {relativePath}: {error.Message}"
            )
        else
            None

    /// Every stored measurement; no file means none.
    let read (root: string) : Result<ErrorMeasurement list, string> =
        if not (File.Exists(path root)) then
            Ok []
        else
            try
                PlanningJson.readErrorHistoryStore (content root) |> Result.mapError (fun message -> $"{relativePath}: {message}")
            with error ->
                Error $"cannot read {relativePath}: {error.Message}"

    let private writeAtomic (file: string) (text: string) : Result<unit, string> =
        let directory = Path.GetDirectoryName file
        let temporary = Path.Combine(directory, $".{Path.GetFileName file}.{Guid.NewGuid():N}.tmp")

        try
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(temporary, text, UTF8Encoding(false))
            File.Move(temporary, file, true)
            Ok()
        with error ->
            try
                if File.Exists temporary then File.Delete temporary
            with _ ->
                ()

            Error $"cannot write {relativePath}: {error.Message}"

    /// Reads, decides and writes under the `planning-error-history` lock.
    /// Nothing is written on a dry run or when `decide` leaves the history
    /// unchanged, so an identical measurement never rewrites the file.
    let transact
        (root: string)
        (dryRun: bool)
        (decide: ErrorMeasurement list -> Result<ErrorMeasurement list * ErrorRecordOutcome, string>)
        : Result<ErrorRecordOutcome, string> =
        match RegistryLock.acquire root "planning-error-history" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                read root
                |> Result.bind decide
                |> Result.bind (fun (updated, outcome) ->
                    match outcome with
                    | ErrorRecordOutcome.Recorded when not dryRun -> writeAtomic (path root) (PlanningJson.renderErrorHistoryStore updated) |> Result.map (fun () -> outcome)
                    | _ -> Ok outcome)

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value
