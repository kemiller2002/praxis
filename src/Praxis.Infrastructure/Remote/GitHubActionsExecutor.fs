namespace Praxis.Infrastructure.Remote

open Praxis.Domain.Remote

/// The GitHub Actions adapter's view of itself (PRAXIS-REMOTE-02,
/// `DF-ROS-2026-A041` sections 7 and 10). GitHub-specific context enters
/// Praxis only here, at the adapter boundary, and only as *observed
/// executor facts*: the runner's run, attempt, workflow, repository, and
/// the GitHub account that triggered it (the transport principal). None of
/// it is ever used as the identity of the actor whose request the runner
/// executes.
[<RequireQualifiedAccess>]
module GitHubActionsExecutor =
    [<Literal>]
    let Kind = "github-actions"

    /// `None` outside GitHub Actions. `GITHUB_TRIGGERING_ACTOR` (the account
    /// that started this attempt, which differs from `GITHUB_ACTOR` on a
    /// re-run) is preferred for the principal.
    let observe (variable: string -> string option) (praxisVersion: string) : ExecutorFacts option =
        match variable "GITHUB_ACTIONS" with
        | Some "true" ->
            Some
                { Kind = Kind
                  RunId = variable "GITHUB_RUN_ID"
                  RunAttempt = variable "GITHUB_RUN_ATTEMPT"
                  WorkflowRef = variable "GITHUB_WORKFLOW_REF"
                  Repository = variable "GITHUB_REPOSITORY"
                  Host = variable "RUNNER_ENVIRONMENT"
                  Principal =
                    variable "GITHUB_TRIGGERING_ACTOR"
                    |> Option.orElse (variable "GITHUB_ACTOR")
                    |> Option.map (fun login -> $"github:{login}")
                  PraxisVersion = praxisVersion }
        | _ -> None
