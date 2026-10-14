namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// GitHub Actions adapter for praxis.remote (PRAXIS-REMOTE-06,
/// DF-ROS-2026-A041 section 10). The persistence script
/// scripts/praxis-remote-persist.sh is exercised against real Git remotes
/// with the built F# executor standing in for the pinned release; the
/// workflow and actions are checked for the trust boundary #90 requires
/// (least privilege, no forks, no interpolation, no secrets, pinned
/// dependencies, no Praxis domain logic in YAML). Ported from the former
/// tests/praxis-remote-adapter.test.mjs; the script tests are POSIX only.
[<RequireQualifiedAccess>]
module RemotePersistScriptTests =
    type private Checkout =
        { Origin: string
          Seed: string
          Runner: string }

    type private Persisted =
        { Exit: int
          Err: string
          Adapter: JsonNode }

    let private repositoryFile (relative: string) =
        Path.Combine(CliPort.repositoryRoot.Value, relative)

    let private read (relative: string) = File.ReadAllText(repositoryFile relative)

    let private persist () = repositoryFile "scripts/praxis-remote-persist.sh"

    let private doesNotMatch (pattern: string) (text: string) =
        if Regex.IsMatch(text, pattern, RegexOptions.Multiline) then
            failwith $"Expected text not matching /{pattern}/ but received:\n{text}"

    /// Git configuration every repository here carries, so commits never
    /// depend on the machine's identity or signing setup.
    let private configure (root: string) (name: string) (email: string) =
        CliHarness.git root [ "config"; "user.email"; email ] |> ignore
        CliHarness.git root [ "config"; "user.name"; name ] |> ignore
        CliHarness.git root [ "config"; "commit.gpgsign"; "false" ] |> ignore

    /// A bare "GitHub" remote and a runner checkout of its main branch.
    let private remoteAndCheckout (temporary: string -> string) =
        let baseDirectory = temporary "praxis-adapter-repo"
        let origin = Path.Combine(baseDirectory, "origin.git")
        let seed = Path.Combine(baseDirectory, "seed")
        CliHarness.git baseDirectory [ "init"; "-q"; "--bare"; "-b"; "main"; origin ] |> ignore
        Directory.CreateDirectory seed |> ignore
        CliHarness.git seed [ "init"; "-q"; "-b"; "main" ] |> ignore
        CliHarness.rosOk seed [ "init"; "--project"; "Adapter" ] |> ignore
        let config = CliPort.readJson seed "ros.json"
        config["remote"] <- JsonNode.Parse """{"capabilities":["read","mutate","complete","reconcile"]}"""
        CliPort.writeJson seed "ros.json" config
        configure seed "Seed" "seed@example.invalid"
        CliHarness.git seed [ "add"; "-A" ] |> ignore
        CliHarness.git seed [ "commit"; "-qm"; "baseline" ] |> ignore
        CliHarness.git seed [ "remote"; "add"; "origin"; origin ] |> ignore
        CliHarness.git seed [ "push"; "-q"; "origin"; "main" ] |> ignore
        let runner = Path.Combine(baseDirectory, "runner")
        CliHarness.git baseDirectory [ "clone"; "-q"; origin; runner ] |> ignore
        CliHarness.git runner [ "config"; "commit.gpgsign"; "false" ] |> ignore

        { Origin = origin
          Seed = seed
          Runner = runner }

    /// Runs `remote execute` with read and mutate grants and returns the path
    /// of the response document it wrote.
    let private execute (temporary: string -> string) (root: string) (body: string) (environment: (string * string) list) =
        let directory = temporary "praxis-adapter-request"
        let file = Path.Combine(directory, "request.json")
        let response = Path.Combine(directory, "response.json")
        File.WriteAllText(file, body)

        CliHarness.runIn
            (Some root)
            "dotnet"
            [ CliHarness.cli; "--root"; root; "remote"; "execute"; "--request"; file; "--grant"; "read"; "--grant"; "mutate"; "--output"; response ]
            environment
        |> ignore

        File.Delete file
        response

    let private persistWith (root: string) (response: string) (mode: string) (environment: (string * string) list) =
        let output = $"{response}.adapter.json"

        let result =
            CliHarness.runIn (Some root) "sh" [ persist (); "--response"; response; "--output"; output; "--mode"; mode ] environment

        { Exit = result.Exit
          Err = result.Err
          Adapter = if File.Exists output then JsonNode.Parse(File.ReadAllText output) else null }

    let private persistResponse (root: string) (response: string) = persistWith root response "push" []

    let private startRequest (root: string) (requestId: string) =
        CliPort.fill
            [ "requestId", requestId; "sha", CliHarness.git root [ "rev-parse"; "HEAD" ] ]
            """{"protocol":"praxis.remote","protocolVersion":"1.0","requestId":"{{requestId}}","operation":"work.start","repository":{"ref":"refs/heads/main","expectedSha":"{{sha}}"},"actor":{"kind":"agent","id":"example/cloud-agent","provider":"example","runtime":"cloud-agent"},"arguments":{"workItemIds":["WI-0100"]}}"""

    let private text (node: JsonNode) = CliPort.text node

    let private isNullNode (node: JsonNode) = isNull node

    let private exited (expected: int) (persisted: Persisted) =
        if persisted.Exit <> expected then
            failwith $"Expected exit {expected} but received {persisted.Exit}: {persisted.Err}{CliPort.compact persisted.Adapter}"

    /// Runs `body` in a fresh remote and runner checkout; POSIX only.
    let private withCheckout (body: (string -> string) -> Checkout -> unit) =
        if not (OperatingSystem.IsWindows()) then
            CliPort.withTemporaries (fun temporary -> body temporary (remoteAndCheckout temporary))

    // -----------------------------------------------------------------------
    // Static trust-boundary checks on the workflow and actions.

    let private workflow () = read ".github/workflows/praxis-remote.yml"
    let private remoteAction () = read ".github/actions/praxis-remote/action.yml"
    let private setupAction () = read ".github/actions/praxis-setup/action.yml"

    let private jobBlock (name: string) =
        let found = Regex.Match(workflow (), $@"\n  {name}:\n([\s\S]*?)(?=\n  [a-z-]+:\n|\z)")
        Assert.isTrue found.Success $"job {name}"
        found.Groups[1].Value

    /// Every `run:` script body, from both block and inline forms.
    let private runBodies (yaml: string) =
        let lines = yaml.Split '\n'

        let firstNonSpace (line: string) =
            line |> Seq.tryFindIndex (Char.IsWhiteSpace >> not) |> Option.defaultValue -1

        lines
        |> Array.indexed
        |> Array.toList
        |> List.collect (fun (index, line) ->
            let inlineBody = Regex.Match(line, @"^(\s*)(?:- )?run: (?!\|)(.+)$")
            let block = Regex.Match(line, @"^(\s*)(?:- )?run: \|\s*$")

            [ if inlineBody.Success then
                  inlineBody.Groups[2].Value
              if block.Success then
                  let indent = (block.Groups[1]).Value.Length

                  lines[index + 1 ..]
                  |> Array.takeWhile (fun next -> next.Trim() = "" || firstNonSpace next > indent)
                  |> String.concat "\n" ])

    let private permissionsOf (block: string) =
        Regex.Match(block, @"\n    permissions:\n((?:      .+\n)+)").Groups[1].Value

    let tests =
        [ { Name = "praxis remote adapter: persists exactly the reported Praxis state as the executor, naming the asserted requester"
            Run =
              fun () ->
                  withCheckout (fun temporary checkout ->
                      let response =
                          execute
                              temporary
                              checkout.Runner
                              (startRequest checkout.Runner "req-adapter-0001")
                              [ "GITHUB_ACTIONS", "true"
                                "GITHUB_RUN_ID", "77"
                                "GITHUB_RUN_ATTEMPT", "3"
                                "GITHUB_ACTOR", "octocat"
                                "GITHUB_TRIGGERING_ACTOR", "octocat" ]

                      let reported =
                          CliPort.items (CliPort.at [ "persistence"; "paths" ] (JsonNode.Parse(File.ReadAllText response))) |> List.map text |> List.sort

                      let persisted = persistResponse checkout.Runner response
                      exited 0 persisted
                      Assert.equal true (CliPort.boolean (persisted.Adapter["persisted"]))
                      Assert.isTrue (isNullNode (persisted.Adapter["failure"])) "no failure"
                      let commit = text (persisted.Adapter["commit"])
                      Assert.equal commit (CliHarness.git checkout.Origin [ "rev-parse"; "main" ])

                      let committed =
                          (CliHarness.git checkout.Runner [ "show"; "--name-only"; "--format="; commit ]).Split('\n') |> Array.toList |> List.sort

                      Assert.equal reported committed
                      Assert.equal "github-actions[bot]" (CliHarness.git checkout.Runner [ "show"; "-s"; "--format=%an"; commit ])
                      let message = CliHarness.git checkout.Runner [ "show"; "-s"; "--format=%B"; commit ]
                      CliPort.matches "^Praxis-Request-Id: req-adapter-0001$" message
                      CliPort.matches @"^Praxis-Requester: agent:example/cloud-agent \(asserted by the request\)$" message
                      CliPort.matches "^Praxis-Executor: github-actions run 77 attempt 3$" message
                      Assert.equal "" (CliHarness.git checkout.Runner [ "status"; "--porcelain" ])) }

          { Name = "praxis remote adapter: a ref that moved before the push is a concurrency conflict and nothing reaches the remote"
            Run =
              fun () ->
                  withCheckout (fun temporary checkout ->
                      let response = execute temporary checkout.Runner (startRequest checkout.Runner "req-adapter-0001") []
                      CliHarness.write checkout.Seed "notes.md" "someone else\n"
                      CliHarness.git checkout.Seed [ "add"; "-A" ] |> ignore
                      CliHarness.git checkout.Seed [ "commit"; "-qm"; "concurrent change" ] |> ignore
                      CliHarness.git checkout.Seed [ "push"; "-q"; "origin"; "main" ] |> ignore
                      let remoteHead = CliHarness.git checkout.Origin [ "rev-parse"; "main" ]

                      let persisted = persistResponse checkout.Runner response
                      exited 1 persisted
                      Assert.equal false (CliPort.boolean (persisted.Adapter["persisted"]))
                      Assert.equal "concurrency-conflict" (text (persisted.Adapter["failure"]["code"]))
                      Assert.equal "after-refresh" (text (persisted.Adapter["failure"]["retry"]))
                      Assert.equal remoteHead (CliHarness.git checkout.Origin [ "rev-parse"; "main" ])) }

          { Name = "praxis remote adapter: a rejected request persists nothing and makes no commit"
            Run =
              fun () ->
                  withCheckout (fun temporary checkout ->
                      let body = JsonNode.Parse(startRequest checkout.Runner "req-adapter-0001")
                      body["repository"] <- JsonNode.Parse $"""{{"ref":"refs/heads/main","expectedSha":"{String('0', 40)}"}}"""
                      let response = execute temporary checkout.Runner (CliPort.compact body) []
                      Assert.equal "stale-ref" (text (CliPort.at [ "failure"; "code" ] (JsonNode.Parse(File.ReadAllText response))))
                      let before = CliHarness.git checkout.Origin [ "rev-parse"; "main" ]

                      let persisted = persistResponse checkout.Runner response
                      exited 0 persisted
                      Assert.equal false (CliPort.boolean (persisted.Adapter["persisted"]))
                      Assert.isTrue (isNullNode (persisted.Adapter["commit"])) "no commit"
                      Assert.equal before (CliHarness.git checkout.Origin [ "rev-parse"; "main" ])) }

          { Name = "praxis remote adapter: the adapter refuses to persist anything that is not reported Praxis-owned state"
            Run =
              fun () ->
                  withCheckout (fun temporary checkout ->
                      let response = execute temporary checkout.Runner (startRequest checkout.Runner "req-adapter-0001") []
                      let forged = JsonNode.Parse(File.ReadAllText response)
                      let forgedPaths = (forged["persistence"]["paths"]).AsArray()
                      forgedPaths.Add(JsonValue.Create("src/Program.fs") :> JsonNode)
                      let forgedFile = $"{response}.forged.json"
                      File.WriteAllText(forgedFile, CliPort.compact forged)
                      exited 2 (persistResponse checkout.Runner forgedFile)

                      CliHarness.write checkout.Runner "README.extra.md" "not Praxis state\n"
                      CliHarness.git checkout.Runner [ "add"; "README.extra.md" ] |> ignore
                      let staged = persistResponse checkout.Runner response
                      exited 2 staged
                      CliPort.matches @"unreported paths: README\.extra\.md" staged.Err) }

          { Name = "praxis remote adapter: only dispatch and workflow_call can start remote execution; forks and pull requests cannot"
            Run =
              fun () ->
                  let yaml = workflow ()
                  let afterOn = yaml.Split("\non:\n")[1]
                  let triggers = afterOn.Split("\n\n")[0]

                  let events =
                      Regex.Matches(triggers, "^  ([a-z_]+):", RegexOptions.Multiline)
                      |> Seq.map (fun found -> (found.Groups[1]).Value)
                      |> Seq.toList

                  Assert.equal [ "workflow_dispatch"; "workflow_call" ] events
                  doesNotMatch "pull_request_target|pull_request:" yaml }

          { Name = "praxis remote adapter: credentials are least-privilege per job and default to none"
            Run =
              fun () ->
                  CliPort.contains "\npermissions: {}\n" (workflow ())
                  let readJob = jobBlock "read"
                  Assert.equal "      contents: read\n      attestations: read\n" (permissionsOf readJob)
                  let writeJob = jobBlock "write"
                  Assert.equal "      contents: write\n      attestations: read\n" (permissionsOf writeJob)
                  CliPort.contains "pull-requests: write" (jobBlock "write-pull-request")
                  // Writes happen only for requests Praxis classified as mutating.
                  CliPort.contains "needs.read.outputs.mutating == 'true'" writeJob }

          { Name = "praxis remote adapter: request content never reaches a shell by interpolation and no secret is used"
            Run =
              fun () ->
                  for yaml in [ workflow (); remoteAction (); setupAction () ] do
                      for body in runBodies yaml do
                          if body.Contains "${{" then
                              failwith $"run bodies take inputs from the environment only:\n{body}"

                      doesNotMatch @"secrets\." yaml }

          { Name = "praxis remote adapter: every third-party action is pinned to a commit SHA"
            Run =
              fun () ->
                  for yaml in [ workflow (); remoteAction (); setupAction () ] do
                      for found in Regex.Matches(yaml, @"uses:\s*(\S+)") do
                          let reference = (found.Groups[1]).Value

                          if not (reference.StartsWith "./") && not (Regex.IsMatch(reference, "@[0-9a-f]{40}$")) then
                              failwith $"{reference} must be pinned to a commit SHA" }

          { Name = "praxis remote adapter: the adapter holds no Praxis domain logic: it only classifies, executes, and persists"
            Run =
              fun () ->
                  let commands =
                      [ workflow (); remoteAction () ]
                      |> List.collect runBodies
                      |> List.collect (fun body ->
                          Regex.Matches(body, "\"\\$PRAXIS\" ([a-z-]+(?: [a-z-]+)?)")
                          |> Seq.map (fun found -> (found.Groups[1]).Value)
                          |> Seq.toList)

                  Assert.equal [ "remote classify"; "remote execute" ] (commands |> List.distinct |> List.sort)

                  for yaml in [ workflow (); remoteAction () ] do
                      doesNotMatch @"work (start|begin|complete|block|resume|reconcile)|--occurred-at|events\.jsonl|current\.json" yaml }

          { Name = "praxis remote adapter: Praxis, not YAML, classifies which requests need write credentials"
            Run =
              fun () ->
                  withCheckout (fun temporary checkout ->
                      let classify (body: string) =
                          let file = Path.Combine(temporary "praxis-adapter-classify", "request.json")
                          File.WriteAllText(file, body)
                          let result = CliHarness.run "dotnet" [ CliHarness.cli; "remote"; "classify"; "--request"; file ] []
                          result.Exit, JsonNode.Parse result.Out

                      let mutationExit, mutation = classify (startRequest checkout.Runner "req-adapter-0001")
                      Assert.equal 0 mutationExit

                      CliPort.deepEqual
                          """{"requestId":"req-adapter-0001","operation":"work.start","capabilities":["mutate"],"mutating":true}"""
                          mutation

                      let _, readOnly =
                          classify """{"protocol":"praxis.remote","protocolVersion":"1.0","requestId":"req-read-0001","operation":"validate"}"""

                      Assert.equal false (CliPort.boolean (readOnly["mutating"]))
                      let invalidExit, invalid = classify """{"protocol":"praxis.remote","protocolVersion":"9.0"}"""
                      Assert.equal 1 invalidExit
                      Assert.equal "unsupported-protocol" (text (invalid["failure"]["code"]))) }

          { Name = "praxis remote adapter: pull-request persistence proposes the state on its own branch instead of pushing the target"
            Run =
              fun () ->
                  withCheckout (fun temporary checkout ->
                      let response = execute temporary checkout.Runner (startRequest checkout.Runner "req-adapter:pr-0001") []
                      let stub = temporary "praxis-adapter-gh"
                      let gh = Path.Combine(stub, "gh")
                      File.WriteAllText(gh, "#!/bin/sh\necho https://github.example/octo/repo/pull/7\n")
                      CliPort.makeExecutable gh
                      let before = CliHarness.git checkout.Origin [ "rev-parse"; "main" ]
                      let basePath = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""

                      let persisted =
                          persistWith checkout.Runner response "pull-request" [ "PATH", stub + string Path.PathSeparator + basePath ]

                      exited 0 persisted
                      // Pending merge is not persisted to the target ref.
                      Assert.equal false (CliPort.boolean (persisted.Adapter["persisted"]))
                      let digest = (CliPort.sha256Text "req-adapter:pr-0001").Substring(0, 24)
                      let branch = text (persisted.Adapter["branch"])
                      Assert.equal $"praxis/remote/{digest}" branch
                      Assert.equal "https://github.example/octo/repo/pull/7" (text (persisted.Adapter["pullRequest"]))
                      Assert.equal before (CliHarness.git checkout.Origin [ "rev-parse"; "main" ])
                      Assert.equal (text (persisted.Adapter["commit"])) (CliHarness.git checkout.Origin [ "rev-parse"; branch ])) }

          { Name = "praxis remote adapter: the operator documentation covers every failure code and names only files that exist"
            Run =
              fun () ->
                  let operations = read "docs/remote-execution-operations.md"
                  let schema = JsonNode.Parse(read "schemas/praxis-remote-response.schema.json")

                  let codes =
                      CliPort.items (CliPort.at [ "properties"; "code"; "enum" ] (CliPort.items (CliPort.at [ "properties"; "failure"; "oneOf" ] schema)).[1]) |> List.map text

                  for code in codes do
                      Assert.isTrue (operations.Contains $"`{code}`") $"failure code {code} is documented"

                  for file in
                      [ ".github/workflows/praxis-remote.yml"
                        ".github/actions/praxis-remote/"
                        ".github/actions/praxis-setup/"
                        "scripts/praxis-bootstrap.sh"
                        "scripts/praxis-remote-persist.sh" ] do
                      Assert.isTrue (operations.Contains file) $"{file} is named"
                      let path = repositoryFile file
                      Assert.isTrue (File.Exists path || Directory.Exists path) $"{file} exists"

                  for heading in
                      [ "Installation"
                        "Permissions"
                        "Version pinning and upgrades"
                        "Invocation and results"
                        "Concurrency and idempotency"
                        "Security model"
                        "Failure and retry"
                        "Reconciliation"
                        "Troubleshooting" ] do
                      CliPort.matches $"^## {heading}$" operations }

          { Name = "praxis remote adapter: a replayed success persists nothing new and reports the state as already persisted"
            Run =
              fun () ->
                  withCheckout (fun temporary checkout ->
                      let body = startRequest checkout.Runner "req-adapter-replay-1"
                      let first = persistResponse checkout.Runner (execute temporary checkout.Runner body [])
                      Assert.equal true (CliPort.boolean (first.Adapter["persisted"]))
                      let head = CliHarness.git checkout.Origin [ "rev-parse"; "main" ]

                      let retried = execute temporary checkout.Runner body []
                      Assert.equal true (CliPort.boolean (JsonNode.Parse(File.ReadAllText retried)["replayed"]))
                      let second = persistResponse checkout.Runner retried
                      exited 0 second
                      Assert.equal true (CliPort.boolean (second.Adapter["persisted"]))
                      Assert.isTrue (isNullNode (second.Adapter["failure"])) "no failure"
                      Assert.equal head (CliHarness.git checkout.Origin [ "rev-parse"; "main" ])) }

          { Name = "praxis remote adapter: every persistence refusal still writes a machine-readable adapter result"
            Run =
              fun () ->
                  withCheckout (fun temporary checkout ->
                      let response = execute temporary checkout.Runner (startRequest checkout.Runner "req-adapter-0001") []
                      CliHarness.write checkout.Runner "README.extra.md" "not Praxis state\n"
                      CliHarness.git checkout.Runner [ "add"; "README.extra.md" ] |> ignore
                      let staged = persistResponse checkout.Runner response
                      exited 2 staged
                      Assert.equal false (CliPort.boolean (staged.Adapter["persisted"]))
                      Assert.equal "internal" (text (staged.Adapter["failure"]["code"]))
                      Assert.equal "never" (text (staged.Adapter["failure"]["retry"]))) }

          { Name = "praxis remote adapter: the enable script validates its inputs and a dry run walks every phase without changing anything"
            Run =
              fun () ->
                  withCheckout (fun temporary checkout ->
                      let script = repositoryFile "scripts/praxis-remote-enable.sh"
                      let matches (pattern: string) (text: string) =
                          Assert.isTrue (Regex.IsMatch(text, pattern, RegexOptions.Multiline)) $"expected /{pattern}/ in:\n{text}"

                      let bad = CliHarness.run "bash" [ script; "--version"; "3.5" ] []
                      Assert.equal 1 bad.Exit
                      matches @"exact MAJOR\.MINOR\.PATCH" bad.Err

                      let runner = checkout.Runner
                      Directory.CreateDirectory(Path.Combine(runner, ".github", "workflows")) |> ignore
                      File.Copy(repositoryFile ".github/workflows/praxis-remote.yml", Path.Combine(runner, ".github", "workflows", "praxis-remote.yml"))
                      configure runner "Runner" "runner@example.invalid"
                      CliHarness.git runner [ "add"; "-A" ] |> ignore
                      CliHarness.git runner [ "commit"; "-qm"; "adapter" ] |> ignore
                      CliHarness.git runner [ "push"; "-q"; "origin"; "main" ] |> ignore

                      let stub = temporary "gh-enable"
                      let gh = Path.Combine(stub, "gh")

                      File.WriteAllText(
                          gh,
                          String.concat
                              "\n"
                              [ "#!/bin/sh"
                                "case \"$1 $2\" in"
                                "  \"auth status\") exit 0 ;;"
                                "  \"repo view\") echo octo/example ;;"
                                "  \"api user\") echo octocat ;;"
                                "  *) echo \"unexpected gh $*\" >&2; exit 9 ;;"
                                "esac"
                                "" ]
                      )

                      File.SetUnixFileMode(gh, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                      let before = CliHarness.git runner [ "rev-parse"; "HEAD" ]
                      let path = $"{stub}:" + Environment.GetEnvironmentVariable "PATH"

                      let result =
                          CliHarness.runIn
                              (Some runner)
                              "bash"
                              [ script; "--version"; "9.9.9"; "--skip-release"; "--dry-run" ]
                              [ "PATH", path; "PRAXIS", $"dotnet {CliHarness.cli}" ]

                      Assert.isTrue (result.Exit = 0) result.Err
                      matches "actor: human:octocat" result.Out
                      matches @"\[dry-run\] praxis_cli work start --id REMOTE-ENABLE-9-9-9 --type mechanical" result.Out
                      matches @"remote\.capabilities=\[read,mutate\]" result.Out
                      matches @"\[dry-run\] gh workflow run praxis-remote\.yml --repo octo/example --ref main" result.Out
                      Assert.equal before (CliHarness.git runner [ "rev-parse"; "HEAD" ])
                      Assert.equal "" (CliHarness.git runner [ "status"; "--porcelain" ])) } ]
