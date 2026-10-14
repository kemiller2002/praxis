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
    let private rejects value = Assert.isTrue (Result.isError value) "unsafe concrete checklist passed"
    let internal fixture test = LocalValidatorTests.fixture (fun root repo evidence baseline ->
        let controller = Path.Combine(root, "controller")
        Directory.CreateDirectory controller |> ignore
        let original = { LocalAgentHandoffTests.packet with IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1.); ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20.) }
        let requests (row: WorkerChecklistRequirement) candidate =
            let input = if row.Kind = WorkerCheckKind.Acceptance then row.CheckId else WorkerChecklist.kindCode row.Kind
            let spec, trx =
                if row.Kind = WorkerCheckKind.Tests then
                    let path = Path.Combine(evidence, "host-tests.trx")
                    { LocalWorkerProcessTests.spec "validator-trx" [ path; "pass" ] with WorkingDirectory = repo; Input = input }, Some path
                else { baseline.Process with Input = input }, None
            { baseline with ValidatorId = "host-" + input; CandidateCommit = candidate; Process = spec; TrxPath = trx }
        let rows = WorkerChecklist.requirements original |> ok
        let acceptance = original.Acceptance |> List.map (fun obligation ->
            let row = rows |> List.find (fun row -> row.CheckId = "acceptance/" + obligation.ObligationId)
            let inputs = FileLocalValidator.observe (requests row baseline.CandidateCommit) CancellationToken.None
                         |> fun task -> task.GetAwaiter().GetResult() |> ok
            { obligation with ValidatorId = inputs.ValidatorId; InputDigest = inputs.ValidatorInputDigest })
        let packet = { original with Acceptance = acceptance }
        let result = { LocalAgentHandoffTests.result with OutputCommit = baseline.CandidateCommit; PacketDigest = LocalAgentHandoff.packetDigest packet }
        let revision = ref "host-policy-1"
        let validators : LocalChecklistValidators =
            { PolicyRevision = fun () -> Ok revision.Value
              Select = fun _ row candidate -> Ok(requests row candidate)
              Construction = LocalConstructionScopeHost.none }
        test controller repo evidence packet result validators revision)
    let private session controller evidence access validators action =
        FileWorkerChecklist.withSession controller evidence access validators (fun () -> DateTimeOffset.UtcNow) CancellationToken.None action
    let private verify ports packet result = WorkerChecklistOperations.verify ports packet result

    let tests =
        [ t "real validators populate all member rows and reread evidence without rerunning a stale TRX path" (fun () -> fixture (fun controller _ evidence packet result validators _ ->
              session controller evidence LocalHostFileAccess.portableDevelopment validators (fun ports ->
                  let first = verify ports packet result |> ok
                  Assert.equal 10 first.Rows.Length
                  Assert.isTrue (WorkerChecklist.isReady first) "concrete results did not satisfy the checklist"
                  Assert.equal 6 (Directory.GetFiles(evidence, "*.json").Length)
                  verify ports packet result |> ok |> ignore
                  WorkerChecklistOperations.recheckCandidate ports packet result result.OutputCommit first |> ok
                  Assert.equal 6 (Directory.GetFiles(evidence, "*.json").Length)
                  Ok()) |> ok))
          t "changed evidence refuses even when its cached process report passed" (fun () -> fixture (fun controller _ evidence packet result validators _ ->
              session controller evidence LocalHostFileAccess.portableDevelopment validators (fun ports ->
                  verify ports packet result |> ok |> ignore
                  File.AppendAllText(Directory.GetFiles(evidence, "*.json")[0], "tampered")
                  verify ports packet result |> rejects
                  Ok()) |> ok))
          t "dirty candidate or changed host policy invalidates previous observations" (fun () ->
              for fault in [ "candidate"; "policy" ] do fixture (fun controller repo evidence packet result validators revision ->
                  session controller evidence LocalHostFileAccess.portableDevelopment validators (fun ports ->
                      verify ports packet result |> ok |> ignore
                      if fault = "candidate" then File.AppendAllText(Path.Combine(repo, "source.txt"), "dirty")
                      else revision.Value <- "changed-host-policy"
                      verify ports packet result |> rejects
                      Ok()) |> ok))
          t "a generic exit-zero process cannot satisfy the mandatory test row" (fun () -> fixture (fun controller _ evidence packet result validators _ ->
              let select packet row candidate =
                  validators.Select packet row candidate |> Result.map (fun request ->
                      if row.Kind = WorkerCheckKind.Tests then { request with TrxPath = None } else request)
              let altered = { validators with Select = select }
              session controller evidence LocalHostFileAccess.portableDevelopment altered (fun ports -> verify ports packet result |> Result.map ignore) |> rejects))
          t "acceptance pins must match the protected command inputs before execution" (fun () -> fixture (fun controller _ evidence packet result validators _ ->
              let changed = { packet with Acceptance = packet.Acceptance |> List.map (fun obligation -> { obligation with InputDigest = "sha256:" + String('0', 64) }) }
              session controller evidence LocalHostFileAccess.portableDevelopment validators (fun ports -> verify ports changed result |> Result.map ignore) |> rejects
              Assert.equal 0 (Directory.GetFiles(evidence, "*.json").Length)))
          t "retained ports and overlapping evidence sessions cannot reuse controller authority" (fun () -> fixture (fun controller _ evidence packet result validators _ ->
              let mutable retained = None
              session controller evidence LocalHostFileAccess.portableDevelopment validators (fun ports ->
                  retained <- Some ports
                  session controller evidence LocalHostFileAccess.portableDevelopment validators (fun _ -> Ok()) |> rejects
                  Ok()) |> ok
              verify retained.Value packet result |> rejects))
          t "Unix private evidence is created with the selected host access policy" (fun () ->
              if not (OperatingSystem.IsWindows()) then fixture (fun controller _ evidence packet result validators _ ->
                  File.SetUnixFileMode(evidence, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                  session controller evidence LocalHostFileAccess.unixPrivate validators (fun ports -> verify ports packet result |> Result.map ignore) |> ok
                  for path in Directory.GetFiles(evidence, "*.json") do
                      Assert.equal (UnixFileMode.UserRead ||| UnixFileMode.UserWrite) (File.GetUnixFileMode path))) ]
