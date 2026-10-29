// Real-GitHub integration tests for `praxis autonomy` (WI-0104): the
// negative cases of docs/autonomous-execution/INTEGRATION-TEST-RESULTS.md,
// run against a disposable base branch with the repository's real rules,
// real CI and the real merge API. Nothing here is simulated except the one
// scenario that needs a lost HTTP response: a forwarding proxy performs the
// real merge on GitHub and then withholds the response from Praxis.
//
//   dotnet fsi tests/integration/autonomy/github-negative.fsx
//
// Environment:
//   E2E_REPOSITORY  owner/repo with the policy on E2E_BASE
//   E2E_BASE        the disposable base branch
//   E2E_WORKDIR     a local clone of E2E_REPOSITORY
//   E2E_PRAXIS      the praxis launcher
//   E2E_AUTHORITY   directory outside every repository holding test-authority.key,
//                   trust.json and policy.json (the test authority, never the owner)
//   E2E_RUN_ID      the run id the valid authorization names
//   E2E_OUT         where the results JSON is written

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading

let env name =
    match Environment.GetEnvironmentVariable name with
    | null | "" -> failwith $"{name} is required"
    | value -> value

let repository = env "E2E_REPOSITORY"
let baseBranch = env "E2E_BASE"
let workdir = env "E2E_WORKDIR"
let praxisExe = env "E2E_PRAXIS"
let authority = env "E2E_AUTHORITY"
let runId = env "E2E_RUN_ID"
let outPath = env "E2E_OUT"
let stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")
let scratch = Path.Combine(authority, $"negative-{stamp}")
Directory.CreateDirectory scratch |> ignore
let trust = Path.Combine(authority, "trust.json")
let policy = Path.Combine(authority, "policy.json")

type Result = { Scenario: string; Expected: string; Actual: string; Passed: bool; Evidence: string }
let results = Collections.Generic.List<Result>()

let run (dir: string) (exe: string) (args: string list) (extraEnv: (string * string) list) =
    let info = ProcessStartInfo(exe, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir)
    args |> List.iter info.ArgumentList.Add
    extraEnv |> List.iter (fun (k, v) -> info.Environment[k] <- v)
    use p = Process.Start info
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    p.ExitCode, out.Result, err.Result

let must (code, out: string, err: string) = if code <> 0 then failwith $"command failed ({code}): {err}{out}" else out
let git args = run workdir "git" args [] |> must
let gh args = run workdir "gh" args [] |> must
let praxis args = run workdir praxisExe args []

let record scenario expected actual evidence =
    let passed = (actual: string).StartsWith(expected: string)
    printfn "%s %-48s expected %-34s actual %s" (if passed then "PASS" else "FAIL") scenario expected actual
    results.Add { Scenario = scenario; Expected = expected; Actual = actual; Passed = passed; Evidence = evidence }

/// Authorizations for the scenarios, signed by the test authority.
let mint name (issuedAt: DateTimeOffset) hours (repo: string) (branch: string) =
    let draft = Path.Combine(scratch, $"{name}.draft.json")
    let signed = Path.Combine(scratch, $"{name}.json")
    praxis [ "autonomy"; "authorize"; "draft"; "--policy"; policy; "--run-id"; runId; "--owner"; "e2e-test-authority"
             "--key-id"; "e2e-test-authority-20261028"; "--repository"; repo; "--branch"; branch
             "--operation"; "merge_eligible_pull_request"; "--operation"; "create_pull_request"
             "--hours"; string hours; "--issued-at"; issuedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"); "--out"; draft ] |> must |> ignore
    run scratch "openssl" [ "dgst"; "-sha256"; "-sign"; Path.Combine(authority, "test-authority.key"); "-out"; draft + ".sig"; draft + ".payload" ] [] |> must |> ignore
    praxis [ "autonomy"; "authorize"; "attach"; "--draft"; draft; "--signature"; draft + ".sig"; "--out"; signed ] |> must |> ignore
    signed

let now = DateTimeOffset.UtcNow.AddMinutes -5.
let valid = mint "valid" now 20 repository baseBranch

/// Open a pull request whose single commit applies `change` to the base.
let openPull (name: string) (message: string) (change: unit -> unit) =
    let branch = $"praxis-autonomy-e2e/neg-{stamp}-{name}"
    git [ "fetch"; "origin"; $"+refs/heads/{baseBranch}:refs/remotes/origin/{baseBranch}" ] |> ignore
    git [ "checkout"; "-B"; branch; $"origin/{baseBranch}" ] |> ignore
    change ()
    git [ "add"; "-A" ] |> ignore
    git [ "-c"; "user.name=Praxis negative suite"; "-c"; "user.email=noreply@anthropic.com"; "commit"; "-m"; message ] |> ignore
    git [ "push"; "-u"; "origin"; branch ] |> ignore
    let sha = (git [ "rev-parse"; "HEAD" ]).Trim()
    let number =
        gh [ "api"; $"repos/{repository}/pulls"; "-X"; "POST"; "-f"; $"title=WI-0104 negative test: {name}"; "-f"; $"head={branch}"
             "-f"; $"base={baseBranch}"; "-f"; "body=Praxis WI-0104 negative integration test. Must not merge unless the scenario says so."; "--jq"; ".number" ]
        |> fun s -> int (s.Trim())
    printfn "opened #%d %s at %s" number name sha
    number, sha, branch

let write (relative: string) (text: string) =
    let path = Path.Combine(workdir, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let checksDone (sha: string) =
    let json = JsonNode.Parse(gh [ "api"; $"repos/{repository}/commits/{sha}/check-runs" ])
    let runs = json["check_runs"].AsArray() |> Seq.toList
    runs.Length >= 2 && runs |> List.forall (fun r -> r["status"].ToString() = "completed")

let waitChecks sha =
    let deadline = DateTimeOffset.UtcNow.AddMinutes 30.
    while not (checksDone sha) && DateTimeOffset.UtcNow < deadline do Thread.Sleep 20000

let decision (code, out: string, err: string) =
    try
        let node = JsonNode.Parse out
        let findings = node["findings"].AsArray() |> Seq.map (fun f -> f["code"].ToString()) |> String.concat ","
        $"""{node["result"]} {findings}""", out
    with _ -> $"error exit {code}", err + out

let outcome (code, out: string, err: string) =
    try
        let node = JsonNode.Parse out
        let detail =
            match node["decision"] with
            | null -> (match node["code"] with null -> "" | c -> c.ToString())
            | d -> d["findings"].AsArray() |> Seq.map (fun f -> f["code"].ToString()) |> String.concat ","
        $"""{node["outcome"]} {detail}""", out
    with _ -> $"error exit {code}", err + out

let evaluate pr sha (authorization: string) (extra: string list) =
    praxis ([ "autonomy"; "evaluate"; "--repository"; repository; "--pr"; string pr; "--candidate"; sha
              "--authorization"; authorization; "--trust-store"; trust; "--json" ] @ extra) |> decision

let journal = Path.Combine(scratch, "journal")
let merge pr sha (authorization: string) (extraEnv: (string * string) list) =
    run workdir praxisExe [ "autonomy"; "merge"; "--repository"; repository; "--pr"; string pr; "--candidate"; sha
                            "--authorization"; authorization; "--trust-store"; trust; "--journal"; journal; "--poll-seconds"; "20"; "--json" ] extraEnv
    |> outcome

// ------------------------------------------------------------- scenarios

// A harmless, traced pull request the authorization variants are evaluated against.
let harmless, harmlessSha, harmlessBranch = openPull "harmless" "WI-0104: harmless negative-suite change" (fun () -> write $"readiness/neg-{stamp}.md" "harmless\n")

// Missing review: evaluated before any check exists.
let early, _ = evaluate harmless harmlessSha valid []
record "Missing required review (before checks)" "DENY" early "evaluated immediately after the pull request opened"

let failCi, failCiSha, _ = openPull "failed-ci" "WI-0104: candidate that fails CI" (fun () -> write "e2e/fail-ci" "fail on purpose\n")
let critical, criticalSha, _ = openPull "critical-defect" "WI-0104: candidate with a critical defect" (fun () -> write "notes/defect.txt" ("-----BEGIN " + "PRIVATE KEY-----\nthis-is-not-a-key\n"))
let policyChange, policyChangeSha, _ = openPull "policy-change" "WI-0104: candidate that edits the execution policy" (fun () ->
    let path = Path.Combine(workdir, ".praxis/autonomy/policy.json")
    File.WriteAllText(path, File.ReadAllText(path).Replace("\"maxHours\": 24", "\"maxHours\": 168")))
let workflow, workflowSha, _ = openPull "workflow-weakened" "WI-0104: candidate that weakens CI" (fun () ->
    let path = Path.Combine(workdir, ".github/workflows/praxis-autonomy-e2e.yml")
    File.WriteAllText(path, File.ReadAllText(path).Replace("      - run: bash tests/verify.sh", "      - run: echo skipped")))
let permission, permissionSha, _ = openPull "permission-expansion" "WI-0104: candidate that expands permissions" (fun () -> write "infra/permissions/agent.json" "{ \"contents\": \"write\", \"administration\": \"write\" }\n")
let ciScript, ciScriptSha, _ = openPull "verification-script" "WI-0104: candidate that edits the script CI runs" (fun () -> write "tests/verify.sh" "#!/usr/bin/env bash\necho always passes\n")

[ harmlessSha; failCiSha; criticalSha; policyChangeSha; workflowSha; permissionSha; ciScriptSha ] |> List.iter waitChecks

let missing = Path.Combine(scratch, "does-not-exist.json")
record "No run authorization" "DENY authorization-missing" (evaluate harmless harmlessSha missing [] |> fst) ""
let expired = mint "expired" (DateTimeOffset.UtcNow.AddHours -30.) 2 repository baseBranch
record "Expired authorization" "DENY authorization-expired" (evaluate harmless harmlessSha expired [] |> fst) ""
let revocations = Path.Combine(scratch, "revocations")
praxis [ "autonomy"; "revoke"; "--run-id"; runId; "--reason"; "negative test"; "--out"; Path.Combine(revocations, "r.json") ] |> must |> ignore
record "Revoked authorization" "DENY authorization-revoked" (evaluate harmless harmlessSha valid [ "--revocations"; revocations ] |> fst) ""
let wrongRepo = mint "wrong-repository" now 20 "kemiller2002/indy-init-rehearsal-006" baseBranch
record "Wrong repository" "DENY repository-out-of-scope" (evaluate harmless harmlessSha wrongRepo [] |> fst) ""
let wrongBranch = mint "wrong-branch" now 20 repository "main"
record "Wrong target branch" "DENY target-branch-out-of-scope" (evaluate harmless harmlessSha wrongBranch [] |> fst) ""

// A forged authorization: a key the trust store does not name.
let forgedKey = Path.Combine(scratch, "forged.key")
run scratch "openssl" [ "ecparam"; "-name"; "prime256v1"; "-genkey"; "-noout"; "-out"; forgedKey ] [] |> must |> ignore
let forgedDraft = Path.Combine(scratch, "valid.draft.json")
run scratch "openssl" [ "dgst"; "-sha256"; "-sign"; forgedKey; "-out"; forgedDraft + ".forged.sig"; forgedDraft + ".payload" ] [] |> must |> ignore
let forged = Path.Combine(scratch, "forged.json")
praxis [ "autonomy"; "authorize"; "attach"; "--draft"; forgedDraft; "--signature"; forgedDraft + ".forged.sig"; "--out"; forged ] |> must |> ignore
record "Forged authorization (untrusted key)" "DENY authorization-invalid" (evaluate harmless harmlessSha forged [] |> fst) ""
// A tampered authorization: valid signature, edited scope.
let tampered = Path.Combine(scratch, "tampered.json")
File.WriteAllText(tampered, File.ReadAllText(valid).Replace("\"praxis-autonomy-e2e/base\"", "\"praxis-autonomy-e2e/*\""))
record "Tampered authorization" "DENY authorization-invalid" (evaluate harmless harmlessSha tampered [] |> fst) ""

record "Failed CI" "DENY required-check-failed" (merge failCi failCiSha valid [] |> fst) ""
record "Critical defect open (independent review failed)" "DENY independent-review-failed" (merge critical criticalSha valid [] |> fst) ""
record "Policy file changed" "REQUIRES_OWNER" (merge policyChange policyChangeSha valid [] |> fst) ""
record "Workflow security weakened" "REQUIRES_OWNER" (merge workflow workflowSha valid [] |> fst) ""
record "Permission expansion" "REQUIRES_OWNER" (merge permission permissionSha valid [] |> fst) ""
record "Verification script changed (effect, not location)" "REQUIRES_OWNER" (merge ciScript ciScriptSha valid [] |> fst) ""

// Candidate SHA changed: a new commit lands after verification.
git [ "checkout"; harmlessBranch ] |> ignore
write $"readiness/neg-{stamp}-2.md" "second commit\n"
git [ "add"; "-A" ] |> ignore
git [ "-c"; "user.name=Praxis negative suite"; "-c"; "user.email=noreply@anthropic.com"; "commit"; "-m"; "WI-0104: a commit after verification" ] |> ignore
git [ "push"; "origin"; harmlessBranch ] |> ignore
let newSha = (git [ "rev-parse"; "HEAD" ]).Trim()
record "Candidate SHA changed" "REVERIFY candidate-changed" (merge harmless harmlessSha valid [] |> fst) $"verified {harmlessSha}, head now {newSha}"

// Merge completed remotely but the response was lost: a forwarding proxy
// performs the real merge and answers 502. Praxis must reconcile from GitHub.
waitChecks newSha
let listener = new HttpListener()
let port = 18000 + Random().Next(1000)
listener.Prefixes.Add $"http://127.0.0.1:{port}/"
listener.Start()
let client = new HttpClient()
let forwarding =
    async {
        while listener.IsListening do
            let! context = listener.GetContextAsync() |> Async.AwaitTask
            let request = context.Request
            use upstream = new HttpRequestMessage(HttpMethod(request.HttpMethod), "https://api.github.com" + request.RawUrl)
            [ "Accept"; "X-GitHub-Api-Version"; "User-Agent" ] |> List.iter (fun h -> match request.Headers[h] with null -> () | v -> upstream.Headers.TryAddWithoutValidation(h, v) |> ignore)
            if request.HasEntityBody then
                use reader = new StreamReader(request.InputStream)
                upstream.Content <- new StringContent(reader.ReadToEnd(), Encoding.UTF8, "application/json")
            let! response = client.SendAsync upstream |> Async.AwaitTask
            let! body = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            let lose = request.HttpMethod = "PUT" && request.RawUrl.EndsWith "/merge" && response.IsSuccessStatusCode
            context.Response.StatusCode <- (if lose then 502 else int response.StatusCode)
            let payload = if lose then Encoding.UTF8.GetBytes "{\"message\":\"injected: response lost after the merge\"}" else body
            context.Response.OutputStream.Write(payload, 0, payload.Length)
            context.Response.Close()
    }
Async.Start forwarding
let lost = merge harmless newSha valid [ "PRAXIS_GITHUB_API", $"http://127.0.0.1:{port}" ]
listener.Stop()
record "Merge completed remotely, response lost" "MERGED" (fst lost) (snd lost)
let receiptReconciled =
    Directory.GetFiles(Path.Combine(journal, "receipts"), "*.json")
    |> Array.exists (fun f -> let receipt = JsonNode.Parse(File.ReadAllText f) in receipt["reconciled"].ToString() = "true")
record "Lost response recorded as reconciled" "true" (if receiptReconciled then "true" else "false") ""

// Duplicate merge request after the merge: idempotent.
record "Duplicate merge request" "ALREADY_MERGED" (merge harmless newSha valid [] |> fst) ""

let node = JsonObject()
node["schema"] <- JsonValue.Create "praxis.autonomy-integration-results/1"
node["repository"] <- JsonValue.Create repository
node["baseBranch"] <- JsonValue.Create baseBranch
node["finishedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
let list = JsonArray()
for r in results do
    let item = JsonObject()
    item["scenario"] <- JsonValue.Create r.Scenario
    item["expected"] <- JsonValue.Create r.Expected
    item["actual"] <- JsonValue.Create r.Actual
    item["passed"] <- JsonValue.Create r.Passed
    list.Add item
node["results"] <- list
File.WriteAllText(outPath, node.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
printfn "%d scenario(s); %d passed; %d failed" results.Count (results |> Seq.filter _.Passed |> Seq.length) (results |> Seq.filter (fun r -> not r.Passed) |> Seq.length)
exit (if results |> Seq.forall _.Passed then 0 else 1)
