namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Work

/// Real repositories and pinned Git. Worker commits are produced by test Git
/// commands, not by a model; these tests qualify the controller adapters only.
[<RequireQualifiedAccess>]
module FileLocalGitCandidatesTests =
    let private t name run = { Name = "local git candidates: " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private refused result = Assert.isTrue (Result.isError result) "unsafe Git candidate accepted"
    let private none = CancellationToken.None
    let private committer = { Name = "Praxis controller"; Email = "controller@example.invalid" }

    let internal gitTool () =
        let name = if OperatingSystem.IsWindows() then "git.exe" else "git"
        let selected = Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator)
                       |> Array.map (fun path -> Path.Combine(path, name)) |> Array.find File.Exists |> Path.GetFullPath
        let file = FileInfo selected
        let path = if file.LinkTarget <> null then file.ResolveLinkTarget(true).FullName else selected
        { Executable = path; ExecutableDigest = GitWorkspace.fileDigest path |> ok; TimeoutMilliseconds = 30000 }

    let private commitAll repo message =
        CliHarness.git repo [ "add"; "-A" ] |> ignore
        CliHarness.git repo [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid"; "commit"; "-q"; "-m"; message ] |> ignore
        CliHarness.git repo [ "rev-parse"; "HEAD" ]

    let private sha (text: string) = "sha256:" + (SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString).ToLowerInvariant()

    let private fixture test =
        let root = CliHarness.temporaryDirectory "praxis-git-candidates"
        let repo, checkouts = Path.Combine(root, "repo"), Path.Combine(root, "checkouts")
        Directory.CreateDirectory repo |> ignore
        Directory.CreateDirectory checkouts |> ignore
        try
            CliHarness.git repo [ "init"; "-q" ] |> ignore
            CliHarness.write repo "src/A.fs" "module A"
            CliHarness.write repo "src/B.fs" "module B"
            let source = commitAll repo "source"
            CliHarness.git repo [ "update-ref"; "refs/heads/integration"; source ] |> ignore
            let git = gitTool ()
            let worktree = FileLocalGitCandidates.provisionCheckout git repo checkouts "worker" source none |> ok
            CliHarness.write worktree "src/A.fs" "module A // changed by worker"
            CliHarness.write worktree "reports/one.md" "acceptance evidence"
            let output = commitAll worktree "worker output"
            let packet =
                { LocalAgentHandoffTests.packet with
                    SourceCommit = source; AllowedPaths = [ "src/A.fs"; "reports/one.md" ]
                    Members = [ LocalAgentHandoffTests.packet.Members.Head ]
                    Acceptance = [ LocalAgentHandoffTests.packet.Acceptance.Head ] }
            let result =
                { LocalAgentHandoffTests.result with
                    OutputCommit = output; PacketDigest = LocalAgentHandoff.packetDigest packet; ChangedPaths = [ "reports/one.md"; "src/A.fs" ]
                    Members = [ { LocalAgentHandoffTests.result.Members.Head with
                                    ChangedPaths = [ "reports/one.md"; "src/A.fs" ]
                                    Evidence = [ { ObligationId = "VERIFY-1"; ArtifactPath = "reports/one.md"; Digest = sha "acceptance evidence" } ] } ] }
            let submission : LocalWorkerSubmission =
                { SchemaVersion = LocalWorkerSubmission.Schema; RepositoryIdentity = packet.RepositoryIdentity; DispatchId = packet.DispatchId
                  AttemptId = packet.AttemptId; PacketDigest = result.PacketDigest; ProcessIdentity = "fixture"; CapturedAt = DateTimeOffset.UtcNow
                  ExitCode = 0; Payload = "{}"; PayloadDigest = sha "{}" }
            let snapshot = { Packet = packet; Submission = submission; Result = result; Disposition = LocalResultDisposition.AwaitingIntegration }
            test git repo checkouts source output snapshot
        finally CliHarness.removeTemporaryGitDirectory root

    let private plan (snapshot: LocalSubmissionInspection) before candidate : LocalIntegrationPlan =
        { SchemaVersion = "praxis.local-integration-plan/1"; RepositoryIdentity = snapshot.Packet.RepositoryIdentity
          DispatchId = snapshot.Packet.DispatchId; AttemptId = snapshot.Packet.AttemptId; PacketDigest = snapshot.Result.PacketDigest
          SubmissionDigest = snapshot.Submission.PayloadDigest; TargetRef = "refs/heads/integration"
          BeforeCommit = before; CandidateCommit = candidate; PreparedAt = DateTimeOffset.UtcNow }

    let tests =
        [ t "worker result is observed from actual objects and claims cannot replace it" (fun () -> fixture (fun git repo _ source output snapshot ->
              let observed = FileLocalGitCandidates.observeResult git repo snapshot.Packet snapshot.Result none |> ok
              Assert.equal (Some output) observed.OutputCommit
              Assert.isTrue observed.SourceIsAncestor "source ancestry not observed"
              Assert.equal (Ok [ "reports/one.md"; "src/A.fs" ]) (observed.ChangedPaths |> Result.map List.sort)
              Assert.equal (Some("reports/one.md", sha "acceptance evidence")) (observed.Evidence |> Map.tryFind "VERIFY-1")
              Assert.equal Map.empty observed.Acceptance
              let forged = { snapshot.Result with OutputCommit = String('f', 40) }
              let missing = FileLocalGitCandidates.observeResult git repo snapshot.Packet forged none |> ok
              Assert.equal None missing.OutputCommit
              Assert.isTrue (Result.isError missing.ChangedPaths) "missing commit reported changes"
              let unrelated = { snapshot.Packet with SourceCommit = output }
              let reversed = FileLocalGitCandidates.observeResult git repo unrelated { snapshot.Result with OutputCommit = source } none |> ok
              Assert.isTrue (not reversed.SourceIsAncestor) "non-descendant output reported as derived from source"))
          t "multi-member attribution must exactly partition observed changes" (fun () -> fixture (fun git repo _ _ _ snapshot ->
              let packet = { snapshot.Packet with Members = LocalAgentHandoffTests.packet.Members }
              let split members = { snapshot.Result with Members = members }
              let first = snapshot.Result.Members.Head
              let second = { LocalAgentHandoffTests.result.Members[1] with ChangedPaths = [ "reports/one.md" ]; Evidence = [] }
              let exact = FileLocalGitCandidates.observeResult git repo packet (split [ { first with ChangedPaths = [ "src/A.fs" ] }; second ]) none |> ok
              Assert.equal (Ok [ "src/A.fs" ]) exact.MemberResults["WI-1"].ChangedPaths
              let overlapping = FileLocalGitCandidates.observeResult git repo packet (split [ first; second ]) none |> ok
              Assert.isTrue (overlapping.MemberResults |> Map.forall (fun _ value -> Result.isError value.ChangedPaths)) "overlapping attribution accepted"))
          t "fast-forward staging validates and updates only with the expected head" (fun () -> fixture (fun git repo _ source output snapshot ->
              let candidate = FileLocalGitCandidates.stage git repo committer snapshot "refs/heads/integration" source none |> ok
              Assert.equal output candidate
              let planned = plan snapshot source candidate
              FileLocalGitCandidates.validateCandidate git repo snapshot planned none |> ok
              FileLocalGitCandidates.compareAndSwap git repo { planned with BeforeCommit = output } none |> refused
              Assert.equal source (FileLocalGitCandidates.readTarget git repo "refs/heads/integration" none |> ok)
              FileLocalGitCandidates.compareAndSwap git repo planned none |> ok
              Assert.equal output (FileLocalGitCandidates.readTarget git repo "refs/heads/integration" none |> ok)
              FileLocalGitCandidates.compareAndSwap git repo planned none |> refused))
          t "advanced target stages a disjoint two-parent merge and refuses conflicts" (fun () -> fixture (fun git repo checkouts source output snapshot ->
              let sibling = FileLocalGitCandidates.provisionCheckout git repo checkouts "sibling" source none |> ok
              CliHarness.write sibling "src/B.fs" "module B // integrated sibling"
              let advanced = commitAll sibling "sibling integrated"
              let merged = FileLocalGitCandidates.stage git repo committer snapshot "refs/heads/integration" advanced none |> ok
              Assert.isTrue (merged <> output && merged <> advanced) "merge candidate not created"
              FileLocalGitCandidates.validateCandidate git repo snapshot (plan snapshot advanced merged) none |> ok
              Assert.equal "module B // integrated sibling" (CliHarness.git repo [ "show"; merged + ":src/B.fs" ])
              // A forged candidate that alters the worker's content refuses.
              FileLocalGitCandidates.validateCandidate git repo snapshot (plan snapshot advanced advanced) none |> refused
              FileLocalGitCandidates.validateCandidate git repo snapshot (plan snapshot source merged) none |> refused
              let conflicting = FileLocalGitCandidates.provisionCheckout git repo checkouts "conflict" source none |> ok
              CliHarness.write conflicting "src/A.fs" "module A // conflicting sibling"
              let conflicted = commitAll conflicting "conflicting sibling"
              FileLocalGitCandidates.stage git repo committer snapshot "refs/heads/integration" conflicted none |> refused
              FileLocalGitCandidates.stage git repo committer snapshot "refs/heads/integration" output none |> refused))
          t "candidate evidence comes only from observed members and must exist in the integrated tree" (fun () -> fixture (fun git repo _ source output snapshot ->
              let planned = plan snapshot source output
              let types = [ "implementation", LocalEvidenceSource.MemberChanges; "tests", LocalEvidenceSource.AcceptanceArtifacts ]
              let evidence = FileLocalGitCandidates.candidateEvidence git repo types snapshot planned none |> ok
              Assert.equal (Set.ofList [ "implementation", "reports/one.md"; "implementation", "src/A.fs"; "tests", "reports/one.md" ])
                           (evidence["WI-1"] |> List.map (fun item -> item.Type, item.Path) |> Set.ofList)
              FileLocalGitCandidates.candidateEvidence git repo types snapshot { planned with CandidateCommit = source } none |> refused
              FileLocalGitCandidates.candidateEvidence git repo [] snapshot planned none |> refused
              CliHarness.git repo [ "update-ref"; "refs/heads/integration"; output ] |> ignore
              let observed = FileLocalGitCandidates.evidenceRepository git repo "refs/heads/integration" none
              Assert.equal EvidencePathObservation.Present (observed.Observe { Type = "tests"; Path = "reports/one.md" })
              Assert.equal EvidencePathObservation.Missing (observed.Observe { Type = "tests"; Path = "reports/absent.md" })))
          t "checkouts are fresh, detached, outside the repository and removable" (fun () -> fixture (fun git repo checkouts source _ _ ->
              FileLocalGitCandidates.provisionCheckout git repo repo "inside" source none |> refused
              FileLocalGitCandidates.provisionCheckout git repo checkouts "../escape" source none |> refused
              let path = FileLocalGitCandidates.provisionCheckout git repo checkouts "validator" source none |> ok
              Assert.equal source (CliHarness.git path [ "rev-parse"; "HEAD" ])
              Assert.equal "" (CliHarness.git path [ "status"; "--porcelain" ])
              FileLocalGitCandidates.removeCheckout git repo path none |> ok
              Assert.isTrue (not (Directory.Exists path)) "checkout not removed"))
          t "replaced Git executable pin refuses every operation" (fun () -> fixture (fun git repo _ source _ _ ->
              let changed = { git with ExecutableDigest = "sha256:" + String('a', 64) }
              FileLocalGitCandidates.resolveCommit changed repo source none |> refused
              FileLocalGitCandidates.readTarget git repo "HEAD" none |> refused)) ]
