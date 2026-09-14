namespace Ros.Infrastructure.Lifecycle

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Lifecycle
open Ros.Domain.Lifecycle
open Ros.Infrastructure.Json

/// Reading and writing the installed state. Every mutation in this module is
/// driven by an already-validated plan; nothing here decides what should
/// change.
[<RequireQualifiedAccess>]
module Installation =
    let private jsonWriteOptions =
        JsonSerializerOptions(
            WriteIndented = true,
            IndentSize = 2,
            Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    let private readAllBytesOrNone (path: string) =
        if File.Exists path then Some(File.ReadAllBytes path) else None

    let private parseOwnership (value: string) =
        Ownership.parse value |> Option.defaultValue Ownership.ToolOwned

    let private stringOf (node: JsonNode) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private intOf (node: JsonNode) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number -> Some(value.GetValue<int>())
        | _ -> None

    /// Parse `.echelon/ros.json`. A present-but-unusable manifest becomes an
    /// InstallationProblem rather than an exception, so `doctor` can explain
    /// it instead of the process dying.
    let readManifest (root: string) : Result<InstallationManifest option, InstallationProblem> =
        let path = Path.Combine(root, Planning.ManifestPath)

        if not (File.Exists path) then
            Ok None
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as node ->
                    match intOf node "schemaVersion" with
                    | Some version when version <> Planning.ManifestSchemaVersion ->
                        Error(InstallationProblem.ManifestSchemaUnsupported(version, Planning.ManifestSchemaVersion))
                    | None -> Error(InstallationProblem.ManifestUnreadable "'schemaVersion' is missing or not a number")
                    | Some version ->
                        let artifacts =
                            match node["managedArtifacts"] with
                            | :? JsonArray as items ->
                                items
                                |> Seq.choose (fun item ->
                                    match item with
                                    | :? JsonObject as entry ->
                                        match stringOf entry "path", stringOf entry "sha256" with
                                        | Some path, Some sha ->
                                            Some
                                                { Path = path
                                                  Ownership =
                                                    stringOf entry "ownership"
                                                    |> Option.map parseOwnership
                                                    |> Option.defaultValue Ownership.ToolOwned
                                                  Sha256 = sha }
                                        | _ -> None
                                    | _ -> None)
                                |> List.ofSeq
                            | _ -> []

                        Ok(
                            Some
                                { SchemaVersion = version
                                  Tool = stringOf node "tool" |> Option.defaultValue "ros"
                                  Package = stringOf node "package" |> Option.defaultValue ""
                                  InstalledVersion = stringOf node "installedVersion" |> Option.defaultValue "0.0.0"
                                  ConfigurationVersion =
                                    intOf node "configurationVersion"
                                    |> Option.defaultValue Migration.LegacyConfigurationVersion
                                  Profile = stringOf node "profile" |> Option.defaultValue "greenfield"
                                  ManagedArtifacts = artifacts }
                        )
                | _ -> Error(InstallationProblem.ManifestUnreadable "the manifest is not a JSON object")
            with
            | :? JsonException as error -> Error(InstallationProblem.ManifestUnreadable error.Message)
            | :? IOException as error -> Error(InstallationProblem.ManifestUnreadable error.Message)

    let private configurationProblem (root: string) =
        let path = Path.Combine(root, Planning.ConfigurationPath)

        if not (File.Exists path) then
            Some(InstallationProblem.ConfigurationMissing Planning.ConfigurationPath)
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject -> None
                | _ ->
                    Some(
                        InstallationProblem.ConfigurationUnreadable(Planning.ConfigurationPath, "the configuration is not a JSON object")
                    )
            with
            | :? JsonException as error -> Some(InstallationProblem.ConfigurationUnreadable(Planning.ConfigurationPath, error.Message))
            | :? IOException as error -> Some(InstallationProblem.ConfigurationUnreadable(Planning.ConfigurationPath, error.Message))

    /// Inspect the repository. Read-only by construction: this is the only
    /// function `status`, `verify` and `doctor` need, and it writes nothing.
    let observe (root: string) (payloadPaths: string list) : ObservedRepository =
        let manifestResult = readManifest root

        let manifest =
            match manifestResult with
            | Ok value -> value
            | Error _ -> None

        let manifestProblem =
            match manifestResult with
            | Ok _ -> None
            | Error problem -> Some problem

        let recordedPaths =
            manifest |> Option.map (fun m -> m.ManagedArtifacts |> List.map (fun a -> a.Path)) |> Option.defaultValue []

        let interesting = (payloadPaths @ recordedPaths) |> List.distinct

        let files =
            interesting
            |> List.choose (fun relative ->
                match Payload.resolveWithin root relative with
                | Error _ -> None
                | Ok absolute -> readAllBytesOrNone absolute |> Option.map (fun bytes -> relative, Payload.sha256Hex bytes))
            |> Map.ofList

        let directories =
            (Planning.ManifestDirectory
             :: (interesting
                 |> List.choose (fun relative ->
                     match relative.LastIndexOf '/' with
                     | -1 -> None
                     | index -> Some(relative.Substring(0, index)))))
            |> List.distinct
            |> List.filter (fun relative ->
                match Payload.resolveWithin root relative with
                | Error _ -> false
                | Ok absolute -> Directory.Exists absolute)
            |> Set.ofList

        let configurationProblem = configurationProblem root

        { Files = files
          Directories = directories
          Manifest = manifest
          ManifestProblem = manifestProblem
          LegacyManifestPresent = File.Exists(Path.Combine(root, Planning.LegacyManifestPath))
          ConfigurationPresent =
            configurationProblem
            <> Some(InstallationProblem.ConfigurationMissing Planning.ConfigurationPath)
          ConfigurationProblem = configurationProblem }

    let private writeTextFile (path: string) (text: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path: string) |> ignore
        File.WriteAllText(path, text, UTF8Encoding false)

    let private writeJsonFile (path: string) (node: JsonNode) =
        writeTextFile path (node.ToJsonString jsonWriteOptions + "\n")

    // ---------------------------------------------------------------------
    // Legacy compatibility: `ros-bootstrap init` (lib/bootstrap.mjs) writes
    // these files, and `ros-bootstrap verify`, `ros validate` and the
    // scaffolded CI workflow all read them. `ros init` writes exactly the
    // same files on a fresh install so that a repository is indistinguishable
    // whichever entry point installed it.
    // ---------------------------------------------------------------------

    let private legacyInstallationNode (payload: Payload) (observed: ObservedRepository) (createdDate: string) =
        let node = JsonObject()
        node["schema_version"] <- JsonValue.Create "1.0.0"
        node["package"] <- JsonValue.Create payload.PackageName
        node["package_version"] <- JsonValue.Create payload.PackageVersion
        node["profile"] <- JsonValue.Create payload.Profile
        node["project"] <- JsonValue.Create payload.ProjectName
        node["project_slug"] <- JsonValue.Create(Payload.slugify payload.ProjectName)
        node["installed_on"] <- JsonValue.Create createdDate
        node["update_policy"] <- JsonValue.Create "additive; collisions require explicit migration"

        let files = JsonArray()

        for file in payload.Files do
            let preexisting = Map.tryFind file.Entry.Path observed.Files
            let preserved = file.Entry.Ownership = Ownership.UserOwned && preexisting.IsSome
            let entry = JsonObject()
            entry["path"] <- JsonValue.Create file.Entry.Path

            entry["sha256"] <-
                JsonValue.Create(
                    match preexisting with
                    | Some existing when preserved -> existing
                    | _ -> file.Entry.Sha256
                )

            entry["managed"] <- JsonValue.Create(not preserved)

            entry["disposition"] <-
                JsonValue.Create(
                    if preserved then "preserved-existing"
                    elif preexisting.IsSome then "adopted-identical"
                    else "installed"
                )

            files.Add(entry: JsonNode)

        node["files"] <- files
        node

    let private installationAttribution (payload: Payload) (now: string) =
        let slug = Payload.slugify payload.ProjectName
        let workItem = $"""ROS-INSTALL-{payload.PackageVersion.Replace(".", "-")}"""

        let paths =
            (Planning.LegacyManifestPath :: (payload.Files |> List.map (fun file -> file.Entry.Path)))
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

        let evidence =
            let item = JsonObject()
            item["type"] <- JsonValue.Create "installation"
            item["path"] <- JsonValue.Create Planning.LegacyManifestPath
            let array = JsonArray()
            array.Add(item: JsonNode)
            array

        // Key order below reproduces the object literal in
        // lib/bootstrap.mjs exactly, because eventId is a digest of
        // JSON.stringify over it and JSON.stringify never reorders keys.
        let event = JsonObject()
        event["schemaVersion"] <- JsonValue.Create "1.0.0"
        event["type"] <- JsonValue.Create "work.completed"
        event["workItem"] <- JsonValue.Create workItem
        event["repository"] <- JsonValue.Create slug
        event["protocolVersion"] <- JsonValue.Create "1.0.0"
        event["occurredAt"] <- JsonValue.Create now
        event["evidence"] <- evidence
        event["paths"] <- (paths |> List.fold (fun (array: JsonArray) path -> array.Add(JsonValue.Create path: JsonNode); array) (JsonArray()))

        let publication = JsonObject()
        publication["status"] <- JsonValue.Create "pending"
        event["publication"] <- publication

        let eventId = CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact event)
        event["eventId"] <- JsonValue.Create eventId

        let contextEvidence = JsonObject()
        contextEvidence["type"] <- JsonValue.Create "installation"
        contextEvidence["path"] <- JsonValue.Create Planning.LegacyManifestPath
        let contextEvidenceArray = JsonArray()
        contextEvidenceArray.Add(contextEvidence: JsonNode)

        let workItemNode = JsonObject()
        workItemNode["id"] <- JsonValue.Create workItem
        workItemNode["type"] <- JsonValue.Create "mechanical"
        workItemNode["state"] <- JsonValue.Create "complete"
        workItemNode["semanticState"] <- JsonValue.Create "complete"
        workItemNode["evidence"] <- contextEvidenceArray
        workItemNode["updatedAt"] <- JsonValue.Create now
        workItemNode["completedAt"] <- JsonValue.Create now

        let workItems = JsonArray()
        workItems.Add(workItemNode: JsonNode)

        let context = JsonObject()
        context["schemaVersion"] <- JsonValue.Create "1.0.0"
        context["protocolVersion"] <- JsonValue.Create "1.0.0"
        context["repository"] <- JsonValue.Create slug
        context["actor"] <- JsonValue.Create "ros-bootstrap"
        context["startedAt"] <- JsonValue.Create now
        context["updatedAt"] <- JsonValue.Create now
        context["baselineDirtyPaths"] <- JsonArray()
        context["workItems"] <- workItems

        let queue = JsonObject()
        queue["schemaVersion"] <- JsonValue.Create "1.0.0"
        queue["repository"] <- JsonValue.Create slug
        queue["nextSeq"] <- JsonValue.Create 1
        queue["items"] <- JsonArray()

        context, event, queue

    let private writeLegacyInstallation (root: string) (payload: Payload) (observed: ObservedRepository) =
        let now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        let createdDate = now.Substring(0, 10)
        writeJsonFile (Path.Combine(root, Planning.LegacyManifestPath)) (legacyInstallationNode payload observed createdDate)

        let context, event, queue = installationAttribution payload now
        writeJsonFile (Path.Combine(root, ".ros", "context", "current.json")) context

        Directory.CreateDirectory(Path.Combine(root, ".ros", "events")) |> ignore

        File.WriteAllText(
            Path.Combine(root, ".ros", "events", "events.jsonl"),
            CanonicalJson.serializeCompact event + "\n",
            UTF8Encoding false
        )

        writeJsonFile (Path.Combine(root, ".ros", "work", "queue.json")) queue

        writeTextFile
            (Path.Combine(root, ".ros", "work", "queue.md"))
            "# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n"

        if payload.Profile = "project-administration" then
            let registry = JsonObject()
            registry["schemaVersion"] <- JsonValue.Create "1.0.0"
            registry["repos"] <- JsonArray()
            writeJsonFile (Path.Combine(root, ".ros", "hub", "registry.json")) registry

            writeTextFile
                (Path.Combine(root, ".ros", "hub", "registry.md"))
                "# Registered Repositories\n\n| ID | Name | Path |\n|---|---|---|\n"

    // ---------------------------------------------------------------------
    // Execution
    // ---------------------------------------------------------------------

    let private setMode (path: string) (executable: bool) =
        if not (OperatingSystem.IsWindows()) then
            let mode =
                if executable then
                    UnixFileMode.UserRead
                    ||| UnixFileMode.UserWrite
                    ||| UnixFileMode.UserExecute
                    ||| UnixFileMode.GroupRead
                    ||| UnixFileMode.GroupExecute
                    ||| UnixFileMode.OtherRead
                    ||| UnixFileMode.OtherExecute
                else
                    UnixFileMode.UserRead
                    ||| UnixFileMode.UserWrite
                    ||| UnixFileMode.GroupRead
                    ||| UnixFileMode.OtherRead

            File.SetUnixFileMode(path, mode)

    /// Apply a validated plan. Content is staged next to each destination and
    /// verified against the hash the plan calculated before any destination is
    /// replaced, so a failure is detectable and never a silent partial write.
    let execute (root: string) (payload: Payload) (installation: InstallationPlan) : Result<string list, string> =
        let byPath = payload.Files |> List.map (fun file -> file.Entry.Path, file) |> Map.ofList
        let stageSuffix = $".ros-stage-{Environment.ProcessId}"

        let writePaths =
            installation.Plan.Changes
            |> List.choose (function
                | PlannedChange.CreateFile(path, _, _)
                | PlannedChange.UpdateManagedFile(path, _, _) -> Map.tryFind path byPath
                | _ -> None)

        let staged = ResizeArray<string * string * PayloadFile>()

        let cleanup () =
            for _, stagePath, _ in staged do
                try
                    File.Delete stagePath
                with _ ->
                    ()

        try
            // Stage every file first.
            for file in writePaths do
                match Payload.resolveWithin root file.Entry.Path with
                | Error message -> failwith message
                | Ok destination ->
                    let stagePath = destination + stageSuffix
                    Directory.CreateDirectory(Path.GetDirectoryName destination: string) |> ignore
                    File.WriteAllBytes(stagePath, file.Content)
                    setMode stagePath file.Entry.Executable

                    let actual = Payload.sha256Hex (File.ReadAllBytes stagePath)

                    if actual <> file.Entry.Sha256 then
                        failwith $"staged content for {file.Entry.Path} does not match the planned hash; refusing to install it"

                    staged.Add(destination, stagePath, file)

            // Create directories the plan asked for that no staged file made.
            for change in installation.Plan.Changes do
                match change with
                | PlannedChange.CreateDirectory relative ->
                    match Payload.resolveWithin root relative with
                    | Error message -> failwith message
                    | Ok absolute -> Directory.CreateDirectory absolute |> ignore
                | _ -> ()

            // Commit.
            let applied = ResizeArray<string>()

            for destination, stagePath, file in staged do
                File.Move(stagePath, destination, true)
                setMode destination file.Entry.Executable
                applied.Add file.Entry.Path

            Ok(List.ofSeq applied)
        with error ->
            cleanup ()
            Error error.Message

    /// Write `.echelon/ros.json`. Always the last write of an installation, so
    /// an interrupted run leaves a manifest that under-claims rather than one
    /// that claims artifacts it never wrote.
    let writeInstallationManifest (root: string) (manifest: InstallationManifest) =
        let path = Path.Combine(root, Planning.ManifestPath)
        Directory.CreateDirectory(Path.GetDirectoryName path: string) |> ignore
        File.WriteAllText(path, LifecycleContract.renderManifest manifest, UTF8Encoding false)

    /// The legacy snapshot and attribution, written only when this is a fresh
    /// install (no `.ros/installation.json` yet). Re-running `init` never
    /// rewrites them, which is what keeps `init` idempotent despite their
    /// timestamps.
    let writeLegacyInstallationIfAbsent (root: string) (payload: Payload) (observed: ObservedRepository) =
        if not observed.LegacyManifestPresent then
            writeLegacyInstallation root payload observed
