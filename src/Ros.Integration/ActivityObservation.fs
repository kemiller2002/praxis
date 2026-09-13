namespace EchelonFoundry.Ros.Integration

open System

/// The validated shape ROS accepts from an external producer describing
/// a unit of observed work activity. Obtainable only through `create` --
/// there is no public record constructor, so an `ActivityObservation`
/// cannot exist without having passed structural validation.
type ActivityObservation =
    { ContractVersion: ContractVersion
      ActivityId: ActivityId
      OrganizationId: OrganizationId
      ProjectId: ProjectId
      RepositoryId: RepositoryId option
      WorkItemId: WorkItemId option
      ActorId: ActorId option
      StartedAt: DateTimeOffset option
      EndedAt: DateTimeOffset option
      Description: string option
      Evidence: Evidence list }

/// The raw, not-yet-validated shape a producer sends. Plain primitives
/// only: a caller cannot construct a nominal identifier type without
/// already knowing it is well-formed, so validation has to start here,
/// one layer below the identifiers themselves.
type ActivityObservationInput =
    { ContractVersion: string
      ActivityId: string
      OrganizationId: string
      ProjectId: string
      RepositoryId: string option
      WorkItemId: string option
      ActorId: string option
      StartedAt: DateTimeOffset option
      EndedAt: DateTimeOffset option
      Description: string option
      Evidence: Evidence list }

/// Structural problems only -- never a statement about whether an
/// otherwise well-formed observation is legal for a project in its
/// current state. That question belongs to ROS's own domain model, not
/// this package. Closed deliberately: a new structural failure mode is a
/// breaking change to this type, exactly as it should be.
type ActivityObservationError =
    | MissingActivityId
    | MissingProjectId
    | EndBeforeStart
    | UnsupportedContractVersion of string

[<RequireQualifiedAccess>]
module ActivityObservation =
    /// Wire `contractVersion` values this package build knows how to
    /// construct an `ActivityObservation` from. Independent of this
    /// package's own NuGet version -- see
    /// docs/migrations/central-integration/INTEGRATION-CONTRACT-STANDARD.md
    /// section 3.
    let supportedContractVersions = set [ "1"; "1.0" ]

    let create (input: ActivityObservationInput) : Result<ActivityObservation, ActivityObservationError list> =
        let errors =
            [ if not (supportedContractVersions.Contains input.ContractVersion) then
                  yield UnsupportedContractVersion input.ContractVersion
              if String.IsNullOrWhiteSpace input.ActivityId then
                  yield MissingActivityId
              if String.IsNullOrWhiteSpace input.ProjectId then
                  yield MissingProjectId
              match input.StartedAt, input.EndedAt with
              | Some started, Some ended when ended < started -> yield EndBeforeStart
              | _ -> () ]

        if not errors.IsEmpty then
            Error errors
        else
            Ok
                { ContractVersion = ContractVersion.create input.ContractVersion
                  ActivityId = ActivityId.create input.ActivityId
                  OrganizationId = OrganizationId.create input.OrganizationId
                  ProjectId = ProjectId.create input.ProjectId
                  RepositoryId = input.RepositoryId |> Option.map RepositoryId.create
                  WorkItemId = input.WorkItemId |> Option.map WorkItemId.create
                  ActorId = input.ActorId |> Option.map ActorId.create
                  StartedAt = input.StartedAt
                  EndedAt = input.EndedAt
                  Description = input.Description
                  Evidence = input.Evidence }
