namespace Praxis.Cli

open System
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Infrastructure.Work

/// `praxis work complete-verified` (PRX-REL-006/007, PRAXIS-REL-009/011/012)
/// and `praxis work completion-identity`. `--id ID --token TOKEN` alone
/// suffices: everything else is derived from the claim store, the receipts
/// (or recorded results), `PRAXIS_COMPLETION_AUTHORITY` and
/// `PRAXIS_ORDO_CONTRACT`. Any explicit flag must equal its derived value.
/// Exit codes: 0 verified (or replayed) and published, 2 invalid
/// arguments, 3 refused, 1 storage failure.
[<RequireQualifiedAccess>]
module WorkCompletionCommands =
    let usage =
        "work complete-verified --id ID --token TOKEN [--authority FILE] [--ordo-contract FILE] [--expected-ordo-digest sha256:HEX] "
        + "[--claimant NAME] [--generation N] [--commit SHA] [--verification-revision SHA] [--ordo-contract-version V] [--ordo-contract-digest sha256:HEX] "
        + "[--requirement ID]* [--decision ID]* [--conflict-resolution ID]* [--validation NAME=passed|failed|unavailable[:EVIDENCE-ID,...]]* "
        + "[--evidence ID=PATH[@sha256:HEX]]* [--telemetry-execution EXE-ID]* [--timeout-seconds N] [--store local|git] [--remote NAME] | "
        + "work completion-identity --id ID [--store local|git] [--remote NAME]"

    let private value = ClaimArguments.optionValue
    let private values = ClaimArguments.optionValues

    let private parse (arguments: string list) : Result<CompletionArguments, string> =
        ClaimArguments.required "--id" arguments
        |> Result.bind (fun id ->
            ClaimArguments.required "--token" arguments
            |> Result.bind (fun token ->
                ClaimArguments.number "--generation" Int64.TryParse arguments
                |> Result.bind (fun generation ->
                    ClaimArguments.seconds "--timeout-seconds" 120.0 arguments
                    |> Result.bind (fun timeout ->
                        ClaimArguments.collect ClaimArguments.validation (values "--validation" arguments)
                        |> Result.bind (fun validations ->
                            ClaimArguments.collect ClaimArguments.evidence (values "--evidence" arguments)
                            |> Result.map (fun evidence ->
                                { WorkItemId = id
                                  Token = token
                                  AuthorityPath = value "--authority" arguments
                                  ContractPath = value "--ordo-contract" arguments
                                  ExpectedOrdoDigest = value "--expected-ordo-digest" arguments
                                  Explicit =
                                    { Claimant = value "--claimant" arguments
                                      Generation = generation
                                      Commit = value "--commit" arguments
                                      VerificationRevision = value "--verification-revision" arguments
                                      OrdoContractVersion = value "--ordo-contract-version" arguments
                                      OrdoContractDigest = value "--ordo-contract-digest" arguments
                                      RequirementIds = values "--requirement" arguments
                                      DecisionIds = values "--decision" arguments
                                      ConflictResolutionIds = values "--conflict-resolution" arguments
                                      Validations = validations
                                      Evidence = []
                                      TelemetryExecutions = values "--telemetry-execution" arguments }
                                  ExplicitEvidence = evidence
                                  Timeout = timeout
                                  Execution = value "--execution" arguments }))))))

    let completeVerified root (actor: Actor) choice (arguments: string list) =
        match parse arguments with
        | Error message -> ClaimArguments.invalid usage message
        | Ok parsed ->
            match FileDerivedCompletion.run root choice (Some actor) None parsed with
            | Error message -> ClaimArguments.invalid usage message
            | Ok report -> VerificationJson.completion report |> ClaimArguments.print

    let completionIdentity root choice (arguments: string list) =
        match ClaimArguments.required "--id" arguments with
        | Error message -> ClaimArguments.invalid usage message
        | Ok id -> CompletionIdentity.check root choice id |> VerificationJson.identity |> ClaimArguments.print
