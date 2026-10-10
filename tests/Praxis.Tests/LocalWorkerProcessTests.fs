namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open Praxis.Application.Work
open Praxis.Infrastructure.Work

/// Deterministic subprocess fixtures, not agents or a local model pilot.
[<RequireQualifiedAccess>]
module LocalWorkerFixture =
    let run mode arguments =
        match mode with
        | "echo" ->
            let input = Console.In.ReadToEnd()
            Console.Write(JsonSerializer.Serialize {| arguments = arguments; input = input; ambient = Environment.GetEnvironmentVariable("PRAXIS_SUPERVISOR_FIXTURE_SECRET") |})
            Console.Error.Write "fixture-stderr"
            0
        | "sleep" -> Console.In.ReadToEnd() |> ignore; Thread.Sleep 10000; 0
        | "flood" -> Console.In.ReadToEnd() |> ignore; Console.Out.Write(String('x', 262144)); Console.Error.Write(String('y', 262144)); 0
        | "bad-utf8" ->
            Console.In.ReadToEnd() |> ignore
            Console.OpenStandardOutput().WriteByte 0xffuy
            0
        | "nonzero" -> Console.In.ReadToEnd() |> ignore; 17
        | _ -> 2

[<RequireQualifiedAccess>]
module LocalWorkerProcessTests =
    let private t name run = { Name = "local worker process: " + name; Run = run }
    let internal spec mode arguments =
        let executable = Environment.ProcessPath
        use binary = File.OpenRead executable
        let prefix = if Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) then [ Path.Combine(AppContext.BaseDirectory, "Praxis.Tests.dll") ] else []
        { Executable = executable; ExecutableDigest = "sha256:" + (SHA256.HashData binary |> Convert.ToHexString).ToLowerInvariant()
          Arguments = prefix @ [ "--local-worker-fixture"; mode ] @ arguments
          WorkingDirectory = Path.GetFullPath(Path.GetTempPath()); Environment = Map.empty; Input = "typed packet fixture"
          TimeoutMilliseconds = 3000; MaxOutputBytes = 65536 }
    // Synthetic fixture identity deliberately does not qualify OS incarnation observation.
    let private run candidate callback cancellation =
        LocalWorkerProcess.runWithIdentityObservation (fun child -> Ok(sprintf "fixture-pid:%d/nonce:%s" child.Id (Guid.NewGuid().ToString("N")))) candidate callback cancellation
        |> fun task -> task.GetAwaiter().GetResult()
    let tests =
        [ t "explicit argv and stdin remain literal and ambient environment is excluded" (fun () ->
              let sentinel = "PRAXIS_SUPERVISOR_FIXTURE_SECRET"
              let previous = Environment.GetEnvironmentVariable sentinel
              Environment.SetEnvironmentVariable(sentinel, "host-only-fixture-value")
              try
                  let root = CliHarness.temporaryDirectory "praxis-supervisor-literal"
                  try
                      let target = Path.Combine(root, "must-not-exist")
                      let argument = ";$(touch " + target + ")"
                      let mutable observed = None
                      let result = run (spec "echo" [ argument ]) (fun identity -> observed <- Some identity; Ok()) CancellationToken.None
                      Assert.isTrue (result.Outcome = LocalWorkerProcessOutcome.Exited 0) (sprintf "spec=%A observation=%A" (spec "echo" [ argument ]) result)
                      Assert.equal observed result.ProcessIdentity
                      Assert.equal (Some true) result.ProcessStarted
                      Assert.isTrue result.RootExitObserved "root exit unobserved"
                      use json = JsonDocument.Parse result.StandardOutput
                      Assert.equal argument (json.RootElement.GetProperty("arguments").EnumerateArray() |> Seq.head |> _.GetString())
                      Assert.equal "typed packet fixture" (json.RootElement.GetProperty("input").GetString())
                      Assert.equal JsonValueKind.Null (json.RootElement.GetProperty("ambient").ValueKind)
                      Assert.equal "fixture-stderr" result.StandardError
                      Assert.isTrue (not (File.Exists target)) "argument executed as a command"
                  finally Directory.Delete(root, true)
              finally Environment.SetEnvironmentVariable(sentinel, previous))
          t "unavailable or invalid process incarnation fails closed" (fun () ->
              for observer in [ (fun _ -> Error "OS incarnation unavailable"); (fun _ -> Ok ""); (fun _ -> Ok "invalid\nidentity") ] do
                  let result = LocalWorkerProcess.runWithIdentityObservation observer (spec "sleep" []) (fun _ -> failwith "accepted unavailable incarnation") CancellationToken.None |> fun task -> task.GetAwaiter().GetResult()
                  Assert.equal (Some true) result.ProcessStarted
                  Assert.isTrue result.RootExitObserved "refused observation left root running"
                  match result.Outcome with LocalWorkerProcessOutcome.ObservationFailed _ -> () | outcome -> failwith (sprintf "%A" outcome))
          t "cancelled before start does not launch" (fun () ->
              use cancellation = new CancellationTokenSource()
              cancellation.Cancel()
              let result = run (spec "sleep" []) (fun _ -> failwith "started after cancellation") cancellation.Token
              Assert.equal (Some false) result.ProcessStarted
              Assert.equal LocalWorkerProcessOutcome.Cancelled result.Outcome)
          t "wrong executable pin and invalid budgets refuse before creating a process" (fun () ->
              let valid = spec "echo" []
              for candidate in [ { valid with ExecutableDigest = "sha256:" + String('0', 64) }; { valid with TimeoutMilliseconds = 0 }
                                 { valid with MaxOutputBytes = -1 }; { valid with Input = String('x', 65537) }
                                 { valid with Executable = "relative-executable" } ] do
                  let result = run candidate (fun _ -> failwith "started after failed preflight") CancellationToken.None
                  Assert.equal (Some false) result.ProcessStarted
                  match result.Outcome with LocalWorkerProcessOutcome.PreflightRefused _ -> () | outcome -> failwith (sprintf "%A" outcome))
          t "timeout and external cancellation observe root exit without implying completion" (fun () ->
              let timed = run { spec "sleep" [] with TimeoutMilliseconds = 250 } (fun _ -> Ok()) CancellationToken.None
              Assert.equal LocalWorkerProcessOutcome.TimedOut timed.Outcome
              Assert.isTrue timed.RootExitObserved "timed-out root exit unconfirmed"
              use cancelled = new CancellationTokenSource()
              let stopped = run (spec "sleep" []) (fun _ -> cancelled.CancelAfter 250; Ok()) cancelled.Token
              Assert.equal LocalWorkerProcessOutcome.Cancelled stopped.Outcome
              Assert.isTrue stopped.RootExitObserved "cancelled root exit unconfirmed")
          t "combined stdout and stderr are capped in bytes" (fun () ->
              let result = run { spec "flood" [] with MaxOutputBytes = 1024 } (fun _ -> Ok()) CancellationToken.None
              Assert.equal LocalWorkerProcessOutcome.OutputLimitExceeded result.Outcome
              Assert.isTrue (result.CapturedBytes <= 1024) "output exceeded combined budget"
              Assert.isTrue result.RootExitObserved "output-limited root exit unconfirmed")
          t "failed start observation stops the root and retains refusal" (fun () ->
              let result = run (spec "sleep" []) (fun _ -> Error "journal append failed") CancellationToken.None
              Assert.isTrue result.RootExitObserved "root left running after observation failure"
              match result.Outcome with LocalWorkerProcessOutcome.ObservationFailed _ -> () | outcome -> failwith (sprintf "%A" outcome))
          t "nonzero exit and invalid UTF-8 remain observed failure evidence" (fun () ->
              Assert.equal (LocalWorkerProcessOutcome.Exited 17) (run (spec "nonzero" []) (fun _ -> Ok()) CancellationToken.None).Outcome
              let invalid = run (spec "bad-utf8" []) (fun _ -> Ok()) CancellationToken.None
              Assert.isTrue invalid.RootExitObserved "invalid-output root exit unobserved"
              match invalid.Outcome with LocalWorkerProcessOutcome.StreamFailed _ -> () | outcome -> failwith (sprintf "%A" outcome)) ]
