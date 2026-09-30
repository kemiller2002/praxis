namespace Ros.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.RegularExpressions

/// Deterministic, verified Praxis bootstrap (PRAXIS-REMOTE-05,
/// DF-ROS-2026-A041 section 9): the pinned version is installed or the run
/// fails explicitly; nothing ever falls forward to another version. Runs the
/// POSIX script scripts/praxis-bootstrap.sh against fake `file://` releases
/// and a stub `gh` on PATH. Ported from the former
/// tests/praxis-bootstrap.test.mjs; skipped on Windows and on platforms
/// without a published native bundle.
[<RequireQualifiedAccess>]
module PraxisBootstrapScriptTests =
    type private Release = { Url: string; Digest: string }

    let private repositoryFile (relative: string) =
        Path.Combine(CliPort.repositoryRoot.Value, relative)

    let private bootstrap () = repositoryFile "scripts/praxis-bootstrap.sh"

    /// The runtime identifier of the bundle the script downloads here.
    let private rid =
        match RuntimeInformation.OSArchitecture with
        | Architecture.X64 when OperatingSystem.IsLinux() -> Some "linux-x64"
        | Architecture.Arm64 when OperatingSystem.IsLinux() -> Some "linux-arm64"
        | Architecture.Arm64 when OperatingSystem.IsMacOS() -> Some "osx-arm64"
        | Architecture.X64 when OperatingSystem.IsMacOS() -> Some "osx-x64"
        | _ -> None

    let private doesNotMatch (pattern: string) (text: string) =
        if Regex.IsMatch(text, pattern, RegexOptions.Multiline) then
            failwith $"Expected text not matching /{pattern}/ but received:\n{text}"

    let private fileSha256 (path: string) =
        Convert.ToHexString(Security.Cryptography.SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

    let private writeExecutable (path: string) (content: string) =
        File.WriteAllText(path, content)
        CliPort.makeExecutable path

    /// A fake release directory whose bundle's binary reports `reports`.
    let private release (temporary: string -> string) (rid: string) (reports: string) (corruptChecksum: bool) =
        let root = temporary "praxis-bootstrap-release"
        let stage = Path.Combine(root, "stage", $"praxis-{rid}")
        Directory.CreateDirectory stage |> ignore
        writeExecutable (Path.Combine(stage, "praxis")) $"#!/bin/sh\necho \"ros-fs {reports}\"\n"
        let asset = $"praxis-{rid}.tar.gz"

        let packed =
            CliHarness.run "tar" [ "-czf"; Path.Combine(root, asset); "-C"; Path.Combine(root, "stage"); $"praxis-{rid}" ] []

        CliPort.exitCode 0 packed
        let digest = fileSha256 (Path.Combine(root, asset))
        let published = if corruptChecksum then String('0', 64) else digest
        File.WriteAllText(Path.Combine(root, "native-checksums.txt"), $"{published}  {asset}\n")
        { Url = $"file://{root}"; Digest = digest }

    /// A toolchain manifest file; `None` leaves it absent.
    let private manifest (temporary: string -> string) (content: string option) =
        let file = Path.Combine(temporary "praxis-bootstrap-manifest", "toolchain.json")
        content |> Option.iter (fun text -> File.WriteAllText(file, text))
        file

    let private pinned = """{"schemaVersion":1,"ordo":"1.4.0","praxis":"9.9.9"}"""

    /// A PATH directory holding a stub `gh` that succeeds or fails.
    let private stubGh (temporary: string -> string) (succeeds: bool) =
        let directory = temporary "praxis-bootstrap-gh"
        let body = if succeeds then "exit 0" else "echo 'no attestation' >&2; exit 1"
        writeExecutable (Path.Combine(directory, "gh")) $"#!/bin/sh\n{body}\n"
        directory

    let private withGh (directory: string) =
        let basePath = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""
        "PATH", directory + string Path.PathSeparator + basePath

    let private run (arguments: string list) (environment: (string * string) list) =
        let result = CliHarness.run "sh" (bootstrap () :: arguments) environment
        { result with Out = result.Out.Trim() }

    let private runPinned (temporary: string -> string) (install: string) (extra: string list) environment =
        run ([ "--manifest"; manifest temporary (Some pinned); "--install-dir"; install ] @ extra) environment

    let private skipAttestation = [ "--attestation"; "skip" ]

    let private entries (directory: string) =
        Directory.GetFileSystemEntries directory |> Array.map Path.GetFileName |> Array.toList

    /// Runs `body` with this platform's RID and a temporary-directory factory;
    /// a no-op where the script cannot run or no bundle is published.
    let private supported (body: string -> (string -> string) -> unit) =
        match rid with
        | Some rid when not (OperatingSystem.IsWindows()) -> CliPort.withTemporaries (body rid)
        | _ -> ()

    let tests =
        [ { Name = "praxis bootstrap: installs exactly the pinned version, verified, and reports it"
            Run =
              fun () ->
                  supported (fun rid temporary ->
                      let fake = release temporary rid "9.9.9" false
                      let install = temporary "praxis-bootstrap-install"
                      let outputs = Path.Combine(install, "github-output")

                      let result =
                          runPinned temporary install skipAttestation [ "PRAXIS_RELEASE_BASE_URL", fake.Url; "GITHUB_OUTPUT", outputs ]

                      CliPort.exitCode 0 result
                      CliPort.matches "attestation verification explicitly skipped" result.Err
                      Assert.equal "ros-fs 9.9.9" ((CliHarness.run result.Out [ "--version" ] []).Out.Trim())
                      let written = File.ReadAllText outputs
                      CliPort.matches @"^version=9\.9\.9$" written
                      CliPort.matches $"^digest=sha256:{fake.Digest}$" written
                      CliPort.matches "^attestation=skip$" written) }

          { Name = "praxis bootstrap: a missing, malformed, or non-exact pin fails instead of falling forward"
            Run =
              fun () ->
                  supported (fun rid temporary ->
                      let fake = release temporary rid "9.9.9" false
                      let install = temporary "praxis-bootstrap-install"

                      let cases =
                          [ None
                            Some "{not json"
                            Some """{"schemaVersion":1,"ordo":"1.4.0"}"""
                            Some """{"schemaVersion":1,"praxis":"latest"}"""
                            Some """{"schemaVersion":1,"praxis":"3.4"}"""
                            Some """{"schemaVersion":2,"praxis":"9.9.9"}""" ]

                      for content in cases do
                          let file = manifest temporary content

                          let result =
                              run ([ "--manifest"; file; "--install-dir"; install ] @ skipAttestation) [ "PRAXIS_RELEASE_BASE_URL", fake.Url ]

                          if result.Exit <> 3 then
                              failwith $"{content}: expected exit 3 but received {result.Exit}: {result.Err}"

                      Assert.empty (entries install)) }

          { Name = "praxis bootstrap: an unavailable pinned release fails explicitly"
            Run =
              fun () ->
                  supported (fun _ temporary ->
                      let empty = temporary "praxis-bootstrap-empty-release"

                      let result =
                          runPinned temporary (temporary "praxis-bootstrap-install") skipAttestation [ "PRAXIS_RELEASE_BASE_URL", $"file://{empty}" ]

                      CliPort.exitCode 4 result
                      CliPort.matches @"9\.9\.9" result.Err) }

          { Name = "praxis bootstrap: a checksum mismatch is refused and nothing is installed"
            Run =
              fun () ->
                  supported (fun rid temporary ->
                      let fake = release temporary rid "9.9.9" true
                      let install = temporary "praxis-bootstrap-install"
                      let result = runPinned temporary install skipAttestation [ "PRAXIS_RELEASE_BASE_URL", fake.Url ]
                      CliPort.exitCode 5 result
                      CliPort.matches "checksum mismatch" result.Err
                      Assert.empty (entries install)) }

          { Name = "praxis bootstrap: a binary reporting another version is a version mismatch"
            Run =
              fun () ->
                  supported (fun rid temporary ->
                      let fake = release temporary rid "9.9.8" false

                      let result =
                          runPinned temporary (temporary "praxis-bootstrap-install") skipAttestation [ "PRAXIS_RELEASE_BASE_URL", fake.Url ]

                      CliPort.exitCode 6 result
                      CliPort.matches @"reports version '9\.9\.8', not the pinned 9\.9\.9" result.Err) }

          { Name = "praxis bootstrap: attestation is required by default and a failed verification is refused"
            Run =
              fun () ->
                  supported (fun rid temporary ->
                      let fake = release temporary rid "9.9.9" false
                      let install = temporary "praxis-bootstrap-install"

                      let refused =
                          runPinned temporary install [] [ "PRAXIS_RELEASE_BASE_URL", fake.Url; withGh (stubGh temporary false) ]

                      CliPort.exitCode 5 refused
                      CliPort.matches "no valid build-provenance attestation" refused.Err

                      let verified =
                          runPinned temporary install [] [ "PRAXIS_RELEASE_BASE_URL", fake.Url; withGh (stubGh temporary true) ]

                      CliPort.exitCode 0 verified) }

          { Name = "praxis bootstrap: a cache populated without attestation never satisfies a run that requires it"
            Run =
              fun () ->
                  supported (fun rid temporary ->
                      let fake = release temporary rid "9.9.9" false
                      let install = temporary "praxis-bootstrap-install"
                      let skipped = runPinned temporary install skipAttestation [ "PRAXIS_RELEASE_BASE_URL", fake.Url ]
                      CliPort.exitCode 0 skipped

                      let required =
                          runPinned temporary install [] [ "PRAXIS_RELEASE_BASE_URL", fake.Url; withGh (stubGh temporary false) ]

                      CliPort.exitCode 5 required

                      let cachedAgain = runPinned temporary install skipAttestation [ "PRAXIS_RELEASE_BASE_URL", fake.Url ]
                      CliPort.matches @"using cached Praxis 9\.9\.9" cachedAgain.Err) }

          { Name = "praxis bootstrap: a tampered cache entry is re-verified and replaced, never trusted"
            Run =
              fun () ->
                  supported (fun rid temporary ->
                      let fake = release temporary rid "9.9.9" false
                      let install = temporary "praxis-bootstrap-install"
                      CliPort.exitCode 0 (runPinned temporary install skipAttestation [ "PRAXIS_RELEASE_BASE_URL", fake.Url ])
                      File.AppendAllText(Path.Combine(install, $"9.9.9-{fake.Digest}", $"praxis-{rid}.tar.gz"), "tampered")
                      let again = runPinned temporary install skipAttestation [ "PRAXIS_RELEASE_BASE_URL", fake.Url ]
                      CliPort.exitCode 0 again
                      doesNotMatch "using cached" again.Err) }

          { Name = "praxis bootstrap: echelon install fails when the pinned installer cannot be downloaded (no silent success)"
            Run =
              fun () ->
                  if not (OperatingSystem.IsWindows()) then
                      CliPort.withTemporaries (fun temporary ->
                          let home = temporary "praxis-bootstrap-echelon-home"
                          let installers = temporary "praxis-bootstrap-no-installers"

                          let result =
                              CliHarness.run
                                  "sh"
                                  [ repositoryFile "bin/echelon.sh"; "install"; "praxis"; "99.99.99" ]
                                  [ "ECHELON_HOME", home; "ECHELON_INSTALLER_BASE_URL", $"file://{installers}" ]

                          CliPort.exitCode 1 result
                          CliPort.matches @"pinned version 99\.99\.99" result.Err
                          Assert.isTrue (not (Directory.Exists(Path.Combine(home, "tools", "praxis")))) "nothing was installed") }

          { Name = "praxis bootstrap: released native assets are immutable, attested, and the setup action pins its dependencies"
            Run =
              fun () ->
                  let workflow = File.ReadAllText(repositoryFile ".github/workflows/native-release.yml")
                  doesNotMatch "--clobber" workflow
                  CliPort.matches "actions/attest-build-provenance@[0-9a-f]{40}" workflow
                  CliPort.matches "attestations: write" workflow

                  let action = File.ReadAllText(repositoryFile ".github/actions/praxis-setup/action.yml")

                  let uses =
                      Regex.Matches(action, @"uses:\s*(\S+)") |> Seq.map (fun found -> (found.Groups[1]).Value) |> Seq.toList

                  Assert.isTrue (not uses.IsEmpty) "the setup action uses pinned dependencies"

                  for reference in uses do
                      if not (Regex.IsMatch(reference, "@[0-9a-f]{40}$")) then
                          failwith $"{reference} must be pinned to a commit SHA"

                  if Regex.IsMatch(action, @"\$\{\{\s*inputs\.[a-z]+\s*\}\}""?\s*--") then
                      failwith "inputs reach the script through the environment, not interpolated into run" } ]
