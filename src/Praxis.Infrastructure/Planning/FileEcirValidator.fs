namespace Praxis.Infrastructure.Planning

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json

/// This is provided by trusted host installation/Conditor release metadata,
/// NEVER by an AI-generated ECIR document or editable Praxis group context.
type PinnedOrdoRelease =
    { Executable: string
      Sha256: string }

/// A verified validator observation, not decision approval or evidence that
/// a requirement was implemented. Callers MUST still independently authorize
/// each decision and account for every cohort member.
type EcirValidatorResult =
    { BlueprintDigest: string
      SourceRequirements: int
      RepresentedRequirements: int
      ModeledRequirements: int
      UnresolvedRequirements: int
      DeferredRequirements: int
      ValidatedByExecutableSha256: string }

[<RequireQualifiedAccess>]
module FileEcirValidator =
    let private digest bytes =
        "sha256:" + (SHA256.HashData bytes |> Convert.ToHexString).ToLowerInvariant()

    let private property (name: string) (json: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if json.ValueKind = JsonValueKind.Object && json.TryGetProperty(name, &value) then Some value
        else None

    let private asText name json =
        property name json
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                value.GetString() |> Option.ofObj
            else None)

    let private asCount name json =
        property name json
        |> Option.bind (fun value ->
            if value.ValueKind <> JsonValueKind.Number then None
            else match value.TryGetInt32() with
                 | true, n when n >= 0 -> Some n
                 | _ -> None)

    /// Only accept Ordo's exact typed, source-complete non-authorizing
    /// response. Never parse a freeform model explanation as an attestation.
    let parseResponse (expectedBlueprintDigest: string) (pinnedBinaryDigest: string) (raw: string) =
        try
            use json = JsonDocument.Parse raw
            let entry = json.RootElement
            match
                asText "schemaVersion" entry,
                asText "status" entry,
                asText "blueprintDigest" entry,
                asCount "sourceRequirements" entry,
                asCount "representedRequirements" entry,
                asCount "modeledRequirements" entry,
                asCount "unresolvedRequirements" entry,
                asCount "deferredRequirements" entry,
                property "executionAuthorized" entry
            with
            | Some "ecir.validate/1", Some "trace-validated", Some digest,
              Some imported, Some represented, Some modeled, Some unresolved, Some deferred,
              Some authority
                when digest = expectedBlueprintDigest
                     && authority.ValueKind = JsonValueKind.False
                     && imported > 0
                     && imported = represented ->
                    Ok {
                        BlueprintDigest = digest
                        SourceRequirements = imported
                        RepresentedRequirements = represented
                        ModeledRequirements = modeled
                        UnresolvedRequirements = unresolved
                        DeferredRequirements = deferred
                        ValidatedByExecutableSha256 = pinnedBinaryDigest }
            | _ -> Error "Ordo result lacked exact source parity, pinned blueprint identity, schema or non-authorization"
        with :? JsonException ->
            Error "Ordo returned malformed validation JSON"

    /// Run the exact host-pinned Ordo native executable. The model must not
    /// choose either the binary or its expected hash. The committed inputs
    /// are furnished by FileEcirPreflight.readCommitted, not a working tree.
    let validate
        (pinned: PinnedOrdoRelease)
        (committed: CommittedEcirDocuments)
        (expectedBlueprintDigest: string)
        : Result<EcirValidatorResult, string> =
        if String.IsNullOrWhiteSpace pinned.Executable
           || not (Path.IsPathFullyQualified pinned.Executable)
           || not (File.Exists pinned.Executable) then
            Error "host-pinned Ordo executable unavailable"
        elif String.IsNullOrWhiteSpace pinned.Sha256
             || pinned.Sha256.Length <> 71 then
            Error "host-pinned Ordo binary fingerprint missing"
        elif String.IsNullOrWhiteSpace expectedBlueprintDigest
             || expectedBlueprintDigest.Length <> 71 then
            Error "expected Ordo blueprint fingerprint missing"
        else
            try
                let actual = File.ReadAllBytes pinned.Executable |> digest
                if not (String.Equals(actual, pinned.Sha256, StringComparison.Ordinal)) then
                    Error "installed Ordo executable does not match its trusted SHA-256 pin"
                else
                    let directory = Path.Combine(Path.GetTempPath(), "praxis-ecir-" + Guid.NewGuid().ToString("N"))
                    Directory.CreateDirectory directory |> ignore
                    try
                        let manifestFile = Path.Combine(directory, "manifest.json")
                        let blueprintFile = Path.Combine(directory, "blueprint.json")
                        File.WriteAllText(manifestFile, committed.Manifest)
                        File.WriteAllText(blueprintFile, committed.Blueprint)

                        use process = new Process()
                        process.StartInfo <- ProcessStartInfo(pinned.Executable)
                        process.StartInfo.WorkingDirectory <- directory
                        process.StartInfo.ArgumentList.Add("ecir")
                        process.StartInfo.ArgumentList.Add("validate")
                        process.StartInfo.ArgumentList.Add("--manifest")
                        process.StartInfo.ArgumentList.Add(manifestFile)
                        process.StartInfo.ArgumentList.Add("--blueprint")
                        process.StartInfo.ArgumentList.Add(blueprintFile)
                        process.StartInfo.ArgumentList.Add("--json")
                        process.StartInfo.UseShellExecute <- false
                        process.StartInfo.RedirectStandardOutput <- true
                        process.StartInfo.RedirectStandardError <- true

                        if not (process.Start()) then
                            Error "pinned Ordo executable failed to start"
                        else
                            let stdout = process.StandardOutput.ReadToEndAsync()
                            let stderr = process.StandardError.ReadToEndAsync()
                            if not (process.WaitForExit(30000)) then
                                process.Kill(true)
                                Error "pinned Ordo validation exceeded 30 seconds"
                            else
                                let response = stdout.Result
                                let diagnostics = stderr.Result
                                if process.ExitCode <> 0 then
                                    Error("Ordo refused ECIR: " + (if String.IsNullOrWhiteSpace diagnostics then response else diagnostics))
                                elif response.Length > 16384 then
                                    Error "pinned Ordo validation response exceeded safe limit"
                                else
                                    parseResponse expectedBlueprintDigest pinned.Sha256 response
                    finally
                        Directory.Delete(directory, true)
            with
            | :? IOException as failure -> Error("Ordo preflight I/O failure: " + failure.Message)
            | :? UnauthorizedAccessException as failure -> Error("Ordo preflight denied access: " + failure.Message)
            | :? Win32Exception as failure -> Error("pinned Ordo process could not launch: " + failure.Message)
            | :? InvalidOperationException as failure -> Error("Ordo preflight invalid process state: " + failure.Message)
