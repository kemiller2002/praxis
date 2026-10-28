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
    /// the repository's review rules. Matching is case-insensitive, so a
    /// differently-cased variant (`NuGet.Config`) is still covered.
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
          // MSBuild configuration decides what builds, which analyzers run and
          // whether warnings fail; a solution decides which projects build.
          path "**/*.props" ProtectedClass.VerificationPolicy
          path "**/*.targets" ProtectedClass.VerificationPolicy
          path "**/*.rsp" ProtectedClass.VerificationPolicy
          path "**/*.sln" ProtectedClass.VerificationPolicy
          path "**/*.slnx" ProtectedClass.VerificationPolicy
          path "**/Makefile" ProtectedClass.VerificationPolicy
          // Package sources, dependency manifests, lock files and vendored
          // packages are the supply chain.
          path "**/nuget.config" ProtectedClass.SecurityPolicy
          path "**/.npmrc" ProtectedClass.SecurityPolicy
          path "**/package.json" ProtectedClass.SecurityPolicy
          path "**/packages.lock.json" ProtectedClass.SecurityPolicy
          path "**/package-lock.json" ProtectedClass.SecurityPolicy
          path "**/yarn.lock" ProtectedClass.SecurityPolicy
          path "**/pnpm-lock.yaml" ProtectedClass.SecurityPolicy
          path ".config/dotnet-tools.json" ProtectedClass.SecurityPolicy
          path ".gitmodules" ProtectedClass.SecurityPolicy
          path "vendor/**" ProtectedClass.SecurityPolicy
          path ".github/dependabot.yml" ProtectedClass.SecurityPolicy
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

    /// Case-insensitive glob match. `**/x` also covers a root-level `x`,
    /// whatever the glob engine's view of an empty `**`. Over-matching only
    /// ever makes a change owner-controlled, never the reverse.
    let isMatch (pattern: string) (value: string) =
        let p = pattern.ToLowerInvariant()
        let v = value.ToLowerInvariant()
        Glob.isMatch p v || (p.StartsWith("**/", StringComparison.Ordinal) && Glob.isMatch (p.Substring 3) v)

    let private isTest value = testPatterns |> List.exists (fun p -> isMatch p value)

    let private workspacePrefixes = [ "${{ github.workspace }}/"; "${{github.workspace}}/"; "${GITHUB_WORKSPACE}/"; "$GITHUB_WORKSPACE/" ]

    let private separators = [| ' '; '\t'; '\n'; '\r'; '"'; '\''; '='; ';'; '('; ')'; '`'; ','; '|'; '&'; '<'; '>' |]

    let private tokens (text: string) =
        workspacePrefixes
        |> List.fold (fun (t: string) prefix -> t.Replace(prefix, "")) text
        |> fun t -> t.Split(separators, StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
        |> List.map (fun token ->
            let trimmed = token.Trim().TrimEnd(':', '.')
            if trimmed.StartsWith("./", StringComparison.Ordinal) then trimmed.Substring 2 else trimmed)

    let private keyed (key: string) (text: string) =
        text.Split('\n')
        |> Array.toList
        |> List.choose (fun line ->
            let t = line.Trim().TrimStart('-').Trim()
            if t.StartsWith(key, StringComparison.Ordinal) then Some(t.Substring(key.Length).Trim().Trim('"', '\'').TrimStart('.', '/').TrimEnd('/'))
            else None)
        |> List.filter (String.IsNullOrWhiteSpace >> not)

    /// Files the protected base's CI runs, so a change to what CI runs is a
    /// change to verification wherever the file lives. Found in the workflow
    /// text, relative to any `working-directory`, through workspace prefixes,
    /// every file of a local action (`uses: ./dir`), and transitively through
    /// the scripts those name (`readText` reads a tracked file at the base).
    let ciReferencedPaths (readText: string -> string option) (workflowTexts: string list) (trackedPaths: string list) =
        let tracked = Set.ofList trackedPaths

        let isScript (value: string) =
            let extension = IO.Path.GetExtension value
            String.IsNullOrEmpty extension || scriptExtensions.Contains(extension.ToLowerInvariant())

        let referencedBy (directory: string) (text: string) =
            let roots = "" :: (if directory = "" then [] else [ directory ]) @ keyed "working-directory:" text
            let scripts =
                tokens text
                |> List.collect (fun token -> roots |> List.map (fun root -> if root = "" then token else $"{root}/{token}"))
                |> List.filter (fun candidate -> tracked.Contains candidate && isScript candidate)
            let actions =
                keyed "uses:" text
                |> List.filter (fun dir -> not (dir.Contains "@"))
                |> List.collect (fun dir -> trackedPaths |> List.filter (fun p -> p.StartsWith(dir + "/", StringComparison.Ordinal)))
            scripts @ actions

        let rec close (known: Set<string>) (frontier: string list) =
            match frontier with
            | [] -> known
            | _ ->
                let next =
                    frontier
                    |> List.collect (fun file ->
                        match readText file with
                        | Some text -> referencedBy (match IO.Path.GetDirectoryName file with null -> "" | d -> d.Replace('\\', '/')) text
                        | None -> [])
                    |> List.filter (known.Contains >> not)
                    |> List.distinct
                close (Set.union known (Set.ofList next)) next

        let direct = workflowTexts |> List.collect (referencedBy "") |> List.distinct
        close (Set.ofList direct) direct |> Set.toList |> List.sort

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
