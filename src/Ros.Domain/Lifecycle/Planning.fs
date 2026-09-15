namespace Ros.Domain.Lifecycle

/// Everything inspection learned about the target repository, as pure data.
/// The planner never touches a filesystem, so every planning decision is
/// reproducible from this value alone.
type ObservedRepository =
    { /// Repository-relative path -> SHA-256 of the file's current bytes.
      /// A path absent from this map is a file absent from the repository.
      Files: Map<string, string>
      /// Repository-relative directories that exist.
      Directories: Set<string>
      /// `.echelon/<tool>.json`, when it was present and parseable.
      Manifest: InstallationManifest option
      /// Why `.echelon/<tool>.json` could not be used, when it existed but
      /// could not be read or is from an unsupported schema.
      ManifestProblem: InstallationProblem option
      /// The legacy `.ros/installation.json` snapshot written by
      /// `ros-bootstrap init` before this manifest existed.
      LegacyManifestPresent: bool
      /// `ros.json`, the repository's own configuration.
      ConfigurationPresent: bool
      ConfigurationProblem: InstallationProblem option }

[<RequireQualifiedAccess>]
module ObservedRepository =
    let empty =
        { Files = Map.empty
          Directories = Set.empty
          Manifest = None
          ManifestProblem = None
          LegacyManifestPresent = false
          ConfigurationPresent = false
          ConfigurationProblem = None }

/// A calculated initialization or upgrade, together with the manifest the
/// repository would carry once it is executed.
type InstallationPlan =
    { Plan: Plan
      Manifest: InstallationManifest
      /// The migration steps this plan runs, in order. Empty for a plain
      /// `init`.
      Steps: MigrationStep list }

[<RequireQualifiedAccess>]
module Planning =
    [<Literal>]
    let ManifestSchemaVersion = 1

    [<Literal>]
    let ManifestPath = ".echelon/ros.json"

    [<Literal>]
    let ManifestDirectory = ".echelon"

    [<Literal>]
    let ConfigurationPath = "ros.json"

    [<Literal>]
    let LegacyManifestPath = ".ros/installation.json"

    let private recorded (observed: ObservedRepository) =
        match observed.Manifest with
        | None -> Map.empty
        | Some manifest -> manifest.ManagedArtifacts |> List.map (fun a -> a.Path, a) |> Map.ofList

    /// The single per-file rule that decides what the tool may do, given the
    /// file's ownership, what is on disk, and what the last installation
    /// recorded. Every ownership decision in this tool goes through here.
    let private reconcile
        (recordedArtifacts: Map<string, RecordedArtifact>)
        (observed: ObservedRepository)
        (entry: PayloadEntry)
        : Choice<PlannedChange, Conflict, string> =
        // Choice1: a change to make. Choice2: a conflict. Choice3: a path left
        // untouched on purpose (already correct, or deliberately preserved).
        match Map.tryFind entry.Path observed.Files with
        | None -> Choice1Of3(PlannedChange.CreateFile(entry.Path, entry.Ownership, entry.Sha256))
        | Some diskSha when diskSha = entry.Sha256 -> Choice3Of3 entry.Path
        | Some diskSha ->
            match entry.Ownership with
            // The repository owns these outright; a divergence is the normal
            // case, not a problem to repair.
            | Ownership.UserOwned -> Choice3Of3 entry.Path
            // Seeded once, then edited by the repository. Only a migration
            // that explicitly targets the file may rewrite it.
            | Ownership.Shared -> Choice3Of3 entry.Path
            // Seeded once. Its real content is derived from the repository's
            // own artifacts by `ros registry build`, so copying the package's
            // empty seed over a populated registry would destroy data.
            | Ownership.Generated -> Choice3Of3 entry.Path
            | Ownership.ToolOwned ->
                match Map.tryFind entry.Path recordedArtifacts with
                | Some artifact when artifact.Sha256 = diskSha ->
                    // Untouched since it was installed: the tool's own
                    // version rule applies and it is replaced.
                    Choice1Of3(PlannedChange.UpdateManagedFile(entry.Path, diskSha, entry.Sha256))
                | Some _ -> Choice2Of3(Conflict.LocallyModifiedToolFile entry.Path)
                | None -> Choice2Of3(Conflict.UnmanagedFileInTheWay entry.Path)

    /// What the manifest would record for one payload entry once the plan has
    /// run. A preserved file is recorded at the hash it actually has, so that
    /// a later `verify` never reports drift the tool itself chose to accept.
    let private artifactRecord (observed: ObservedRepository) (entry: PayloadEntry) : RecordedArtifact =
        let sha =
            if Ownership.integrityChecked entry.Ownership then
                entry.Sha256
            else
                Map.tryFind entry.Path observed.Files |> Option.defaultValue entry.Sha256

        { Path = entry.Path
          Ownership = entry.Ownership
          Sha256 = sha }

    let private manifestFor (profile: string) (packageName: string) (version: string) (artifacts: RecordedArtifact list) =
        { SchemaVersion = ManifestSchemaVersion
          Tool = "ros"
          Package = packageName
          InstalledVersion = version
          ConfigurationVersion = Migration.CurrentConfigurationVersion
          Profile = profile
          ManagedArtifacts = artifacts |> List.sortWith (fun a b -> System.String.CompareOrdinal(a.Path, b.Path)) }

    let private directoryChanges (observed: ObservedRepository) (entries: PayloadEntry list) =
        let required =
            ManifestDirectory :: (entries |> List.choose (fun entry ->
                match entry.Path.LastIndexOf '/' with
                | -1 -> None
                | index -> Some(entry.Path.Substring(0, index))))
            |> List.distinct
            |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))

        required
        |> List.filter (fun directory -> not (observed.Directories.Contains directory))
        |> List.map PlannedChange.CreateDirectory

    let private integrationChanges (entries: PayloadEntry list) (changed: Set<string>) =
        entries
        |> List.choose (fun entry ->
            match entry.Integration with
            | Some name when changed.Contains entry.Path -> Some(PlannedChange.RegisterIntegration(name, entry.Path))
            | _ -> None)

    /// Calculate the transition from the observed repository to the state the
    /// payload describes. Used by both `init` and `upgrade`; the only
    /// difference between them is the migration steps prepended here.
    let private calculate
        (profile: string)
        (packageName: string)
        (version: string)
        (payload: PayloadEntry list)
        (observed: ObservedRepository)
        (steps: MigrationStep list)
        : InstallationPlan =
        let recordedArtifacts = recorded observed
        let outcomes = payload |> List.map (fun entry -> entry, reconcile recordedArtifacts observed entry)

        let fileChanges =
            outcomes |> List.choose (fun (_, outcome) -> match outcome with Choice1Of3 change -> Some change | _ -> None)

        let conflicts =
            outcomes |> List.choose (fun (_, outcome) -> match outcome with Choice2Of3 conflict -> Some conflict | _ -> None)

        let preserved =
            outcomes |> List.choose (fun (_, outcome) -> match outcome with Choice3Of3 path -> Some path | _ -> None)

        let changedPaths = fileChanges |> List.choose PlannedChange.path |> Set.ofList

        let manifest =
            payload |> List.map (artifactRecord observed) |> manifestFor profile packageName version

        let manifestChange =
            if observed.Manifest = Some manifest then
                []
            else
                let description =
                    $"record {packageName}@{version} (configuration version {Migration.CurrentConfigurationVersion}) and {manifest.ManagedArtifacts.Length} managed artifact(s)"

                [ PlannedChange.UpdateConfiguration(ManifestPath, description) ]

        let migrationChanges =
            steps
            |> List.map (fun step -> PlannedChange.RunMigration(step.FromVersion, step.ToVersion, step.Description))

        let migrationConflicts =
            steps
            |> List.choose (fun step ->
                match step.FromVersion, step.ToVersion with
                | 0, 1 ->
                    if observed.LegacyManifestPresent || observed.Manifest.IsSome then
                        None
                    else
                        Some(
                            Conflict.MigrationPreconditionFailed(
                                step.FromVersion,
                                step.ToVersion,
                                $"no {LegacyManifestPath} and no {ManifestPath} were found, so there is no installation to migrate"
                            )
                        )
                | fromVersion, toVersion ->
                    Some(
                        Conflict.MigrationPreconditionFailed(
                            fromVersion,
                            toVersion,
                            "this CLI has no implementation for the declared migration step"
                        )
                    ))

        { Plan =
            { Changes =
                migrationChanges
                @ directoryChanges observed payload
                @ fileChanges
                @ integrationChanges payload changedPaths
                @ manifestChange
              Conflicts = migrationConflicts @ conflicts
              Preserved = preserved }
          Manifest = manifest
          Steps = steps }

    /// `init`: bring the repository into a valid installed state for this
    /// capability, whatever state it starts in.
    let initialize profile packageName version payload observed =
        calculate profile packageName version payload observed []

    /// `upgrade`: the same reconciliation, preceded by the ordered migration
    /// steps between the installed configuration version and this CLI's.
    let upgrade profile packageName version payload observed steps =
        calculate profile packageName version payload observed steps

    /// The installation's state, derived purely from inspection plus the
    /// version this CLI would install. Never mutates, never plans.
    let installationState (availableVersion: string) (observed: ObservedRepository) : InstallationState =
        let problems =
            [ match observed.ManifestProblem with
              | Some problem -> yield problem
              | None -> ()

              match observed.ConfigurationProblem with
              | Some problem -> yield problem
              | None -> ()

              match observed.Manifest with
              | None -> ()
              | Some manifest ->
                  for artifact in manifest.ManagedArtifacts do
                      if Ownership.integrityChecked artifact.Ownership then
                          match Map.tryFind artifact.Path observed.Files with
                          | None -> yield InstallationProblem.ManagedArtifactMissing artifact.Path
                          | Some sha when sha <> artifact.Sha256 ->
                              yield InstallationProblem.ManagedArtifactModified artifact.Path
                          | Some _ -> () ]

        // "Not installed" is decided before "invalid": an empty repository is
        // missing its configuration precisely because nothing installed it,
        // and reporting that as an invalid installation would misdirect.
        match problems, observed.Manifest with
        | _, None when not observed.LegacyManifestPresent -> InstallationState.NotInstalled
        | _ :: _, _ -> InstallationState.Invalid problems
        | [], None ->
            // A legacy install: real, recorded only in .ros/installation.json,
            // and one migration away from current.
            InstallationState.UpgradeRequired(InstalledVersion "legacy", AvailableVersion availableVersion)
        | [], Some manifest when
            manifest.InstalledVersion <> availableVersion
            || manifest.ConfigurationVersion <> Migration.CurrentConfigurationVersion
            ->
            InstallationState.UpgradeRequired(InstalledVersion manifest.InstalledVersion, AvailableVersion availableVersion)
        | [], Some manifest -> InstallationState.Installed(InstalledVersion manifest.InstalledVersion)

/// `verify` and `doctor` answer the same question at different depths, so they
/// share one diagnosis calculation: `verify` reports the failures, `doctor`
/// reports everything with the reason and the remedy.
[<RequireQualifiedAccess>]
module Diagnostics =
    let private diagnosis severity code message path remedy =
        { Severity = severity
          Code = code
          Message = message
          Path = path
          Remedy = remedy }

    let private fromProblem (problem: InstallationProblem) =
        diagnosis
            Severity.Error
            (InstallationProblem.code problem)
            (InstallationProblem.message problem)
            (InstallationProblem.path problem)
            (Some(InstallationProblem.remedy problem))

    /// Every diagnosis the repository's observed state supports, most severe
    /// first. `payload` is `None` only when this CLI has no scaffold at all --
    /// neither a directory nor the copy compiled into it -- which limits what
    /// can be checked but is not itself an error.
    let inspect
        (availableVersion: string)
        (payload: PayloadEntry list option)
        (observed: ObservedRepository)
        : Diagnosis list =
        let state = Planning.installationState availableVersion observed

        let stateDiagnoses =
            match state with
            | InstallationState.Invalid problems -> problems |> List.map fromProblem
            | InstallationState.NotInstalled ->
                [ diagnosis
                      Severity.Error
                      "not-installed"
                      $"no installation manifest at {Planning.ManifestPath} and no legacy {Planning.LegacyManifestPath}"
                      (Some Planning.ManifestPath)
                      (Some "Run 'ros init' to install this capability into the repository.") ]
            | InstallationState.UpgradeRequired(InstalledVersion installed, AvailableVersion available) ->
                [ diagnosis
                      Severity.Warning
                      "upgrade-available"
                      $"installed version {installed} differs from this CLI's {available}"
                      None
                      (Some "Run 'ros upgrade' (add --dry-run first to see the plan).") ]
            | InstallationState.Installed _ -> []

        let legacyDiagnoses =
            if observed.LegacyManifestPresent && observed.Manifest.IsNone then
                [ diagnosis
                      Severity.Warning
                      "legacy-installation"
                      $"this repository was installed by 'ros-bootstrap init' and has no {Planning.ManifestPath} yet"
                      (Some Planning.LegacyManifestPath)
                      (Some "Run 'ros upgrade' to adopt the installation manifest; the legacy snapshot is left in place.") ]
            else
                []

        let seededDiagnoses =
            match observed.Manifest with
            | None -> []
            | Some manifest ->
                manifest.ManagedArtifacts
                |> List.filter (fun artifact -> not (Ownership.integrityChecked artifact.Ownership))
                |> List.choose (fun artifact ->
                    match Map.tryFind artifact.Path observed.Files with
                    | None ->
                        Some(
                            diagnosis
                                Severity.Warning
                                "seeded-file-missing"
                                $"{artifact.Path}: {Ownership.toString artifact.Ownership} file the installation seeded is gone"
                                (Some artifact.Path)
                                (Some(Ownership.repairHint artifact.Ownership))
                        )
                    | Some sha when sha <> artifact.Sha256 ->
                        Some(
                            diagnosis
                                Severity.Information
                                "seeded-file-modified"
                                $"{artifact.Path}: {Ownership.toString artifact.Ownership} file has local changes, which the tool preserves"
                                (Some artifact.Path)
                                None
                        )
                    | Some _ -> None)

        let payloadDiagnoses =
            match payload with
            | None ->
                [ diagnosis
                      Severity.Information
                      "payload-unavailable"
                      "this CLI has no scaffold available, so init and upgrade are unavailable here"
                      None
                      (Some "This build shipped without its embedded scaffold; reinstall the package, or pass --package-root at a checkout.") ]
            | Some entries ->
                let planned =
                    Planning.initialize
                        (observed.Manifest |> Option.map (fun m -> m.Profile) |> Option.defaultValue "greenfield")
                        (observed.Manifest |> Option.map (fun m -> m.Package) |> Option.defaultValue "")
                        availableVersion
                        entries
                        observed

                // A path already reported as a manifest-integrity problem
                // needs no second, differently-worded finding for the same
                // fact; the state diagnosis above is the specific one.
                let alreadyReported =
                    stateDiagnoses |> List.choose (fun item -> item.Path) |> Set.ofList

                planned.Plan.Conflicts
                |> List.filter (fun conflict ->
                    match Conflict.path conflict with
                    | Some path -> not (alreadyReported.Contains path)
                    | None -> true)
                |> List.map (fun conflict ->
                    diagnosis
                        Severity.Error
                        (Conflict.code conflict)
                        (Conflict.message conflict)
                        (Conflict.path conflict)
                        (Some(Conflict.remedy conflict)))

        let severityRank =
            function
            | Severity.Error -> 0
            | Severity.Warning -> 1
            | Severity.Information -> 2

        stateDiagnoses @ legacyDiagnoses @ seededDiagnoses @ payloadDiagnoses
        |> List.distinct
        |> List.sortBy (fun item -> severityRank item.Severity, item.Code, item.Path)

    let errors diagnoses =
        diagnoses |> List.filter (fun item -> item.Severity = Severity.Error)

    let warnings diagnoses =
        diagnoses |> List.filter (fun item -> item.Severity = Severity.Warning)

    /// `verify` fails on any error; `verify --strict` also fails on any
    /// warning. Strictness never invents new checks, so a strict pass always
    /// implies a non-strict pass.
    let verificationFailures strict diagnoses =
        if strict then
            diagnoses |> List.filter (fun item -> item.Severity <> Severity.Information)
        else
            errors diagnoses
