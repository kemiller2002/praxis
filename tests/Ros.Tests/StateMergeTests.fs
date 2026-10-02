namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Work

/// Merging Praxis state from parallel branches (PRAXIS-STATE-MERGE-01): the
/// pure merge rules, then the installed Git merge driver end to end.
[<RequireQualifiedAccess>]
module StateMergeTests =
    let private merged outcome =
        match outcome with
        | StateMergeOutcome.Merged content -> content
        | other -> failwith $"expected a merge, got %A{other}"

    let private conflicts outcome =
        match outcome with
        | StateMergeOutcome.Conflicted found -> found |> List.map (fun conflict -> conflict.Item)
        | other -> failwith $"expected a conflict, got %A{other}"

    let private event id (occurredAt: string) (workItem: string) =
        $"{{\"type\":\"work.started\",\"workItem\":\"{workItem}\",\"occurredAt\":\"{occurredAt}\",\"eventId\":\"{id}\"}}"

    let private lines (values: string list) = values |> List.map (fun line -> line + "\n") |> String.concat ""

    let private eventIds (text: string) =
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun line -> (JsonNode.Parse line).["eventId"].GetValue<string>())
        |> Array.toList

    let private queue (items: string list) (nextSeq: int) =
        let body = String.Join(",", items)
        $"{{\"schemaVersion\":\"1.0.0\",\"nextSeq\":{nextSeq},\"items\":[{body}]}}"

    let private item id title status = $"{{\"id\":\"{id}\",\"title\":\"{title}\",\"status\":\"{status}\"}}"

    let private ids (text: string) (property: string) =
        (JsonNode.Parse text).[property].AsArray() |> Seq.map (fun node -> node["id"].GetValue<string>()) |> Seq.toList

    let private eventsPath = StateMergeJson.eventsPath

    let private unitTests =
        [ { Name = "state merge: append-only events keep every event once, in one order whichever side is ours"
            Run =
              fun () ->
                  let ancestor = lines [ event "e1" "2026-01-01T00:00:00.000Z" "A" ]
                  let ours = lines [ event "e1" "2026-01-01T00:00:00.000Z" "A"; event "e3" "2026-01-03T00:00:00.000Z" "B"; event "e4" "2026-01-04T00:00:00.000Z" "B" ]
                  let theirs = lines [ event "e1" "2026-01-01T00:00:00.000Z" "A"; event "e2" "2026-01-02T00:00:00.000Z" "C"; event "e5" "2026-01-05T00:00:00.000Z" "C" ]
                  let forward = merged (StateMergeJson.merge eventsPath (Some ancestor) ours theirs)
                  let backward = merged (StateMergeJson.merge eventsPath (Some ancestor) theirs ours)
                  Assert.equal [ "e1"; "e2"; "e3"; "e4"; "e5" ] (eventIds forward)
                  Assert.equal forward backward

                  // An event both sides carry (for example a shared cherry-pick) appears once.
                  let shared = event "e9" "2026-01-09T00:00:00.000Z" "D"
                  let both = merged (StateMergeJson.merge eventsPath (Some ancestor) (ours + shared + "\n") (theirs + shared + "\n"))
                  Assert.equal 1 (eventIds both |> List.filter ((=) "e9") |> List.length) }
          { Name = "state merge: rewriting or removing history, or one event id with two contents, fails closed"
            Run =
              fun () ->
                  let ancestor = lines [ event "e1" "2026-01-01T00:00:00.000Z" "A" ]
                  let rewritten = lines [ event "e1" "2026-01-01T00:00:00.000Z" "Z" ]
                  Assert.equal [ "eventId e1" ] (conflicts (StateMergeJson.merge eventsPath (Some ancestor) rewritten ancestor))
                  Assert.equal [ "eventId e1" ] (conflicts (StateMergeJson.merge eventsPath (Some ancestor) "" ancestor))

                  let left = ancestor + event "e2" "2026-01-02T00:00:00.000Z" "B" + "\n"
                  let right = ancestor + event "e2" "2026-01-02T00:00:00.000Z" "C" + "\n"
                  Assert.equal [ "eventId e2" ] (conflicts (StateMergeJson.merge eventsPath (Some ancestor) left right))

                  match StateMergeJson.merge eventsPath (Some ancestor) "not json\n" ancestor with
                  | StateMergeOutcome.Unsupported _ -> ()
                  | other -> failwith $"expected malformed input to be refused, got %A{other}" }
          { Name = "state merge: id-keyed items merge three-way and a counter takes the larger value"
            Run =
              fun () ->
                  let ancestor = queue [ item "WI-1" "one" "ready"; item "WI-2" "two" "ready" ] 3
                  let ours = queue [ item "WI-1" "one" "active"; item "WI-2" "two" "ready"; item "WI-3" "three" "captured" ] 4
                  let theirs = queue [ item "WI-1" "one" "ready"; item "WI-2" "two" "blocked"; item "WI-4" "four" "captured" ] 5
                  let result = JsonNode.Parse(merged (StateMergeJson.merge StateMergeJson.queuePath (Some ancestor) ours theirs))
                  let statuses = result["items"].AsArray() |> Seq.map (fun node -> node["id"].GetValue<string>(), node["status"].GetValue<string>()) |> Seq.toList
                  Assert.equal [ "WI-1", "active"; "WI-2", "blocked"; "WI-3", "captured"; "WI-4", "captured" ] statuses
                  Assert.equal 5 (result["nextSeq"].GetValue<int>())

                  // Removing an item one side did not touch removes it.
                  let removed = queue [ item "WI-2" "two" "ready" ] 3
                  Assert.equal [ "WI-2" ] (ids (merged (StateMergeJson.merge StateMergeJson.queuePath (Some ancestor) removed ancestor)) "items") }
          { Name = "state merge: one item changed differently on both sides is refused and named"
            Run =
              fun () ->
                  let ancestor = queue [ item "WI-1" "one" "ready" ] 2
                  let ours = queue [ item "WI-1" "one (ours)" "ready" ] 2
                  let theirs = queue [ item "WI-1" "one (theirs)" "ready" ] 2
                  Assert.equal [ "items 'WI-1'" ] (conflicts (StateMergeJson.merge StateMergeJson.queuePath (Some ancestor) ours theirs))

                  let removed = queue [] 2
                  Assert.equal [ "items 'WI-1'" ] (conflicts (StateMergeJson.merge StateMergeJson.queuePath (Some ancestor) ours removed))

                  // The same item added on both sides with different content (a colliding generated id).
                  let empty = queue [] 2
                  Assert.equal [ "items 'WI-2'" ] (conflicts (StateMergeJson.merge StateMergeJson.queuePath (Some empty) (queue [ item "WI-2" "a" "captured" ] 3) (queue [ item "WI-2" "b" "captured" ] 3))) }
          { Name = "state merge: context bookkeeping follows the latest write while work items merge by id"
            Run =
              fun () ->
                  let context items (updatedAt: string) (actor: string) =
                      let body = String.Join(",", items |> List.map (fun (id, state) -> $"{{\"id\":\"{id}\",\"state\":\"{state}\"}}"))
                      $"{{\"schemaVersion\":\"1.0.0\",\"workItems\":[{body}],\"actor\":\"{actor}\",\"updatedAt\":\"{updatedAt}\"}}"

                  let ancestor = context [ "A", "ready"; "B", "ready" ] "2026-01-01T00:00:00.000Z" "x"

                  // A context first started on both branches keeps the earlier start.
                  let started (startedAt: string) = (context [] startedAt "x").Replace("\"actor\"", $"\"startedAt\":\"{startedAt}\",\"actor\"")
                  let first = JsonNode.Parse(merged (StateMergeJson.merge StateMergeJson.contextPath None (started "2026-01-05T00:00:00.000Z") (started "2026-01-04T00:00:00.000Z")))
                  Assert.equal "2026-01-04T00:00:00.000Z" (first.["startedAt"].GetValue<string>())
                  let ours = context [ "A", "complete"; "B", "ready" ] "2026-01-03T00:00:00.000Z" "alice"
                  let theirs = context [ "A", "ready"; "B", "active"; "C", "active" ] "2026-01-02T00:00:00.000Z" "bob"
                  let result = JsonNode.Parse(merged (StateMergeJson.merge StateMergeJson.contextPath (Some ancestor) ours theirs))
                  Assert.equal "2026-01-03T00:00:00.000Z" (result["updatedAt"].GetValue<string>())
                  Assert.equal "alice" (result["actor"].GetValue<string>())

                  Assert.equal
                      [ "A", "complete"; "B", "active"; "C", "active" ]
                      (result["workItems"].AsArray() |> Seq.map (fun node -> node["id"].GetValue<string>(), node["state"].GetValue<string>()) |> Seq.toList) }
          { Name = "state merge: the queue projection merges by row, generated registries stay sorted, receipts merge by key"
            Run =
              fun () ->
                  let table rows = "# Work Queue\n\n| ID | Work | Status |\n|---|---|---|\n" + (rows |> List.map (fun (id, status) -> $"| {id} | {id} work | {status} |\n") |> String.concat "")
                  let ancestor = table [ "A", "ready"; "C", "ready" ]
                  let ours = table [ "A", "active"; "C", "ready" ]
                  let theirs = table [ "A", "ready"; "B", "captured"; "C", "complete" ]
                  Assert.equal (table [ "A", "active"; "B", "captured"; "C", "complete" ]) (merged (StateMergeJson.merge StateMergeJson.queueMarkdownPath (Some ancestor) ours theirs))
                  Assert.equal [ "row 'A'" ] (conflicts (StateMergeJson.merge StateMergeJson.queueMarkdownPath (Some ancestor) ours (table [ "A", "blocked"; "C", "ready" ])))

                  let registry entries = "[" + String.Join(",", entries |> List.map (fun id -> $"{{\"id\":\"{id}\",\"path\":\"p/{id}.md\"}}")) + "]"
                  let result = merged (StateMergeJson.merge "registries/evidence.json" (Some(registry [ "EV-A" ])) (registry [ "EV-A"; "EV-C" ]) (registry [ "EV-A"; "EV-B" ]))
                  Assert.equal [ "EV-A"; "EV-B"; "EV-C" ] (JsonNode.Parse(result).AsArray() |> Seq.map (fun node -> node["id"].GetValue<string>()) |> Seq.toList)

                  let receipts = merged (StateMergeJson.merge StateMergeJson.publicationsPath (Some "{}") "{\"e1\":{\"status\":\"success\"}}" "{\"e2\":{\"status\":\"success\"}}")
                  Assert.equal [ "e1"; "e2" ] (JsonNode.Parse(receipts).AsObject() |> Seq.map _.Key |> Seq.toList) }
          { Name = "state merge: files outside the covered set are refused rather than guessed"
            Run =
              fun () ->
                  match StateMergeJson.merge "src/Program.fs" None "a" "b" with
                  | StateMergeOutcome.Unsupported _ -> ()
                  | other -> failwith $"expected refusal, got %A{other}"

                  Assert.isTrue (StateMergeJson.ruleFor "./.ros/work/queue.json" |> Option.isSome) "a ./ prefix is the same path" } ]

    // ---- end to end through Git -----------------------------------------------

    let private cli = Path.Combine(AppContext.BaseDirectory, "praxis.dll")

    let private identityVariables =
        [ "CLAUDE_CODE_SESSION_ID"; "CODEX_SESSION_ID"; "CODEX_THREAD_ID"; "GEMINI_SESSION_ID"; "COPILOT_SESSION_ID"
          "GITHUB_ACTIONS"; "GITHUB_RUN_ID"; "OLLAMA_HOST"; "ROS_ACTOR"; "ROS_ACTOR_KIND"; "ROS_TELEMETRY_PROVIDER"
          "ROS_TELEMETRY_RUNTIME"; "ROS_TELEMETRY_MODEL"; "ROS_TELEMETRY_MODEL_VERSION"; "ROS_TELEMETRY_RUNTIME_VERSION"
          "ROS_TELEMETRY_SESSION_ID"; "ROS_TELEMETRY_CONVERSATION_ID"; "ROS_TELEMETRY_RUN_ID"; "ROS_BASE_REF"
          "PRAXIS_ACTOR"; "PRAXIS_ACTOR_KIND"; "PRAXIS_TELEMETRY_PROVIDER"; "PRAXIS_TELEMETRY_RUNTIME"; "PRAXIS_TELEMETRY_MODEL" ]

    type private Run = { Exit: int; Out: string; Err: string }

    let private run (directory: string) (fileName: string) (arguments: string list) =
        let startInfo = ProcessStartInfo(fileName)
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.WorkingDirectory <- directory
        arguments |> List.iter startInfo.ArgumentList.Add
        identityVariables |> List.iter (startInfo.Environment.Remove >> ignore)
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        { Exit = child.ExitCode; Out = output.Result; Err = error.Result }

    let private succeed (result: Run) =
        if result.Exit <> 0 then failwith $"exit {result.Exit}\nstdout: {result.Out}\nstderr: {result.Err}"
        result

    let private git root arguments = run root "git" arguments |> succeed |> fun result -> result.Out.Trim()

    let private praxis root arguments = run root "dotnet" ([ cli; "--root"; root ] @ arguments)

    let private now () = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    let private human = [ "--actor-kind"; "human"; "--actor"; "kevin" ]

    let private write root (relative: string) (content: string) =
        let path = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let private commit root message =
        git root [ "add"; "-A" ] |> ignore
        git root [ "-c"; "user.name=Tester"; "-c"; "user.email=tester@example.invalid"; "commit"; "-qm"; message ] |> ignore

    let private config =
        """{
  "repository": { "id": "merge-test" },
  "workProtocol": {
    "version": "1.0.0",
    "enforceAttribution": true,
    "meaningfulPaths": ["**"],
    "ignoredPaths": [".git/**", ".ros/**", "registries/**", ".gitattributes"]
  },
  "telemetry": { "enabled": false, "disabledReason": "state merge tests" }
}
"""

    let private context =
        """{
  "schemaVersion": "1.0.0",
  "repository": "merge-test",
  "workItems": [],
  "baselineDirtyPaths": []
}
"""

    let private backlog =
        """{
  "schemaVersion": "1.0.0",
  "nextSeq": 3,
  "items": [
    { "id": "WI-0001", "title": "first item", "status": "ready", "tags": [], "priority": null, "attachments": [] },
    { "id": "WI-0002", "title": "second item", "status": "ready", "tags": [], "priority": null, "attachments": [] }
  ]
}
"""

    /// A repository with Praxis state, current registries and the merge
    /// driver installed; `./praxis` is an untracked launcher for this build
    /// of the CLI, standing in for the repository's own launcher.
    let private withRepository (test: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-state-merge-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            write root "ros.json" config
            write root ".ros/context/current.json" context
            write root ".ros/work/queue.json" backlog
            write root ".ros/events/events.jsonl" ""
            write root "README.md" "merge test\n"
            write root "praxis" $"#!/bin/sh\nexec dotnet \"{cli}\" --root \"$(pwd)\" \"$@\"\n"
            File.SetUnixFileMode(Path.Combine(root, "praxis"), UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            git root [ "init"; "-q"; "-b"; "main" ] |> ignore
            git root [ "config"; "commit.gpgsign"; "false" ] |> ignore
            write root ".git/info/exclude" "/praxis\n"
            praxis root [ "registry"; "build" ] |> succeed |> ignore
            praxis root [ "state"; "merge-driver"; "install" ] |> succeed |> ignore
            commit root "baseline"
            test root
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ()

    let private eventCount root =
        File.ReadAllLines(Path.Combine(root, ".ros", "events", "events.jsonl")) |> Array.filter (fun line -> line.Trim().Length > 0) |> Array.length

    let private validationFindings root =
        let result = praxis root [ "validate"; "--json" ]
        (JsonNode.Parse result.Out).["findings"].AsArray() |> Seq.map (fun finding -> finding.ToJsonString()) |> Seq.toList

    let private e2eTests =
        [ { Name = "state merge driver: install is idempotent and status reports it"
            Run =
              fun () ->
                  withRepository (fun root ->
                      let attributes = File.ReadAllText(Path.Combine(root, ".gitattributes"))
                      Assert.isTrue (attributes.Contains "/.ros/events/events.jsonl merge=praxis-state") attributes
                      Assert.isTrue (attributes.Contains "/registries/evidence.json merge=praxis-state") attributes
                      praxis root [ "state"; "merge-driver"; "install" ] |> succeed |> ignore
                      Assert.equal attributes (File.ReadAllText(Path.Combine(root, ".gitattributes")))
                      Assert.equal 0 (praxis root [ "state"; "merge-driver"; "status" ]).Exit
                      git root [ "config"; "--unset"; "merge.praxis-state.driver" ] |> ignore
                      Assert.equal 3 (praxis root [ "state"; "merge-driver"; "status" ]).Exit) }
          { Name = "state merge driver: branches that started, blocked and completed different items merge and validate"
            Run =
              fun () ->
                  withRepository (fun root ->
                      git root [ "checkout"; "-qb"; "left" ] |> ignore
                      praxis root ([ "work"; "start"; "--id"; "WI-0001"; "--occurred-at"; now () ] @ human) |> succeed |> ignore
                      praxis root ([ "work"; "complete"; "--id"; "WI-0001"; "--occurred-at"; now (); "--evidence"; "implementation=README.md"; "--evidence"; "tests=README.md" ] @ human) |> succeed |> ignore
                      commit root "left: WI-0001"
                      let left = eventCount root

                      git root [ "checkout"; "-q"; "main" ] |> ignore
                      git root [ "checkout"; "-qb"; "right" ] |> ignore
                      praxis root ([ "work"; "start"; "--id"; "WI-0002"; "--occurred-at"; now () ] @ human) |> succeed |> ignore
                      praxis root ([ "work"; "block"; "--id"; "WI-0002"; "--reason"; "waiting"; "--occurred-at"; now () ] @ human) |> succeed |> ignore
                      commit root "right: WI-0002"
                      let right = eventCount root

                      // Without the driver this is exactly the reported conflict.
                      git root [ "checkout"; "-q"; "left" ] |> ignore
                      git root [ "config"; "--remove-section"; "merge.praxis-state" ] |> ignore
                      let plain = run root "git" [ "-c"; "user.name=Tester"; "-c"; "user.email=tester@example.invalid"; "merge"; "--no-edit"; "right" ]
                      Assert.isTrue (plain.Exit <> 0) $"expected the unassisted merge to conflict: {plain.Out}"
                      Assert.isTrue (plain.Out.Contains ".ros/events/events.jsonl") plain.Out
                      git root [ "merge"; "--abort" ] |> ignore

                      praxis root [ "state"; "merge-driver"; "install" ] |> succeed |> ignore
                      git root [ "-c"; "user.name=Tester"; "-c"; "user.email=tester@example.invalid"; "merge"; "--no-edit"; "right" ] |> ignore
                      Assert.equal "" (git root [ "status"; "--porcelain"; "--untracked-files=no" ])
                      Assert.equal (left + right) (eventCount root)

                      let shown id = JsonNode.Parse((praxis root [ "work"; "show"; id ] |> succeed).Out)
                      Assert.equal "complete" ((shown "WI-0001").["liveWorkItem"].["state"].GetValue<string>())
                      Assert.equal "blocked" ((shown "WI-0002").["liveWorkItem"].["state"].GetValue<string>())
                      Assert.empty (validationFindings root)) }
          { Name = "state merge driver: the same item changed on both branches fails closed and names the item"
            Run =
              fun () ->
                  withRepository (fun root ->
                      git root [ "checkout"; "-qb"; "left" ] |> ignore
                      praxis root [ "work"; "update"; "--id"; "WI-0001"; "--title"; "left title"; "--occurred-at"; now () ] |> succeed |> ignore
                      commit root "left"
                      git root [ "checkout"; "-q"; "main" ] |> ignore
                      git root [ "checkout"; "-qb"; "right" ] |> ignore
                      praxis root [ "work"; "update"; "--id"; "WI-0001"; "--title"; "right title"; "--occurred-at"; now () ] |> succeed |> ignore
                      commit root "right"
                      git root [ "checkout"; "-q"; "left" ] |> ignore
                      let result = run root "git" [ "-c"; "user.name=Tester"; "-c"; "user.email=tester@example.invalid"; "merge"; "--no-edit"; "right" ]
                      Assert.isTrue (result.Exit <> 0) "the merge must not succeed"
                      Assert.isTrue (result.Err.Contains "CONFLICT .ros/work/queue.json: items 'WI-0001'") $"{result.Out}\n{result.Err}"
                      let queueText = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (queueText.Contains "<<<<<<<") "conflict markers keep the refusal visible"
                      Assert.isTrue ((git root [ "diff"; "--name-only"; "--diff-filter=U" ]).Contains ".ros/work/queue.json") "queue.json stays unmerged") } ]

    let tests = unitTests @ e2eTests
