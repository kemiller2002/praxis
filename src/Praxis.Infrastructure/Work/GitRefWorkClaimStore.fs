namespace Praxis.Infrastructure.Work

open System
open Praxis.Application.Work
open Praxis.Domain.Work

/// Claim store for agents on different hosts (PRX-REL-002). Each claim is
/// a commit whose tree holds `claim.json`, published at
/// `refs/praxis/claims/<work-item-id>` on a shared remote. Every write is
/// `git push --force-with-lease=<ref>:<observed-oid>` (an empty expected
/// oid means "must not exist yet"), so the remote's own ref update is the
/// linearization point: of two racing clones exactly one push lands and
/// the other is told it is stale. No local lock is assumed to be shared.
[<RequireQualifiedAccess>]
module GitRefWorkClaimStore =
    let refName (id: string) = $"refs/praxis/claims/{id}"

    let private observedRef (remote: string) (id: string) = $"refs/praxis/observed-claims/{remote}/{id}"

    let private identity =
        [ "GIT_TERMINAL_PROMPT", "0"
          "GIT_AUTHOR_NAME", "Praxis claims"
          "GIT_AUTHOR_EMAIL", "claims@praxis.invalid"
          "GIT_COMMITTER_NAME", "Praxis claims"
          "GIT_COMMITTER_EMAIL", "claims@praxis.invalid" ]

    type private Observed = { Claim: WorkClaim option; Oid: string option }

    let create (repository: string) (remote: string) (timeout: TimeSpan) : WorkClaimStore =
        let describe (arguments: string list) = arguments |> List.truncate 2 |> String.concat " "

        let git (stdin: string option) (arguments: string list) =
            match ClaimProcess.run "git" repository identity stdin timeout arguments with
            | Error(ClaimProcessFailure.NotStarted reason) -> Error $"git is unavailable: {reason}"
            | Error(ClaimProcessFailure.TimedOut after) -> Error $"git {describe arguments} timed out after {after.TotalSeconds:F0}s"
            | Ok result -> Ok result

        let gitOk stdin arguments =
            git stdin arguments
            |> Result.bind (fun result ->
                if result.ExitCode = 0 then
                    Ok(result.Stdout.Trim())
                else
                    Error $"git {describe arguments} failed ({result.ExitCode}): {result.Stderr.Trim()}")

        /// `<oid>\t<ref>` lines for refs under the claims namespace.
        let remoteRefs pattern =
            gitOk None [ "ls-remote"; "--refs"; remote; pattern ]
            |> Result.map (fun output ->
                output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                |> Array.choose (fun line ->
                    match line.Split('\t') with
                    | [| oid; name |] -> Some(name.Trim(), oid.Trim())
                    | _ -> None)
                |> Array.toList)

        let observe (id: string) : Result<Observed, string> =
            if not (WorkItemId.isValid id) then
                Error $"'{id}' is not a work-item id"
            else
                remoteRefs (refName id)
                |> Result.bind (fun refs ->
                    match refs |> List.tryFind (fun (name, _) -> name = refName id) with
                    | None -> Ok { Claim = None; Oid = None }
                    | Some _ ->
                        gitOk None [ "fetch"; "--no-tags"; "--quiet"; remote; $"+{refName id}:{observedRef remote id}" ]
                        |> Result.bind (fun _ -> gitOk None [ "rev-parse"; "--verify"; $"{observedRef remote id}^{{commit}}" ])
                        |> Result.bind (fun oid ->
                            gitOk None [ "cat-file"; "blob"; $"{oid}:claim.json" ]
                            |> Result.bind WorkClaimJson.parseClaim
                            |> Result.map (fun claim -> { Claim = Some claim; Oid = Some oid })))

        let commitFor (parent: string option) (next: WorkClaim) =
            gitOk (Some(WorkClaimJson.renderClaim next)) [ "hash-object"; "-w"; "--stdin" ]
            |> Result.bind (fun blob -> gitOk (Some $"100644 blob {blob}\tclaim.json\n") [ "mktree" ])
            |> Result.bind (fun tree ->
                let parents = parent |> Option.map (fun oid -> [ "-p"; oid ]) |> Option.defaultValue []
                let message = $"praxis claim {next.WorkItemId} revision {next.Revision} generation {next.Generation} {ClaimStatus.code next.Status}"
                gitOk None ([ "commit-tree"; "--no-gpg-sign"; tree ] @ parents @ [ "-m"; message ]))

        let compareAndSwap (expected: int64 option) (next: WorkClaim) =
            observe next.WorkItemId
            |> Result.bind (fun observed ->
                if observed.Claim |> Option.map _.Revision <> expected then
                    Ok(ClaimWrite.Conflict observed.Claim)
                else
                    commitFor observed.Oid next
                    |> Result.bind (fun commit ->
                        let lease = $"--force-with-lease={refName next.WorkItemId}:{observed.Oid |> Option.defaultValue String.Empty}"

                        git None [ "push"; "--porcelain"; "--no-verify"; lease; remote; $"{commit}:{refName next.WorkItemId}" ]
                        |> Result.bind (fun pushed ->
                            if pushed.ExitCode = 0 then
                                gitOk None [ "update-ref"; observedRef remote next.WorkItemId; commit ]
                                |> Result.map (fun _ -> ClaimWrite.Written next)
                            elif pushed.Stdout.Split('\n') |> Array.exists (fun line -> line.StartsWith "!") then
                                // The remote refused the lease: someone else won.
                                observe next.WorkItemId |> Result.map (fun latest -> ClaimWrite.Conflict latest.Claim)
                            else
                                Error $"git push of claim '{next.WorkItemId}' failed ({pushed.ExitCode}): {pushed.Stderr.Trim()}")))

        let list () =
            remoteRefs "refs/praxis/claims/*"
            |> Result.bind (fun refs ->
                refs
                |> List.map (fun (name, _) -> name.Substring((refName "").Length))
                |> List.filter WorkItemId.isValid
                |> List.sort
                |> List.fold
                    (fun state id ->
                        state
                        |> Result.bind (fun claims ->
                            observe id |> Result.map (fun observed -> claims @ (observed.Claim |> Option.toList))))
                    (Ok []))

        { Read = fun id -> observe id |> Result.map _.Claim
          CompareAndSwap = compareAndSwap
          List = list }
