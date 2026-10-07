namespace Praxis.Application.Planning

open Praxis.Domain.Planning

type RepositoryIdentity =
    { Name: string
      Commit: string option
      Branch: string option }

/// Everything the planner reads. Every member is a query: the planning
/// port has no write operation at all, so no planner command can mutate
/// work state, `.ros/`, branches or priorities (PRX-PLAN-001).
type PlanningReadPort =
    { Repository: unit -> RepositoryIdentity
      Queue: unit -> Result<PlanningQueueItem list, string>
      Live: unit -> Result<PlanningLiveItem list, string>
      Executions: unit -> HistoricalExecution list
      /// Evidence the repository itself can show (Git), given the live items
      /// whose checkpoints it should check.
      RepositoryObservations: PlanningLiveItem list -> Observation list
      /// CI status of checkpoints waiting on CI, when the caller opted in
      /// (`--observe-ci`); otherwise no observation (PRX-PLAN-020).
      ContinuousIntegration: PlanningLiveItem list -> Observation list
      /// Evidence a caller supplied (CI, GitHub), if any.
      SuppliedObservations: unit -> Result<Observation list, string>
      Configuration: unit -> Result<PlannerConfiguration, string> }

[<RequireQualifiedAccess>]
module PlanningOperations =
    let private bind (next: 'a -> Result<'b, string>) (result: Result<'a, string>) = Result.bind next result

    /// Assembles one deterministic planning input from the port.
    let gather (port: PlanningReadPort) (plannedAt: string) (plannerVersion: string) : Result<PlanningInput, string> =
        port.Queue()
        |> bind (fun queue ->
            port.Live()
            |> bind (fun live ->
                port.SuppliedObservations()
                |> bind (fun supplied ->
                    port.Configuration()
                    |> Result.map (fun configuration ->
                        let repository = port.Repository()

                        { Repository = repository.Name
                          Commit = repository.Commit
                          Branch = repository.Branch
                          PlannedAt = plannedAt
                          PlannerVersion = plannerVersion
                          Queue = queue
                          Live = live
                          Executions = port.Executions()
                          Observations = port.RepositoryObservations live @ port.ContinuousIntegration live @ supplied
                          Configuration = configuration }))))

    let analyze port plannedAt plannerVersion =
        gather port plannedAt plannerVersion |> Result.map (fun input -> input, Planner.analyze input)
