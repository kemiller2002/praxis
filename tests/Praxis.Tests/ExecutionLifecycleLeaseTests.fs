namespace Praxis.Tests

open System.IO
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Execution

/// WI-0087: execution-envelope writers share the native lock order, so a
/// host scope holding work-protocol/work-groups observes stable envelopes.
[<RequireQualifiedAccess>]
module ExecutionLifecycleLeaseTests =
    open PraxisCli

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private t name run = { Name = "execution lifecycle lease: " + name; Run = run }

    let private withExecution test =
        let parent = GitFixture.temporaryDirectory "execution-lease"
        try
            let _, clone = installedRepository parent "clone-a"
            let started = run clone (Some agentA) [ "execution"; "start"; "--work-item"; "FEAT-1"; "--role"; "review"; "--json" ] |> ok
            test clone (text started.Json["executionId"])
        finally
            GitFixture.cleanup parent

    let private envelope clone id = File.ReadAllText(Path.Combine(clone, ".ros", "executions", id, "envelope.json"))

    let private holding root resource action =
        match RegistryLock.acquire root resource RegistryLock.defaultSettings with
        | Error failure -> failwith failure.Message
        | Ok lease ->
            try action ()
            finally lease.Release() |> ignore

    let tests =
        [ t "execution transitions refuse without writing while a host holds either native lease" (fun () ->
              for resource in ExecutionLifecycleLease.resources do
                  withExecution (fun clone id ->
                      let before = envelope clone id
                      let ledger = File.ReadAllText(Path.Combine(clone, ".ros", "executions", id, "events.jsonl"))
                      let refused = holding clone resource (fun () ->
                          run clone (Some agentA) [ "execution"; "transition"; id; "--action"; "block"; "--reason"; "host dispatch active" ])
                      Assert.equal 3 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "execution lifecycle lease unavailable") refused.Error
                      Assert.equal before (envelope clone id)
                      Assert.equal ledger (File.ReadAllText(Path.Combine(clone, ".ros", "executions", id, "events.jsonl")))
                      run clone (Some agentA) [ "execution"; "transition"; id; "--action"; "block"; "--reason"; "after host release" ] |> ok |> ignore
                      Assert.isTrue ((envelope clone id).Contains "blocked") "transition after release did not apply"))
          t "serialized ports refuse each write while the lease is held and never nest it" (fun () ->
              withExecution (fun clone id ->
                  let ports = FileExecutionPorts.createSerialized clone
                  let current = ports.Store.Load id |> function Ok value -> value | Error error -> failwith error
                  let before = envelope clone id
                  holding clone "work-protocol" (fun () ->
                      try
                          ports.Store.Save current
                          failwith "envelope write bypassed the lifecycle lease"
                      with ExecutionLifecycleLeaseUnavailable _ -> ())
                  Assert.equal before (envelope clone id)
                  // Locked ports are for callers already inside the lease scope.
                  ExecutionLifecycleLease.within clone (fun () -> (FileExecutionPorts.createLocked clone).Store.Save current)
                  |> function Ok() -> () | Error error -> failwith error
                  for resource in ExecutionLifecycleLease.resources do
                      Assert.isTrue (not (File.Exists(RegistryLock.lockPath clone resource))) "lifecycle lease leaked"))
          t "lease acquisition failure inside the scope releases the outer lease" (fun () ->
              withExecution (fun clone _ ->
                  holding clone "work-groups" (fun () ->
                      let result = ExecutionLifecycleLease.within clone (fun () -> failwith "action must not run")
                      Assert.isTrue (Result.isError result) "inner lease contention was ignored"
                      Assert.isTrue (not (File.Exists(RegistryLock.lockPath clone "work-protocol"))) "outer lease leaked")))
          t "work transitions still mirror envelopes through the serialized path" (fun () ->
              let parent = GitFixture.temporaryDirectory "execution-lease-mirror"
              try
                  let _, clone = installedRepository parent "clone-a"
                  run clone (Some agentA) [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                  run clone (Some agentA) [ "work"; "block"; "--id"; "FEAT-1"; "--reason"; "waiting"; "--occurred-at"; now () ] |> ok |> ignore
                  let directory = Directory.GetDirectories(Path.Combine(clone, ".ros", "executions")) |> Array.exactlyOne
                  Assert.isTrue (File.ReadAllText(Path.Combine(directory, "envelope.json")).Contains "blocked") "block was not mirrored"
              finally
                  GitFixture.cleanup parent) ]
