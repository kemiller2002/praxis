namespace Ros.ProjectAdministration

open EchelonFoundry.Ros.Integration

/// Central's own organizational model:
/// `Organization -> Project -> RepositoryAssignment`, per
/// docs/migrations/central-integration/MIGRATION-PLAN.md Phase 3.
/// Identifiers are the same nominal types `Ros.Integration` already
/// defines for the wire contract -- reused rather than reinvented, per
/// docs/migrations/central-integration/INTEGRATION-CONTRACT-STANDARD.md.
type Organization = { Id: OrganizationId; Name: string }
type Project = { Id: ProjectId; OrganizationId: OrganizationId; Name: string }

/// A repository's membership in a project, keyed by the repository's
/// immutable GitHub id -- never its owner/name, which can be renamed or
/// transferred without the repository's identity actually changing.
type RepositoryAssignment = { RepositoryId: RepositoryId; ProjectId: ProjectId }

/// The organizational map Central is the authority for. A plain,
/// persistence-free snapshot plus pure decision functions over it --
/// reading and writing it durably is `Ros.Persistence`'s job, not this
/// module's (Tier 1/2 semantic model and state transition, kept apart
/// from Tier 4 effects, per .sde/architecture/FOUR-TIER-ARCHITECTURE.md).
type ProjectAdministrationState =
    { Organizations: Organization list
      Projects: Project list
      RepositoryAssignments: RepositoryAssignment list }

[<RequireQualifiedAccess>]
module ProjectAdministrationState =
    let empty =
        { Organizations = []
          Projects = []
          RepositoryAssignments = [] }

    let projectFor (repositoryId: RepositoryId) (state: ProjectAdministrationState) : Project option =
        state.RepositoryAssignments
        |> List.tryFind (fun assignment -> assignment.RepositoryId = repositoryId)
        |> Option.bind (fun assignment -> state.Projects |> List.tryFind (fun project -> project.Id = assignment.ProjectId))

    let organizationFor (projectId: ProjectId) (state: ProjectAdministrationState) : Organization option =
        state.Projects
        |> List.tryFind (fun project -> project.Id = projectId)
        |> Option.bind (fun project -> state.Organizations |> List.tryFind (fun org -> org.Id = project.OrganizationId))

    /// Registers an organization. Fails on a duplicate id rather than
    /// silently replacing it -- Central is the authority for this
    /// relationship, so a second registration under the same id is a
    /// real caller error.
    let addOrganization (organization: Organization) (state: ProjectAdministrationState) : Result<ProjectAdministrationState, string> =
        if state.Organizations |> List.exists (fun existing -> existing.Id = organization.Id) then
            Error $"organization '{OrganizationId.value organization.Id}' is already registered"
        else
            Ok { state with Organizations = organization :: state.Organizations }

    /// Registers a project under an already-known organization. Fails
    /// rather than silently creating the organization.
    let addProject (project: Project) (state: ProjectAdministrationState) : Result<ProjectAdministrationState, string> =
        if state.Organizations |> List.exists (fun org -> org.Id = project.OrganizationId) |> not then
            Error $"organization '{OrganizationId.value project.OrganizationId}' is not registered"
        elif state.Projects |> List.exists (fun existing -> existing.Id = project.Id) then
            Error $"project '{ProjectId.value project.Id}' is already registered"
        else
            Ok { state with Projects = project :: state.Projects }

    /// Assigns a repository to a project, keyed by the repository's
    /// immutable id. Reassigning an already-assigned repository moves
    /// it -- a repository belongs to exactly one project at a time.
    let assignRepository (assignment: RepositoryAssignment) (state: ProjectAdministrationState) : Result<ProjectAdministrationState, string> =
        if state.Projects |> List.exists (fun project -> project.Id = assignment.ProjectId) |> not then
            Error $"project '{ProjectId.value assignment.ProjectId}' is not registered"
        else
            let withoutPriorAssignment =
                state.RepositoryAssignments
                |> List.filter (fun existing -> existing.RepositoryId <> assignment.RepositoryId)

            Ok { state with RepositoryAssignments = assignment :: withoutPriorAssignment }
