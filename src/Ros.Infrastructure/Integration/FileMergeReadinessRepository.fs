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
        | false, _ -> Ok []
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.toList
            |> List.fold
                (fun state item ->
                    state
                    |> Result.bind (fun values ->
                        if item.ValueKind <> JsonValueKind.String then
                            Error $"mergeReadiness.{name} entries must be strings"
                        else
                            let value = item.GetString()
                            if String.IsNullOrWhiteSpace value then
                                Error $"mergeReadiness.{name} entries must be non-blank"
                            else
                                Ok(values @ [ value ])))
                (Ok [])
            |> Result.map List.distinct
        | _ -> Error $"mergeReadiness.{name} must be an array of strings"

    let private boolOr fallback (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | false, _ -> Ok fallback
        | true, value when value.ValueKind = JsonValueKind.True -> Ok true
        | true, value when value.ValueKind = JsonValueKind.False -> Ok false
        | _ -> Error $"mergeReadiness.{name} must be a boolean"

    let readPolicy (root: string) : Result<MergeReadinessPolicy, string> =
        let fallback =
            { Enabled = false
              RequiredChecks = []
              OptionalChecks = []
              RequireCleanWorkingTree = true
              RequireRemoteCandidate = true }

        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            Ok fallback
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)

                match document.RootElement.TryGetProperty "mergeReadiness" with
                | false, _ -> Ok fallback
                | true, value when value.ValueKind = JsonValueKind.Object ->
                    boolOr false value "enabled"
                    |> Result.bind (fun enabled ->
                        stringArray value "requiredChecks"
                        |> Result.bind (fun requiredChecks ->
                            stringArray value "optionalChecks"
                            |> Result.bind (fun optionalChecks ->
                                boolOr true value "requireCleanWorkingTree"
                                |> Result.bind (fun requireCleanWorkingTree ->
                                    boolOr true value "requireRemoteCandidate"
                                    |> Result.map (fun requireRemoteCandidate ->
                                        { Enabled = enabled
                                          RequiredChecks = requiredChecks
                                          OptionalChecks = optionalChecks
                                          RequireCleanWorkingTree = requireCleanWorkingTree
                                          RequireRemoteCandidate = requireRemoteCandidate }
                                        |> MergeReadiness.normalizePolicy)))))
                | _ -> Error "mergeReadiness must be a JSON object"
            with error ->
                Error $"cannot parse ros.json mergeReadiness policy: {error.Message}"

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
