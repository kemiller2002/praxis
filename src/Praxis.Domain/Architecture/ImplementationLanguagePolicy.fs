namespace Praxis.Domain.Architecture

open System
open System.Text.RegularExpressions

/// Why a repository path is a prohibited Node/JavaScript/TypeScript
/// artifact. The two cases stay distinct because their repair differs:
/// source is rewritten in F#, while a toolchain manifest is simply removed.
[<RequireQualifiedAccess>]
type ProhibitedArtifact =
    | NodeSource of extension: string
    | NodeToolchainManifest of fileName: string
    /// A tracked Python source file (RQ-ROS-2026-A024, DF-ROS-2026-A054).
    | PythonSource
    /// A `python`/`python3` invocation, or Python set-up in a workflow, at
    /// this 1-based line of a tracked script, workflow or action.
    | PythonInvocation of line: int

/// A narrowly documented, approved exception: one exact file path, or one
/// directory (a path ending in `/`), justified by an accepted `DF-` decision.
type ImplementationException = { Path: string; Decision: string }

/// `ros.json` `implementationPolicy`: a repository opts in explicitly, so a
/// project that legitimately owns JavaScript is never failed by default.
type ImplementationPolicy =
    { ProhibitNodeArtifacts: bool
      /// Opt-in like `ProhibitNodeArtifacts`: tracked Python, and Python
      /// invoked by repository-owned automation, is prohibited.
      ProhibitPythonAutomation: bool
      Exceptions: ImplementationException list }

type ImplementationViolation =
    { Path: string
      Artifact: ProhibitedArtifact }

type ImplementationPolicyFinding =
    { Path: string
      Field: string
      Message: string }

[<RequireQualifiedAccess>]
module ImplementationLanguagePolicy =
    let prohibitedExtensions = [ ".js"; ".jsx"; ".mjs"; ".cjs"; ".ts"; ".tsx" ]

    let prohibitedFileNames =
        [ "package.json"
          "package-lock.json"
          "npm-shrinkwrap.json"
          "yarn.lock"
          "pnpm-lock.yaml"
          "bun.lock"
          "bun.lockb"
          "tsconfig.json" ]

    let disabled =
        { ProhibitNodeArtifacts = false
          ProhibitPythonAutomation = false
          Exceptions = [] }

    /// Files whose text is scanned for Python invocations: shell and
    /// PowerShell scripts, and workflow or action YAML.
    let automationExtensions = [ ".sh"; ".bash"; ".ps1"; ".yml"; ".yaml" ]

    /// A `python`/`python3`/`python3.N` command word (after a line start,
    /// whitespace, `;`, `|`, `&`, `(`, `$(` or a backtick, and followed by
    /// whitespace or the end of the line), or `actions/setup-python`.
    let private pythonCommand =
        Regex(@"(?:^|[\s;|&(`])python(?:3(?:\.\d+)?)?(?=\s|$)|actions/setup-python@", RegexOptions.CultureInvariant)

    let private decisionPattern = Regex(@"^DF-[A-Z0-9]+(-[A-Z0-9]+)+$", RegexOptions.CultureInvariant)

    let private normalize (path: string) = path.Replace('\\', '/')

    let private fileName (path: string) =
        match path.LastIndexOf '/' with
        | -1 -> path
        | index -> path.Substring(index + 1)

    let private extension (name: string) =
        match name.LastIndexOf '.' with
        | index when index > 0 -> name.Substring(index).ToLowerInvariant()
        | _ -> ""

    /// Classifies one repository-relative path; `None` means permitted.
    /// Names are matched exactly and extensions case-insensitively, so
    /// `Package.json` is not a manifest but `app.TS` is TypeScript source.
    let classify (path: string) : ProhibitedArtifact option =
        let name = path |> normalize |> fileName

        if prohibitedFileNames |> List.contains name then
            Some(ProhibitedArtifact.NodeToolchainManifest name)
        else
            let suffix = extension name

            if prohibitedExtensions |> List.contains suffix then
                Some(ProhibitedArtifact.NodeSource suffix)
            else
                None

    let private covers (path: string) (ex: ImplementationException) =
        let target = normalize ex.Path

        if target.EndsWith("/", StringComparison.Ordinal) then
            path.StartsWith(target, StringComparison.Ordinal)
        else
            String.Equals(path, target, StringComparison.Ordinal)

    let private owned (policy: ImplementationPolicy) (paths: string list) =
        paths
        |> List.map normalize
        |> List.distinct
        |> List.filter (fun path -> not (path.StartsWith(".git/", StringComparison.Ordinal)))
        |> List.filter (fun path -> not (policy.Exceptions |> List.exists (covers path)))

    /// Deterministic: ordinal path order, one violation per path, regardless
    /// of the order or duplication of the input listing.
    let violations (policy: ImplementationPolicy) (paths: string list) : ImplementationViolation list =
        let node =
            if not policy.ProhibitNodeArtifacts then []
            else owned policy paths |> List.choose (fun path -> classify path |> Option.map (fun artifact -> { Path = path; Artifact = artifact }))

        let python =
            if not policy.ProhibitPythonAutomation then []
            else
                owned policy paths
                |> List.filter (fun path -> extension (fileName path) = ".py")
                |> List.map (fun path -> { Path = path; Artifact = ProhibitedArtifact.PythonSource })

        node @ python |> List.sortWith (fun a b -> String.CompareOrdinal(a.Path, b.Path))

    /// Whether a path's text must be scanned for Python invocations.
    let scansContent (policy: ImplementationPolicy) (path: string) =
        policy.ProhibitPythonAutomation
        && automationExtensions |> List.contains (extension (fileName (normalize path)))
        && not (policy.Exceptions |> List.exists (covers (normalize path)))

    /// The 1-based lines of `text` that invoke Python. Comment lines (`#`)
    /// are not commands and are ignored.
    let pythonInvocations (text: string) : int list =
        text.Replace("\r\n", "\n").Split('\n')
        |> Array.mapi (fun index line -> index + 1, line)
        |> Array.filter (fun (_, line) -> not (line.TrimStart().StartsWith "#") && pythonCommand.IsMatch line)
        |> Array.map fst
        |> List.ofArray

    /// Python invocations in the scanned automation files, one violation
    /// per line, in path then line order.
    let contentViolations (policy: ImplementationPolicy) (files: (string * string) list) : ImplementationViolation list =
        files
        |> List.filter (fun (path, _) -> scansContent policy path)
        |> List.collect (fun (path, text) ->
            pythonInvocations text |> List.map (fun line -> { Path = normalize path; Artifact = ProhibitedArtifact.PythonInvocation line }))
        |> List.sortWith (fun a b ->
            match String.CompareOrdinal(a.Path, b.Path) with
            | 0 ->
                match a.Artifact, b.Artifact with
                | ProhibitedArtifact.PythonInvocation left, ProhibitedArtifact.PythonInvocation right -> compare left right
                | _ -> 0
            | order -> order)

    let describe (violation: ImplementationViolation) =
        match violation.Artifact with
        | ProhibitedArtifact.NodeSource suffix ->
            $"repository-owned JavaScript/TypeScript source ('{suffix}') is prohibited; implement this behaviour in F#/.NET"
        | ProhibitedArtifact.NodeToolchainManifest name ->
            $"repository-owned Node toolchain file '{name}' is prohibited; the repository must not depend on npm or a Node runtime"
        | ProhibitedArtifact.PythonSource ->
            "repository-owned Python is prohibited; implement this automation in F#/.NET or record an approved exception"
        | ProhibitedArtifact.PythonInvocation line ->
            $"line {line} runs Python; repository-owned automation must be F#/.NET (for example a dotnet fsi script) or covered by an approved exception"

    /// Policy configuration errors. An exception must name a real path and an
    /// accepted decision; wildcards are refused so an exception can never
    /// silently widen beyond what was approved.
    let configurationFindings (policy: ImplementationPolicy) : ImplementationPolicyFinding list =
        policy.Exceptions
        |> List.mapi (fun index ex ->
            let field = $"implementationPolicy.exceptions[{index}]"

            [ if String.IsNullOrWhiteSpace ex.Path then
                  { Path = "ros.json"; Field = field + ".path"; Message = "exception path must be a non-empty repository-relative path" }
              elif ex.Path.IndexOfAny([| '*'; '?'; '[' |]) >= 0 then
                  { Path = "ros.json"; Field = field + ".path"; Message = $"exception path '{ex.Path}' must not contain wildcards" }

              if not (decisionPattern.IsMatch(ex.Decision)) then
                  { Path = "ros.json"
                    Field = field + ".decision"
                    Message = $"exception '{ex.Path}' must cite the accepted DF- decision that approves it" } ])
        |> List.concat

    /// Every finding the policy produces, in the shared validation shape.
    let findings (policy: ImplementationPolicy) (paths: string list) (contents: (string * string) list) : ImplementationPolicyFinding list =
        configurationFindings policy
        @ (violations policy paths @ contentViolations policy contents
           |> List.map (fun violation ->
               { Path = violation.Path
                 Field = "implementation_language"
                 Message = describe violation }))
