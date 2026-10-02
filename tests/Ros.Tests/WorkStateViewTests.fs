namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Ros.Cli
open Ros.Contracts.Work
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkStateViewTests =
    let private configuration =
        { DefaultEvidence = Set.ofList [ "implementation"; "tests" ]
          EvidenceByType = Map.ofList [ "research", Set.ofList [ "conclusion" ] ]
          RequireDurableCheckpoint = false }

    let private queued id status : QueueItemDetail =
        { Id = id
          Title = $"{id} title"
          Description = None
          Tags = [ "web" ]
          Priority = Some "medium"
          Status = status
          BlockedReason = None
          Attachments = [] }

    let private live id workType state : LiveWorkItem =
        { Id = id
          WorkType = workType
          LocalState = "local"
          SemanticState = state
          Evidence = []
          BlockReason = None
          UpdatedAt = None
          CompletedAt = None
          TelemetryExecutionIds = [] }

    let private single (configuration: WorkStateConfiguration) queueItems contextItems =
        WorkStateView.project configuration queueItems contextItems |> Assert.single

    let private legal (item: WorkItemState) =
        item.Actions
        |> List.choose (fun action ->
            match action.Availability with
            | ActionAvailability.Legal _ -> Some action.Action
            | ActionAvailability.Refused _ -> None)

    let private availability (item: WorkItemState) (name: string) =
        (item.Actions |> List.find (fun action -> action.Action = name)).Availability

    let private sources queueItems contextItems : Result<WorkStateConfiguration * QueueItemDetail list * LiveWorkItem list, string> =
        Ok(configuration, queueItems, contextItems)

    let private projectionTests =
        [ { Name = "work state: a backlog-only item's legal actions are exactly the backlog kernel's, in every state"
            Run =
              fun () ->
                  [ "captured"; "ready"; "blocked"; "abandoned" ]
                  |> List.iter (fun status ->
                      let item = single configuration [ queued "WI-1" status ] []
                      Assert.equal GoverningKernel.Backlog item.GovernedBy

                      let expected =
                          BacklogState.parse status
                          |> Option.get
                          |> BacklogTransition.allowedActions
                          |> List.map BacklogAction.code
                          |> List.sort

                      Assert.equal expected (legal item |> List.sort)
                      Assert.equal [ "ready"; "block"; "start"; "abandon" ] (item.Actions |> List.map (fun action -> action.Action))) }

          { Name = "work state: a live item's legal actions are exactly the live kernel's, in every state"
            Run =
              fun () ->
                  [ LiveWorkState.Ready; LiveWorkState.Active; LiveWorkState.Blocked; LiveWorkState.Complete; LiveWorkState.Abandoned ]
                  |> List.iter (fun state ->
                      let item = single configuration [ queued "WI-1" "ready" ] [ live "WI-1" "feature" state ]
                      Assert.equal GoverningKernel.Live item.GovernedBy
                      let expected = WorkTransition.allowedActions state |> List.map WorkListView.actionCode |> List.sort
                      Assert.equal expected (legal item |> List.sort)
                      Assert.equal (WorkListView.stateCode state) item.SemanticState) }

          { Name = "work state: refused transitions carry the kernel's reason"
            Run =
              fun () ->
                  let captured = single configuration [ queued "WI-1" "captured" ] []

                  Assert.equal
                      (ActionAvailability.Refused("illegal-transition", "cannot start backlog item 'WI-1' from 'captured'"))
                      (availability captured "start")

                  let complete = single configuration [] [ live "WI-2" "feature" LiveWorkState.Complete ]
                  Assert.equal (ActionAvailability.Refused("illegal-transition", "cannot resume 'WI-2' from 'complete'")) (availability complete "resume") }

          { Name = "work state: legal actions state what the kernel will demand (reason, evidence by work type)"
            Run =
              fun () ->
                  let feature = single configuration [] [ live "WI-1" "feature" LiveWorkState.Active ]
                  Assert.equal (ActionAvailability.Legal { Reason = false; EvidenceTypes = [ "implementation"; "tests" ] }) (availability feature "complete")
                  Assert.equal (ActionAvailability.Legal { Reason = true; EvidenceTypes = [] }) (availability feature "block")
                  Assert.equal (ActionAvailability.Legal { Reason = true; EvidenceTypes = [] }) (availability feature "abandon")
                  let research = single configuration [] [ live "WI-2" "research" LiveWorkState.Active ]
                  Assert.equal (ActionAvailability.Legal { Reason = false; EvidenceTypes = [ "conclusion" ] }) (availability research "complete")
                  let ready = single configuration [ queued "WI-3" "ready" ] []
                  Assert.equal (ActionAvailability.Legal { Reason = true; EvidenceTypes = [] }) (availability ready "block")
                  Assert.equal (ActionAvailability.Legal { Reason = false; EvidenceTypes = [] }) (availability ready "abandon") }

          { Name = "work state: obligations are recorded for live work and unavailable, not empty, for backlog-only items"
            Run =
              fun () ->
                  match (single configuration [ queued "WI-1" "ready" ] []).Obligations with
                  | Recorded.Unavailable reason -> Assert.equal WorkStateView.backlogObligationsUnavailable reason
                  | other -> failwith $"expected unavailable, got {other}"

                  let enforcing = { configuration with RequireDurableCheckpoint = true }

                  match (single enforcing [] [ live "WI-2" "feature" LiveWorkState.Active ]).Obligations with
                  | Recorded.Available(_, items) ->
                      Assert.equal [ "completion-evidence"; "durable-checkpoint" ] (items |> List.map (fun item -> item.Code))
                      Assert.equal [ "implementation"; "tests" ] items.Head.EvidenceTypes
                  | other -> failwith $"expected available, got {other}"

                  match (single configuration [] [ live "WI-3" "feature" LiveWorkState.Active ]).Obligations with
                  | Recorded.Available(_, items) -> Assert.equal [ "completion-evidence" ] (items |> List.map (fun item -> item.Code))
                  | other -> failwith $"expected available, got {other}"

                  match (single enforcing [] [ live "WI-4" "feature" LiveWorkState.Complete ]).Obligations with
                  | Recorded.Available(_, items) -> Assert.empty items
                  | other -> failwith $"expected available, got {other}" }

          { Name = "work state: unknowns are unavailable without a source, and an unrecognized backlog status is a recorded unknown"
            Run =
              fun () ->
                  match (single configuration [ queued "WI-1" "ready" ] []).Unknowns with
                  | Recorded.Unavailable reason -> Assert.equal WorkStateView.noUnknownSource reason
                  | other -> failwith $"expected unavailable, got {other}"

                  let odd = single configuration [ queued "WI-2" "parked" ] []
                  Assert.empty odd.Actions

                  match odd.Unknowns with
                  | Recorded.Available(_, [ unknown ]) -> Assert.equal "unrecognized-backlog-status" unknown.Code
                  | other -> failwith $"expected one unknown, got {other}" }

          { Name = "work state: ids, order and status agree with work list, and the filter is work list's own"
            Run =
              fun () ->
                  let queueItems = [ queued "WI-B" "ready"; queued "WI-A" "captured"; { queued "WI-C" "ready" with Tags = [] } ]
                  let contextItems = [ live "WI-C" "feature" LiveWorkState.Active; live "WI-D" "task" LiveWorkState.Blocked ]
                  let rows = WorkListView.mergedRows queueItems contextItems
                  let items = WorkStateView.project configuration queueItems contextItems
                  Assert.equal (rows |> List.map (fun row -> row.Id, row.Status)) (items |> List.map (fun item -> item.Id, item.SemanticState))

                  let filtered = WorkStateView.projectFiltered configuration queueItems contextItems [ "web" ] (Some "ready")
                  Assert.equal (WorkListView.filter [ "web" ] (Some "ready") rows |> List.map (fun row -> row.Id)) (filtered |> List.map (fun item -> item.Id))
                  Assert.equal [ "WI-B" ] (filtered |> List.map (fun item -> item.Id)) } ]

    let private contractTests =
        [ { Name = "work state contract: documents carry contract, version and kind; unavailable is an object, never a list"
            Run =
              fun () ->
                  let items = WorkStateView.project configuration [ queued "WI-1" "ready" ] [ live "WI-2" "feature" LiveWorkState.Active ]
                  let list = WorkStateJson.listDocument items
                  Assert.equal "praxis.work-state" (list["contract"].GetValue<string>())
                  Assert.equal 1 (list["version"].GetValue<int>())
                  Assert.equal "work-list" (list["kind"].GetValue<string>())
                  let backlogOnly = list["items"].[0]
                  Assert.equal "backlog" (backlogOnly["governedBy"].GetValue<string>())
                  Assert.isTrue (isNull backlogOnly["live"]) "a backlog-only item has no live record"
                  Assert.equal "ready" (backlogOnly["backlog"].["status"].GetValue<string>())
                  Assert.equal "unavailable" (backlogOnly["obligations"].["availability"].GetValue<string>())
                  Assert.isTrue (backlogOnly["obligations"].["reason"].GetValue<string>() <> "") "unavailable states its reason"
                  Assert.isTrue (isNull backlogOnly["obligations"].["items"]) "unavailable has no items list"
                  Assert.equal "unavailable" (backlogOnly["unknowns"].["availability"].GetValue<string>())
                  let liveItem = list["items"].[1]
                  Assert.equal "live" (liveItem["governedBy"].GetValue<string>())
                  Assert.equal "feature" (liveItem["live"].["workType"].GetValue<string>())
                  Assert.equal "available" (liveItem["obligations"].["availability"].GetValue<string>())
                  let complete = liveItem["actions"].AsArray() |> Seq.find (fun action -> action["action"].GetValue<string>() = "complete")
                  Assert.isTrue (complete["legal"].GetValue<bool>()) "complete is legal for an active item"
                  Assert.equal 2 (complete["requires"].["evidenceTypes"].AsArray().Count)
                  let resume = liveItem["actions"].AsArray() |> Seq.find (fun action -> action["action"].GetValue<string>() = "resume")
                  Assert.equal "illegal-transition" (resume["refusal"].["code"].GetValue<string>())
                  let document = WorkStateJson.itemDocument items.Head (Some "notes")
                  Assert.equal "work-item" (document["kind"].GetValue<string>())
                  Assert.equal "notes" (document["item"].["detail"].GetValue<string>()) }

          { Name = "work state contract: routing and responses for list, item, unknown item and unreadable state"
            Run =
              fun () ->
                  let request path query : HttpRequestData =
                      { Method = "GET"; Segments = HttpMessages.pathSegments path; Query = query; ContentType = None; Body = [||] }

                  Assert.equal (WebRoute.State(StateQuery.List([ "a"; "b" ], Some "ready"))) (WebInterface.route (request "/api/v1/work" [ "tag", "a,b"; "status", "ready" ]))
                  Assert.equal (WebRoute.State(StateQuery.Item "WI 1")) (WebInterface.route (request "/api/v1/work/WI%201" []))
                  Assert.equal (WebRoute.Api(WorkOperation.Show "WI-1")) (WebInterface.route (request "/api/work/WI-1" []))
                  let state = sources [ queued "WI-1" "ready" ] []
                  let status, body = WebInterface.stateResponse state (fun _ -> None) (StateQuery.Item "WI-404")
                  Assert.equal 404 status
                  Assert.equal "work-item-not-found" (body["code"].GetValue<string>())
                  Assert.equal "WI-404" (body["workItemId"].GetValue<string>())
                  Assert.isTrue ((body["error"].GetValue<string>()).Contains "WI-404") "the message names the item"
                  let found, _ = WebInterface.stateResponse state (fun _ -> None) (StateQuery.Item "WI-1")
                  Assert.equal 200 found
                  let broken, failure = WebInterface.stateResponse (Error "bad json") (fun _ -> None) (StateQuery.List([], None))
                  Assert.equal 500 broken
                  Assert.equal "work-state-unreadable" (failure["code"].GetValue<string>())
                  Assert.equal (Some false) (WebInterface.isKnown state "WI-404")
                  Assert.equal (Some true) (WebInterface.isKnown state "WI-1")
                  Assert.equal None (WebInterface.isKnown (Error "bad json") "WI-1") } ]

    /// Every file under `root` with its content hash, `.git` included.
    let private snapshot (root: string) =
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.map (fun path -> Path.GetRelativePath(root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))
        |> Map.ofSeq

    let private withServer (test: string -> ServedProcess -> unit) =
        let root = CliHarness.initializedRepository "ros-web-state" None
        CliHarness.optOutOfDurableCheckpoints root
        CliHarness.commitAll root "pre-continuity completion semantics"

        try
            use server = new ServedProcess(root, [ "web"; "serve" ])
            test root server
        finally
            CliHarness.removeDirectory root

    let private httpTests =
        [ { Name = "web serve: GET /api/v1/work and /api/v1/work/ID answer the versioned work-state contract"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"Typed","id":"WI-TYPED","tags":["api"]}""") |> ignore
                      server.PostJson("/api/work/WI-TYPED/ready", "") |> ignore
                      let list = server.Get "/api/v1/work?tag=api"
                      Assert.equal 200 (Http.status list)
                      let document = Http.json list
                      Assert.equal "praxis.work-state" (Http.text document "contract")
                      Assert.equal [ "WI-TYPED" ] (document["items"].AsArray() |> Seq.map (fun item -> Http.text item "id") |> Seq.toList)
                      server.PostJson("/api/work/WI-TYPED/start", """{"type":"feature"}""") |> ignore
                      let item = Http.json (server.Get "/api/v1/work/WI-TYPED")
                      Assert.equal "work-item" (Http.text item "kind")
                      Assert.equal "active" (Http.text item["item"] "semanticState")
                      Assert.equal "live" (Http.text item["item"] "governedBy")
                      Assert.equal "feature" (Http.text item["item"].["live"] "workType")
                      Assert.equal "ready" (Http.text item["item"].["backlog"] "status")) }

          { Name = "web serve: an unknown work item is a structured 404 on the versioned and the legacy routes"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      [ "/api/v1/work/WI-NOPE"; "/api/work/WI-NOPE" ]
                      |> List.iter (fun path ->
                          let response = server.Get path
                          Assert.equal 404 (Http.status response)
                          let body = Http.json response
                          Assert.equal "work-item-not-found" (Http.text body "code")
                          Assert.equal "WI-NOPE" (Http.text body "workItemId")
                          Http.contains "WI-NOPE" (Http.text body "error"))) }

          { Name = "web serve: read endpoints leave every repository file byte-identical"
            Run =
              fun () ->
                  withServer (fun root server ->
                      server.PostJson("/api/work", """{"title":"Read me","id":"WI-READ"}""") |> ignore
                      let before = snapshot root

                      [ "/api/v1/work"; "/api/v1/work?status=captured"; "/api/v1/work/WI-READ"; "/api/v1/work/WI-NOPE"; "/api/work"; "/api/work/WI-READ"; "/api/work/WI-NOPE" ]
                      |> List.iter (fun path -> server.Get path |> ignore)

                      let after = snapshot root
                      let changed = Set.union (Set.ofSeq before.Keys) (Set.ofSeq after.Keys) |> Set.filter (fun path -> before.TryFind path <> after.TryFind path)
                      Assert.isTrue changed.IsEmpty $"""reads changed: {String.Join(", ", changed)}""") }

          { Name = "web serve: the legacy /api/work shapes are unchanged by the versioned contract"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"Legacy","id":"WI-LEGACY"}""") |> ignore
                      let rows = (Http.json (server.Get "/api/work")).AsArray()
                      let row = rows |> Seq.find (fun item -> Http.text item "id" = "WI-LEGACY")
                      Assert.equal "captured" (Http.text row "status")
                      Assert.equal [ "abandon"; "ready" ] (Http.strings row "backlogActions")
                      let shown = Http.json (server.Get "/api/work/WI-LEGACY")
                      Assert.equal "WI-LEGACY" (Http.text shown "id")
                      Assert.isTrue (isNull shown["contract"]) "the legacy record is not wrapped in the versioned envelope") } ]

    let tests = projectionTests @ contractTests @ httpTests
