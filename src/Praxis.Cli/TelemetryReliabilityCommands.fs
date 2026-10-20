namespace Praxis.Cli

open System
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Telemetry

/// `telemetry reliability-report` (PRX-REL-009, RES-REL-003): deterministic,
/// machine-readable cost and tokens per independently verified requirement
/// from a work event log, execution records and provider transcripts.
/// Read-only over its inputs; identical inputs give byte-identical output.
[<RequireQualifiedAccess>]
module TelemetryReliabilityCommands =
    let usage =
        "telemetry reliability-report [--events FILE] [--executions DIR] [--transcripts PATH]* [--discover-transcripts] [--price-table FILE] [--agent-binding FILE] [--work-item ID]* [--attribution session-window|explicit-only] [--out FILE]"

    let private values (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name then Some value else None)

    let private value name arguments = values name arguments |> List.tryLast

    let private known =
        set [ "--events"; "--executions"; "--transcripts"; "--price-table"; "--agent-binding"; "--work-item"; "--attribution"; "--out" ]

    /// Flags that take a value, and the switches; anything else is refused.
    let private unknownArguments (arguments: string list) =
        let rec walk remaining unknown =
            match remaining with
            | [] -> List.rev unknown
            | "--discover-transcripts" :: rest -> walk rest unknown
            | flag :: _ :: rest when known.Contains flag -> walk rest unknown
            | other :: rest -> walk rest (other :: unknown)

        walk arguments []

    let private combine (root: string) (path: string) =
        if IO.Path.IsPathRooted path then path else IO.Path.Combine(root, path)

    let run (root: string) (arguments: string list) =
        let path name fallback = value name arguments |> Option.defaultValue fallback |> combine root
        let attribution = value "--attribution" arguments |> Option.defaultValue "session-window"

        let priceTable =
            value "--price-table" arguments
            |> Option.map (combine root >> ReliabilityInputs.readPriceTable >> Result.map Some)
            |> Option.defaultValue (Ok None)

        let binding =
            value "--agent-binding" arguments
            |> Option.map (combine root >> ReliabilityInputs.readAgentBinding >> Result.map Some)
            |> Option.defaultValue (Ok None)

        match unknownArguments arguments, priceTable, binding with
        | (_ :: _ as unknown), _, _ ->
            eprintfn "ERROR unknown arguments: %s\nUsage: praxis %s" (String.concat " " unknown) usage
            2
        | _, Error message, _ ->
            eprintfn "ERROR --price-table: %s" message
            2
        | _, _, Error message ->
            eprintfn "ERROR --agent-binding: %s" message
            2
        | _ when attribution <> "session-window" && attribution <> "explicit-only" ->
            eprintfn "ERROR --attribution must be session-window or explicit-only"
            2
        | [], Ok table, Ok agentBinding ->
            let events, lines, malformed = ReliabilityInputs.readEvents (path "--events" ".ros/events/events.jsonl")
            let executions = ReliabilityInputs.readExecutions (path "--executions" ".ros/telemetry/executions")
            let explicitFiles = ReliabilityInputs.transcriptFiles (values "--transcripts" arguments |> List.map (combine root))

            // Discovery reads only the transcripts of sessions the in-scope
            // execution records name, never unrelated history.
            let discovered =
                if arguments |> List.contains "--discover-transcripts" then
                    let sessions = executions |> List.choose _.SessionId |> List.distinct
                    ReliabilityInputs.transcriptFiles (ReliabilityInputs.defaultTranscriptRoots ()) |> ReliabilityInputs.forSessions sessions
                else
                    []

            let observations, sources = ReliabilityInputs.readTranscripts (explicitFiles @ discovered |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)))

            let report =
                ReliabilityReport.build
                    { Events = events
                      EventLines = lines
                      MalformedEventLines = malformed
                      Executions = executions
                      Transcripts = observations
                      Sources = sources
                      PriceTable = table
                      AgentBinding = agentBinding
                      SessionWindow = (attribution = "session-window")
                      WorkItemFilter = values "--work-item" arguments }
                |> ReliabilityReport.toJson

            match value "--out" arguments with
            | Some out -> ReliabilityInputs.writeReport (combine root out) report
            | None -> printf "%s" report

            0
