namespace EchelonFoundry.Ros.Integration

/// Nominal identifier wrappers -- distinct types purely for compile-time
/// safety (an `ActivityId` cannot be passed where a `ProjectId` is
/// expected), never for validation. Whether a given string is *required*
/// or *well-formed* is decided by `ActivityObservation.create`, not by
/// these types: an integration package answers "is this structurally
/// valid?", and structural validity here is scoped to "is this the right
/// kind of string", not "is this non-empty" -- see
/// docs/migrations/central-integration/INTEGRATION-CONTRACT-STANDARD.md.
type ContractVersion = private ContractVersion of string
type OrganizationId = private OrganizationId of string
type ProjectId = private ProjectId of string
type RepositoryId = private RepositoryId of string
type WorkItemId = private WorkItemId of string
type ActorId = private ActorId of string
type ActivityId = private ActivityId of string

[<RequireQualifiedAccess>]
module ContractVersion =
    let create (version: string) = ContractVersion version
    let value (ContractVersion version) = version

[<RequireQualifiedAccess>]
module OrganizationId =
    let create (value: string) = OrganizationId value
    let value (OrganizationId value) = value

[<RequireQualifiedAccess>]
module ProjectId =
    let create (value: string) = ProjectId value
    let value (ProjectId value) = value

[<RequireQualifiedAccess>]
module RepositoryId =
    let create (value: string) = RepositoryId value
    let value (RepositoryId value) = value

[<RequireQualifiedAccess>]
module WorkItemId =
    let create (value: string) = WorkItemId value
    let value (WorkItemId value) = value

[<RequireQualifiedAccess>]
module ActorId =
    let create (value: string) = ActorId value
    let value (ActorId value) = value

[<RequireQualifiedAccess>]
module ActivityId =
    let create (value: string) = ActivityId value
    let value (ActivityId value) = value
