namespace Ros.Contracts.Lifecycle

open System.Text.Json
open Ros.Contracts
open Ros.Domain.Lifecycle

/// The machine-readable lifecycle interface. Every `--json` mode in the CLI
/// renders through this module so that the schema has one definition rather
/// than an ad-hoc object per call site.
///
/// `schemaVersion` is the version of these documents, not of the tool. It is
/// bumped only when a field is removed or changes meaning; adding a field is
/// not a breaking change and does not bump it.
[<RequireQualifiedAccess>]
module LifecycleContract =
    [<Literal>]
    let SchemaVersion = 1

    let private writeArtifact (writer: Utf8JsonWriter) (artifact: RecordedArtifact) =
        writer.WriteStartObject()
        writer.WriteString("path", artifact.Path)
        writer.WriteString("ownership", Ownership.toString artifact.Ownership)
        writer.WriteString("sha256", artifact.Sha256)
        writer.WriteEndObject()

    let private writeOptionalString (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, text)
        | None -> writer.WriteNull(name)

    let private writeDiagnosis (writer: Utf8JsonWriter) (item: Diagnosis) =
        writer.WriteStartObject()
        writer.WriteString("severity", Severity.toString item.Severity)
        writer.WriteString("code", item.Code)
        writer.WriteString("message", item.Message)
        writeOptionalString writer "path" item.Path
        writeOptionalString writer "remedy" item.Remedy
        writer.WriteEndObject()

    let private writeChange (writer: Utf8JsonWriter) (change: PlannedChange) =
        writer.WriteStartObject()
        writer.WriteString("kind", PlannedChange.kind change)
        writeOptionalString writer "path" (PlannedChange.path change)
        writer.WriteString("description", PlannedChange.describe change)

        match change with
        | PlannedChange.CreateFile(_, ownership, _) -> writer.WriteString("ownership", Ownership.toString ownership)
        | _ -> writer.WriteNull("ownership")

        writer.WriteEndObject()

    let private writeConflict (writer: Utf8JsonWriter) (conflict: Conflict) =
        writer.WriteStartObject()
        writer.WriteString("code", Conflict.code conflict)
        writeOptionalString writer "path" (Conflict.path conflict)
        writer.WriteString("message", Conflict.message conflict)
        writer.WriteString("remedy", Conflict.remedy conflict)
        writer.WriteBoolean("blocking", Conflict.isBlocking conflict)
        writer.WriteEndObject()

    /// The installation manifest exactly as it is written to
    /// `.echelon/ros.json`. Kept here, next to the other lifecycle documents,
    /// because the file on disk is as public as any `--json` output.
    let writeManifest (writer: Utf8JsonWriter) (manifest: InstallationManifest) =
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", manifest.SchemaVersion)
        writer.WriteString("tool", manifest.Tool)
        writer.WriteString("package", manifest.Package)
        writer.WriteString("installedVersion", manifest.InstalledVersion)
        writer.WriteNumber("configurationVersion", manifest.ConfigurationVersion)
        writer.WriteString("profile", manifest.Profile)
        writer.WriteStartArray("managedArtifacts")

        for artifact in manifest.ManagedArtifacts do
            writeArtifact writer artifact

        writer.WriteEndArray()
        writer.WriteEndObject()

    let renderManifest (manifest: InstallationManifest) =
        JsonRendering.renderIndented (fun writer -> writeManifest writer manifest)

    /// Shared installation block embedded in `status`, `verify` and `doctor`
    /// output so the three never disagree about what is installed.
    let private writeInstallation
        (includeArtifacts: bool)
        (writer: Utf8JsonWriter)
        (packageName: string)
        (cliVersion: string)
        (state: InstallationState)
        (manifest: InstallationManifest option)
        (verified: bool)
        =
        writer.WriteStartObject("installation")
        writer.WriteNumber("schemaVersion", SchemaVersion)
        writer.WriteString("tool", "ros")
        writer.WriteString("package", packageName)
        writer.WriteString("cliVersion", cliVersion)
        writeOptionalString writer "installedVersion" (InstallationState.installedVersion state)

        match manifest with
        | Some value ->
            writer.WriteNumber("configurationVersion", value.ConfigurationVersion)
            writer.WriteString("profile", value.Profile)
            writer.WriteNumber("managedArtifactCount", value.ManagedArtifacts.Length)
        | None ->
            writer.WriteNull("configurationVersion")
            writer.WriteNull("profile")
            writer.WriteNull("managedArtifactCount")

        writer.WriteString("state", InstallationState.toString state)
        writer.WriteBoolean("verified", verified)

        match state with
        | InstallationState.UpgradeRequired(_, AvailableVersion available) -> writer.WriteString("upgradeAvailable", available)
        | _ -> writer.WriteNull("upgradeAvailable")

        // The full artifact list is verbose-only: it is long, and every
        // summary field a caller normally needs is already above.
        if includeArtifacts then
            match manifest with
            | Some value ->
                writer.WriteStartArray("managedArtifacts")

                for artifact in value.ManagedArtifacts do
                    writeArtifact writer artifact

                writer.WriteEndArray()
            | None -> writer.WriteNull("managedArtifacts")

        writer.WriteEndObject()

    /// `status --json` embeds this object under `installation`; see
    /// `Ros.Cli` for how it is merged into the existing status document.
    let renderInstallation packageName cliVersion state manifest verified =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writeInstallation true writer packageName cliVersion state manifest verified
            writer.WriteEndObject())

    /// `verify --json`.
    let renderVerification
        (packageName: string)
        (cliVersion: string)
        (state: InstallationState)
        (manifest: InstallationManifest option)
        (strict: bool)
        (diagnoses: Diagnosis list)
        =
        let failures = Diagnostics.verificationFailures strict diagnoses

        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("command", "verify")
            writer.WriteNumber("schemaVersion", SchemaVersion)
            writer.WriteBoolean("valid", failures.IsEmpty)
            writer.WriteBoolean("strict", strict)
            writeInstallation false writer packageName cliVersion state manifest failures.IsEmpty
            writer.WriteStartArray("failures")

            for item in failures do
                writeDiagnosis writer item

            writer.WriteEndArray()
            writer.WriteEndObject())

    /// `doctor --json`.
    let renderDiagnosis
        (packageName: string)
        (cliVersion: string)
        (state: InstallationState)
        (manifest: InstallationManifest option)
        (diagnoses: Diagnosis list)
        =
        let errors = Diagnostics.errors diagnoses

        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("command", "doctor")
            writer.WriteNumber("schemaVersion", SchemaVersion)
            writer.WriteBoolean("healthy", errors.IsEmpty)
            writeInstallation false writer packageName cliVersion state manifest errors.IsEmpty
            writer.WriteNumber("errorCount", errors.Length)
            writer.WriteNumber("warningCount", (Diagnostics.warnings diagnoses).Length)
            writer.WriteStartArray("diagnoses")

            for item in diagnoses do
                writeDiagnosis writer item

            writer.WriteEndArray()
            writer.WriteEndObject())

    /// `init --json`, `init --dry-run --json`, `upgrade --json` and
    /// `upgrade --dry-run --json` all render this one document, so an agent
    /// parses the same shape whether or not the plan was executed.
    let renderPlan
        (command: string)
        (packageName: string)
        (cliVersion: string)
        (dryRun: bool)
        (applied: bool)
        (installation: InstallationPlan)
        =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("command", command)
            writer.WriteNumber("schemaVersion", SchemaVersion)
            writer.WriteString("package", packageName)
            writer.WriteString("cliVersion", cliVersion)
            writer.WriteBoolean("dryRun", dryRun)
            writer.WriteBoolean("applied", applied)
            writer.WriteBoolean("changesRequired", not (Plan.isNoOp installation.Plan))
            writer.WriteStartArray("migrations")

            for step in installation.Steps do
                writer.WriteStartObject()
                writer.WriteNumber("fromVersion", step.FromVersion)
                writer.WriteNumber("toVersion", step.ToVersion)
                writer.WriteString("description", step.Description)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteStartArray("changes")

            for change in installation.Plan.Changes do
                writeChange writer change

            writer.WriteEndArray()
            writer.WriteStartArray("conflicts")

            for conflict in installation.Plan.Conflicts do
                writeConflict writer conflict

            writer.WriteEndArray()
            writer.WriteStartArray("preserved")

            for path in installation.Plan.Preserved do
                writer.WriteStringValue(path: string)

            writer.WriteEndArray()
            writer.WritePropertyName("manifest")
            writeManifest writer installation.Manifest
            writer.WriteEndObject())
