namespace Ros.Cli

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module Foundations =
    [<Literal>]
    let ConfigRelativePath = ".echelon/foundations.json"

    type CapabilityRule =
        { Name: string
          Required: bool
          Version: string option
          SourceCommit: string option
          BoundaryManifest: string option }

    type CapabilityResult =
        { Name: string
          Required: bool
          ExpectedVersion: string option
          Installed: bool
          Pinned: bool
          Used: bool
          EvidencePresent: bool
          Passed: bool
          Details: string list }

    type Finding =
        { Code: string
          Capability: string
          Severity: string
          Message: string
          Remediation: string }

    type Report =
        { SchemaVersion: int
          Application: string
          Configuration: string
          Passed: bool
          Capabilities: CapabilityResult list
          Findings: Finding list }

    let private ignoredDirectories =
        set
            [ ".git"
              ".ros"
              ".sde"
              ".echelon"
              "node_modules"
              "bin"
              "obj"
              "dist"
              "coverage"
              ".cache"
              "archive"
              "research"
              "input-documents"
              "docs" ]

    let private sourceExtensions =
        set
            [ ".fs"
              ".fsx"
              ".cs"
              ".html"
              ".htm"
              ".css"
              ".js"
              ".mjs"
              ".cjs"
              ".ts"
              ".tsx"
              ".jsx"
              ".razor" ]

    let private projectExtensions = set [ ".fsproj"; ".csproj"; ".props"; ".targets" ]

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private stringProperty name element =
        tryProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                value.GetString() |> Option.ofObj
            else
                None)

    let private boolProperty defaultValue name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.True -> true
        | Some value when value.ValueKind = JsonValueKind.False -> false
        | _ -> defaultValue

    let private parseRule name (element: JsonElement) =
        { Name = name
          Required = boolProperty false "required" element
          Version = stringProperty "version" element
          SourceCommit = stringProperty "sourceCommit" element
          BoundaryManifest = stringProperty "boundaryManifest" element }

    let private readConfig root =
        let path = Path.Combine(root, ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar))

        if not (File.Exists path) then
            Error $"missing {ConfigRelativePath}"
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let rootElement = document.RootElement
                let schemaVersion =
                    tryProperty "schemaVersion" rootElement
                    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Number)
                    |> Option.map _.GetInt32()
                    |> Option.defaultValue 0

                if schemaVersion <> 1 then
                    Error $"{ConfigRelativePath} schemaVersion must be 1"
                else
                    match tryProperty "capabilities" rootElement with
                    | None -> Error $"{ConfigRelativePath} must contain a capabilities object"
                    | Some capabilities when capabilities.ValueKind <> JsonValueKind.Object ->
                        Error $"{ConfigRelativePath} capabilities must be an object"
                    | Some capabilities ->
                        let application =
                            stringProperty "application" rootElement
                            |> Option.defaultValue (DirectoryInfo(root).Name)

                        let rule name =
                            tryProperty name capabilities
                            |> Option.map (parseRule name)
                            |> Option.defaultValue
                                { Name = name
                                  Required = false
                                  Version = None
                                  SourceCommit = None
                                  BoundaryManifest = None }

                        Ok(
                            application,
                            [ rule "aegis"
                              rule "forma"
                              rule "folio"
                              rule "limen"
                              rule "ordo"
                              rule "praxis" ]
                        )
            with error ->
                Error $"{ConfigRelativePath} could not be parsed: {error.Message}"

    let rec private filesUnder root (directory: string) =
        seq {
            let info = DirectoryInfo directory

            for file in info.EnumerateFiles() do
                if file.Length <= 2L * 1024L * 1024L then
                    yield file.FullName

            for child in info.EnumerateDirectories() do
                if not (ignoredDirectories.Contains child.Name) then
                    yield! filesUnder root child.FullName
        }

    let private projectFiles root =
        filesUnder root root
        |> Seq.filter (fun path -> projectExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        |> Seq.toList

    let private sourceFiles root =
        filesUnder root root
        |> Seq.filter (fun path -> sourceExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        |> Seq.toList

    let private fileContainsAny (needles: string list) (path: string) =
        try
            let text = File.ReadAllText path
            needles |> List.exists (fun needle -> text.Contains(needle, StringComparison.OrdinalIgnoreCase))
        with _ ->
            false

    let private anySourceContains (root: string) (needles: string list) =
        sourceFiles root |> List.exists (fileContainsAny needles)

    let private allProjectText root =
        projectFiles root
        |> List.choose (fun path ->
            try Some(File.ReadAllText path) with _ -> None)
        |> String.concat "\n"

    let private tryPackageSpec (root: string) (packageName: string) : string option =
        let packageJson = Path.Combine(root, "package.json")

        if not (File.Exists packageJson) then
            None
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText packageJson)
                let sections = [ "dependencies"; "devDependencies"; "optionalDependencies"; "peerDependencies" ]

                sections
                |> List.tryPick (fun section ->
                    tryProperty section document.RootElement
                    |> Option.bind (fun dependencies ->
                        tryProperty packageName dependencies
                        |> Option.bind (fun value ->
                            if value.ValueKind = JsonValueKind.String then
                                value.GetString() |> Option.ofObj
                            else
                                None)))
            with _ ->
                None

    let private isFloatingSpec (spec: string) =
        let value = spec.Trim()
        value.StartsWith("^", StringComparison.Ordinal)
        || value.StartsWith("~", StringComparison.Ordinal)
        || value = "*"
        || value.Equals("latest", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(value, "(^|[#/@])main($|[/?#])", RegexOptions.IgnoreCase)

    let private npmPinned (expectedVersion: string option) (sourceCommit: string option) (spec: string option) =
        match spec with
        | None -> false
        | Some value when isFloatingSpec value -> false
        | Some value ->
            match sourceCommit, expectedVersion with
            | Some commit, _ -> value.Contains(commit, StringComparison.OrdinalIgnoreCase)
            | None, Some version ->
                value.Equals(version, StringComparison.OrdinalIgnoreCase)
                || value.Contains($"/v{version}/", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith($"#v{version}", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith($"@{version}", StringComparison.OrdinalIgnoreCase)
            | None, None -> true

    let private projectDependencyStatus (root: string) (packageName: string) (expectedVersion: string option) =
        let projectTexts =
            projectFiles root
            |> List.choose (fun path ->
                try Some(File.ReadAllText path) with _ -> None)

        let text = String.concat "\n" projectTexts

        let installed =
            projectTexts
            |> List.exists (fun projectText ->
                projectText.Contains("PackageReference", StringComparison.OrdinalIgnoreCase)
                && projectText.Contains(packageName, StringComparison.OrdinalIgnoreCase))

        let pinned =
            match expectedVersion with
            | None -> installed
            | Some version ->
                installed
                && text.Contains(version, StringComparison.OrdinalIgnoreCase)
                && not (text.Contains("Version=\"*\"", StringComparison.OrdinalIgnoreCase))
                && not (text.Contains("Version=\"latest\"", StringComparison.OrdinalIgnoreCase))

        installed, pinned

    let private manifestVersionMatches (root: string) (relativePath: string) (expectedVersion: string option) =
        let path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))

        if not (File.Exists path) then
            false, false
        else
            let text = File.ReadAllText path
            let pinned =
                match expectedVersion with
                | None -> true
                | Some version -> text.Contains(version, StringComparison.OrdinalIgnoreCase)

            true, pinned

    let private findingCode (capability: string) (suffix: string) =
        $"ECHELON-FND-{capability.ToUpperInvariant()}-{suffix}"

    let private verifyAegis (root: string) (rule: CapabilityRule) =
        let installed, pinned = projectDependencyStatus root "EchelonFoundry.Aegis.Core" rule.Version

        let used =
            anySourceContains
                root
                [ "open Aegis"
                  "Aegis.capture"
                  "Aegis.captureAsync"
                  "Aegis.guard"
                  "Aegis.guardAsync"
                  "Bootstrap.validate"
                  "Sinks.Collector" ]

        let boundaryManifest =
            rule.BoundaryManifest |> Option.defaultValue "aegis-boundaries.json"

        let evidence = File.Exists(Path.Combine(root, boundaryManifest.Replace('/', Path.DirectorySeparatorChar)))
        installed, pinned, used, evidence, [ $"boundary manifest: {boundaryManifest}" ]

    let private verifyForma (root: string) (rule: CapabilityRule) =
        let spec = tryPackageSpec root "@echelon-foundry/design-system"
        let installed = spec.IsSome
        let pinned = npmPinned rule.Version rule.SourceCommit spec

        let used =
            anySourceContains
                root
                [ "@echelon-foundry/design-system"
                  "design-system/all.css"
                  "<ef-button"
                  "<ef-input"
                  "<ef-field"
                  "<ef-card"
                  "<ef-dialog"
                  "<ef-alert"
                  "<ef-select"
                  "<ef-checkbox"
                  "<ef-toggle" ]

        let dependencyDetail = spec |> Option.defaultValue "missing"
        installed, pinned, used, used, [ $"dependency: {dependencyDetail}" ]

    let private verifyFolio (root: string) (rule: CapabilityRule) =
        let spec = tryPackageSpec root "@echelon-foundry/print-components"
        let installed = spec.IsSome
        let pinned = npmPinned rule.Version rule.SourceCommit spec

        let used =
            anySourceContains
                root
                [ "@echelon-foundry/print-components"
                  "<ef-print-"
                  "print-components/print.css"
                  "print-components/register" ]

        let dependencyDetail = spec |> Option.defaultValue "missing"
        installed, pinned, used, used, [ $"dependency: {dependencyDetail}" ]

    let private verifyLimen (root: string) (rule: CapabilityRule) =
        let spec = tryPackageSpec root "@echelon-foundry/typescript-wasm-kernel"
        let manifest =
            [ ".echelon/limen.json"; "limen.config.json" ]
            |> List.tryFind (fun relative ->
                File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))))

        let installed = spec.IsSome || manifest.IsSome
        let pinned = npmPinned rule.Version rule.SourceCommit spec || (manifest.IsSome && rule.Version.IsNone)

        let used =
            manifest.IsSome
            || anySourceContains
                root
                [ "@echelon-foundry/typescript-wasm-kernel"
                  "Limen"
                  "limen"
                  "WebAssembly" ]

        let manifestDetail = manifest |> Option.defaultValue "missing"
        installed, pinned, used, manifest.IsSome, [ $"manifest: {manifestDetail}" ]

    let private verifyOrdo (root: string) (rule: CapabilityRule) =
        let manifestInstalled, manifestPinned = manifestVersionMatches root ".echelon/sde.json" rule.Version
        let directoryInstalled = Directory.Exists(Path.Combine(root, ".sde"))
        let installed = manifestInstalled || directoryInstalled
        let pinned = if rule.Version.IsSome then manifestPinned else installed
        installed, pinned, installed, manifestInstalled, [ "manifest: .echelon/sde.json"; "state: .sde/" ]

    let private verifyPraxis (root: string) (rule: CapabilityRule) =
        let manifestInstalled, manifestPinned = manifestVersionMatches root ".echelon/ros.json" rule.Version
        let directoryInstalled = Directory.Exists(Path.Combine(root, ".ros"))
        let installed = manifestInstalled || directoryInstalled
        let pinned = if rule.Version.IsSome then manifestPinned else installed
        installed, pinned, installed, manifestInstalled, [ "manifest: .echelon/ros.json"; "state: .ros/" ]

    let private evaluate (root: string) (rule: CapabilityRule) =
        if not rule.Required then
            { Name = rule.Name
              Required = false
              ExpectedVersion = rule.Version
              Installed = false
              Pinned = false
              Used = false
              EvidencePresent = false
              Passed = true
              Details = [ "not applicable by repository declaration" ] },
            []
        else
            let installed, pinned, used, evidencePresent, details =
                match rule.Name with
                | "aegis" -> verifyAegis root rule
                | "forma" -> verifyForma root rule
                | "folio" -> verifyFolio root rule
                | "limen" -> verifyLimen root rule
                | "ordo" -> verifyOrdo root rule
                | "praxis" -> verifyPraxis root rule
                | _ -> false, false, false, false, []

            let result =
                { Name = rule.Name
                  Required = true
                  ExpectedVersion = rule.Version
                  Installed = installed
                  Pinned = pinned
                  Used = used
                  EvidencePresent = evidencePresent
                  Passed = installed && pinned && used && evidencePresent
                  Details = details }

            let findings =
                [ if not installed then
                      yield
                          { Code = findingCode rule.Name "001"
                            Capability = rule.Name
                            Severity = "error"
                            Message = $"{rule.Name} is required but is not installed/declared."
                            Remediation = $"Install the canonical {rule.Name} dependency or lifecycle component." }

                  if installed && not pinned then
                      yield
                          { Code = findingCode rule.Name "002"
                            Capability = rule.Name
                            Severity = "error"
                            Message = $"{rule.Name} is present but is not pinned to the declared immutable baseline."
                            Remediation = $"Pin {rule.Name} to the version/commit declared in {ConfigRelativePath}." }

                  if installed && pinned && not used then
                      yield
                          { Code = findingCode rule.Name "003"
                            Capability = rule.Name
                            Severity = "error"
                            Message = $"{rule.Name} is declared but no canonical application usage was found."
                            Remediation = $"Use the shared {rule.Name} capability in the applicable implementation boundary instead of merely listing it." }

                  if installed && pinned && used && not evidencePresent then
                      yield
                          { Code = findingCode rule.Name "004"
                            Capability = rule.Name
                            Severity = "error"
                            Message = $"{rule.Name} usage exists but required repository evidence/configuration is missing."
                            Remediation =
                                if rule.Name = "aegis" then
                                    "Add the declared Aegis machine-readable boundary manifest."
                                elif rule.Name = "limen" then
                                    "Add the Limen repository/application configuration manifest."
                                else
                                    $"Add repository evidence proving canonical {rule.Name} use." } ]

            result, findings

    let verify (root: string) : Result<Report, string> =
        let absoluteRoot = Path.GetFullPath root

        match readConfig absoluteRoot with
        | Error message -> Error message
        | Ok(application, rules) ->
            let evaluated = rules |> List.map (evaluate absoluteRoot)
            let capabilities = evaluated |> List.map fst
            let findings = evaluated |> List.collect snd

            Ok
                { SchemaVersion = 1
                  Application = application
                  Configuration = ConfigRelativePath
                  Passed = findings.IsEmpty
                  Capabilities = capabilities
                  Findings = findings }

    let private optionNode (value: string option) =
        match value with
        | Some text -> JsonValue.Create(text) :> JsonNode
        | None -> null

    let renderJson (report: Report) =
        let capabilities = JsonArray()

        for capability in report.Capabilities do
            let node = JsonObject()
            node["name"] <- JsonValue.Create capability.Name
            node["required"] <- JsonValue.Create capability.Required
            node["expectedVersion"] <- optionNode capability.ExpectedVersion
            node["installed"] <- JsonValue.Create capability.Installed
            node["pinned"] <- JsonValue.Create capability.Pinned
            node["used"] <- JsonValue.Create capability.Used
            node["evidencePresent"] <- JsonValue.Create capability.EvidencePresent
            node["passed"] <- JsonValue.Create capability.Passed

            let details = JsonArray()
            capability.Details |> List.iter (fun detail -> details.Add(JsonValue.Create detail))
            node["details"] <- details
            capabilities.Add(node)

        let findings = JsonArray()

        for finding in report.Findings do
            let node = JsonObject()
            node["code"] <- JsonValue.Create finding.Code
            node["capability"] <- JsonValue.Create finding.Capability
            node["severity"] <- JsonValue.Create finding.Severity
            node["message"] <- JsonValue.Create finding.Message
            node["remediation"] <- JsonValue.Create finding.Remediation
            findings.Add(node)

        let summary = JsonObject()
        summary["required"] <- JsonValue.Create(report.Capabilities |> List.filter _.Required |> List.length)
        summary["passed"] <- JsonValue.Create(report.Capabilities |> List.filter (fun item -> item.Required && item.Passed) |> List.length)
        summary["failed"] <- JsonValue.Create(report.Capabilities |> List.filter (fun item -> item.Required && not item.Passed) |> List.length)

        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create report.SchemaVersion
        root["tool"] <- JsonValue.Create "praxis"
        root["command"] <- JsonValue.Create "foundations verify"
        root["application"] <- JsonValue.Create report.Application
        root["configuration"] <- JsonValue.Create report.Configuration
        root["passed"] <- JsonValue.Create report.Passed
        root["capabilities"] <- capabilities
        root["findings"] <- findings
        root["summary"] <- summary
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2))

    let renderText (report: Report) =
        let lines = ResizeArray<string>()
        lines.Add($"Echelon Application Foundations: {report.Application}")
        lines.Add("")

        for capability in report.Capabilities do
            let status =
                if not capability.Required then "N/A"
                elif capability.Passed then "PASS"
                else "FAIL"

            let expected = capability.ExpectedVersion |> Option.map (sprintf " expected=%s") |> Option.defaultValue ""
            lines.Add(
                $"  [{status}] {capability.Name,-8} installed={capability.Installed} pinned={capability.Pinned} used={capability.Used} evidence={capability.EvidencePresent}{expected}"
            )

        if not report.Findings.IsEmpty then
            lines.Add("")
            lines.Add("Findings:")

            for finding in report.Findings do
                lines.Add($"  {finding.Code} {finding.Message}")
                lines.Add($"    REPAIR {finding.Remediation}")

        lines.Add("")
        lines.Add(if report.Passed then "application foundations passed" else $"application foundations failed with {report.Findings.Length} finding(s)")
        String.concat Environment.NewLine lines

    let run root asJson =
        match verify root with
        | Error message ->
            if asJson then
                let node = JsonObject()
                node["schemaVersion"] <- JsonValue.Create 1
                node["tool"] <- JsonValue.Create "praxis"
                node["command"] <- JsonValue.Create "foundations verify"
                node["passed"] <- JsonValue.Create false
                node["configuration"] <- JsonValue.Create ConfigRelativePath
                node["error"] <- JsonValue.Create message
                printf "%s" (node.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
            else
                eprintfn "ERROR %s" message

            2
        | Ok report ->
            if asJson then printf "%s" (renderJson report) else printfn "%s" (renderText report)
            if report.Passed then 0 else 3
