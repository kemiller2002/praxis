namespace Praxis.Infrastructure.Planning

open System

/// A host-declared handwritten extension slot (Praxis #243): one exact file
/// name directly under the construction mount, the Ordo extension contracts
/// it may implement, and its size limit. Slots are operator policy in the
/// host configuration. A worker or plan cannot declare, widen or rename one.
type ConstructionExtensionSlot = { File: string; Contracts: string list; MaxBytes: int }

/// A host-declared ACCEPTANCE ADAPTER slot (CONSTRUCT-008): an existing
/// handwritten file directly under the construction mount that implements a
/// frozen acceptance (oracle adapter) contract rather than a generated
/// extension contract. Operator policy, like an extension slot; it never
/// widens what an extension slot may be, and only replaces a file the
/// admitted source already carries.
type ConstructionAcceptanceSlot = { AcceptanceFile: string; AcceptancePath: string; AcceptanceContract: string; AcceptanceMaxBytes: int }

/// Names that can never be a handwritten extension, whatever the host
/// declares. The same rule refuses a host declaration at configuration load
/// and a submitted file at `construct extend`, so neither layer relies on the
/// other.
[<RequireQualifiedAccess>]
module ConstructionExtensionSlot =
    [<Literal>]
    let MaxSlotBytes = 262144

    [<Literal>]
    let MaxSlots = 32

    let private lower (value: string) = value.ToLowerInvariant()

    let private projectExtensions =
        [ ".fsproj"; ".csproj"; ".vbproj"; ".proj"; ".sln"; ".slnx"; ".props"; ".targets"; ".shproj"; ".projitems"; ".esproj" ]

    let private dependencyNames =
        set [ "nuget.config"; "packages.config"; "packages.lock.json"; "global.json"; "dotnet-tools.json"
              "package.json"; "package-lock.json"; "npm-shrinkwrap.json"; "yarn.lock"; "pnpm-lock.yaml"
              "cargo.toml"; "cargo.lock"; "go.mod"; "go.sum"; "pyproject.toml"; "requirements.txt"; "pipfile"; "pipfile.lock"
              "gemfile"; "gemfile.lock"; "vcpkg.json"; "conanfile.txt" ]

    let private policyNames =
        set [ "ros.json"; "agents.md"; "claude.md"; "gemini.md"; "codeowners"; "security.md"; "license"; "license.md"
              "makefile"; "dockerfile"; "jenkinsfile"; "azure-pipelines.yml"; "bitbucket-pipelines.yml"; "praxis"; "ros" ]

    let private scriptExtensions = [ ".sh"; ".bash"; ".ps1"; ".psm1"; ".cmd"; ".bat"; ".fsx"; ".csx"; ".py"; ".exe"; ".dll" ]

    let private ecirNames = set [ "source-manifest.json"; "blueprint.json"; "construction-manifest.json"; "construction-plan.json" ]

    let private portable (name: string) =
        name.Length <= 128
        && not (name.EndsWith '.' || name.EndsWith ' ' || name.StartsWith ' ')
        && name |> Seq.forall (fun c -> not (Char.IsControl c) && not (":*?\"<>|".Contains c))

    /// Why `name` can never be a handwritten extension file, or None.
    let denied (name: string) : string option =
        let n = lower name
        let ends (suffixes: string list) = suffixes |> List.exists n.EndsWith
        if String.IsNullOrWhiteSpace name then Some "empty file name"
        elif name.Contains '/' || name.Contains '\\' then Some "contains a path separator; a slot is one file name directly under the construction mount"
        elif name = "." || name = ".." then Some "relative path segment"
        elif not (portable name) then Some "not a portable file name"
        elif name.StartsWith '.' then Some "dot entry (CI, agent or repository configuration such as .github, .claude, .echelon, .git) is never an extension"
        elif n = "generated" then Some "the generated ownership directory is written only by construction"
        elif n.Contains ".g." then Some "generated-file name; generated files are written only by construction"
        elif ends projectExtensions then Some "project or build file"
        elif dependencyNames.Contains n || n.StartsWith "paket." || n.EndsWith ".nuspec" || n.EndsWith ".nupkg" then Some "package or dependency file"
        elif policyNames.Contains n || n.EndsWith ".yml" || n.EndsWith ".yaml" then Some "repository policy, agent instruction or CI file"
        elif ends scriptExtensions then Some "executable script or binary"
        elif ecirNames.Contains n || n.EndsWith ".ecir.json" then Some "ECIR or construction source document"
        elif n.StartsWith "requirements" then Some "requirement source document"
        else None
