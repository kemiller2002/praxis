namespace Ros.Domain.Lifecycle

open System

/// Ownership classification carried by every file this tool manages.
///
/// The classification -- not the fact that `init` once created a file --
/// decides what the tool may later do to it. See `docs/installation.md`.
[<RequireQualifiedAccess>]
type Ownership =
    /// Controlled by the tool. Replaced when it is byte-identical to what the
    /// installation recorded; a locally modified copy blocks instead.
    | ToolOwned
    /// Derived from authoritative inputs already in the repository. The
    /// installer seeds it once; after that only the generator that owns it
    /// (`ros registry build`) rewrites it, never a copy from the package.
    | Generated
    /// Controlled by the repository. Seeded once if absent, never rewritten.
    | UserOwned
    /// Seeded by the tool and then edited by the repository. Only a declared
    /// migration may change it, and only when it is unmodified.
    | Shared

[<RequireQualifiedAccess>]
module Ownership =
    let toString =
        function
        | Ownership.ToolOwned -> "tool-owned"
        | Ownership.Generated -> "generated"
        | Ownership.UserOwned -> "user-owned"
        | Ownership.Shared -> "shared"

    let parse (value: string) =
        match value with
        | "tool-owned" -> Some Ownership.ToolOwned
        | "generated" -> Some Ownership.Generated
        | "user-owned" -> Some Ownership.UserOwned
        | "shared" -> Some Ownership.Shared
        | _ -> None

    /// Ownerships whose integrity `verify` asserts against the recorded
    /// installation. Generated, shared and user-owned files are all expected
    /// to diverge from what was installed: the repository, or a generator the
    /// repository runs, is what makes them current.
    let integrityChecked =
        function
        | Ownership.ToolOwned -> true
        | Ownership.Generated
        | Ownership.UserOwned
        | Ownership.Shared -> false

    /// How a file of this ownership gets back to a correct state when it has
    /// drifted. Used by `doctor` so a remedy names the right command.
    let repairHint =
        function
        | Ownership.ToolOwned -> "Run 'ros init' to restore it from the package."
        | Ownership.Generated -> "Run 'ros registry build' to regenerate it from the repository's own artifacts."
        | Ownership.UserOwned -> "This file belongs to the repository; restore it from version control if it was lost."
        | Ownership.Shared -> "Run 'ros init' to seed it again, or restore your edited copy from version control."

/// One file the payload (this package's own scaffold) wants present in the
/// target repository, already rendered and hashed.
type PayloadEntry =
    { Path: string
      Ownership: Ownership
      Sha256: string
      Executable: bool
      /// Named when installing this file also registers an integration with
      /// something outside the repository (today: a CI workflow).
      Integration: string option }

/// One file a previous installation recorded in `.echelon/ros.json`.
type RecordedArtifact =
    { Path: string
      Ownership: Ownership
      Sha256: string }

/// `.echelon/<tool>.json` -- the machine-readable installation record. The
/// presence of arbitrary files is never the source of truth; this is.
/// Deliberately carries no timestamp, no absolute path, no machine identity,
/// and no secret, so a re-`init` of an unchanged repository is a genuine
/// no-op.
type InstallationManifest =
    { SchemaVersion: int
      Tool: string
      Package: string
      InstalledVersion: string
      ConfigurationVersion: int
      Profile: string
      ManagedArtifacts: RecordedArtifact list }

type InstalledVersion = InstalledVersion of string
type AvailableVersion = AvailableVersion of string

[<RequireQualifiedAccess>]
type InstallationProblem =
    | ManifestUnreadable of detail: string
    | ManifestSchemaUnsupported of found: int * supported: int
    | ConfigurationVersionUnsupported of found: int * supported: int
    | ManagedArtifactMissing of path: string
    | ManagedArtifactModified of path: string
    | ConfigurationMissing of path: string
    | ConfigurationUnreadable of path: string * detail: string

[<RequireQualifiedAccess>]
module InstallationProblem =
    let message =
        function
        | InstallationProblem.ManifestUnreadable detail -> $"installation manifest is unreadable: {detail}"
        | InstallationProblem.ManifestSchemaUnsupported(found, supported) ->
            $"installation manifest schema version {found} is not supported by this CLI (supports {supported})"
        | InstallationProblem.ConfigurationVersionUnsupported(found, supported) ->
            $"installed configuration version {found} is newer than this CLI supports ({supported})"
        // The path travels in the diagnosis's own Path field, so it is not
        // repeated here; renderers prefix it once.
        | InstallationProblem.ManagedArtifactMissing _ -> "managed artifact is missing"
        | InstallationProblem.ManagedArtifactModified _ -> "managed artifact no longer matches the installed snapshot"
        | InstallationProblem.ConfigurationMissing _ -> "required configuration is missing"
        | InstallationProblem.ConfigurationUnreadable(_, detail) -> $"configuration is unreadable: {detail}"

    let code =
        function
        | InstallationProblem.ManifestUnreadable _ -> "manifest-unreadable"
        | InstallationProblem.ManifestSchemaUnsupported _ -> "manifest-schema-unsupported"
        | InstallationProblem.ConfigurationVersionUnsupported _ -> "configuration-version-unsupported"
        | InstallationProblem.ManagedArtifactMissing _ -> "managed-artifact-missing"
        | InstallationProblem.ManagedArtifactModified _ -> "managed-artifact-modified"
        | InstallationProblem.ConfigurationMissing _ -> "configuration-missing"
        | InstallationProblem.ConfigurationUnreadable _ -> "configuration-unreadable"

    let path =
        function
        | InstallationProblem.ManagedArtifactMissing path
        | InstallationProblem.ManagedArtifactModified path
        | InstallationProblem.ConfigurationMissing path
        | InstallationProblem.ConfigurationUnreadable(path, _) -> Some path
        | _ -> None

    let remedy =
        function
        | InstallationProblem.ManifestUnreadable _
        | InstallationProblem.ManifestSchemaUnsupported _ ->
            "Restore .echelon/ros.json from version control, or re-run init in a clean checkout."
        | InstallationProblem.ConfigurationVersionUnsupported _ ->
            "Upgrade this CLI: the repository was installed by a newer release than the one running."
        | InstallationProblem.ManagedArtifactMissing _ -> "Run 'ros init' to restore the missing tool-owned artifact."
        | InstallationProblem.ManagedArtifactModified _ ->
            "Revert the local edit, or move the change into a user-owned file; tool-owned artifacts are replaced on upgrade."
        | InstallationProblem.ConfigurationMissing _ -> "Run 'ros init' to create the missing configuration."
        | InstallationProblem.ConfigurationUnreadable _ -> "Repair the malformed JSON, then re-run 'ros verify'."

/// The installation's state, derived only from inspection. Nothing here
/// mutates the repository.
[<RequireQualifiedAccess>]
type InstallationState =
    | NotInstalled
    | Installed of InstalledVersion
    | UpgradeRequired of InstalledVersion * AvailableVersion
    | Invalid of InstallationProblem list

[<RequireQualifiedAccess>]
module InstallationState =
    let toString =
        function
        | InstallationState.NotInstalled -> "not-installed"
        | InstallationState.Installed _ -> "installed"
        | InstallationState.UpgradeRequired _ -> "upgrade-required"
        | InstallationState.Invalid _ -> "invalid"

    let installedVersion =
        function
        | InstallationState.Installed(InstalledVersion version)
        | InstallationState.UpgradeRequired(InstalledVersion version, _) -> Some version
        | InstallationState.NotInstalled
        | InstallationState.Invalid _ -> None

    let problems =
        function
        | InstallationState.Invalid problems -> problems
        | _ -> []

/// A change the tool intends to make, calculated before anything is written.
/// Planning and execution are deliberately separate types so that inspection
/// can never mutate.
[<RequireQualifiedAccess>]
type PlannedChange =
    | CreateDirectory of path: string
    | CreateFile of path: string * ownership: Ownership * sha256: string
    | UpdateManagedFile of path: string * fromSha256: string * toSha256: string
    | UpdateConfiguration of path: string * description: string
    | RegisterIntegration of name: string * path: string
    | RunMigration of fromVersion: int * toVersion: int * description: string

[<RequireQualifiedAccess>]
module PlannedChange =
    let path =
        function
        | PlannedChange.CreateDirectory path
        | PlannedChange.CreateFile(path, _, _)
        | PlannedChange.UpdateManagedFile(path, _, _)
        | PlannedChange.UpdateConfiguration(path, _)
        | PlannedChange.RegisterIntegration(_, path) -> Some path
        | PlannedChange.RunMigration _ -> None

    let kind =
        function
        | PlannedChange.CreateDirectory _ -> "create-directory"
        | PlannedChange.CreateFile _ -> "create-file"
        | PlannedChange.UpdateManagedFile _ -> "update-managed-file"
        | PlannedChange.UpdateConfiguration _ -> "update-configuration"
        | PlannedChange.RegisterIntegration _ -> "register-integration"
        | PlannedChange.RunMigration _ -> "run-migration"

    let describe =
        function
        | PlannedChange.CreateDirectory path -> $"create directory {path}"
        | PlannedChange.CreateFile(path, ownership, _) -> $"create {path} ({Ownership.toString ownership})"
        | PlannedChange.UpdateManagedFile(path, _, _) -> $"update {path}"
        | PlannedChange.UpdateConfiguration(path, description) -> $"update configuration {path}: {description}"
        | PlannedChange.RegisterIntegration(name, path) -> $"register integration {name} at {path}"
        | PlannedChange.RunMigration(fromVersion, toVersion, description) ->
            $"migrate configuration {fromVersion} -> {toVersion}: {description}"

/// A reason a plan cannot be executed as calculated, or a fact about the
/// repository the caller must see before it is. Blocking conflicts stop
/// execution; non-blocking ones are reported and the plan proceeds.
[<RequireQualifiedAccess>]
type Conflict =
    | LocallyModifiedToolFile of path: string
    | LocallyModifiedSharedFile of path: string
    | UnmanagedFileInTheWay of path: string
    | MigrationPreconditionFailed of fromVersion: int * toVersion: int * reason: string

[<RequireQualifiedAccess>]
module Conflict =
    /// A blocking conflict stops execution before anything is written.
    let isBlocking =
        function
        | Conflict.LocallyModifiedToolFile _
        | Conflict.LocallyModifiedSharedFile _
        | Conflict.UnmanagedFileInTheWay _
        | Conflict.MigrationPreconditionFailed _ -> true

    let code =
        function
        | Conflict.LocallyModifiedToolFile _ -> "locally-modified-tool-file"
        | Conflict.LocallyModifiedSharedFile _ -> "locally-modified-shared-file"
        | Conflict.UnmanagedFileInTheWay _ -> "unmanaged-file-in-the-way"
        | Conflict.MigrationPreconditionFailed _ -> "migration-precondition-failed"

    let path =
        function
        | Conflict.LocallyModifiedToolFile path
        | Conflict.LocallyModifiedSharedFile path
        | Conflict.UnmanagedFileInTheWay path -> Some path
        | Conflict.MigrationPreconditionFailed _ -> None

    // As with InstallationProblem, the path travels in the conflict's own
    // `path` and is prefixed once by the renderer rather than embedded here.
    let message =
        function
        | Conflict.LocallyModifiedToolFile _ ->
            "tool-owned file was modified locally; replacing it would destroy that change"
        | Conflict.LocallyModifiedSharedFile _ ->
            "shared file was modified locally and a migration wants to change it"
        | Conflict.UnmanagedFileInTheWay _ ->
            "a different file already exists here and no installation records it"
        | Conflict.MigrationPreconditionFailed(fromVersion, toVersion, reason) ->
            $"migration {fromVersion} -> {toVersion} precondition failed: {reason}"

    let remedy =
        function
        | Conflict.LocallyModifiedToolFile _ ->
            "Revert the file to its installed content, or move the change into a user-owned file."
        | Conflict.LocallyModifiedSharedFile _ ->
            "Reconcile the local edit with the migration by hand, then re-run the command."
        | Conflict.UnmanagedFileInTheWay _ ->
            "Move or delete the existing file, or keep it and accept that this capability is not installed there."
        | Conflict.MigrationPreconditionFailed _ -> "Resolve the named precondition before upgrading."

/// A calculated transition. `Preserved` names files left untouched on purpose
/// so that "no changes" is never confused with "nothing was inspected".
type Plan =
    { Changes: PlannedChange list
      Conflicts: Conflict list
      Preserved: string list }

[<RequireQualifiedAccess>]
module Plan =
    let empty = { Changes = []; Conflicts = []; Preserved = [] }

    let blockingConflicts plan =
        plan.Conflicts |> List.filter Conflict.isBlocking

    /// Validation gate between "calculate transition" and "execute
    /// transition": a plan with any blocking conflict is never executed.
    let validate plan : Result<Plan, Conflict list> =
        match blockingConflicts plan with
        | [] -> Ok plan
        | conflicts -> Error conflicts

    let isNoOp plan = plan.Changes.IsEmpty

[<RequireQualifiedAccess>]
type Severity =
    | Error
    | Warning
    | Information

[<RequireQualifiedAccess>]
module Severity =
    let toString =
        function
        | Severity.Error -> "error"
        | Severity.Warning -> "warning"
        | Severity.Information -> "information"

/// One `doctor` finding. Unlike a verification failure, a diagnosis always
/// explains why the repository is in this state and, where one exists, how to
/// leave it.
type Diagnosis =
    { Severity: Severity
      Code: string
      Message: string
      Path: string option
      Remedy: string option }

/// Stable process exit semantics. Documented in `docs/cli.md`; changing a
/// value here is a breaking change to the public contract.
[<RequireQualifiedAccess>]
module ExitCode =
    [<Literal>]
    let Success = 0

    [<Literal>]
    let InternalFailure = 1

    [<Literal>]
    let InvalidArguments = 2

    [<Literal>]
    let VerificationFailed = 3

    [<Literal>]
    let IncompatibleInstallation = 4

    [<Literal>]
    let MigrationBlocked = 5

    [<Literal>]
    let PrerequisiteFailure = 6

    [<Literal>]
    let UnsupportedPlatform = 7

    let describe =
        [ Success, "success"
          InternalFailure, "internal failure"
          InvalidArguments, "invalid arguments"
          VerificationFailed, "verification failed"
          IncompatibleInstallation, "incompatible installation"
          MigrationBlocked, "migration blocked"
          PrerequisiteFailure, "prerequisite or environment failure"
          UnsupportedPlatform, "unsupported platform" ]
