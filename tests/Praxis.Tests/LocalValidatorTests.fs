namespace Praxis.Tests

open System
open System.IO
open System.Threading
open Praxis.Application.Work
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalValidatorTests =
    let private t name run = { Name = "local validator: " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private refused result = Assert.isTrue (Result.isError result) "unsafe validator request passed"
    let private execute request = FileLocalValidator.execute request CancellationToken.None |> fun value -> value.GetAwaiter().GetResult()
    let private gitPath () =
        let name = if OperatingSystem.IsWindows() then "git.exe" else "git"
        let selected = Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator)
                       |> Array.map (fun path -> Path.Combine(path, name)) |> Array.find File.Exists |> Path.GetFullPath
        let file = FileInfo selected
        if file.LinkTarget <> null then file.ResolveLinkTarget(true).FullName else selected
    let internal fixture test =
        let root = CliHarness.temporaryDirectory "praxis-local-validator"
        let repo, evidence = Path.Combine(root, "repo"), Path.Combine(root, "evidence")
        Directory.CreateDirectory repo |> ignore
        Directory.CreateDirectory evidence |> ignore
        try
            CliHarness.git repo [ "init" ] |> ignore
            CliHarness.write repo "source.txt" "candidate source"
            CliHarness.git repo [ "add"; "source.txt" ] |> ignore
            CliHarness.git repo [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid"; "commit"; "-m"; "candidate" ] |> ignore
            let candidate = CliHarness.git repo [ "rev-parse"; "HEAD" ]
            let git = gitPath()
            let spec = { LocalWorkerProcessTests.spec "echo" [] with WorkingDirectory = repo }
            let request =
                { ValidatorId = "fixture-validator"; CandidateCommit = candidate
                  GitExecutable = git; GitExecutableDigest = GitWorkspace.fileDigest git |> ok
                  Dependencies = Map.ofList [ Path.Combine(AppContext.BaseDirectory, "Praxis.Tests.dll"), GitWorkspace.fileDigest (Path.Combine(AppContext.BaseDirectory, "Praxis.Tests.dll")) |> ok ]
                  Process = spec; TrxPath = None; EvidenceRoot = evidence }
            test root repo evidence request
        finally CliHarness.removeTemporaryGitDirectory root

    let tests =
        [ t "real process produces candidate-bound evidence without native acceptance" (fun () -> fixture (fun _ _ _ request ->
              let observed = FileLocalValidator.observe request CancellationToken.None |> fun value -> value.GetAwaiter().GetResult() |> ok
              let report = execute request |> ok
              report.Verdict |> ok
              Assert.equal request.CandidateCommit report.CandidateCommit
              Assert.equal None report.TestsExecuted
              Assert.equal observed.CandidateManifestDigest report.CandidateManifestDigest
              Assert.equal observed.ValidatorInputDigest report.ValidatorInputDigest
              Assert.equal (GitWorkspace.fileDigest report.EvidencePath |> ok) report.EvidenceDigest
              Assert.isTrue (report.FinishedAt >= report.StartedAt) "validator time moved backwards"))
          t "fresh TRX counts actually represented passing tests, not exit code alone" (fun () ->
              for outcome in [ "pass"; "fail" ] do fixture (fun _ repo evidence request ->
                  let path = Path.Combine(evidence, "results.trx")
                  let spec = { LocalWorkerProcessTests.spec "validator-trx" [ path; outcome ] with WorkingDirectory = repo }
                  let report = execute { request with Process = spec; TrxPath = Some path } |> ok
                  if outcome = "pass" then report.Verdict |> ok; Assert.equal (Some 1) report.TestsExecuted
                  else report.Verdict |> refused; Assert.equal None report.TestsExecuted))
          t "worker prose and preexisting results cannot satisfy a test run" (fun () ->
              for stale in [ false; true ] do fixture (fun _ _ evidence request ->
                  let path = Path.Combine(evidence, "results.trx")
                  if stale then File.WriteAllText(path, "stale successful result")
                  let result = execute { request with TrxPath = Some path }
                  if stale then result |> refused else (result |> ok).Verdict |> refused))
          t "wrong candidate, dirty inputs and replaced tool pins refuse before execution" (fun () ->
              for fault in [ "candidate"; "dirty"; "tool"; "dependency" ] do fixture (fun _ repo _ request ->
                  let changed =
                      match fault with
                      | "candidate" -> { request with CandidateCommit = String('a', 40) }
                      | "dirty" -> CliHarness.write repo "source.txt" "uncommitted"; request
                      | "dependency" -> { request with Dependencies = request.Dependencies |> Map.map (fun _ _ -> "sha256:" + String('a', 64)) }
                      | _ -> { request with Process = { request.Process with ExecutableDigest = "sha256:" + String('a', 64) } }
                  execute changed |> refused))
          t "candidate mutation during a successful process invalidates the observation" (fun () -> fixture (fun _ repo _ request ->
              let spec = { LocalWorkerProcessTests.spec "validator-edit" [ Path.Combine(repo, "source.txt") ] with WorkingDirectory = repo }
              (execute { request with Process = spec } |> ok).Verdict |> refused))
          t "changed pinned dependency cannot be mistaken for a passing command" (fun () -> fixture (fun root repo _ request ->
              let dependency = Path.Combine(root, "validator-config.txt")
              File.WriteAllText(dependency, "approved validator inputs")
              let spec = { LocalWorkerProcessTests.spec "validator-edit" [ dependency ] with WorkingDirectory = repo }
              let changed = { request with Process = spec; Dependencies = request.Dependencies |> Map.add dependency (GitWorkspace.fileDigest dependency |> ok) }
              execute changed |> refused))
          t "committed symlink inputs and duplicate test results fail closed" (fun () -> fixture (fun _ repo evidence request ->
              // Git index-only fixture is portable, including Windows without symlink privileges.
              let blob = CliHarness.git repo [ "hash-object"; "source.txt" ]
              CliHarness.git repo [ "update-index"; "--add"; "--cacheinfo"; "120000," + blob + ",linked-input" ] |> ignore
              CliHarness.git repo [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid"; "commit"; "-m"; "linked candidate" ] |> ignore
              CliHarness.git repo [ "update-index"; "--skip-worktree"; "--"; "linked-input" ] |> ignore
              match execute { request with CandidateCommit = CliHarness.git repo [ "rev-parse"; "HEAD" ] } with
              | Error error -> Assert.isTrue (error.Contains "unsupported linked") "candidate tree refusal was not exercised"
              | Ok _ -> failwith "linked candidate input accepted"
              let path = Path.Combine(evidence, "duplicate.trx")
              File.WriteAllText(path, "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><Results><UnitTestResult executionId='same' outcome='Passed'/><UnitTestResult executionId='same' outcome='Passed'/></Results><ResultSummary outcome='Completed'><Counters total='2' executed='2' passed='2' failed='0' error='0' notExecuted='0'/></ResultSummary></TestRun>")
              FileLocalValidator.readTrx path |> refused))
          t "nonzero exit, output overflow, timeout and cancellation do not pass" (fun () ->
              for mode in [ "nonzero"; "flood"; "sleep"; "cancel" ] do fixture (fun _ repo _ request ->
                  let spec = { LocalWorkerProcessTests.spec (if mode = "cancel" then "echo" else mode) [] with WorkingDirectory = repo; TimeoutMilliseconds = 1000 }
                  use cancellation = new CancellationTokenSource()
                  if mode = "cancel" then cancellation.Cancel()
                  let result = FileLocalValidator.execute { request with Process = spec } cancellation.Token |> fun value -> value.GetAwaiter().GetResult()
                  if mode = "cancel" then result |> refused
                  else
                      let report = result |> ok
                      report.Verdict |> refused
                      Assert.isTrue (File.Exists report.EvidencePath) "failed process evidence was not retained"))
          t "evidence and test artifacts cannot be placed in the candidate checkout" (fun () -> fixture (fun _ repo _ request ->
              execute { request with EvidenceRoot = repo } |> refused
              execute { request with TrxPath = Some(Path.Combine(repo, "results.trx")) } |> refused))
          t "TRX refuses zero tests, DTDs and summary-only forged counts" (fun () -> fixture (fun _ _ evidence _ ->
              let path = Path.Combine(evidence, "invalid.trx")
              for xml in [ "<!DOCTYPE TestRun [<!ENTITY x 'payload'>]><TestRun>&x;</TestRun>"
                           "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><ResultSummary outcome='Completed'><Counters total='0' executed='0' passed='0' failed='0' error='0' notExecuted='0'/></ResultSummary></TestRun>"
                           "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><ResultSummary outcome='Completed'><Counters total='1' executed='1' passed='1' failed='0' error='0' notExecuted='0'/></ResultSummary></TestRun>" ] do
                  File.WriteAllText(path, xml)
                  FileLocalValidator.readTrx path |> refused)) ]
