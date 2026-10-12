namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work

/// Host-pinned Git, verified before every invocation. Never resolved from a
/// worker-controlled PATH at use time.
type LocalGitTool = { Executable: string; ExecutableDigest: string; TimeoutMilliseconds: int }

/// Identity written on controller-staged merge commits. It is controller
/// provenance for the merge object only, never a worker or native actor.
type LocalIntegrationCommitter = { Name: string; Email: string }

/// Where native completion evidence for a member comes from in the integrated
/// candidate tree: its independently observed changed paths, or the artifacts
/// it claimed for acceptance obligations (which validators checked).
[<RequireQualifiedAccess>]
type LocalEvidenceSource = MemberChanges | AcceptanceArtifacts

/// Concrete Git observation, staging and expected-head update adapters for the
/// local controller. These observe actual objects in the shared repository
/// object database; worker claims are compared with, never substituted for,
/// these observations. This is not OS containment.
[<RequireQualifiedAccess>]
module FileLocalGitCandidates =
    let private maxBlobBytes = 8388608

    let private digest (bytes: byte array) = "sha256:" + (SHA256.HashData bytes |> Convert.ToHexString).ToLowerInvariant()

    /// Repository hooks are disabled for controller Git operations: a
    /// nonexistent hooks directory means no hook can run.
    let private hooksOff () = Path.Combine(Path.GetTempPath(), "praxis-no-hooks-" + Guid.NewGuid().ToString("N"))

    let private run (git: LocalGitTool) (cwd: string) (environment: Map<string, string>) (arguments: string list) (cancellation: CancellationToken) =
        let spec : LocalWorkerProcessSpec =
            { Executable = git.Executable; ExecutableDigest = git.ExecutableDigest
              Arguments = [ "--no-optional-locks"; "-c"; "core.fsmonitor=false"; "-c"; "core.hooksPath=" + hooksOff ()
                            "-c"; "core.autocrlf=false"; "-c"; "advice.detachedHead=false" ] @ arguments
              WorkingDirectory = cwd; Environment = environment; Input = ""
              TimeoutMilliseconds = git.TimeoutMilliseconds; MaxOutputBytes = maxBlobBytes + 1 }
        let observed = (LocalWorkerProcess.runControllerTool spec cancellation).GetAwaiter().GetResult()
        match observed.Outcome with
        | LocalWorkerProcessOutcome.Exited code when observed.RootExitObserved && observed.ProcessStarted = Some true ->
            Ok(code, observed.StandardOutput, observed.StandardError)
        | other -> Error(sprintf "controller Git unavailable: %A" other)

    let private succeed git cwd environment arguments cancellation =
        run git cwd environment arguments cancellation |> Result.bind (fun (code, output, error) ->
            if code = 0 then Ok output
            else
                let detail = error.Trim()
                Error(sprintf "git %s exited %d%s" (List.head arguments) code (if detail = "" then "" else ": " + detail.Substring(0, min 512 detail.Length))))

    let private entries (text: string) = text.Split('\u0000', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    let private literal (path: string) = ":(literal)" + path

    let resolveCommit git repository (revision: string) cancellation =
        if String.IsNullOrWhiteSpace revision || revision.StartsWith '-' then Error "invalid Git revision"
        else
            succeed git repository Map.empty [ "rev-parse"; "--verify"; "--quiet"; revision + "^{commit}" ] cancellation
            |> Result.bind (fun output ->
                let commit = output.Trim()
                if LocalAgentHandoff.isCommit commit then Ok commit else Error "Git revision is not a full commit")

    /// `Ok false` only on Git's definite "not an ancestor" answer.
    let isAncestor git repository ancestor descendant cancellation =
        run git repository Map.empty [ "merge-base"; "--is-ancestor"; ancestor; descendant ] cancellation
        |> Result.bind (function
            | 0, _, _ -> Ok true
            | 1, _, _ -> Ok false
            | code, _, _ -> Error(sprintf "Git ancestry unavailable (%d)" code))

    let changedPaths git repository baseline candidate cancellation =
        succeed git repository Map.empty [ "diff"; "--name-only"; "-z"; "--no-renames"; "--no-ext-diff"; "--no-textconv"; baseline; candidate; "--" ] cancellation
        |> Result.map entries

    /// Digest of the exact committed blob bytes. Evidence artifacts must be
    /// UTF-8 text; any other blob refuses rather than being reinterpreted.
    let blobDigest git repository commit (path: string) cancellation =
        if not (LocalAgentHandoff.isResourcePath path) then Error "evidence path is not a portable resource"
        else
            succeed git repository Map.empty [ "cat-file"; "-t"; commit + ":" + path ] cancellation
            |> Result.bind (fun kind ->
                if kind.Trim() <> "blob" then Error "evidence path is not a committed file"
                else succeed git repository Map.empty [ "cat-file"; "blob"; commit + ":" + path ] cancellation)
            |> Result.bind (fun content ->
                let bytes = UTF8Encoding(false, true).GetBytes content
                if bytes.Length > maxBlobBytes then Error "evidence artifact exceeds bound" else Ok(digest bytes))

    let private present git repository commit (path: string) cancellation =
        LocalAgentHandoff.isResourcePath path
        && (succeed git repository Map.empty [ "cat-file"; "-t"; commit + ":" + path ] cancellation
            |> Result.map (fun kind -> kind.Trim() = "blob") |> Result.defaultValue false)

    /// Fresh detached checkout of an exact commit outside the repository. The
    /// caller owns it until `removeCheckout`; validators verify it is clean.
    let provisionCheckout git repository checkoutsRoot (label: string) commit cancellation =
        try
            if String.IsNullOrWhiteSpace label || label.Length > 64 || not (label |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-')) then
                Error "invalid checkout label"
            elif not (LocalAgentHandoff.isCommit commit) then Error "checkout requires a full commit"
            else
                let root = LocalHostJournalFiles.rootOutside repository checkoutsRoot
                let path = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N").Substring(0, 12))
                succeed git repository Map.empty [ "worktree"; "add"; "--detach"; path; commit ] cancellation
                |> Result.bind (fun _ ->
                    resolveCommit git path "HEAD" cancellation |> Result.bind (fun head ->
                        if head <> commit then Error "provisioned checkout is not the requested commit" else Ok path))
        with error -> Error("checkout provisioning refused: " + error.Message)

    let removeCheckout git repository (path: string) cancellation =
        succeed git repository Map.empty [ "worktree"; "remove"; "--force"; path ] cancellation
        |> Result.bind (fun _ -> succeed git repository Map.empty [ "worktree"; "prune" ] cancellation)
        |> Result.map ignore

    /// Independently observe a worker result against actual Git objects. Member
    /// attribution in multi-member packets is the worker's claim, accepted only
    /// as an exact disjoint partition of the observed changes.
    let observeResult git repository (packet: LocalWorkerPacket) (result: LocalWorkerResult) cancellation : Result<LocalWorkerResultObservation, string> =
        let output =
            if LocalAgentHandoff.isCommit result.OutputCommit then
                resolveCommit git repository result.OutputCommit cancellation |> Result.toOption |> Option.filter ((=) result.OutputCommit)
            else None
        let ancestor = output |> Option.map (fun commit -> isAncestor git repository packet.SourceCommit commit cancellation)
        match ancestor with
        | Some(Error error) -> Error error
        | _ ->
            let changed =
                match output with
                | Some commit -> changedPaths git repository packet.SourceCommit commit cancellation
                | None -> Error "worker output commit is not present"
            let claims = result.Members |> List.map (fun item -> item.WorkItemId, item.ChangedPaths) |> Map.ofList
            let claimed = result.Members |> List.collect _.ChangedPaths
            let partition =
                match changed with
                | Ok actual -> claimed.Length = (Set.ofList claimed).Count && Set.ofList claimed = Set.ofList actual
                | Error _ -> false
            let members =
                packet.Members |> List.map (fun assigned ->
                    let paths =
                        match changed with
                        | Error error -> Error error
                        | Ok actual when packet.Members.Length = 1 -> Ok actual
                        | Ok _ when partition -> claims |> Map.tryFind assigned.WorkItemId |> function Some value -> Ok value | None -> Error "member attribution missing"
                        | Ok _ -> Error "member attribution is not an exact partition of observed changes"
                    assigned.WorkItemId, { LocalMemberResultObservation.ExecutionId = assigned.ExecutionId; ChangedPaths = paths })
                |> Map.ofList
            let evidence =
                match output with
                | None -> Map.empty
                | Some commit ->
                    result.Members |> List.collect _.Evidence |> List.choose (fun claim ->
                        blobDigest git repository commit claim.ArtifactPath cancellation |> Result.toOption
                        |> Option.map (fun actual -> claim.ObligationId, (claim.ArtifactPath, actual)))
                    |> Map.ofList
            Ok { WorkerId = packet.WorkerId; ChildExecutionId = packet.ChildExecutionId; AttemptId = packet.AttemptId
                 ReservedPacketDigest = LocalAgentHandoff.packetDigest packet
                 OutputCommit = output; SourceIsAncestor = (ancestor = Some(Ok true))
                 ChangedPaths = changed; MemberResults = members; Evidence = evidence; Acceptance = Map.empty }

    let readTarget git repository (targetRef: string) cancellation =
        if not (LocalIntegrationJournal.isTargetRef targetRef) then Error "invalid integration target ref"
        else resolveCommit git repository targetRef cancellation

    /// Stage without touching any working tree. A target still at the source
    /// fast-forwards to the worker output; otherwise Git's tree-only merge
    /// builds a two-parent candidate. Conflicts refuse; nothing is resolved.
    let stage git repository (committer: LocalIntegrationCommitter) (snapshot: LocalSubmissionInspection) (targetRef: string) (before: string) cancellation =
        let source, output = snapshot.Packet.SourceCommit, snapshot.Result.OutputCommit
        isAncestor git repository source before cancellation |> Result.bind (fun descends ->
            if not descends then Error "integration target does not descend from the delegated source"
            elif before = source then Ok output
            else
                isAncestor git repository output before cancellation |> Result.bind (fun integrated ->
                    if integrated then Error "worker output is already contained in the integration target"
                    else
                        run git repository Map.empty [ "merge-tree"; "--write-tree"; "--no-messages"; "-z"; before; output ] cancellation
                        |> Result.bind (function
                            | 0, tree, _ ->
                                let tree = (entries tree |> List.tryHead |> Option.defaultValue "").Trim()
                                if not (LocalAgentHandoff.isCommit tree) then Error "Git merge produced no tree"
                                else
                                    let identity =
                                        Map.ofList [ "GIT_AUTHOR_NAME", committer.Name; "GIT_AUTHOR_EMAIL", committer.Email
                                                     "GIT_COMMITTER_NAME", committer.Name; "GIT_COMMITTER_EMAIL", committer.Email ]
                                    let message = sprintf "Integrate local dispatch %s (%s)" snapshot.Packet.DispatchId targetRef
                                    succeed git repository identity [ "commit-tree"; tree; "-p"; before; "-p"; output; "-m"; message ] cancellation
                                    |> Result.bind (fun created ->
                                        let commit = created.Trim()
                                        if LocalAgentHandoff.isCommit commit then Ok commit else Error "Git did not create a merge candidate")
                            | 1, _, _ -> Error "integration candidate has merge conflicts; reconciliation is a new delegation"
                            | code, _, _ -> Error(sprintf "Git merge staging unavailable (%d)" code))))

    /// The staged candidate may differ from the target only by the worker's
    /// observed changes, with exactly the worker's committed contents for them.
    let validateCandidate git repository (snapshot: LocalSubmissionInspection) (plan: LocalIntegrationPlan) cancellation =
        let source, output = snapshot.Packet.SourceCommit, snapshot.Result.OutputCommit
        let paths = snapshot.Result.ChangedPaths
        resolveCommit git repository plan.CandidateCommit cancellation |> Result.bind (fun candidate ->
            if candidate <> plan.CandidateCommit then Error "staged candidate differs from plan"
            else
                succeed git repository Map.empty [ "rev-list"; "--parents"; "-n"; "1"; candidate ] cancellation
                |> Result.bind (fun line ->
                    let parents = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.toList |> List.tail
                    let shape =
                        if candidate = output then
                            if plan.BeforeCommit = source then Ok() else Error "fast-forward candidate requires the target at the delegated source"
                        elif parents = [ plan.BeforeCommit; output ] then Ok()
                        else Error "staged candidate is not a merge of the observed target and worker output"
                    shape |> Result.bind (fun () ->
                        changedPaths git repository plan.BeforeCommit candidate cancellation |> Result.bind (fun changed ->
                            if not (Set.isSubset (Set.ofList changed) (Set.ofList paths)) then Error "staged candidate changes paths outside the worker result"
                            elif paths.IsEmpty then Ok()
                            else
                                run git repository Map.empty ([ "diff"; "--quiet"; "--no-ext-diff"; "--no-textconv"; output; candidate; "--" ] @ (paths |> List.map literal)) cancellation
                                |> Result.bind (function
                                    | 0, _, _ -> Ok()
                                    | 1, _, _ -> Error "staged candidate does not carry the worker's exact committed changes"
                                    | code, _, _ -> Error(sprintf "Git candidate comparison unavailable (%d)" code))))))

    /// Atomic expected-head update. Git refuses unless the ref still names the
    /// recorded baseline.
    let compareAndSwap git repository (plan: LocalIntegrationPlan) cancellation =
        if not (LocalIntegrationJournal.isTargetRef plan.TargetRef) then Error "invalid integration target ref"
        else
            succeed git repository Map.empty [ "update-ref"; "-m"; "praxis local integration " + plan.DispatchId; plan.TargetRef; plan.CandidateCommit; plan.BeforeCommit ] cancellation
            |> Result.map ignore

    /// Native evidence entries for each member, present in the integrated
    /// candidate tree. Configuration chooses which observed sources back each
    /// native evidence type; it cannot add unobserved paths.
    let candidateEvidence git repository (types: (string * LocalEvidenceSource) list) (snapshot: LocalSubmissionInspection) (plan: LocalIntegrationPlan) cancellation =
        if types.IsEmpty then Error "no native evidence types configured"
        else
            snapshot.Result.Members
            |> List.fold (fun acc memberResult ->
                acc |> Result.bind (fun (map: Map<string, WorkEvidence list>) ->
                    let entries =
                        types |> List.collect (fun (kind, source) ->
                            let paths =
                                match source with
                                | LocalEvidenceSource.MemberChanges -> memberResult.ChangedPaths
                                | LocalEvidenceSource.AcceptanceArtifacts -> memberResult.Evidence |> List.map _.ArtifactPath
                            paths |> List.distinct |> List.map (fun path -> { WorkEvidence.Type = kind; Path = path }))
                    if entries |> List.exists (fun entry -> not (present git repository plan.CandidateCommit entry.Path cancellation)) then
                        Error("candidate evidence is absent from the integrated tree for " + memberResult.WorkItemId)
                    else Ok(map.Add(memberResult.WorkItemId, entries))))
                (Ok Map.empty)

    /// Evidence observations against the integrated target tip, not the
    /// controller's working tree.
    let evidenceRepository git repository targetRef cancellation : WorkEvidenceRepository =
        { Observe = fun evidence ->
            match readTarget git repository targetRef cancellation with
            | Error error -> EvidencePathObservation.Unavailable error
            | Ok commit ->
                if present git repository commit evidence.Path cancellation then EvidencePathObservation.Present
                else EvidencePathObservation.Missing }
