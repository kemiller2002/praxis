namespace Praxis.Tests

open System
open System.IO
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Work

/// Real Git refs, validator processes, archives and native persistence.
/// Authority, attribution and telemetry remain explicit fixtures.
[<RequireQualifiedAccess>]
module ConcreteAcceptanceFlowTests =
    let private t name run = { Name = "concrete acceptance flow: " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private fixture passing test = FileWorkerChecklistTests.fixture (fun _ candidate evidence original baseline validators _ ->
        let source = baseline.OutputCommit
        for path, content in [ "src/A.fs", "fixture candidate"; "reports/one.json", "fixture evidence one"; "reports/two.json", "fixture evidence two" ] do
            CliHarness.write candidate path content
        CliHarness.git candidate [ "add"; "." ] |> ignore
        CliHarness.git candidate [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid"; "commit"; "-m"; "worker candidate fixture" ] |> ignore
        let output = CliHarness.git candidate [ "rev-parse"; "HEAD" ]
        let packet = { original with SourceCommit = source; AllowedPaths = original.AllowedPaths @ [ "reports/one.json"; "reports/two.json" ] }
        let members = baseline.Members |> List.map (fun memberItem ->
            let paths = if memberItem.WorkItemId = "WI-1" then [ "src/A.fs"; "reports/one.json" ] else [ "reports/two.json" ]
            let artifacts = memberItem.Evidence |> List.map (fun claim ->
                { claim with Digest = GitWorkspace.fileDigest (Path.Combine(candidate, claim.ArtifactPath)) |> ok })
            { memberItem with ChangedPaths = paths; Evidence = artifacts })
        let result = { baseline with OutputCommit = output; PacketDigest = LocalAgentHandoff.packetDigest packet
                                     ChangedPaths = members |> List.collect _.ChangedPaths; Members = members }
        let changed = CliHarness.git candidate [ "diff"; "--name-only"; source; output ]
        let raw =
            { LocalAgentHandoffTests.observed with ReservedPacketDigest = result.PacketDigest; OutputCommit = Some output
                                                   ChangedPaths = Ok(changed.Split('\n') |> Array.toList)
                                                   MemberResults = members |> List.map (fun item -> item.WorkItemId, { ExecutionId = item.ExecutionId; ChangedPaths = Ok item.ChangedPaths }) |> Map.ofList
                                                   Evidence = members |> List.collect _.Evidence |> List.map (fun item -> item.ObligationId, (item.ArtifactPath, item.Digest)) |> Map.ofList
                                                   Acceptance = Map.empty }
        LocalIntegrationTests.fixtureWith packet result raw (fun root _ _ ports _ _ _ _ ->
            let context =
                { WorkItems = packet.Members |> List.map (fun item ->
                      { Id = item.WorkItemId; WorkType = "task"; LocalState = "active"; SemanticState = LiveWorkState.Active
                        Evidence = []; BlockReason = None; UpdatedAt = None; CompletedAt = None; TelemetryExecutionIds = [ item.ExecutionId ] })
                  StartedAt = Some(packet.IssuedAt.ToString("O")); BaselineDirtyPaths = [] }
            let request : WorkContextPlanRequest =
                { Context = context; Action = WorkAction.Complete; WorkItemIds = packet.Members |> List.map _.WorkItemId
                  NewItemType = "task"; TargetLocalState = "complete"; BlockReason = None; DefaultRequiredEvidence = Set.singleton "tests"
                  RequiredEvidenceByType = Map.empty; ProvidedEvidence = []; Repository = packet.RepositoryIdentity; ProtocolVersion = "1.0.0"
                  Actor = "fixture-controller"; OccurredAt = packet.IssuedAt.ToString("O"); MeaningfulChangedPaths = []; ObservedGitPaths = []; TelemetryEnabled = true }
            let observed = { Request = request; ActiveExecutions = packet.Members |> List.map (fun item -> item.WorkItemId, [ item.ExecutionId ]) |> Map.ofList }
            let effects = LocalCompletionCommitTests.provision root observed
            for memberItem in members do
                for claim in memberItem.Evidence do CliHarness.write root claim.ArtifactPath (File.ReadAllText(Path.Combine(candidate, claim.ArtifactPath)))
            let target = "refs/heads/integration"
            CliHarness.git candidate [ "update-ref"; target; source ] |> ignore
            let select packet row candidateCommit =
                validators.Select packet row candidateCommit |> Result.map (fun request ->
                    if not passing && row.Kind = WorkerCheckKind.Tests then
                        { request with Process = { request.Process with Arguments = request.Process.Arguments |> List.map (fun value -> if value = "pass" then "fail" else value) } }
                    else request)
            let selected = { validators with Select = select }
            FileWorkerChecklist.withEvidenceSession root evidence LocalHostFileAccess.portableDevelopment selected (fun () -> DateTimeOffset.UtcNow) CancellationToken.None (fun session ->
                let observe packet result = session.Acceptance packet result |> Result.map (fun acceptance -> { raw with Acceptance = acceptance })
                let intake = { ports.Intake with Observe = observe; Checklists = session.Checks; Now = fun () -> DateTimeOffset.UtcNow }
                let readTarget _ = Ok(CliHarness.git candidate [ "rev-parse"; target ])
                let swap (plan: LocalIntegrationPlan) =
                    CliHarness.git candidate [ "update-ref"; target; plan.CandidateCommit; plan.BeforeCommit ] |> ignore
                    Ok()
                let stage _ _ _ = Ok output
                let validate _ (plan: LocalIntegrationPlan) = if plan.CandidateCommit = output then Ok() else Error "unexpected candidate"
                let integration = { ports with Intake = intake; ReadTarget = readTarget; Stage = stage; ValidateCandidate = validate; CompareAndSwap = swap }
                let candidateEvidence _ _ = Ok(members |> List.map (fun item -> item.WorkItemId, item.Evidence |> List.map (fun claim -> { Type = "tests"; Path = claim.ArtifactPath })) |> Map.ofList)
                let native () = effects.ReadContext() |> Result.map (fun current -> { observed with Request = { request with Context = current } })
                let completion : LocalIntegratedCompletionPorts =
                    { Integration = integration; ObserveNative = native; CandidateEvidence = candidateEvidence
                      Evidence = FileEvidenceRepository.create root; QualityGate = (fun _ _ -> Ok()); CheckpointGuard = fun _ -> Ok() }
                let applied = LocalIntegration.run integration packet.RepositoryIdentity packet.DispatchId target
                let outcome = applied |> Result.bind (fun _ -> FileLocalIntegratedCompletion.commitLockedWith effects completion packet.RepositoryIdentity packet.DispatchId target Actor.unknown Map.empty None)
                test root effects source output (readTarget target |> ok) outcome
                Ok()) |> ok))

    let tests =
        [ t "real validators and Git candidate reach integration and native completion" (fun () -> fixture true (fun root effects _ output target outcome ->
              outcome |> ok |> ignore
              Assert.equal output target
              Assert.equal 2 (File.ReadAllLines(Path.Combine(root, ".ros/events/events.jsonl")).Length)
              for item in (effects.ReadContext() |> ok).WorkItems do Assert.equal LiveWorkState.Complete item.SemanticState))
          t "exit-zero failing TRX refuses Git integration and native completion" (fun () -> fixture false (fun root effects source _ target outcome ->
              Assert.isTrue (Result.isError outcome) "failing tests were accepted"
              Assert.equal source target
              Assert.isTrue (not (File.Exists(Path.Combine(root, ".ros/events/events.jsonl")))) "unchecked work completed"
              for item in (effects.ReadContext() |> ok).WorkItems do Assert.equal LiveWorkState.Active item.SemanticState)) ]
