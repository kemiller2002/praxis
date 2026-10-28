namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Praxis.Contracts.Work
open Praxis.Domain.Git
open Praxis.Domain.Work
open Praxis.Infrastructure.Git
open Praxis.Infrastructure.Planning

/// The Git shell of `praxis streams check`: reads the integration contract
/// and gathers read-only Git facts for the pure check
/// (`IntegrationStreams.check`). It never fetches, merges or writes.
[<RequireQualifiedAccess>]
module FileIntegrationStreams =
    let private eventsPath = ".ros/events/events.jsonl"
    let private decisionId = Regex(@"^DF-[A-Z0-9]+-\d{4}-[A-Z0-9]+", RegexOptions.CultureInvariant)

    let private git root arguments =
        ProcessGitRepository.readLines root ("-c" :: "core.quotePath=false" :: arguments)

    let private gitFailure (failure: GitFailure) = $"{failure.Operation} failed: {failure.Message}"

    /// The contract from `--contract FILE`, else the `integration` member of
    /// `ros.json`.
    let readContract (root: string) (contractFile: string option) : Result<IntegrationContract, string> =
        match contractFile with
        | Some path ->
            let full = if Path.IsPathRooted path then path else Path.Combine(root, path)
            if File.Exists full then IntegrationContractJson.parse (File.ReadAllText full)
            else Error $"integration contract {path} does not exist"
        | None ->
            let rosJson = Path.Combine(root, "ros.json")

            if File.Exists rosJson && IntegrationContractJson.declares (File.ReadAllText rosJson) then
                IntegrationContractJson.parse (File.ReadAllText rosJson)
            else
                Error "no integration contract: add an `integration` object to ros.json (praxis.integration-contract/1) or pass --contract FILE"

    let private resolveRef root (remote: string) (reference: string) =
        [ reference; $"refs/remotes/{remote}/{reference}" ]
        |> List.tryPick (fun candidate ->
            match git root [ "rev-parse"; "--verify"; "--quiet"; $"{candidate}^{{commit}}" ] with
            | Ok [ commit ] -> Some commit
            | _ -> None)

    let private mergeBase root left right =
        match git root [ "merge-base"; left; right ] with
        | Ok [ commit ] -> Some commit
        | _ -> None

    let private changed root left right =
        git root [ "diff"; "--name-only"; "--no-renames"; "--no-ext-diff"; left; right; "--" ] |> Result.mapError gitFailure

    let private treePaths root commit =
        git root [ "ls-tree"; "-r"; "--name-only"; commit ] |> Result.mapError gitFailure

    /// The latest `work.completion.verified` event for the work item in the
    /// tip's tracked event log, as (verifiedCommit, receiptDigests).
    let private verifiedCompletion root tip (workItem: string) =
        match ProcessGitRepository.readFileAtRevision root tip eventsPath with
        | None -> None
        | Some content ->
            content.Split('\n')
            |> Array.choose (fun line ->
                if line.Trim().Length = 0 then None
                else
                    try
                        use document = JsonDocument.Parse line
                        let event = document.RootElement
                        let property (name: string) =
                            match event.TryGetProperty name with
                            | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                            | _ -> None

                        match property "type", property "workItem", property "verifiedCommit" with
                        | Some "work.completion.verified", Some item, Some commit when item = workItem ->
                            let digests =
                                match event.TryGetProperty "receiptDigests" with
                                | true, value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
                                | _ -> []

                            Some(commit, digests)
                        | _ -> None
                    with _ ->
                        None)
            |> Array.tryLast

    let private streamFacts root remote target (stream: ResolvedStream) : Result<StreamFacts, string> =
        let empty =
            { Stream = stream.Contract.Id
              Tip = None
              MergeBase = None
              PendingPaths = []
              TargetIncomingPaths = []
              Integrated = false
              PresentArtifacts = []
              Completion = CompletionEvidence.Missing }

        match resolveRef root remote stream.Contract.Ref with
        | None -> Ok empty
        | Some tip ->
            let basis = mergeBase root target tip |> Option.defaultValue tip

            let artifacts () =
                if stream.Contract.Artifacts.IsEmpty then Ok []
                else
                    treePaths root tip
                    |> Result.map (fun paths ->
                        stream.Contract.Artifacts
                        |> List.filter (fun pattern -> paths |> List.exists (PathRule.matches (PathRule.Glob pattern))))

            let completion () =
                match verifiedCompletion root tip stream.Contract.WorkItem with
                | None -> Ok CompletionEvidence.Missing
                | Some(commit, digests) ->
                    changed root commit tip |> Result.map (fun since -> CompletionEvidence.Verified(commit, digests, since))

            changed root basis tip
            |> Result.bind (fun pending ->
                changed root basis target
                |> Result.bind (fun incoming ->
                    artifacts ()
                    |> Result.bind (fun present ->
                        completion ()
                        |> Result.map (fun evidence ->
                            { empty with
                                Tip = Some tip
                                MergeBase = Some basis
                                PendingPaths = pending
                                TargetIncomingPaths = incoming
                                Integrated = (basis = tip)
                                PresentArtifacts = present
                                Completion = evidence }))))

    let private pairFacts root (selected: string list option) (streams: StreamFacts list) : Result<PairFacts list, string> =
        let candidates = streams |> List.filter (fun stream -> stream.Tip.IsSome && not stream.Integrated)
        let isSelected id = selected |> Option.forall (List.contains id)

        candidates
        |> List.mapi (fun index left -> candidates |> List.skip (index + 1) |> List.map (fun right -> left, right))
        |> List.concat
        |> List.filter (fun (left, right) -> isSelected left.Stream || isSelected right.Stream)
        |> List.fold
            (fun state (left, right) ->
                state
                |> Result.bind (fun pairs ->
                    let leftTip, rightTip = left.Tip.Value, right.Tip.Value
                    let basis = mergeBase root leftTip rightTip

                    match basis with
                    | None -> Ok(pairs @ [ { Left = left.Stream; Right = right.Stream; LeftPaths = []; RightPaths = [] } ])
                    | Some basis ->
                        changed root basis leftTip
                        |> Result.bind (fun leftPaths ->
                            changed root basis rightTip
                            |> Result.map (fun rightPaths -> pairs @ [ { Left = left.Stream; Right = right.Stream; LeftPaths = leftPaths; RightPaths = rightPaths } ]))))
            (Ok [])

    let private knownDecisions root target (contract: IntegrationContract) =
        let cited =
            contract.Interfaces
            |> List.exists (fun shared -> shared.Approvals |> List.exists (fun approval -> match approval.Authority with ApprovalAuthority.Decision _ -> true | _ -> false))

        if not cited then Ok []
        else
            treePaths root target
            |> Result.map (fun paths ->
                paths
                |> List.choose (fun path ->
                    let found = decisionId.Match(Path.GetFileName path)
                    if found.Success then Some found.Value else None)
                |> List.distinct)

    /// Reads the contract, gathers facts and runs the pure check.
    let check (root: string) (contractFile: string option) (against: string option) (selected: string list option) : Result<IntegrationReport, string> =
        let root = Path.GetFullPath root
        let sync = FileUpstreamSynchronization.readConfig root

        readContract root contractFile
        |> Result.bind (fun contract ->
            FilePlanningRepository.readDefaultConfiguration root
            |> Result.bind (fun planner ->
                let resolved = IntegrationStreams.resolve planner.Areas planner.Dependencies contract

                let unknown =
                    selected |> Option.defaultValue [] |> List.filter (fun id -> not (resolved |> List.exists (fun stream -> stream.Contract.Id = id)))

                if not unknown.IsEmpty then
                    Error $"""unknown stream(s): {String.Join(", ", unknown)}"""
                else
                    let targetName =
                        against |> Option.orElse contract.Target |> Option.defaultValue $"refs/remotes/{sync.Remote}/{sync.Branch}"

                    match resolveRef root sync.Remote targetName with
                    | None -> Error $"the integration target '{targetName}' does not resolve to a commit"
                    | Some targetCommit ->
                        resolved
                        |> List.fold (fun state stream -> state |> Result.bind (fun facts -> streamFacts root sync.Remote targetCommit stream |> Result.map (fun fact -> facts @ [ fact ]))) (Ok [])
                        |> Result.bind (fun streams ->
                            pairFacts root selected streams
                            |> Result.bind (fun pairs ->
                                knownDecisions root targetCommit contract
                                |> Result.map (fun decisions ->
                                    IntegrationStreams.check
                                        contract
                                        resolved
                                        selected
                                        { Target = targetName
                                          TargetCommit = Some targetCommit
                                          Streams = streams
                                          Pairs = pairs
                                          KnownDecisions = decisions })))))
