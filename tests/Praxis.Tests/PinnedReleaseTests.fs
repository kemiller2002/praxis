namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes

/// The released Praxis that `.echelon/toolchain.json` pins must accept this
/// repository's own records, not only the validator built from source. When the
/// two disagree (as the released 3.11.0 did with the interrupted-predecessor
/// finalization WI-0088 introduced), the pinned tool rejects the repository
/// that pins it. Run with `--suite pinned-release`; the binary is
/// `PRAXIS_PINNED_RELEASE`, installed and verified (SHA-256 and
/// build-provenance attestation) by `scripts/praxis-bootstrap.sh`. There is
/// no fallback: without the binary the suite fails rather than passing
/// vacuously.
[<RequireQualifiedAccess>]
module PinnedReleaseTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, ".echelon", "toolchain.json")) && File.Exists(Path.Combine(directory.FullName, "Praxis.slnx")) then
            directory.FullName
        else
            match directory.Parent with
            | null -> failwith "repository root not found"
            | parent -> repositoryRoot parent

    let private root () = repositoryRoot (DirectoryInfo(AppContext.BaseDirectory))

    let private pinnedBinary () =
        match Environment.GetEnvironmentVariable "PRAXIS_PINNED_RELEASE" with
        | null
        | "" -> failwith "PRAXIS_PINNED_RELEASE is not set: run scripts/praxis-bootstrap.sh and point it at the verified binary it prints"
        | path when not (File.Exists path) -> failwith $"PRAXIS_PINNED_RELEASE does not exist: {path}"
        | path -> path

    let private pinnedVersion (root: string) =
        let toolchain = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".echelon", "toolchain.json")))
        (nonNull toolchain["praxis"]).GetValue<string>()

    /// The harness clears ambient Praxis variables; CI's committed-range base
    /// (`PRAXIS_BASE_REF`) is passed back so the pinned release checks the same
    /// range the source-built `./praxis validate` does.
    let private baseRef () =
        match Environment.GetEnvironmentVariable "PRAXIS_BASE_REF" with
        | null
        | "" -> []
        | value -> [ "PRAXIS_BASE_REF", value ]

    let private pinned (root: string) (arguments: string list) =
        CliHarness.run (pinnedBinary ()) ([ "--root"; root ] @ arguments) (baseRef ())

    /// Executions finalized with an `interruption` block: the record shape the
    /// released 3.11.0 refused.
    let private interruptedExecutions (root: string) =
        Directory.GetFiles(Path.Combine(root, ".ros", "telemetry", "executions"), "*.json")
        |> Array.filter (fun path ->
            let events = JsonNode.Parse(File.ReadAllText path)["events"]

            match events with
            | :? JsonArray as items -> items |> Seq.exists (fun item -> not (isNull item) && not (isNull item["interruption"]))
            | _ -> false)
        |> Array.toList

    let tests =
        [ { Name = "pinned release: the binary reports the version .echelon/toolchain.json pins"
            Run =
              fun () ->
                  let root = root ()
                  let result = CliHarness.run (pinnedBinary ()) [ "--version" ] []
                  Assert.equal 0 result.Exit
                  Assert.equal $"praxis {pinnedVersion root}" (result.Out.Trim()) }
          { Name = "pinned release: validates this repository's own records"
            Run =
              fun () ->
                  let result = pinned (root ()) [ "validate" ]
                  Assert.isTrue (result.Exit = 0) $"the pinned release rejected the repository (exit {result.Exit}):\n{result.Out}\n{result.Err}" }
          { Name = "pinned release: the records include an interrupted-predecessor finalization"
            Run =
              fun () ->
                  // Guards the test above against passing only because the
                  // shape the released validator once refused is absent.
                  Assert.isTrue (not (interruptedExecutions (root ())).IsEmpty) "no execution record carries an interruption block" }
          { Name = "pinned release: the artifact registry check accepts this repository"
            Run =
              fun () ->
                  let result = pinned (root ()) [ "registry"; "check" ]
                  Assert.isTrue (result.Exit = 0) $"the pinned release's registry check failed (exit {result.Exit}):\n{result.Out}\n{result.Err}" } ]
