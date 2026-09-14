namespace Ros.Integrations.GitHub

open System
open EchelonFoundry.Ros.Integration

/// **Not** the real `Chrona.Integration` package -- Chrona has not
/// published one yet (WI-16 is still an open proposal; see
/// docs/migrations/central-integration/CHRONA-INTEGRATION-REQUIREMENTS-PROPOSAL.md).
/// This is a local placeholder, shaped like that proposal's
/// `TimeObservationCandidate`, that exists only to prove the delivery
/// mechanism in `GitHubDatastoreWriter` and `LocalGitSimulation`
/// mechanically ahead of Chrona's real contract existing (the migration
/// spec explicitly allows this: "ROS can collect activities with Chrona
/// delivery state NotReady until the adapter is installed" -- see
/// `Ros.ProjectAdministration.IntegrationTarget`/`OutboundDeliveryState`).
///
/// The moment `EchelonFoundry.Chrona.Integration` is published, this
/// whole module is deleted and replaced by a reference to that real
/// package. Per `AGENTS.md`'s "Compatibility Before Replacement" rule,
/// that swap gets its own characterization and parallel run -- it is
/// not a silent one-line change just because the shapes happen to
/// match.
type TimeObservationCandidate =
    { CandidateId: string // the originating ROS ActivityId, by convention
      OrganizationId: string
      ProjectId: string
      ActorId: string option
      StartedAt: DateTimeOffset option
      EndedAt: DateTimeOffset option
      Description: string option }

[<RequireQualifiedAccess>]
module TimeObservationCandidate =
    /// Maps ROS's own `ActivityObservation` to the placeholder candidate
    /// shape. This is the one piece of real mapping logic in this
    /// module -- everything else here is temporary scaffolding for the
    /// swap described above.
    let fromActivity (activity: ActivityObservation) : TimeObservationCandidate =
        { CandidateId = ActivityId.value activity.ActivityId
          OrganizationId = OrganizationId.value activity.OrganizationId
          ProjectId = ProjectId.value activity.ProjectId
          ActorId = activity.ActorId |> Option.map ActorId.value
          StartedAt = activity.StartedAt
          EndedAt = activity.EndedAt
          Description = activity.Description }
