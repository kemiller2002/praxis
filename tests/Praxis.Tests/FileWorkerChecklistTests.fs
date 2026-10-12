namespace Praxis.Tests

open System
open System.IO
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module FileWorkerChecklistTests =
    let private t name run = { Name = "concrete checklist: " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private refuses (fragment: string) = function
        | Error (error: string) -> Assert.isTrue (error.Contains(fragment, StringComparison.Ordinal)) error
        | Ok _ -> failwith ("expected refusal: " + fragment)
    let private observe request = FileLocalValidator.observe request CancellationToken.None |> fun t -> t.GetAwaiter().GetResult() |> ok
    let private fixture test = LocalValidatorTests.fixture (fun _ repo evidence request ->
        let system = WorkerChecklist.requiredKinds |> List.map (fun kind ->
            let id = WorkerChecklist.kindCode kind
            let path = Path.Combine(evidence, "tests.trx")
            let spec = if kind = WorkerCheckKind.Tests then
                              { LocalWorkerProcessTests.spec "validator-trx" [path; "pass"] with WorkingDirectory = repo }
                          else request.Process
            kind, {request with ValidatorId = "concrete-" + id; Process = spec
                                TrxPath = if kind = WorkerCheckKind.Tests then Some path else None }) |> Map.ofList
        let acceptance = {request with ValidatorId = "acceptance-command"}
        let pin (request: LocalValidatorRequest) : WorkerCheckValidator = {ValidatorId = request.ValidatorId; InputDigest = (observe request).ValidatorInputDigest}
        let acceptancePin = pin acceptance
        let clock = DateTimeOffset.UtcNow
        let packet =
            { LocalAgentHandoffTests.packet with
                IssuedAt = clock.AddSeconds(-1.); ExpiresAt = clock.AddMinutes(15.)
                Acceptance = LocalAgentHandoffTests.packet.Acceptance |> List.map (fun row ->
                    {row with ValidatorId = acceptancePin.ValidatorId; InputDigest = acceptancePin.InputDigest}) }
        let result = {LocalAgentHandoffTests.result with OutputCommit = request.CandidateCommit; PacketDigest = LocalAgentHandoff.packetDigest packet}
        let policy = {Revision = "concrete-policy-1"; Validators = Map.map (fun _ request -> pin request) system}
        let bindings =
            { Packet = packet; Result = result; Policy = fun () -> Ok policy
              Request = fun candidate row -> Ok { (if row.Kind = WorkerCheckKind.Acceptance then acceptance else system[row.Kind]) with CandidateCommit = candidate }
              Guard = fun () -> Ok()
              Cancellation = CancellationToken.None
              Now = fun () -> DateTimeOffset.UtcNow }
        test repo evidence bindings)
    let private verify bindings = WorkerChecklistOperations.verify (FileWorkerChecklist.create bindings) bindings.Packet bindings.Result

    let tests =
        [ t "actual commands and fresh TRX satisfy every generated row" (fun () -> fixture (fun _ evidence bindings ->
              let assessment = verify bindings |> ok
              Assert.isTrue (WorkerChecklist.isReady assessment) "concrete execution failed"
              Assert.equal 10 assessment.Rows.Length
              // Four system validators plus shared acceptance, not worker claims.
              Assert.equal 5 (Directory.GetFiles(evidence, "*.json").Length)
              // Repeated evaluation is not authorized by an earlier in-memory pass.
              verify bindings |> refuses "validator host operation refused"))
          t "guard revocation after an actual command prevents a passing checklist" (fun () -> fixture (fun _ evidence bindings ->
              let guard () =
                  if Directory.GetFiles(evidence, "*.json").Length > 0 then Error "lease-revoked-after-command" else Ok()
              let changed = {bindings with Guard = guard}
              verify changed |> refuses "lease-revoked-after-command"
              Assert.equal 1 (Directory.GetFiles(evidence, "*.json").Length)))
          t "wrong assignment, candidate and validator closure refuse before commands" (fun () -> fixture (fun _ evidence bindings ->
              let ports = FileWorkerChecklist.create bindings
              ports.Observe {bindings.Packet with WorkerId = "foreign-worker"} bindings.Result bindings.Result.OutputCommit |> refuses "unbound-assignment"
              ports.Observe bindings.Packet {bindings.Result with AttemptId = "foreign-attempt"} bindings.Result.OutputCommit |> refuses "unbound-assignment"
              let changed = {bindings with Request = fun candidate row -> bindings.Request candidate row |> Result.map (fun request -> {request with CandidateCommit = String('f', 40)})}
              verify changed |> refuses "request-identity-mismatch"
              let changed = {bindings with Request = fun candidate row -> bindings.Request candidate row |> Result.map (fun request -> {request with Process = {request.Process with Input = "substituted invocation"}})}
              verify changed |> refuses "input-pin-mismatch"
              Assert.equal 0 (Directory.GetFiles(evidence, "*.json").Length)))
          t "test rows require a host-selected artifact before execution" (fun () -> fixture (fun _ evidence bindings ->
              let changed = {bindings with Request = fun candidate row -> bindings.Request candidate row |> Result.map (fun request -> {request with TrxPath = None})}
              verify changed |> refuses "test-artifact-required"
              Assert.equal 0 (Directory.GetFiles(evidence, "*.json").Length)))
          t "revoked guards, cancellation and policy drift cannot become passes" (fun () -> fixture (fun _ _ bindings ->
              verify {bindings with Guard = fun () -> Error "lease-revoked"} |> refuses "lease-revoked"
              use cancelled = new CancellationTokenSource()
              cancelled.Cancel()
              verify {bindings with Cancellation = cancelled.Token} |> refuses "refused"
              let mutable calls = 0
              let changed = {bindings with Policy = fun () -> calls <- calls + 1; bindings.Policy() |> Result.map (fun policy -> {policy with Revision = string calls})}
              verify changed |> refuses "inputs-changed"))
          t "failed test artifacts block readiness despite command exit zero" (fun () -> fixture (fun _ _ bindings ->
              let requests = System.Collections.Generic.Dictionary<WorkerCheckKind, LocalValidatorRequest>()
              for kind in WorkerChecklist.requiredKinds do
                  let row: WorkerChecklistRow = {CheckId = "fixture"; Kind = kind; WorkItemId = "WI-1"; ExecutionId = "EXE-WI1"; RequirementKeys = []; Validator = (bindings.Policy() |> ok).Validators[kind]}
                  let request = bindings.Request bindings.Result.OutputCommit row |> ok
                  requests.Add(kind, if kind = WorkerCheckKind.Tests then {request with Process = {request.Process with Arguments = request.Process.Arguments |> List.map (fun arg -> if arg = "pass" then "fail" else arg)}} else request)
              let policy = bindings.Policy() |> ok
              let policy = {policy with Validators = policy.Validators |> Map.map (fun kind validator -> {validator with InputDigest = (observe requests[kind]).ValidatorInputDigest})}
              let changed = {bindings with Policy = fun () -> Ok policy
                                           Request = fun candidate row -> if row.Kind = WorkerCheckKind.Acceptance then bindings.Request candidate row else Ok requests[row.Kind]}
              verify changed |> refuses "check-failed"))
          t "archived evidence tampering, foreign custody and forged reports refuse" (fun () -> LocalValidatorTests.fixture (fun _ _ evidence request ->
              let report = FileLocalValidator.execute request CancellationToken.None |> fun t -> t.GetAwaiter().GetResult() |> ok
              FileLocalValidator.verifyEvidence request report |> ok
              FileLocalValidator.verifyEvidence request {report with CandidateCommit = String('f', 40)} |> refuses "evidence refused"
              let foreign = Path.Combine(evidence, "not-a-generated-id.json")
              File.Copy(report.EvidencePath, foreign)
              FileLocalValidator.verifyEvidence request {report with EvidencePath = foreign} |> refuses "evidence refused"
              File.AppendAllText(report.EvidencePath, " ")
              FileLocalValidator.verifyEvidence request report |> refuses "evidence refused")) ]
