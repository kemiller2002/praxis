namespace Ros.Application.Lifecycle

open Ros.Domain.Lifecycle

/// What a caller must supply to reach the lifecycle core. Deliberately not a
/// parsed command line: the CLI is one adapter over this API, and ROS, an
/// integration assembly, a test or a future service host can call the same
/// functions without simulating argv.
type LifecycleRequest =
    { /// Repository to act on.
      Root: string
      /// Where this package's own scaffold lives; None lets the core locate
      /// it, and a core that cannot find one still answers every read-only
      /// question.
      PackageRoot: string option
      Profile: string
      /// Display name for a new installation. None derives it from the
      /// repository folder.
      Project: string option }

/// The port the lifecycle core needs from the outside world. Injecting it
/// keeps `Ros.Application` free of a direct `Ros.Infrastructure` reference,
/// which the architecture tests enforce.
type LifecycleEnvironment<'payload> =
    { /// Locate and load the packaged scaffold, or explain why it is absent.
      LoadPayload: LifecycleRequest -> Result<'payload, string> option
      /// Every payload entry, as pure desired-state data.
      PayloadEntries: 'payload -> PayloadEntry list
      PayloadProfile: 'payload -> string
      PayloadPackageName: 'payload -> string
      PayloadVersion: 'payload -> string
      /// Read-only inspection of the repository.
      Observe: string -> string list -> ObservedRepository
      /// Apply a validated plan, returning the paths actually written.
      Apply: 'payload -> InstallationPlan -> Result<string list, string>
      /// Persist the installation manifest.
      RecordManifest: InstallationManifest -> unit
      /// Lay down the legacy `ros-bootstrap` snapshot on a fresh install.
      RecordLegacyInstallation: 'payload -> ObservedRepository -> unit }

/// Outcome of a command that can change the repository.
type ExecutionOutcome =
    { Installation: InstallationPlan
      /// True when the plan was actually executed (false for a dry run, for a
      /// no-op, and for a plan blocked by a conflict).
      Applied: bool
      AppliedPaths: string list }

[<RequireQualifiedAccess>]
type LifecycleFailure =
    /// The command needs the packaged scaffold and could not reach it.
    | PayloadUnavailable of reason: string
    /// The plan has blocking conflicts; nothing was written.
    | Blocked of Conflict list
    /// No supported path from the installed configuration version to this
    /// CLI's.
    | MigrationUnavailable of reason: string
    /// A write failed. The message names what was and was not applied.
    | ExecutionFailed of reason: string

/// The lifecycle core. Each function maps onto exactly one public command,
/// and the read-only ones are read-only by construction: they never call
/// `Apply` or `RecordManifest`.
[<RequireQualifiedAccess>]
module Lifecycle =
    let private payloadOf environment request =
        match environment.LoadPayload request with
        | None ->
            Error(
                LifecycleFailure.PayloadUnavailable
                    "this CLI cannot see its own packaged scaffold; run it through the npm package or pass --package-root"
            )
        | Some(Error message) -> Error(LifecycleFailure.PayloadUnavailable message)
        | Some(Ok payload) -> Ok payload

    /// Inspect the repository without the packaged scaffold. The foundation of
    /// every read-only command.
    let inspectRepository environment (request: LifecycleRequest) : ObservedRepository =
        let payloadPaths =
            match environment.LoadPayload request with
            | Some(Ok payload) -> environment.PayloadEntries payload |> List.map (fun entry -> entry.Path)
            | _ -> []

        environment.Observe request.Root payloadPaths

    let getInstallationState environment request (availableVersion: string) : InstallationState =
        inspectRepository environment request |> Planning.installationState availableVersion

    /// Calculate, but never execute, the plan that would bring the repository
    /// into a valid installed state.
    let createInitializationPlan environment request : Result<InstallationPlan, LifecycleFailure> =
        payloadOf environment request
        |> Result.map (fun payload ->
            let entries = environment.PayloadEntries payload
            let observed = environment.Observe request.Root (entries |> List.map (fun entry -> entry.Path))

            Planning.initialize
                (environment.PayloadProfile payload)
                (environment.PayloadPackageName payload)
                (environment.PayloadVersion payload)
                entries
                observed)

    /// Calculate the ordered migration plan from the installed configuration
    /// version to this CLI's.
    let planUpgrade environment request : Result<InstallationPlan, LifecycleFailure> =
        payloadOf environment request
        |> Result.bind (fun payload ->
            let entries = environment.PayloadEntries payload
            let observed = environment.Observe request.Root (entries |> List.map (fun entry -> entry.Path))

            let installedConfiguration =
                match observed.Manifest with
                | Some manifest -> manifest.ConfigurationVersion
                | None when observed.LegacyManifestPresent -> Migration.LegacyConfigurationVersion
                | None -> Migration.CurrentConfigurationVersion

            match Migration.path installedConfiguration with
            | Error reason -> Error(LifecycleFailure.MigrationUnavailable reason)
            | Ok steps ->
                Ok(
                    Planning.upgrade
                        (environment.PayloadProfile payload)
                        (environment.PayloadPackageName payload)
                        (environment.PayloadVersion payload)
                        entries
                        observed
                        steps
                ))

    let private run environment request (plan: Result<InstallationPlan, LifecycleFailure>) dryRun =
        plan
        |> Result.bind (fun installation ->
            match Plan.validate installation.Plan with
            | Error conflicts -> Error(LifecycleFailure.Blocked conflicts)
            | Ok _ when dryRun ->
                Ok
                    { Installation = installation
                      Applied = false
                      AppliedPaths = [] }
            | Ok _ when Plan.isNoOp installation.Plan ->
                // Nothing to do. Idempotency is a property of the plan being
                // empty, not of re-writing identical bytes.
                Ok
                    { Installation = installation
                      Applied = false
                      AppliedPaths = [] }
            | Ok _ ->
                match payloadOf environment request with
                | Error failure -> Error failure
                | Ok payload ->
                    let entries = environment.PayloadEntries payload
                    let observedBefore = environment.Observe request.Root (entries |> List.map (fun entry -> entry.Path))

                    match environment.Apply payload installation with
                    | Error message -> Error(LifecycleFailure.ExecutionFailed message)
                    | Ok appliedPaths ->
                        environment.RecordLegacyInstallation payload observedBefore
                        environment.RecordManifest installation.Manifest

                        Ok
                            { Installation = installation
                              Applied = true
                              AppliedPaths = appliedPaths })

    /// `init`: inspect, plan, validate, execute, and leave the repository in a
    /// state `verify` accepts. Safe to run repeatedly; a second run plans no
    /// changes.
    let initialize environment request dryRun : Result<ExecutionOutcome, LifecycleFailure> =
        run environment request (createInitializationPlan environment request) dryRun

    /// `upgrade`: the same, preceded by the ordered migrations.
    let performUpgrade environment request dryRun : Result<ExecutionOutcome, LifecycleFailure> =
        run environment request (planUpgrade environment request) dryRun

    /// `doctor`, and the shared basis of `verify` and `status`.
    let diagnose environment request (availableVersion: string) : Diagnosis list =
        let payloadEntries =
            match environment.LoadPayload request with
            | Some(Ok payload) -> Some(environment.PayloadEntries payload)
            | _ -> None

        let observed =
            environment.Observe request.Root (payloadEntries |> Option.defaultValue [] |> List.map (fun entry -> entry.Path))

        Diagnostics.inspect availableVersion payloadEntries observed

    /// `verify`: valid or not, with the diagnoses that decided it.
    let verify environment request availableVersion strict : bool * Diagnosis list =
        let diagnoses = diagnose environment request availableVersion
        (Diagnostics.verificationFailures strict diagnoses).IsEmpty, diagnoses

    /// `status`: the installation half of the status document. Read-only.
    let getStatus environment request availableVersion : InstallationState * InstallationManifest option * bool =
        let observed = inspectRepository environment request
        let state = Planning.installationState availableVersion observed
        let _, diagnoses = verify environment request availableVersion false
        state, observed.Manifest, (Diagnostics.errors diagnoses).IsEmpty
