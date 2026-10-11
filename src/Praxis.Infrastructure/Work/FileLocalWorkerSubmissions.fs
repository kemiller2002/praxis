namespace Praxis.Infrastructure.Work

open System.IO
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// Immutable untrusted submission archive. The host provisions a separate
/// protected root outside the repository; this mechanism does not supply trust.
[<RequireQualifiedAccess>]
module FileLocalWorkerSubmissions =
    let create repositoryRoot storeRoot : Result<LocalWorkerSubmissionStore, string> =
        try
            let root = LocalHostJournalFiles.rootOutside repositoryRoot storeRoot
            let directory repositoryIdentity dispatchId = LocalHostJournalFiles.directory root repositoryIdentity dispatchId
            let guarded operation = try operation() with e -> Error("local submission archive refused: " + e.Message)
            let load repositoryIdentity dispatchId = guarded (fun () ->
                let target = directory repositoryIdentity dispatchId
                let names = Directory.GetFileSystemEntries target |> Array.map Path.GetFileName
                if names <> [| "submission.json" |] then invalidOp "incomplete or unexpected submission archive"
                let file = Path.Combine(target, "submission.json")
                if not (LocalHostJournalFiles.noLinks file) then invalidOp "submission archive contains a link"
                LocalHostJournalFiles.readBounded file |> LocalWorkerSubmissionJson.read |> Result.bind (fun submission ->
                    if submission.RepositoryIdentity <> repositoryIdentity || submission.DispatchId <> dispatchId then Error "submission archive identity differs"
                    else Ok submission))
            let save submission = guarded (fun () ->
                let problems = LocalWorkerSubmission.problems submission
                if not problems.IsEmpty then Error(String.concat "; " problems)
                else
                    let target = directory submission.RepositoryIdentity submission.DispatchId
                    Directory.CreateDirectory target |> ignore
                    let file = Path.Combine(target, "submission.json")
                    if not (LocalHostJournalFiles.noLinks file) then invalidOp "submission archive contains a link"
                    try
                        if Directory.GetFileSystemEntries(target).Length <> 0 then raise (IOException "submission already saved or incomplete")
                        LocalHostJournalFiles.writeExclusive file (LocalWorkerSubmissionJson.render submission)
                        Ok LocalSubmissionSaveOutcome.Created
                    with :? IOException ->
                        load submission.RepositoryIdentity submission.DispatchId |> Result.bind (fun existing ->
                            if LocalWorkerSubmission.sameContent existing submission then Ok LocalSubmissionSaveOutcome.Existing
                            else Error "dispatch submission cannot be replaced"))
            Ok { Save = save; Load = load }
        with e -> Error("cannot configure local submission archive: " + e.Message)
