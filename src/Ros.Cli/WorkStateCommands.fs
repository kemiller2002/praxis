namespace Ros.Cli

open System
open System.Text.Json.Nodes
open Ros.Contracts.Work
open Ros.Infrastructure.Work

/// `praxis work state` and `praxis work transition`: the versioned
/// control-plane work-state contract (`praxis.work-state`) on the command
/// line. They print exactly what `web serve`'s `GET /api/v1/work[/ID]` and
/// `POST /api/v1/work/ID/transitions` answer, from the same functions, so a
/// process that may only run a repository's own Praxis -- the hub -- reads
/// and changes that repository through the same contract (PRX-CTL-007).
[<RequireQualifiedAccess>]
module WorkStateCommands =
    let usage =
        "work state [ID] [--tag T]* [--status S] | work transition --id ID --request JSON"

    /// The read a `work state` argument list names. `--json` is accepted
    /// (the output is always the JSON contract).
    let stateQuery (arguments: string list) : Result<StateQuery, string> =
        let rec loop remaining (id: string option) (tags: string list) (status: string option) =
            match remaining with
            | [] -> Ok(id, tags, status)
            | ("--tag" | "-t") :: value :: rest when not (value.StartsWith "-") -> loop rest id (tags @ HttpMessages.splitTags value) status
            | "--status" :: value :: rest when not (value.StartsWith "-") -> loop rest id tags (Some value)
            | "--json" :: rest -> loop rest id tags status
            | flag :: _ when flag.StartsWith "-" -> Error $"unknown or incomplete option '{flag}'"
            | value :: rest when id.IsNone -> loop rest (Some value) tags status
            | value :: _ -> Error $"unexpected argument '{value}'"

        match loop arguments None [] None with
        | Error message -> Error message
        | Ok(Some id, [], None) -> Ok(StateQuery.Item id)
        | Ok(Some _, _, _) -> Error "--tag and --status filter the work list; they do not apply to one work item"
        | Ok(None, tags, status) -> Ok(StateQuery.List(tags, status))

    /// The work item and request body a `work transition` argument list
    /// names: `--request` is the JSON body `POST /api/v1/work/ID/transitions`
    /// accepts, parsed by the same `WebInterface.transitionIntent`.
    let transitionRequest (arguments: string list) : Result<string * Result<RequestBody, string>, string> =
        let rec loop remaining (id: string option) (request: string option) =
            match remaining with
            | [] -> Ok(id, request)
            | "--id" :: value :: rest when not (value.StartsWith "-") -> loop rest (Some value) request
            | "--request" :: value :: rest -> loop rest id (Some value)
            | "--json" :: rest -> loop rest id request
            | flag :: _ -> Error $"unknown or incomplete option '{flag}'"

        let body (text: string) =
            try
                match JsonNode.Parse text with
                | :? JsonObject as node -> Ok(JsonBody node)
                | _ -> Error "--request must be a JSON object"
            with error ->
                Error $"--request is not valid JSON: {error.Message}"

        match loop arguments None None with
        | Error message -> Error message
        | Ok(None, _) -> Error "work transition requires --id ID"
        | Ok(Some _, None) -> Error "work transition requires --request JSON"
        | Ok(Some id, Some request) -> Ok(id, body request)

    /// Exit status of a printed document: 0 for an answer, 1 for a
    /// `praxis.error` the repository reports about its own state.
    let private exitFor (status: int) = if status = 200 then 0 else 1

    let private print (document: JsonNode) = printfn "%s" (HttpMessages.renderJson document)

    let state (root: string) (arguments: string list) : int =
        match stateQuery arguments with
        | Error message ->
            eprintfn "ERROR %s" message
            eprintfn "Usage: praxis [--root PATH] %s" usage
            2
        | Ok query ->
            let status, document =
                WebInterface.stateResponse (FileWorkListRepository.readStateSources root) (FileWorkListRepository.readDetail root) query

            print document
            exitFor status

    /// Runs the transition through `WebInterface.transition`, the path of
    /// `POST /api/v1/work/ID/transitions`: this CLI's own transition command,
    /// identity from this process's environment, never from the caller.
    let transition (root: string) (arguments: string list) : int =
        match transitionRequest arguments with
        | Error message ->
            eprintfn "ERROR %s" message
            eprintfn "Usage: praxis [--root PATH] %s" usage
            2
        | Ok(id, Error message) ->
            let _, document =
                WebInterface.refusalResponse
                    { Category = RefusalCategory.InvalidRequest
                      Message = message
                      RequestedAction = None
                      WorkItemId = id }

            print document
            2
        | Ok(id, Ok body) ->
            let status, document =
                match WebInterface.transitionIntent id body with
                | Ok intent -> WebInterface.transition root intent
                | Error refusal -> WebInterface.refusalResponse refusal

            print document

            match status with
            | 400 -> 2
            | _ -> exitFor status
