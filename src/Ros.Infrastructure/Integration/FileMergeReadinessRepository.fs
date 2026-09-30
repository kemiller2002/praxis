namespace Ros.Infrastructure.Integration

open System
open System.IO
open System.Text.Json
open Ros.Contracts.Integration
open Ros.Domain.Git
open Ros.Domain.Integration
open Ros.Infrastructure.Git

[<RequireQualifiedAccess>]
module FileMergeReadinessRepository =
    let private stringArray (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun item ->
                if item.ValueKind = JsonValueKind.String then
                    let value = item.GetString()
                    if String.IsNullOrWhiteSpace value then None else Some value
                else
                    None)
            |> Seq.distinct
            |> Seq.toList
        | _ -> []

    let private boolOr fallback (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True -> true
        | true, value when value.ValueKind = JsonValueKind.False -> false
        | _ -> fallback

    let readPolicy (root: string) : MergeReadinessPolicy =
        let fallback =
            { Enabled = false
              RequiredChecks = []
              OptionalChecks = []
              RequireCleanWorkingTree = true
              RequireRemoteCandidate = true }

        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            fallback
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)

                match document.RootElement.TryGetProperty "mergeReadiness" with
                | true, value when value.ValueKind = JsonValueKind.Object ->
                    { Enabled = boolOr false value "enabled"
                      RequiredChecks = stringArray value "requiredChecks"
                      OptionalChecks = stringArray value "optionalChecks"
                      RequireCleanWorkingTree = boolOr true value "requireCleanWorkingTree"
                      RequireRemoteCandidate = boolOr true value "requireRemoteCandidate" }
                    |> MergeReadiness.normalizePolicy
                | _ -> fallback
            with _ ->
                fallback

    let readEvidence (root: string) (relativeOrAbsolutePath: string option) : Result<MergeReadinessEvidence, string> =
        match relativeOrAbsolutePath with
        | None ->
            Ok
                { CandidateCommit = None
                  RemoteCandidateCurrent = None
                  Checks = [] }
        | Some supplied ->
            let path =
                if Path.IsPathRooted supplied then supplied
                else Path.GetFullPath(Path.Combine(root, supplied))

            if not (File.Exists path) then
                Error $"merge-readiness evidence file does not exist: {supplied}"
            else
                try
                    File.ReadAllText path |> MergeReadinessJson.parseEvidence
                with error ->
                    Error $"cannot read merge-readiness evidence '{supplied}': {error.Message}"

    let observe (root: string) (evidence: MergeReadinessEvidence) : MergeReadinessObservation =
        let _, rawHead = ProcessGitRepository.readBranchAndCommit root

        let head =
            rawHead
            |> Option.bind (fun value -> value.ToLowerInvariant() |> CommitId.tryParse)

        let workingTreeClean =
            let repository = ProcessGitRepository.create root

            match repository.ObserveStatus() with
            | GitStatusObservation.Clean -> Some true
            | GitStatusObservation.Changed _ -> Some false
            | GitStatusObservation.Unavailable _ -> None

        { Head = head
          WorkingTreeClean = workingTreeClean
          Evidence = evidence }
