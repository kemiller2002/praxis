namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// Remote-adapter behavior main added after PR #92 diverged, ported from
/// main's Node suites (tests/praxis-remote-inbox.test.mjs and the additions
/// to tests/praxis-remote-adapter.test.mjs) so PR #92's removal of Node does
/// not leave it untested: the request inbox relay (DF-ROS-2026-A045 on main),
/// the persistence script's rate-limit classification and same-request
/// retry reuse, and the job-log report (PRAXIS-PR92-PREMERGE-REGRESSION-FENCE).
[<RequireQualifiedAccess>]
module PremergeRemoteScriptTests =
    open PremergeFence

    // ---- request inbox relay -------------------------------------------------

    type private Relay =
        { Result: Result
          /// Each dispatch: its arguments and the exact bytes of its @file.
          Dispatches: (string list * string option) list
          Summary: string }

    /// A stand-in for `gh`: records its arguments and the bytes of the
    /// `request=@FILE` it was handed, one directory per call.
    let private dispatchStub (root: string) =
        let calls = Path.Combine(root, "dispatches")
        Directory.CreateDirectory calls |> ignore
        let stub = Path.Combine(root, "dispatch.sh")

        File.WriteAllText(
            stub,
            $"#!/bin/sh\nset -eu\ncalls='{calls}'\nn=$(ls \"$calls\" | wc -l | tr -d ' ')\ncall=\"$calls/$(printf '%%04d' \"$n\")\"\nmkdir \"$call\"\n"
            + "printf '%s\\n' \"$@\" > \"$call/args\"\nfor argument in \"$@\"; do\n  case \"$argument\" in request=@*) cp \"${argument#request=@}\" \"$call/bytes\" ;; esac\ndone\n"
        )

        exec "chmod" root [] [ "+x"; stub ] |> ignore
        stub, calls

    let private readDispatches (calls: string) =
        Directory.GetDirectories calls
        |> Array.sort
        |> Array.map (fun call ->
            let args = File.ReadAllLines(Path.Combine(call, "args")) |> List.ofArray
            let bytes = Path.Combine(call, "bytes")
            args, (if File.Exists bytes then Some(File.ReadAllText bytes) else None))
        |> List.ofArray

    /// A real "GitHub" remote with main, and a runner workspace checked out
    /// at an inbox branch. `earlier` is committed in an earlier push, `files`
    /// in the push under test; `newBranch` makes the push under test the one
    /// that creates the branch (an all-zero `before`).
    let private relayPush (files: (string * string) list) (earlier: (string * string) list) (newBranch: bool) (deletions: string list) =
        let root = temporaryDirectory "inbox"
        let origin = Path.Combine(root, "origin.git")
        let workspace = Path.Combine(root, "workspace")
        git root [ "init"; "-q"; "--bare"; "-b"; "main"; origin ] |> ignore
        git root [ "clone"; "-q"; origin; workspace ] |> ignore
        configureGitIdentity workspace
        write workspace "README.md" "main\n"
        commitAll workspace "main" |> ignore
        git workspace [ "push"; "-q"; "origin"; "main" ] |> ignore
        git workspace [ "switch"; "-q"; "-c"; "praxis-inbox/test" ] |> ignore

        if not earlier.IsEmpty then
            earlier |> List.iter (fun (relative, content) -> write workspace relative content)
            commitAll workspace "earlier inbox push" |> ignore

        let before =
            if newBranch then String('0', 40) else git workspace [ "rev-parse"; "HEAD" ]

        files |> List.iter (fun (relative, content) -> write workspace relative content)
        deletions |> List.iter (fun relative -> File.Delete(Path.Combine(workspace, relative)))
        commitAll workspace "inbox push" |> ignore
        let stub, calls = dispatchStub root
        let summary = Path.Combine(root, "summary.md")

        let result =
            exec
                "bash"
                root
                [ "PRAXIS_INBOX_DISPATCH", stub
                  "GITHUB_STEP_SUMMARY", summary
                  "GITHUB_REF_NAME", "praxis-inbox/test"
                  "GITHUB_SHA", "abc123"
                  "GITHUB_ACTOR", "someone" ]
                [ repositoryFile "scripts/praxis-remote-inbox.sh"
                  "--before"
                  before
                  "--workspace"
                  workspace
                  "--default-ref"
                  "refs/heads/main" ]

        { Result = result
          Dispatches = readDispatches calls
          Summary = if File.Exists summary then File.ReadAllText summary else "" }

    let private inboxRequest (change: JsonObject -> unit) =
        let body =
            JsonNode.Parse(
                """{ "protocol": "praxis.remote", "protocolVersion": "1.3", "requestId": "req-inbox-test-0001", "operation": "work.continue",
                     "repository": { "ref": "refs/heads/proof/chatgpt-continuation", "expectedSha": "3333333333333333333333333333333333333333" },
                     "actor": { "kind": "agent", "id": "example/successor" }, "arguments": { "workItemId": "WI-1" } }"""
            )
            :?> JsonObject

        change body
        body.ToJsonString()

    let private unchanged (_: JsonObject) = ()

    let private withRef (reference: string) (body: JsonObject) =
        body.["repository"] <- JsonNode.Parse $$"""{ "ref": "{{reference}}", "expectedSha": "1111111111111111111111111111111111111111" }"""

    let private requestIdArgument (arguments: string list, _) = arguments |> List.item 6

    let private succeededRelay relay =
        Assert.isTrue (relay.Result.ExitCode = 0) $"relay failed: {relay.Result.Error}{relay.Result.Output}"
        relay

    // ---- persistence script ----------------------------------------------------

    type private Remote =
        { Origin: string
          Seed: string
          Runner: string }

    /// A bare "GitHub" remote seeded with a Praxis installation, and a runner
    /// checkout of its main branch.
    let private remoteAndCheckout () =
        let root = temporaryDirectory "adapter"
        let origin = Path.Combine(root, "origin.git")
        let seed = Path.Combine(root, "seed")
        git root [ "init"; "-q"; "--bare"; "-b"; "main"; origin ] |> ignore
        Directory.CreateDirectory seed |> ignore
        git seed [ "init"; "-q"; "-b"; "main" ] |> ignore
        configureGitIdentity seed
        cli seed [ "init"; "--project"; "Adapter" ] |> ok |> ignore
        editConfig seed (fun config -> config.["remote"] <- JsonNode.Parse """{ "capabilities": ["read", "mutate", "complete", "reconcile"] }""")
        commitAll seed "baseline" |> ignore
        git seed [ "remote"; "add"; "origin"; origin ] |> ignore
        git seed [ "push"; "-q"; "origin"; "main" ] |> ignore
        let runner = Path.Combine(root, "runner")
        git root [ "clone"; "-q"; origin; runner ] |> ignore
        configureGitIdentity runner

        { Origin = origin
          Seed = seed
          Runner = runner }

    let private startRequest (runner: string) (requestId: string) =
        $$"""{ "protocol": "praxis.remote", "protocolVersion": "1.0", "requestId": "{{requestId}}", "operation": "work.start",
               "repository": { "ref": "refs/heads/main", "expectedSha": "{{git runner [ "rev-parse"; "HEAD" ]}}" },
               "actor": { "kind": "agent", "id": "example/cloud-agent", "provider": "example", "runtime": "cloud-agent" },
               "arguments": { "workItemIds": ["WI-0100"] } }"""

    /// Executes a request as the runner does, writing the response file the
    /// persistence script reads.
    let private execute (runner: string) (body: string) =
        let file = Path.Combine(Path.GetTempPath(), $"praxis-fence-adapter-request-{Guid.NewGuid():N}.json")
        let response = Path.Combine(Path.GetTempPath(), $"praxis-fence-adapter-response-{Guid.NewGuid():N}.json")
        File.WriteAllText(file, body)

        try
            cli runner [ "remote"; "execute"; "--request"; file; "--grant"; "read"; "--grant"; "mutate"; "--output"; response ] |> ignore
            response
        finally
            File.Delete file

    let private persistWith (runner: string) (response: string) (mode: string) (environment: (string * string) list) =
        let output = response + ".adapter.json"
        let result = exec "sh" runner environment [ repositoryFile "scripts/praxis-remote-persist.sh"; "--response"; response; "--output"; output; "--mode"; mode ]
        result, readJson output

    /// A `gh` on PATH: `pr list` prints `list`; `pr create` prints `created`
    /// or fails with `createError`.
    let private ghStub (list: string) (created: string) (createError: string option) =
        let directory = temporaryDirectory "gh"
        let path = Path.Combine(directory, "gh")

        let create =
            match createError with
            | Some message -> $"echo '{message}' >&2; exit 1"
            | None -> $"echo '{created}'"

        File.WriteAllText(path, $"#!/bin/sh\ncase \"$1 $2\" in\n  \"pr list\") printf '%%s' '{list}' ;;\n  \"pr create\") {create} ;;\n  *) exit 64 ;;\nesac\n")
        exec "chmod" directory [] [ "+x"; path ] |> ignore
        [ "PATH", $"""{directory}:{Environment.GetEnvironmentVariable "PATH"}""" ]

    let private refusePushes (origin: string) (message: string) =
        let hook = Path.Combine(origin, "hooks", "pre-receive")
        File.WriteAllText(hook, $"#!/bin/sh\necho '{message}' >&2\nexit 1\n")
        exec "chmod" origin [] [ "+x"; hook ] |> ignore

    let private stateBranch (requestId: string) =
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes requestId) |> Convert.ToHexString
        $"praxis/remote/{digest.ToLowerInvariant().Substring(0, 24)}"

    let private freshRunner (origin: string) =
        let runner = Path.Combine(temporaryDirectory "retry", "runner")
        git (Path.GetDirectoryName runner) [ "clone"; "-q"; origin; runner ] |> ignore
        configureGitIdentity runner
        runner

    let private isNull (node: JsonNode) = isNull (box node)

    let private pushRefusals =
        [ "a secondary rate limit", "You have exceeded a secondary rate limit. Please wait a few minutes before you try again.", "rate-limited"
          "HTTP 429", "error: RPC failed; HTTP 429 curl 22 The requested URL returned error: 429", "rate-limited"
          "any other refusal", "pre-receive hook declined: repository is read-only", "repository-write-failed" ]

    let private pullRequestRefusals =
        [ "the API rate limit", "HTTP 403: API rate limit exceeded for installation ID 1234. (https://api.github.com/graphql)", "rate-limited"
          "any other error", "HTTP 422: Validation Failed (https://api.github.com/graphql)", "repository-write-failed" ]

    let private inboxTests =
        [ { Name = "fence inbox: a committed request is dispatched on the branch it names, byte for byte, under its own requestId"
            Run =
              fun () ->
                  // Unusual formatting must survive: the relay passes the file, never a re-serialization.
                  let bytes = (JsonNode.Parse(inboxRequest unchanged)).ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true, IndentSize = 3)) + "\n\n"
                  let relay = relayPush [ ".praxis-inbox/req-inbox-test-0001.json", bytes ] [] true [] |> succeededRelay
                  let arguments, sent = Assert.single relay.Dispatches

                  Assert.equal
                      [ "workflow"; "run"; "praxis-remote.yml"; "--ref"; "proof/chatgpt-continuation"; "-f"; "request_id=req-inbox-test-0001" ]
                      (arguments |> List.take 7)

                  Assert.equal (Some bytes) sent
                  contains "pushed by `someone`" relay.Summary "summary"
                  contains "dispatched `praxis remote req-inbox-test-0001` on `proof/chatgpt-continuation`" relay.Summary "summary" }

          { Name = "fence inbox: the relay never supplies or alters an actor"
            Run =
              fun () ->
                  let bytes = inboxRequest (fun body -> body.Remove "actor" |> ignore)
                  let relay = relayPush [ ".praxis-inbox/a.json", bytes ] [] true [] |> succeededRelay
                  Assert.equal (Some bytes) (snd (Assert.single relay.Dispatches))

                  Assert.isTrue
                      (not (Regex.IsMatch(readRepositoryFile "scripts/praxis-remote-inbox.sh", "\"actor\"\\s*[:=]|\\bactor\\s*=")))
                      "the relay has no actor logic" }

          { Name = "fence inbox: a read request with no ref goes to the default branch"
            Run =
              fun () ->
                  let describe =
                      """{"protocol":"praxis.remote","protocolVersion":"1.3","requestId":"req-describe-0001","operation":"praxis.describe"}"""

                  let relay = relayPush [ ".praxis-inbox/describe.json", describe ] [] true [] |> succeededRelay
                  Assert.equal "main" (relay.Dispatches |> List.head |> fst |> List.item 4) }

          { Name = "fence inbox: unroutable files are refused visibly without dispatch while valid ones in the same push still go"
            Run =
              fun () ->
                  let relay =
                      relayPush
                          [ ".praxis-inbox/good.json", inboxRequest unchanged
                            ".praxis-inbox/broken.json", "{ not json"
                            ".praxis-inbox/other.json", inboxRequest (fun body -> body.["protocol"] <- JsonValue.Create "something-else")
                            ".praxis-inbox/short-id.json", inboxRequest (fun body -> body.["requestId"] <- JsonValue.Create "r1")
                            ".praxis-inbox/inbox-target.json", inboxRequest (withRef "refs/heads/praxis-inbox/loop")
                            ".praxis-inbox/traversal.json", inboxRequest (withRef "refs/heads/a/../main")
                            ".praxis-inbox/tag.json", inboxRequest (withRef "refs/tags/v1") ]
                          []
                          true
                          []

                  Assert.equal 1 relay.Result.ExitCode
                  Assert.equal [ "request_id=req-inbox-test-0001" ] (relay.Dispatches |> List.map requestIdArgument)

                  let refusals =
                      relay.Result.Output.Split('\n') |> Array.filter (fun line -> line.StartsWith("::error", StringComparison.Ordinal))

                  Assert.equal 6 refusals.Length
                  contains "`.praxis-inbox/broken.json`: refused, not valid JSON" relay.Summary "summary"
                  contains "Nothing was dispatched" relay.Summary "summary" }

          { Name = "fence inbox: a later push relays only the inbox files it added or modified"
            Run =
              fun () ->
                  let relay =
                      relayPush
                          [ ".praxis-inbox/new.json", inboxRequest unchanged
                            "README.md", "changed\n"
                            ".praxis-inbox/sub/nested.json", "{}"
                            ".praxis-inbox/bad name.json", "{}" ]
                          [ ".praxis-inbox/old.json", inboxRequest (fun body -> body.["requestId"] <- JsonValue.Create "req-old-00001")
                            ".praxis-inbox/gone.json", inboxRequest (fun body -> body.["requestId"] <- JsonValue.Create "req-gone-0001") ]
                          false
                          [ ".praxis-inbox/gone.json" ]
                      |> succeededRelay

                  Assert.equal [ "request_id=req-inbox-test-0001" ] (relay.Dispatches |> List.map requestIdArgument) }

          { Name = "fence inbox: the push that creates an inbox branch relays its request"
            Run =
              fun () ->
                  let relay = relayPush [ ".praxis-inbox/first.json", inboxRequest unchanged ] [] true [] |> succeededRelay
                  Assert.equal 1 relay.Dispatches.Length }

          { Name = "fence inbox: a push with no inbox request dispatches nothing and succeeds"
            Run =
              fun () ->
                  let relay = relayPush [ "README.md", "x" ] [] true [] |> succeededRelay
                  Assert.empty relay.Dispatches }

          { Name = "fence inbox: its trust boundary is dispatch's: push to inbox branches only, least privilege, no secrets, pinned actions"
            Run =
              fun () ->
                  let workflow = readRepositoryFile ".github/workflows/praxis-remote-inbox.yml"
                  let triggers = workflow.Split("\non:\n").[1].Split("\n\n").[0]

                  Assert.equal
                      [ "push" ]
                      (Regex.Matches(triggers, "^  ([a-z_]+):", RegexOptions.Multiline) |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq)

                  contains "branches: [\"praxis-inbox/**\"]" triggers "inbox branches"
                  contains "paths: [\".praxis-inbox/*.json\"]" triggers "inbox paths"
                  excludes "pull_request" triggers "no pull request trigger"
                  excludes "pull_request_target" workflow "no pull_request_target trigger"
                  excludes "workflow_run" workflow "no workflow_run trigger"
                  contains "\npermissions: {}\n" workflow "no default permissions"
                  contains "\n    permissions:\n      contents: read\n      actions: write\n" workflow "least privilege"
                  excludes "secrets." workflow "no secrets"

                  Regex.Matches(workflow, "uses:\\s*(\\S+)")
                  |> Seq.iter (fun m -> matches "@[0-9a-f]{40}$" m.Groups.[1].Value "actions are pinned to a commit")

                  Regex.Matches(workflow, "\n        run: (.+)\n")
                  |> Seq.iter (fun m -> excludes "${{" m.Groups.[1].Value "run bodies take values from the environment only") }

          { Name = "fence inbox: the relay targets praxis-remote.yml only, and the agent contract documents the inbox"
            Run =
              fun () ->
                  let targets =
                      Regex.Matches(readRepositoryFile "scripts/praxis-remote-inbox.sh", "workflow run (\\S+)")
                      |> Seq.map (fun m -> m.Groups.[1].Value)
                      |> List.ofSeq

                  Assert.isTrue (not targets.IsEmpty && targets |> List.forall ((=) "praxis-remote.yml")) (String.concat ", " targets)
                  contains "\n  workflow_dispatch:\n" (readRepositoryFile ".github/workflows/praxis-remote.yml") "praxis-remote.yml is dispatchable"
                  let contract = readRepositoryFile "docs/remote-agent-contract.md"
                  contains "praxis-inbox/" contract "contract"
                  contains ".praxis-inbox/<requestId>.json" contract "contract"
                  matches "(?i)cannot dispatch" contract "contract" } ]

    let private persistTests =
        (pushRefusals
         |> List.map (fun (label, message, code) ->
             { Name = $"fence persist: a push refused with {label} is {code} and nothing reaches the remote"
               Run =
                 fun () ->
                     let remote = remoteAndCheckout ()
                     let response = execute remote.Runner (startRequest remote.Runner "req-adapter-0001")
                     let before = git remote.Origin [ "rev-parse"; "main" ]
                     refusePushes remote.Origin message
                     let result, adapter = persistWith remote.Runner response "push" []
                     Assert.equal 1 result.ExitCode
                     Assert.equal false (boolean adapter.["persisted"])
                     Assert.isTrue (isNull adapter.["commit"]) "no commit"
                     Assert.equal code (text adapter.["failure"].["code"])
                     Assert.equal "executor" (text adapter.["failure"].["decidedBy"])
                     Assert.equal "same-request" (text adapter.["failure"].["retry"])
                     Assert.equal before (git remote.Origin [ "rev-parse"; "main" ]) }))
        @ (pullRequestRefusals
           |> List.map (fun (label, message, code) ->
               { Name = $"fence persist: a pull request refused with {label} is {code}, keeping the pushed state branch"
                 Run =
                   fun () ->
                       let remote = remoteAndCheckout ()
                       let response = execute remote.Runner (startRequest remote.Runner "req-adapter-pr-throttle")
                       let before = git remote.Origin [ "rev-parse"; "main" ]
                       let result, adapter = persistWith remote.Runner response "pull-request" (ghStub "" "" (Some message))
                       Assert.equal 1 result.ExitCode
                       Assert.equal false (boolean adapter.["persisted"])
                       Assert.isTrue (isNull adapter.["pullRequest"]) "no pull request"
                       Assert.equal code (text adapter.["failure"].["code"])
                       Assert.equal "same-request" (text adapter.["failure"].["retry"])
                       Assert.equal (text adapter.["commit"]) (git remote.Origin [ "rev-parse"; text adapter.["branch"] ])
                       Assert.equal before (git remote.Origin [ "rev-parse"; "main" ])
                       contains message result.Error "gh's diagnostic is kept for the log" }))
        @ [ { Name = "fence persist: a same-request retry after the pull request could not be opened reuses the pushed state branch"
              Run =
                fun () ->
                    let remote = remoteAndCheckout ()
                    let requestId = "req-adapter-pr-retry"

                    let _, first =
                        persistWith remote.Runner (execute remote.Runner (startRequest remote.Runner requestId)) "pull-request" (ghStub "" "" (Some "HTTP 403: API rate limit exceeded for installation ID 1234."))

                    Assert.equal "rate-limited" (text first.["failure"].["code"])
                    let pushed = git remote.Origin [ "rev-parse"; stateBranch requestId ]
                    let retry = freshRunner remote.Origin

                    let result, second =
                        persistWith retry (execute retry (startRequest retry requestId)) "pull-request" (ghStub "" "https://github.example/octo/repo/pull/8" None)

                    Assert.equal 0 result.ExitCode
                    Assert.isTrue (isNull second.["failure"]) "no failure"
                    Assert.equal true (boolean second.["reused"])
                    Assert.equal pushed (text second.["commit"])
                    Assert.equal "https://github.example/octo/repo/pull/8" (text second.["pullRequest"])
                    Assert.equal pushed (git remote.Origin [ "rev-parse"; stateBranch requestId ]) }

            { Name = "fence persist: a same-request retry reports the pull request an earlier attempt already opened"
              Run =
                fun () ->
                    let remote = remoteAndCheckout ()
                    let requestId = "req-adapter-pr-open"

                    let _, first =
                        persistWith remote.Runner (execute remote.Runner (startRequest remote.Runner requestId)) "pull-request" (ghStub "" "https://github.example/octo/repo/pull/9" None)

                    Assert.equal false (boolean first.["reused"])
                    let retry = freshRunner remote.Origin

                    let result, second =
                        persistWith
                            retry
                            (execute retry (startRequest retry requestId))
                            "pull-request"
                            (ghStub "https://github.example/octo/repo/pull/9" "" (Some "a pull request already exists"))

                    Assert.equal 0 result.ExitCode
                    Assert.equal true (boolean second.["reused"])
                    Assert.equal "https://github.example/octo/repo/pull/9" (text second.["pullRequest"])
                    Assert.equal (text first.["commit"]) (text second.["commit"]) }

            { Name = "fence persist: a request's state branch that holds other changes is a conflict and is left untouched"
              Run =
                fun () ->
                    let remote = remoteAndCheckout ()
                    let requestId = "req-adapter-pr-occupied"
                    write remote.Seed "notes.md" "not this request's state\n"
                    commitAll remote.Seed "unrelated" |> ignore
                    git remote.Seed [ "push"; "-q"; "origin"; $"HEAD:refs/heads/{stateBranch requestId}" ] |> ignore
                    let occupied = git remote.Origin [ "rev-parse"; stateBranch requestId ]

                    let result, adapter =
                        persistWith remote.Runner (execute remote.Runner (startRequest remote.Runner requestId)) "pull-request" (ghStub "" "" None)

                    Assert.equal 1 result.ExitCode
                    Assert.equal false (boolean adapter.["persisted"])
                    Assert.equal false (boolean adapter.["reused"])
                    Assert.equal "concurrency-conflict" (text adapter.["failure"].["code"])
                    Assert.equal "after-refresh" (text adapter.["failure"].["retry"])
                    Assert.equal occupied (git remote.Origin [ "rev-parse"; stateBranch requestId ]) }

            { Name = "fence action: the response reaches the job log with workflow commands suspended"
              Run =
                fun () ->
                    let action = readRepositoryFile ".github/actions/praxis-remote/action.yml"
                    let report = Regex.Replace(Regex.Match(action, "<<'PY'\n([\\s\\S]*?)\n\\s*PY\n").Groups.[1].Value, "^ {8}", "", RegexOptions.Multiline)
                    let directory = temporaryDirectory "report"

                    let file name (content: string) =
                        let path = Path.Combine(directory, name)
                        File.WriteAllText(path, content)
                        path

                    let response =
                        """{"requestId":"req-log-1","operation":"work.block","outcome":"rejected","failure":{"code":"domain-rejected","retry":"never","message":"::error::injected\n::add-mask::x"}}"""

                    let script = file "report.py" report

                    let result =
                        exec
                            "python3"
                            directory
                            [ "GITHUB_OUTPUT", file "output" ""; "GITHUB_STEP_SUMMARY", file "summary" "" ]
                            [ script; file "response.json" response; file "adapter.json" """{"persisted":false,"failure":null}""" ]

                    Assert.equal 1 result.ExitCode
                    let logged = Regex.Match(result.Output, "::stop-commands::([0-9a-f]{32})\n([\\s\\S]*?)\n::\\1::\n")
                    Assert.isTrue logged.Success $"the response is logged inside stop-commands: {result.Output}"
                    let logResponse = (JsonNode.Parse(logged.Groups.[2].Value)).["response"]
                    Assert.equal (JsonNode.Parse(response).ToJsonString()) (logResponse.ToJsonString())
                    matches "^::error::praxis\\.remote rejected: domain-rejected \\(never\\)$" result.Output "the adapter's own error follows once commands resume" } ]

    let tests = inboxTests @ persistTests
