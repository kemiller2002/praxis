namespace Praxis.Tests

open System
open System.ComponentModel
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Aegis
open Praxis.Cli
open Praxis.Infrastructure.Boundary

/// The Praxis Aegis boundary driven in process with a replaceable collector
/// sink (SAF-AEGIS-1, 5, 6): each test proves what is recorded, with which
/// code, and that nothing secret reaches the sink.
[<RequireQualifiedAccess>]
module AegisBoundaryTests =
    let private collector () =
        let sink = Sinks.Collector()

        match AegisBoundary.configure (Some "test") [ sink.Sink() ] with
        | Ok aegis -> sink, aegis
        | Error problems -> failwith (String.concat "; " problems)

    let private codes (sink: Sinks.Collector) =
        sink.Events |> List.map (fun event -> (JsonNode.Parse event).["code"].GetValue<string>())

    let private failing (ex: exn) = fun () -> raise ex

    let private capture aegis fallback (step: unit -> int) =
        AegisBoundary.capture aegis fallback "work.complete" (Some "/home/someone/private-repo") "could not complete the command" step

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "aegis-boundaries.json")) then directory.FullName
        else
            match directory.Parent with
            | null -> failwith "repository root not found"
            | parent -> repositoryRoot parent

    let tests =
        [ { Name = "aegis boundary: a completed command keeps its exit code and records nothing"
            Run =
              fun () ->
                  let sink, aegis = collector ()

                  for code in [ 0; 1; 2; 3; 4; 5; 6 ] do
                      Assert.equal (Ok code) (capture aegis OperationalBoundary.Unexpected (fun () -> code) |> Result.mapError _.Code.Value)

                  // Refusals are exit codes the command chose (SAF-AEGIS-2), not faults.
                  Assert.empty sink.Events }
          { Name = "aegis boundary: configuration is released, blocking and keeps Aegis's credential rules"
            Run =
              fun () ->
                  let _, aegis = collector ()
                  Assert.equal Blocking aegis.Persistence
                  Assert.equal "Praxis" aegis.Application

                  for rule in Redaction.defaultRules do
                      Assert.isTrue (aegis.Rules |> List.exists (fun r -> r.Name = rule.Name)) $"default rule {rule.Name} kept"

                  let project =
                      File.ReadAllText(Path.Combine(repositoryRoot (DirectoryInfo(AppContext.BaseDirectory)), "src", "Praxis.Infrastructure", "Praxis.Infrastructure.fsproj"))

                  Assert.isTrue
                      (project.Contains("<PackageReference Include=\"EchelonFoundry.Aegis.Core\" Version=\"1.0.0\" />"))
                      "Aegis is pinned to the released 1.0.0" }
          { Name = "aegis boundary: each operational boundary is recorded under its own code"
            Run =
              fun () ->
                  let sink, aegis = collector ()

                  let cases =
                      [ OperationalBoundary.Unexpected, (IOException "disk gone" :> exn), "PRAXIS.FILES.FAILURE"
                        OperationalBoundary.Unexpected, (UnauthorizedAccessException "denied" :> exn), "PRAXIS.FILES.FAILURE"
                        OperationalBoundary.Unexpected, (HttpRequestException "unreachable" :> exn), "PRAXIS.NETWORK.FAILURE"
                        OperationalBoundary.Unexpected, (TimeoutException "slow" :> exn), "PRAXIS.NETWORK.FAILURE"
                        OperationalBoundary.Unexpected, (Win32Exception "no such program" :> exn), "PRAXIS.PROCESS.FAILURE"
                        OperationalBoundary.Unexpected, (JsonException "bad queue.json" :> exn), "PRAXIS.STATE.FAILURE"
                        OperationalBoundary.Unexpected, (Exception "boom"), "PRAXIS.CLI.UNEXPECTED"
                        OperationalBoundary.WebRequest, (Exception "boom"), "PRAXIS.WEB.FAILURE" ]

                  for fallback, ex, expected in cases do
                      match capture aegis fallback (failing ex) with
                      | Ok _ -> failwith "expected a fault"
                      | Error fault -> Assert.equal expected fault.Code.Value

                  Assert.equal (cases |> List.map (fun (_, _, c) -> c)) (codes sink) }
          { Name = "aegis boundary: a failure raised inside the Git adapter is attributed to Git"
            Run =
              fun () ->
                  Assert.equal
                      OperationalBoundary.Git
                      (AegisBoundary.boundaryFrom (Some "Praxis.Infrastructure.Git") OperationalBoundary.Unexpected (IOException "pipe closed"))

                  Assert.equal
                      OperationalBoundary.Network
                      (AegisBoundary.boundaryFrom (Some "Praxis.Infrastructure.Remote") OperationalBoundary.Unexpected (Exception "x"))

                  Assert.equal
                      OperationalBoundary.Filesystem
                      (AegisBoundary.boundaryFrom (Some "Praxis.Infrastructure.Work") OperationalBoundary.Unexpected (IOException "x"))

                  Assert.equal "PRAXIS.GIT.FAILURE" (AegisBoundary.code OperationalBoundary.Git) }
          { Name = "aegis boundary: programming defects and cancellation are re-raised, not disguised"
            Run =
              fun () ->
                  let sink, aegis = collector ()

                  for ex in [ InvalidOperationException "defect" :> exn; OperationCanceledException() :> exn ] do
                      let raised =
                          try
                              capture aegis OperationalBoundary.Unexpected (failing ex) |> ignore
                              false
                          with e when e.GetType() = ex.GetType() ->
                              true

                      Assert.isTrue raised $"{ex.GetType().Name} re-raised"

                  Assert.empty sink.Events }
          { Name = "aegis boundary: credentials, repository data and work content never reach a sink"
            Run =
              fun () ->
                  let sink, aegis = collector ()

                  let secret =
                      "git push https://kevin:ghp_abcdefghijklmnopqrstuvwxyz0123@github.com/org/private.git failed; token=s3cr3t-value; Authorization: Bearer abcdefghijklmnop"

                  match capture aegis OperationalBoundary.Unexpected (failing (IOException secret)) with
                  | Ok _ -> failwith "expected a fault"
                  | Error fault ->
                      let line = AegisBoundary.describe fault

                      for leaked in [ "ghp_"; "s3cr3t"; "abcdefghijklmnop"; "kevin:"; "IOException"; "private-repo" ] do
                          Assert.isTrue (not (line.Contains leaked)) $"operator line leaks {leaked}"

                  let scope =
                      Aegis.scope
                          aegis
                          "Praxis.work.capture"
                          (Map
                              [ "description", Public "confidential roadmap for the acquisition"
                                "remoteUrl", Internal "https://git.example/internal/secret-project.git"
                                "prompt", Public "summarise the customer's medical notes" ])

                  Aegis.capture aegis scope (AegisBoundary.classify OperationalBoundary.Unexpected "could not capture" aegis) (failing (Exception "boom"))
                  |> ignore

                  let recorded = String.concat "\n" sink.Events
                  Assert.equal 2 sink.Events.Length

                  for leaked in
                      [ "ghp_abcdefghijklmnopqrstuvwxyz0123"
                        "s3cr3t-value"
                        "Bearer abcdefghijklmnop"
                        "kevin:"
                        "/home/someone/private-repo"
                        "confidential roadmap"
                        "medical notes"
                        "secret-project" ] do
                      Assert.isTrue (not (recorded.Contains leaked)) $"sink received {leaked}"

                  Assert.isTrue (recorded.Contains Redaction.Placeholder) "redaction is visible as a placeholder"
                  Assert.isTrue (recorded.Contains "github.com") "non-secret context of the failure is kept" }
          { Name = "aegis boundary: a web request that fails answers 500 with a reference and no exception text"
            Run =
              fun () ->
                  let sink, aegis = collector ()

                  let request =
                      { Method = "GET"
                        Segments = [ "work"; "X" ]
                        Query = []
                        ContentType = None
                        Body = [||] }

                  let response = HttpHost.respond aegis (fun _ -> raise (Exception "internal: /home/ops/.ros token=abc")) request
                  let body = Encoding.UTF8.GetString response.Body
                  Assert.equal 500 response.Status
                  Assert.isTrue (body.Contains "AG-") "fault reference shown"
                  Assert.isTrue (not (body.Contains "/home/ops")) "no exception text in the page"
                  Assert.equal [ "PRAXIS.WEB.FAILURE" ] (codes sink)

                  let ok = HttpHost.respond aegis (fun _ -> HttpMessages.text 200 "fine") request
                  Assert.equal 200 ok.Status
                  Assert.equal 1 sink.Events.Length }
          { Name = "aegis boundary: aegis-boundaries.json declares exactly the codes Praxis records"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo(AppContext.BaseDirectory))
                  let manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "aegis-boundaries.json")))

                  let declared =
                      manifest["boundaries"].AsArray()
                      |> Seq.collect (fun b -> b["codes"].AsArray() |> Seq.map (fun c -> c.GetValue<string>()))
                      |> Set.ofSeq

                  Assert.equal (Set.ofList AegisBoundary.codes) declared
                  Assert.equal "Praxis" (manifest["application"].GetValue<string>()) }
          { Name = "aegis boundary: the scope names only public command words"
            Run =
              fun () ->
                  Assert.equal "work.complete" (AegisBoundary.operationOf [ "work"; "complete"; "--id"; "SECRET-1" ])
                  Assert.equal "add" (AegisBoundary.operationOf [ "add"; "Fix the payroll export for ACME" ])
                  Assert.equal "cli" (AegisBoundary.operationOf [ "--root"; "/private" ]) } ]
