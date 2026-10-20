namespace Praxis.Cli

open System
open Praxis.Application.Work
open Praxis.Infrastructure.Work

/// F1 (PRAXIS-REL-009): the trusted verifier and its agent client.
/// `praxis verifier serve` (host, long-running, key in memory),
/// `praxis verifier run` (host or CI, one-shot, `--key-file` PEM) and
/// `praxis work verify` (agent, over `PRAXIS_VERIFIER_SOCKET`). Also
/// `praxis work claims migrate` (I8 legacy store). Exit codes: 0 done (all
/// receipts passed), 3 refused or not passed, 2 invalid arguments, 1 failure.
[<RequireQualifiedAccess>]
module VerifierCommands =
    let usage =
        "verifier serve --socket PATH --policy FILE --contract-digest sha256:HEX [--key-file PEM] | "
        + "verifier run --repo PATH --commit SHA --work-item ID --claimant NAME --generation N [--store local|git] [--remote NAME] --policy FILE --contract-digest sha256:HEX --key-file PEM --out FILE | "
        + "work verify --id ID --token TOKEN [--timeout-seconds N] [--store local|git] [--remote NAME] | work claims migrate [--keep-legacy]"

    let private value = ClaimArguments.optionValue
    let private required = ClaimArguments.required
    let private invalid = ClaimArguments.invalid usage

    let private serve (arguments: string list) =
        match required "--socket" arguments, required "--policy" arguments, required "--contract-digest" arguments with
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message -> invalid message
        | Ok socket, Ok policy, Ok digest ->
            match FileTrustedVerifier.serve socket policy digest (value "--key-file" arguments) (printfn "%s") with
            | Ok() -> 0
            | Error message ->
                eprintfn "ERROR %s" message
                1

    let private runOnce (arguments: string list) =
        let request =
            required "--repo" arguments
            |> Result.bind (fun repo ->
                required "--commit" arguments
                |> Result.bind (fun commit ->
                    required "--work-item" arguments
                    |> Result.bind (fun id ->
                        required "--claimant" arguments
                        |> Result.bind (fun claimant ->
                            match ClaimArguments.number "--generation" Int64.TryParse arguments with
                            | Ok(Some generation) ->
                                Ok
                                    { RepoPath = repo
                                      WorkItemId = id
                                      Claimant = claimant
                                      FencingGeneration = generation
                                      Commit = commit
                                      Remote = if ClaimArguments.optionValue "--store" arguments = Some "git" then Some(ClaimArguments.optionValue "--remote" arguments |> Option.defaultValue "origin") else None }
                            | Ok None -> Error "--generation is required"
                            | Error message -> Error message))))

        match request, required "--policy" arguments, required "--contract-digest" arguments, required "--key-file" arguments, required "--out" arguments with
        | Error message, _, _, _, _
        | _, Error message, _, _, _
        | _, _, Error message, _, _
        | _, _, _, Error message, _
        | _, _, _, _, Error message -> invalid message
        | Ok request, Ok policy, Ok digest, Ok key, Ok out ->
            VerificationJson.verifierRun (FileTrustedVerifier.runOnce request policy digest key out) out |> ClaimArguments.print

    let verify root (arguments: string list) =
        match required "--id" arguments, required "--token" arguments, ClaimArguments.seconds "--timeout-seconds" 3600.0 arguments, ClaimArguments.storeChoice arguments with
        | Error message, _, _, _
        | _, Error message, _, _
        | _, _, Error message, _
        | _, _, _, Error message -> invalid message
        | Ok id, Ok token, Ok timeout, Ok choice ->
            let ports = FileWorkVerification.ports root choice (FileWorkVerification.environmentSocket ()) timeout
            WorkVerification.run ports id token |> VerificationJson.verify |> ClaimArguments.print

    let migrate root (arguments: string list) =
        FileWorkClaimStore.migrate root (List.contains "--keep-legacy" arguments) |> VerificationJson.migration |> ClaimArguments.print

    /// `verifier serve|run`.
    let run (verb: string) (arguments: string list) =
        match verb with
        | "serve" -> serve arguments
        | "run" -> runOnce arguments
        | other -> invalid $"unknown verifier command '{other}'"
