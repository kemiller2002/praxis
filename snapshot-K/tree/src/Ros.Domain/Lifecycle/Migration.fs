namespace Ros.Domain.Lifecycle

/// One supported configuration-version transition. Upgrades are expressed as
/// a chain of these rather than as arbitrary N-to-N jumps, so that
/// `0 -> 2` is always `0 -> 1` followed by `1 -> 2`, each with its own
/// precondition.
type MigrationStep =
    { FromVersion: int
      ToVersion: int
      Description: string }

[<RequireQualifiedAccess>]
module Migration =
    /// The configuration version this CLI writes. Bump it, and add the
    /// matching step below, whenever the installed layout changes in a way an
    /// older installation must be migrated through.
    [<Literal>]
    let CurrentConfigurationVersion = 1

    /// Configuration version 0 is the implied version of an installation made
    /// before `.echelon/<tool>.json` existed: one that recorded its state only
    /// in the legacy `.ros/installation.json` snapshot written by
    /// `ros-bootstrap init`.
    [<Literal>]
    let LegacyConfigurationVersion = 0

    let known: MigrationStep list =
        [ { FromVersion = LegacyConfigurationVersion
            ToVersion = 1
            Description =
              "Adopt the .echelon/ros.json installation manifest; a legacy install recorded its state only in .ros/installation.json." } ]

    /// The ordered steps from an installed configuration version to the
    /// current one. An installation already at the current version needs no
    /// steps; one ahead of this CLI, or one with a gap in the chain, is an
    /// error rather than a silent no-op.
    let path (fromVersion: int) : Result<MigrationStep list, string> =
        let rec walk acc version =
            if version = CurrentConfigurationVersion then
                Ok(List.rev acc)
            elif version > CurrentConfigurationVersion then
                Error
                    $"installed configuration version {version} is newer than this CLI supports ({CurrentConfigurationVersion}); upgrade the CLI instead"
            else
                match known |> List.tryFind (fun step -> step.FromVersion = version && step.ToVersion > version) with
                | None -> Error $"no migration is defined from configuration version {version}"
                | Some step -> walk (step :: acc) step.ToVersion

        if fromVersion < 0 then
            Error $"configuration version {fromVersion} is not a valid version"
        else
            walk [] fromVersion
