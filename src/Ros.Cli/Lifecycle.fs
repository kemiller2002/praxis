module Ros.Cli.Lifecycle

open System
open System.Reflection
open System.Text.Json.Nodes
open Ros.Application.Lifecycle
open Ros.Contracts.Lifecycle
open Ros.Domain.Lifecycle
open Ros.Infrastructure.Lifecycle

/// The distributed package version, taken from the assembly's own
/// informational version. `Directory.Build.props` sets that from
/// `package.json`, so the CLI and the npm release can never report different
/// versions.
let Version =
    let raw =
        typeof<LifecycleRequest>.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        |> function
            | null -> Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            | attribute -> attribute
        |> function
            | null -> "0.0.0"
            | attribute -> attribute.InformationalVersion

    match raw.IndexOf '+' with
    | -1 -> raw
    | index -> raw.Substring(0, index)

[<Literal>]
let PackageName = "@echelon-foundry/repository-operating-system"

[<RequireQualifiedAccess>]
type OutputMode =
    | Text
    | Json

type CommonOptions =
    { Output: OutputMode
      Verbose: bool
      PackageRoot: string option }

type InitOptions =
    { Common: CommonOptions
      Profile: string
      Project: string option
      DryRun: bool
      Check: bool }

type StatusOptions = { Common: CommonOptions }

type VerifyOptions = { Common: CommonOptions; Strict: bool }

type UpgradeOptions =
    { Common: CommonOptions
      DryRun: bool
      Check: bool }

type DoctorOptions = { Common: CommonOptions; Strict: bool }

/// The command the caller asked for, as a value. Parsing produces one of
/// these and nothing else; every behaviour difference downstream is a match
/// on this type rather than a re-reading of argv.
[<RequireQualifiedAccess>]
type Command =
    | Init of InitOptions
    | Status of StatusOptions
    | Verify of VerifyOptions
    | Upgrade of UpgradeOptions
    | Doctor of DoctorOptions
    | Help of topic: string option
    | Version

// ---------------------------------------------------------------------------
// Help. Treated as public documentation: every command, option and side
// effect named here is also in docs/cli.md and the README.
// ---------------------------------------------------------------------------

let private globalHelp =
    """ros -- Repository Operating System lifecycle CLI

Usage:
  ros <command> [options]

Commands:
  init        Bring this repository into a valid installed state. Idempotent.
  status      Report installation, validation and work state. Read-only.
  verify      Check that the capability is correctly installed. Read-only.
  upgrade     Migrate an existing installation to this CLI's version.
  doctor      Diagnose problems and explain how to fix them. Read-only.

Options:
  -h, --help            Show this help, or 'ros <command> --help'.
  -V, --version         Print the CLI version and exit.
  --root PATH           Repository to act on (default: current directory).
  --package-root PATH   Where this package's scaffold lives. Normally supplied
                        by the npm launcher; needed only when running the
                        executable directly from outside the package.
  --json                Emit machine-readable JSON on stdout.
  --verbose             Emit extra detail.

Exit codes:
  0 success                        4 incompatible installation
  1 internal failure               5 migration blocked
  2 invalid arguments              6 prerequisite or environment failure
  3 verification failed            7 unsupported platform

This CLI also carries the repository's artifact, work and telemetry commands
(validate, registry, work, add, telemetry, adapter). Their full argument list
follows below; see docs/cli.md for the complete reference."""

let private initHelp =
    """ros init -- bring this repository into a valid installed state

Usage:
  ros init [--profile NAME] [--project NAME] [--dry-run] [--check] [--json] [--verbose]

What it does:
  Inspects the repository, determines the installed state, calculates the
  changes needed, validates them, and applies them. Running it again when
  nothing has changed applies nothing and exits 0.

Side effects:
  Creates tool-owned files, seeds shared and user-owned files that are absent,
  creates .echelon/ros.json, and on a first install also writes the legacy
  .ros/installation.json snapshot and its work attribution.

  It never overwrites a user-owned or shared file that already exists, and it
  never overwrites a tool-owned file that was modified locally -- that stops
  the command instead, with the path named.

Options:
  --profile NAME   Starter profile to install (default: greenfield).
  --project NAME   Display name for a new installation. Derived from the
                   repository folder when omitted.
  --dry-run        Calculate and report the full plan; change nothing.
  --check          Change nothing and exit 3 if any change would be needed.
  --json           Emit the plan as JSON on stdout.
  --verbose        List every planned change, not just the counts.

Examples:
  ros init
  ros init --dry-run --json
  ros init --profile project-administration --project 'Project Administration'
"""

let private statusHelp =
    """ros status -- report installation, validation and work state

Usage:
  ros status [--json] [--verbose]

What it does:
  Reads the repository and prints a JSON document describing the work items,
  validation findings, telemetry counts and the installation. Never writes.

  Output is JSON with or without --json: this command emitted JSON before the
  lifecycle interface existed and consumers depend on that. --json is accepted
  so scripts can be explicit.

Options:
  --json      Accepted for symmetry; the output is JSON either way.
  --verbose   Include every managed artifact in the installation block.

Example:
  ros status --json"""

let private verifyHelp =
    """ros verify -- check that the capability is correctly installed

Usage:
  ros verify [--strict] [--json] [--verbose]

What it does:
  Reads .echelon/ros.json and checks that every tool-owned artifact it records
  is present and unmodified, that the configuration is present and parseable,
  and that the manifest schema is one this CLI supports. Never writes.

  Generated, shared and user-owned files are checked for presence, not for
  content: the repository, or a generator the repository runs, is what makes
  them current.

Options:
  --strict   Also fail on warnings: an available upgrade, a legacy installation
             with no manifest, or a seeded file that has been deleted.
  --json     Emit the result as JSON on stdout.
  --verbose  Print each failure's remedy.

Exit codes:
  0 valid; 3 verification failed.

Examples:
  ros verify
  ros verify --strict --json"""

let private upgradeHelp =
    """ros upgrade -- migrate an existing installation to this CLI's version

Usage:
  ros upgrade [--dry-run] [--check] [--json] [--verbose]

What it does:
  Determines the installed configuration version, resolves the ordered chain of
  migrations to this CLI's version, checks each step's precondition, then
  applies the migrations and reconciles tool-owned files.

  Migrations run in sequence (0 -> 1 -> 2), never as one arbitrary jump. If a
  precondition fails, nothing is written and the failing step is named.

Side effects:
  Replaces tool-owned files that are unmodified since installation, and
  rewrites .echelon/ros.json. User-owned and shared files are preserved; a
  locally modified tool-owned file blocks the upgrade instead of being lost.

Options:
  --dry-run   Calculate and report the full plan; change nothing.
  --check     Change nothing and exit 3 if an upgrade is needed.
  --json      Emit the plan as JSON on stdout.
  --verbose   List every planned change and migration step.

Exit codes:
  0 success; 3 --check found pending work; 4 incompatible installation;
  5 migration blocked.

Examples:
  ros upgrade --dry-run --json
  ros upgrade"""

let private doctorHelp =
    """ros doctor -- diagnose problems and explain how to fix them

Usage:
  ros doctor [--strict] [--json] [--verbose]

What it does:
  Reports every problem it can detect, each with the reason it is a problem
  and, where one exists, the command that fixes it. Never writes.

  Findings are classified: error (the installation is not usable as recorded),
  warning (usable, but something should be attended to), information (expected
  states worth knowing, such as a shared file you have edited).

Options:
  --strict   Exit 3 on warnings as well as errors.
  --json     Emit every diagnosis as JSON on stdout.
  --verbose  Accepted; doctor already prints every diagnosis.

Exit codes:
  0 no errors; 3 at least one error (or, with --strict, any warning).

Examples:
  ros doctor
  ros doctor --json"""

let helpFor (topic: string option) =
    match topic with
    | Some "init" -> initHelp
    | Some "status" -> statusHelp
    | Some "verify" -> verifyHelp
    | Some "upgrade" -> upgradeHelp
    | Some "doctor" -> doctorHelp
    | _ -> globalHelp

// ---------------------------------------------------------------------------
// Parsing. A small typed parser, not a framework: the surface is five commands
// and eight flags.
// ---------------------------------------------------------------------------

let private knownValueFlags = Set.ofList [ "--profile"; "--project" ]

let private takeValue (name: string) (arguments: string list) =
    let rec walk (remaining: string list) =
        match remaining with
        | [] -> None
        | key :: value :: _ when key = name && not (value.StartsWith("--", StringComparison.Ordinal)) -> Some value
        | _ :: rest -> walk rest

    walk arguments

let private unknownFlags (allowed: Set<string>) (arguments: string list) =
    let rec walk acc =
        function
        | [] -> List.rev acc
        | argument :: rest when knownValueFlags.Contains argument ->
            if allowed.Contains argument then
                walk acc (if rest.IsEmpty then rest else List.tail rest)
            else
                walk (argument :: acc) (if rest.IsEmpty then rest else List.tail rest)
        | argument :: rest when argument.StartsWith("--", StringComparison.Ordinal) ->
            walk (if allowed.Contains argument then acc else argument :: acc) rest
        | _ :: rest -> walk acc rest

    walk [] arguments

let private commonOptions (packageRoot: string option) (arguments: string list) =
    { Output = if List.contains "--json" arguments then OutputMode.Json else OutputMode.Text
      Verbose = List.contains "--verbose" arguments
      PackageRoot = packageRoot }

let private baseFlags = Set.ofList [ "--json"; "--verbose" ]

/// Parse a lifecycle invocation. Returns None when the arguments are not a
/// lifecycle command at all, so the caller can fall through to the repository
/// commands this CLI also carries.
let parse (packageRoot: string option) (arguments: string list) : Result<Command, string> option =
    let commonOptions = commonOptions packageRoot
    let missingValue name =
        Some(Error $"{name} requires a value")

    let wantsHelp = arguments |> List.exists (fun argument -> argument = "--help" || argument = "-h")

    let check (allowed: Set<string>) (build: unit -> Command) rest =
        match unknownFlags allowed rest with
        | [] -> Some(Ok(build ()))
        | unknown :: _ -> Some(Error $"unknown option '{unknown}'")

    match arguments with
    | [] -> None
    | command :: rest ->
        match command, wantsHelp with
        | ("init" | "status" | "verify" | "upgrade" | "doctor"), true -> Some(Ok(Command.Help(Some command)))
        | "help", _ ->
            match rest |> List.tryHead with
            | Some topic -> Some(Ok(Command.Help(Some topic)))
            | None -> Some(Ok(Command.Help None))
        | ("--help" | "-h"), _ -> Some(Ok(Command.Help(rest |> List.tryHead)))
        | ("--version" | "-V"), _ -> Some(Ok Command.Version)
        | "init", _ ->
            if List.contains "--profile" rest && (takeValue "--profile" rest).IsNone then
                missingValue "--profile"
            elif List.contains "--project" rest && (takeValue "--project" rest).IsNone then
                missingValue "--project"
            else
                rest
                |> check
                    (baseFlags + Set.ofList [ "--profile"; "--project"; "--dry-run"; "--check" ])
                    (fun () ->
                        Command.Init
                            { Common = commonOptions rest
                              Profile = takeValue "--profile" rest |> Option.defaultValue "greenfield"
                              Project = takeValue "--project" rest
                              DryRun = List.contains "--dry-run" rest
                              Check = List.contains "--check" rest })
        | "verify", _ ->
            rest
            |> check (baseFlags + Set.singleton "--strict") (fun () ->
                Command.Verify
                    { Common = commonOptions rest
                      Strict = List.contains "--strict" rest })
        | "upgrade", _ ->
            rest
            |> check (baseFlags + Set.ofList [ "--dry-run"; "--check" ]) (fun () ->
                Command.Upgrade
                    { Common = commonOptions rest
                      DryRun = List.contains "--dry-run" rest
                      Check = List.contains "--check" rest })
        | "doctor", _ ->
            rest
            |> check (baseFlags + Set.singleton "--strict") (fun () ->
                Command.Doctor
                    { Common = commonOptions rest
                      Strict = List.contains "--strict" rest })
        | "status", _ ->
            // `status` is also a pre-existing repository command; the
            // lifecycle parser only claims it to validate its flags.
            rest
            |> check baseFlags (fun () -> Command.Status { Common = commonOptions rest })
        | _ -> None

// ---------------------------------------------------------------------------
// Wiring: the CLI is the only place that knows both the core API and the
// filesystem implementation of its ports.
// ---------------------------------------------------------------------------

let environment: LifecycleEnvironment<Payload> =
    { LoadPayload =
        fun request ->
            match Payload.locate request.PackageRoot with
            | None -> None
            | Some packageRoot ->
                let projectName =
                    match request.Project with
                    | Some name when name.Trim().Length > 0 -> Ok(name.Trim())
                    | _ ->
                        match Installation.readManifest request.Root with
                        | Ok(Some _) -> Payload.deriveProjectName request.Root
                        | _ -> Payload.deriveProjectName request.Root

                match projectName with
                | Error message -> Some(Error message)
                | Ok name -> Some(Payload.load packageRoot request.Profile name)
      PayloadEntries = fun payload -> payload.Files |> List.map (fun file -> file.Entry)
      PayloadProfile = fun payload -> payload.Profile
      PayloadPackageName = fun payload -> payload.PackageName
      PayloadVersion = fun payload -> payload.PackageVersion
      Observe = Installation.observe
      Apply = fun _ _ -> Error "apply is bound per-request"
      RecordManifest = fun _ -> ()
      RecordLegacyInstallation = fun _ _ -> () }

/// The environment above is request-independent except for the three effects
/// that need the repository root, which is supplied here.
let private environmentFor (root: string) =
    { environment with
        Apply = fun payload installation -> Installation.execute root payload installation
        RecordManifest = fun manifest -> Installation.writeInstallationManifest root manifest
        RecordLegacyInstallation = fun payload observed -> Installation.writeLegacyInstallationIfAbsent root payload observed }

let private requestFor root (common: CommonOptions) profile project =
    { Root = root
      PackageRoot = common.PackageRoot
      Profile = profile
      Project = project }

// ---------------------------------------------------------------------------
// Rendering. Decorative output goes to stdout only in text mode; in JSON mode
// stdout carries the document and nothing else.
// ---------------------------------------------------------------------------

let private note (common: CommonOptions) (line: string) =
    if common.Verbose then eprintfn "%s" line

let private renderDiagnosisLine (item: Diagnosis) =
    let location = item.Path |> Option.map (fun path -> $"{path}: ") |> Option.defaultValue ""
    $"{(Severity.toString item.Severity).ToUpperInvariant()} {location}{item.Message}"

let private failureExit command (failure: LifecycleFailure) =
    let message, code =
        match failure with
        | LifecycleFailure.PayloadUnavailable reason -> reason, ExitCode.PrerequisiteFailure
        | LifecycleFailure.Blocked conflicts ->
            let detail =
                conflicts
                |> List.map (fun conflict ->
                    let location = Conflict.path conflict |> Option.map (fun path -> $"{path}: ") |> Option.defaultValue ""
                    $"  {location}{Conflict.message conflict}\n    REMEDY {Conflict.remedy conflict}")
                |> String.concat "\n"

            $"{command} cannot proceed; {conflicts.Length} blocking conflict(s):\n{detail}",
            (if command = "upgrade" then ExitCode.MigrationBlocked else ExitCode.IncompatibleInstallation)
        | LifecycleFailure.MigrationUnavailable reason -> reason, ExitCode.IncompatibleInstallation
        | LifecycleFailure.ExecutionFailed reason -> reason, ExitCode.InternalFailure

    eprintfn "ERROR %s" message
    code

let private renderOutcome command (options: CommonOptions) dryRun (outcome: ExecutionOutcome) =
    match options.Output with
    | OutputMode.Json ->
        printf "%s" (LifecycleContract.renderPlan command PackageName Version dryRun outcome.Applied outcome.Installation)
    | OutputMode.Text ->
        let plan = outcome.Installation.Plan

        if options.Verbose then
            for step in outcome.Installation.Steps do
                printfn "MIGRATE %d -> %d  %s" step.FromVersion step.ToVersion step.Description

            for change in plan.Changes do
                printfn "%s %s" (if outcome.Applied then "APPLIED" else "WOULD") (PlannedChange.describe change)

        if plan.Changes.IsEmpty then
            printfn "%s: no changes needed; %s@%s is installed and current" command PackageName Version
        elif outcome.Applied then
            printfn "%s: applied %d change(s); %d file(s) preserved" command plan.Changes.Length plan.Preserved.Length
            printfn "manifest: %s" Planning.ManifestPath
        else
            printfn "%s: would apply %d change(s); %d file(s) preserved" command plan.Changes.Length plan.Preserved.Length

            if not options.Verbose then
                printfn "run with --verbose to list them, or --json for the full plan"

/// The `installation` block `status` merges into its existing document. Parsed
/// back from the contract renderer so the schema has exactly one definition.
let installationNode (root: string) (packageRoot: string option) : JsonNode option =
    let request = requestFor root { Output = OutputMode.Json; Verbose = false; PackageRoot = packageRoot } "greenfield" None
    let environment = environmentFor root
    let state, manifest, verified = Lifecycle.getStatus environment request Version
    let rendered = LifecycleContract.renderInstallation PackageName Version state manifest verified

    match JsonNode.Parse rendered with
    | :? JsonObject as node ->
        match node["installation"] with
        | null -> None
        | value -> Some(value.DeepClone())
    | _ -> None

// ---------------------------------------------------------------------------
// Command execution
// ---------------------------------------------------------------------------

let private runInit root (options: InitOptions) =
    let request = requestFor root options.Common options.Profile options.Project
    let environment = environmentFor root
    note options.Common $"root: {root}"

    if options.Check then
        match Lifecycle.createInitializationPlan environment request with
        | Error failure -> failureExit "init" failure
        | Ok installation ->
            let outcome =
                { Installation = installation
                  Applied = false
                  AppliedPaths = [] }

            renderOutcome "init" options.Common true outcome

            if Plan.isNoOp installation.Plan then
                ExitCode.Success
            else
                ExitCode.VerificationFailed
    else
        match Lifecycle.initialize environment request options.DryRun with
        | Error failure -> failureExit "init" failure
        | Ok outcome ->
            renderOutcome "init" options.Common options.DryRun outcome
            ExitCode.Success

let private runUpgrade root (options: UpgradeOptions) =
    let request = requestFor root options.Common "greenfield" None
    let environment = environmentFor root

    if options.Check then
        match Lifecycle.planUpgrade environment request with
        | Error failure -> failureExit "upgrade" failure
        | Ok installation ->
            let outcome =
                { Installation = installation
                  Applied = false
                  AppliedPaths = [] }

            renderOutcome "upgrade" options.Common true outcome

            if Plan.isNoOp installation.Plan then
                ExitCode.Success
            else
                ExitCode.VerificationFailed
    else
        match Lifecycle.performUpgrade environment request options.DryRun with
        | Error failure -> failureExit "upgrade" failure
        | Ok outcome ->
            renderOutcome "upgrade" options.Common options.DryRun outcome
            ExitCode.Success

let private runVerify root (options: VerifyOptions) =
    let request = requestFor root options.Common "greenfield" None
    let environment = environmentFor root
    let valid, diagnoses = Lifecycle.verify environment request Version options.Strict
    let failures = Diagnostics.verificationFailures options.Strict diagnoses
    let state, manifest, _ = Lifecycle.getStatus environment request Version

    match options.Common.Output with
    | OutputMode.Json ->
        printf "%s" (LifecycleContract.renderVerification PackageName Version state manifest options.Strict diagnoses)
    | OutputMode.Text ->
        if valid then
            printfn "verification passed: %s@%s" PackageName Version
        else
            for item in failures do
                eprintfn "%s" (renderDiagnosisLine item)

                match item.Remedy with
                | Some remedy when options.Common.Verbose -> eprintfn "  REMEDY %s" remedy
                | _ -> ()

            eprintfn "verification failed with %d finding(s)" failures.Length

            if not options.Common.Verbose then
                eprintfn "run 'ros doctor' to see why, and how to fix each one"

    if valid then ExitCode.Success else ExitCode.VerificationFailed

let private runDoctor root (options: DoctorOptions) =
    let request = requestFor root options.Common "greenfield" None
    let environment = environmentFor root
    let diagnoses = Lifecycle.diagnose environment request Version
    let state, manifest, _ = Lifecycle.getStatus environment request Version

    match options.Common.Output with
    | OutputMode.Json -> printf "%s" (LifecycleContract.renderDiagnosis PackageName Version state manifest diagnoses)
    | OutputMode.Text ->
        if diagnoses.IsEmpty then
            printfn "doctor: no problems found; %s@%s is installed and current" PackageName Version
        else
            for item in diagnoses do
                printfn "%s" (renderDiagnosisLine item)

                match item.Remedy with
                | Some remedy -> printfn "  REMEDY %s" remedy
                | None -> ()

            printfn
                "%d error(s), %d warning(s)"
                (Diagnostics.errors diagnoses).Length
                (Diagnostics.warnings diagnoses).Length

    let failures = Diagnostics.verificationFailures options.Strict diagnoses
    if failures.IsEmpty then ExitCode.Success else ExitCode.VerificationFailed

/// Run a parsed lifecycle command. `status` returns None so the caller keeps
/// ownership of the existing status document.
///
/// `renderHelp` is supplied by the caller rather than being `helpFor` directly:
/// the CLI carries repository commands this module does not know about, and
/// `--help` is public documentation that has to cover all of them.
let run (renderHelp: string option -> string) (root: string) (command: Command) : int option =
    match command with
    | Command.Help topic ->
        printfn "%s" (renderHelp topic)
        Some ExitCode.Success
    | Command.Version ->
        // Format unchanged from before the lifecycle interface existed; only
        // the value changed, from a placeholder to the real package version.
        printfn "ros-fs %s" Version
        Some ExitCode.Success
    | Command.Init options -> Some(runInit root options)
    | Command.Verify options -> Some(runVerify root options)
    | Command.Upgrade options -> Some(runUpgrade root options)
    | Command.Doctor options -> Some(runDoctor root options)
    | Command.Status _ -> None
