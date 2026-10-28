namespace Praxis.Domain.Autonomy

open System
open Praxis.Domain.Execution

/// Which owner-controlled classes a candidate's changes touch. Classification
/// is by effect, not only by location: removing a test, changing a test
/// project or editing a script CI runs weakens verification wherever the file
/// lives, so it is owner-controlled even outside any declared path.
[<RequireQualifiedAccess>]
module Classification =
    let private path pattern cls = { Pattern = pattern; Class = cls }

    /// The built-in floor. A policy adds to it and can never remove from it,
    /// so a policy cannot unprotect itself, the workflows that verify it, or
    /// the repository's review rules.
    let floor: ProtectedPath list =
        [ path ".praxis/autonomy/**" ProtectedClass.ExecutionPolicy
          path ".github/workflows/**" ProtectedClass.GitHubWorkflows
          path ".github/actions/**" ProtectedClass.GitHubWorkflows
          path "**/action.yml" ProtectedClass.GitHubWorkflows
          path "**/action.yaml" ProtectedClass.GitHubWorkflows
          path "CODEOWNERS" ProtectedClass.RepositoryRules
          path ".github/CODEOWNERS" ProtectedClass.RepositoryRules
          path "docs/CODEOWNERS" ProtectedClass.RepositoryRules
          path ".github/settings.yml" ProtectedClass.RepositoryRules
          path "ros.json" ProtectedClass.VerificationPolicy
          path "global.json" ProtectedClass.VerificationPolicy
          path "**/Directory.Build.props" ProtectedClass.VerificationPolicy
          path "**/Directory.Build.targets" ProtectedClass.VerificationPolicy
          path "NuGet.config" ProtectedClass.SecurityPolicy
          path "**/nuget.config" ProtectedClass.SecurityPolicy
          path "**/.npmrc" ProtectedClass.SecurityPolicy
          path "aegis-boundaries.json" ProtectedClass.SecurityPolicy
          path "**/*.pem" ProtectedClass.Credentials
          path "**/*.key" ProtectedClass.Credentials
          path "**/*.pfx" ProtectedClass.Credentials
          path "**/*.p12" ProtectedClass.Credentials
          path "**/.env" ProtectedClass.Credentials
          path "**/.env.*" ProtectedClass.Credentials ]

    let private testPatterns =
        [ "tests/**"; "test/**"; "**/tests/**"; "**/test/**"; "**/*Tests.fs"; "**/*Test.fs"; "**/*.spec.*"; "**/*.test.*" ]

    let private testProjectPatterns = [ "**/*Tests.fsproj"; "**/*Tests.csproj"; "tests/**/*.fsproj"; "**/tests/**/*.fsproj" ]

    let private scriptExtensions = set [ ".sh"; ".bash"; ".ps1"; ".cmd"; ".fsx"; ".py"; ".js"; ".mjs"; ".cjs"; ".ts" ]

    /// `**/x` also covers a root-level `x`, whatever the glob engine's view of
    /// an empty `**`.
    let isMatch (pattern: string) (value: string) =
        Glob.isMatch pattern value
        || (pattern.StartsWith("**/", StringComparison.Ordinal) && Glob.isMatch (pattern.Substring 3) value)

    let private isTest value = testPatterns |> List.exists (fun p -> isMatch p value)

    /// Script files the protected base's CI workflows name, so a change to
    /// what CI runs is recognized as a change to verification. Pure: the
    /// caller supplies the workflow texts and the tracked paths.
    let ciReferencedPaths (workflowTexts: string list) (trackedPaths: string list) =
        let tracked = Set.ofList trackedPaths
        let separators = [| ' '; '\t'; '\n'; '\r'; '"'; '\''; '='; ';'; '('; ')'; '`'; ','; '|'; '&'; '<'; '>' |]

        let normalize (token: string) =
            let trimmed = token.Trim().TrimEnd(':', '.')
            if trimmed.StartsWith("./", StringComparison.Ordinal) then trimmed.Substring 2 else trimmed

        let isScript (value: string) =
            let extension = IO.Path.GetExtension value
            String.IsNullOrEmpty extension || scriptExtensions.Contains(extension.ToLowerInvariant())

        workflowTexts
        |> List.collect (fun text -> text.Split(separators, StringSplitOptions.RemoveEmptyEntries) |> Array.toList)
        |> List.map normalize
        |> List.filter (fun token -> tracked.Contains token && isScript token)
        |> List.distinct
        |> List.sort

    /// Every (path, class, reason) the changes touch, in path order.
    let classify (declared: ProtectedPath list) (ciReferenced: string list) (changes: ChangedPath list) =
        let rules = floor @ declared
        let ci = Set.ofList ciReferenced

        let byLocation (value: string) =
            rules
            |> List.filter (fun rule -> isMatch rule.Pattern value)
            |> List.map (fun rule -> rule.Class, $"matches protected path {rule.Pattern}")

        let byEffect (change: ChangedPath) =
            let removedTest =
                match change.Kind with
                | ChangeKind.Removed when isTest change.Path -> [ ProtectedClass.VerificationPolicy, "removes a test" ]
                | ChangeKind.Renamed previous when isTest previous && not (isTest change.Path) ->
                    [ ProtectedClass.VerificationPolicy, $"moves test {previous} out of the test suite" ]
                | _ -> []

            let testProject =
                if testProjectPatterns |> List.exists (fun p -> isMatch p change.Path) then
                    [ ProtectedClass.VerificationPolicy, "changes a test project" ]
                else []

            let ciScript =
                [ change.Path
                  match change.Kind with
                  | ChangeKind.Renamed previous -> previous
                  | _ -> () ]
                |> List.filter ci.Contains
                |> List.map (fun p -> ProtectedClass.VerificationPolicy, $"changes {p}, which CI runs")

            removedTest @ testProject @ ciScript

        let paths (change: ChangedPath) =
            match change.Kind with
            | ChangeKind.Renamed previous -> [ previous; change.Path ]
            | _ -> [ change.Path ]

        changes
        |> List.collect (fun change ->
            (paths change |> List.collect byLocation) @ byEffect change
            |> List.map (fun (cls, reason) -> change.Path, cls, reason))
        |> List.distinct
        |> List.sortBy (fun (p, c, _) -> p, ProtectedClass.toWire c)
