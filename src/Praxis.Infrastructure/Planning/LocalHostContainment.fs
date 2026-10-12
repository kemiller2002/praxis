namespace Praxis.Infrastructure.Planning

open System
open System.IO
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// Binds the host configuration to Linux containment: which host paths are
/// sealed from a worker, the launch gate for workers that require
/// containment, and the qualification that writes the evidence file.
[<RequireQualifiedAccess>]
module LocalHostContainment =
    /// Every host root, the repository, the configuration directory and the
    /// evidence directory are sealed from a contained worker.
    let paths (configuration: LocalHostConfiguration) : ContainmentHostPaths =
        { RepositoryRoot = configuration.RepositoryRoot
          CheckoutRoot = configuration.CheckoutRoot
          HostRoots =
            [ "policy", configuration.PolicyRoot
              "journal", configuration.JournalRoot
              "spool", configuration.SpoolRoot
              "submissions", configuration.SubmissionRoot
              "integration", configuration.IntegrationRoot
              "evidence", configuration.EvidenceRoot ]
          Extra =
            [ yield Path.GetDirectoryName configuration.Path
              match configuration.Containment with
              | Some containment -> yield Path.GetDirectoryName containment.Evidence
              | None -> () ] }

    let private profile (configuration: LocalHostConfiguration) =
        match configuration.Containment with
        | Some containment -> Ok containment
        | None -> Error "the host configuration declares no containment profile"

    /// The launch gate for one worker template. A worker that does not
    /// require containment is unaffected; one that does is admitted only
    /// with a current, fully passing qualification for this exact host.
    let admit (configuration: LocalHostConfiguration) (worker: LocalWorkerTemplate) (now: DateTimeOffset) =
        if not worker.RequiresContainment then Ok None
        else
            profile configuration
            |> Result.bind (fun containment ->
                LocalContainmentQualification.admit containment.Settings containment.Evidence containment.MaxEvidenceAge now
                |> Result.map (fun (tools, _) -> Some(containment.Settings, tools)))
            |> Result.mapError (fun reason -> sprintf "worker %s requires containment; launch refused: %s" worker.WorkerId reason)

    /// Wraps a prepared worker spec in the qualified confinement, using a
    /// fresh staging root the caller removes after the run.
    let confine (configuration: LocalHostConfiguration) (settings: LinuxContainmentSettings) tools (registerStage: string -> unit) (spec: LocalWorkerProcessSpec) =
        LinuxContainmentHost.createStage configuration.CheckoutRoot
        |> Result.bind (fun stage ->
            registerStage stage
            LinuxContainmentHost.confine settings tools (paths configuration) stage spec)

    /// Runs the real probe suite and replaces the evidence file with the
    /// result, qualified or not, so a failed run also revokes admission.
    let qualify (configuration: LocalHostConfiguration) (now: unit -> DateTimeOffset) =
        profile configuration
        |> Result.bind (fun containment ->
            let qualification, failure =
                LocalContainmentQualification.qualify containment.Settings (paths configuration) (Path.GetDirectoryName configuration.Path) now
            LinuxContainmentHost.writeEvidence containment.Evidence qualification
            |> Result.map (fun written -> qualification, failure, written))

    /// The gate decision for the configured profile, without launching.
    let status (configuration: LocalHostConfiguration) (now: DateTimeOffset) =
        profile configuration
        |> Result.map (fun containment ->
            let evidence = LinuxContainmentHost.readEvidence containment.Evidence
            let admission = LocalContainmentQualification.admit containment.Settings containment.Evidence containment.MaxEvidenceAge now
            containment, evidence, admission)
