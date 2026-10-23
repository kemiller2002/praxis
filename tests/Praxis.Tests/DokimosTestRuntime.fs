namespace Praxis.Tests

open System
open System.IO
open Praxis.Infrastructure.Work

/// EP-A3-R2: the real-Dokimos completion cases. With `PRAXIS_TEST_DOKIMOS_DLL`
/// unset (a local run) a case reports itself skipped. Once it is set, as CI
/// sets it to a pinned Dokimos release, the Dokimos it names must exist and,
/// for an attested case, provide `completion.verify-attested/1`; otherwise
/// the case fails, so a misconfiguration can never pass as a silent skip.
[<RequireQualifiedAccess>]
module DokimosTestRuntime =
    [<Literal>]
    let Variable = "PRAXIS_TEST_DOKIMOS_DLL"

    let private configured () =
        Environment.GetEnvironmentVariable Variable
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    /// `None` only when the variable is unset; a set path that does not exist fails the case.
    let dll () =
        configured ()
        |> Option.map (fun path ->
            if File.Exists path then path
            else failwith $"{Variable} is set to '{path}', which does not exist: refusing to skip a real-Dokimos case")

    /// `None` only when the variable is unset; a Dokimos without attested mode fails the case.
    let attested () =
        dll ()
        |> Option.map (fun path ->
            match DokimosCompletionVerifier.supportsAttestation path (TimeSpan.FromSeconds 60.0) with
            | Ok() -> path
            | Error reason -> failwith $"{Variable} names a Dokimos without completion.verify-attested/1 ({reason}): refusing to skip an attested case")

    /// The line a skipped case prints; CI fails the job if one appears while the variable is set.
    let skip (suite: string) = eprintfn "SKIP %s: %s is not set to a built Dokimos.Cli.dll" suite Variable
