namespace Praxis.Cli

open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Infrastructure.Work

/// `praxis work finish` (REL-TOKEN-003): `work verify` at HEAD, then, when
/// every receipt passed, `work complete-verified` with the same derivation,
/// in one command. Takes `work complete-verified`'s flags (each must equal
/// its derived value) plus `--verify-timeout-seconds` (verify's
/// `--timeout-seconds`) and `--json`. Exit codes: 0 verified or replayed,
/// 3 refused or not passed, 2 invalid arguments, 1 storage failure.
[<RequireQualifiedAccess>]
module WorkFinishCommands =
    let usage =
        "work finish --id ID --token TOKEN [--json] [--excerpt-lines N] [--verify-timeout-seconds N] [--authority FILE] [--ordo-contract FILE] "
        + "[--timeout-seconds N] [--store local|git] [--remote NAME] [any explicit work complete-verified flag]"

    let private invalid = ClaimArguments.invalid usage

    let private excerptLines arguments =
        ClaimArguments.number "--excerpt-lines" System.Int32.TryParse arguments
        |> Result.bind (fun value ->
            match value with
            | Some lines when lines < 1 -> Error "--excerpt-lines must be positive"
            | value -> Ok(value |> Option.defaultValue WorkFinish.ExcerptLines))

    let run root (actor: Actor) choice (arguments: string list) =
        match WorkCompletionCommands.parse arguments, ClaimArguments.seconds "--verify-timeout-seconds" 3600.0 arguments, excerptLines arguments with
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message -> invalid message
        | Ok parsed, _, _ when not (FileWorkFinish.authorityConfigured parsed) ->
            invalid $"--authority FILE (or {FileDerivedCompletion.AuthorityVariable}) is required"
        | Ok parsed, Ok timeout, Ok lines ->
            let ports = FileWorkVerification.ports root choice (FileWorkVerification.environmentSocket ()) timeout
            let outcome = FileWorkFinish.run root choice (Some actor) ports None parsed

            if List.contains "--json" arguments then
                WorkFinishOutput.json parsed.WorkItemId lines outcome |> ClaimArguments.print
            else
                WorkFinishOutput.text parsed.WorkItemId lines outcome |> ClaimArguments.print
