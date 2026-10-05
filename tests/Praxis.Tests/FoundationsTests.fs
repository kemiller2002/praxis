namespace Praxis.Tests

open System
open System.IO
open Praxis.Cli
open Praxis.Domain.Foundations
open Praxis.Infrastructure.Foundations

[<RequireQualifiedAccess>]
module FoundationsTests =
    let private withTemp action =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-foundations-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore

        try
            action root
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    let private write (root: string) (relativePath: string) (content: string) =
        let path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))
        let parent = Path.GetDirectoryName path
        if not (String.IsNullOrWhiteSpace parent) then Directory.CreateDirectory(parent) |> ignore
        File.WriteAllText(path, content)

    let private config (application: string) (capability: string) =
        $"""{{
  "schemaVersion": 1,
  "application": "{application}",
  "capabilities": {{
    {capability}
  }}
}}"""

    let private report root =
        match Foundations.verify root with
        | Ok value -> value
        | Error message -> failwith message

    let private dependency (name: string) (spec: string) =
        $"""{{ "dependencies": {{ "{name}": "{spec}" }} }}"""

    /// A repository whose foundations require Limen at `expected`, with a
    /// Limen manifest recording `package` and `installed`.
    let private limenRepository root (expected: string) (package: string option) (installed: string) =
        write root ".echelon/foundations.json" (config "Example" $"\"limen\": {{ \"required\": true, \"version\": \"{expected}\" }}")
        let packageField = package |> Option.map (fun name -> $"\"package\": \"{name}\", ") |> Option.defaultValue ""
        write root ".echelon/limen.json" $"{{ \"schemaVersion\": 1, \"tool\": \"limen\", {packageField}\"installedVersion\": \"{installed}\" }}"

    let private limenResult root =
        (report root).Capabilities |> List.find (fun item -> item.Name = "limen")

    let tests =
        [ { Name = "foundation verifier accepts pinned Forma with canonical usage"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write
                          root
                          ".echelon/foundations.json"
                          (config "Example" "\"forma\": { \"required\": true, \"version\": \"0.2.0\" }")

                      write
                          root
                          "package.json"
                          """{
  "dependencies": {
    "@echelon-foundry/design-system": "0.2.0"
  }
}"""

                      write
                          root
                          "web/index.html"
                          """<!doctype html>
<link rel="stylesheet" href="/node_modules/@echelon-foundry/design-system/all.css">
<ef-button><button type="button">Save</button></ef-button>"""

                      let result = report root
                      Assert.isTrue result.Passed $"Expected pass but received {result.Findings}"
                      let forma = result.Capabilities |> List.find (fun item -> item.Name = "forma")
                      Assert.isTrue forma.Installed "Forma should be detected"
                      Assert.isTrue forma.Pinned "Forma should be pinned"
                      Assert.isTrue forma.Used "Forma canonical usage should be detected") }

          { Name = "foundation verifier rejects dependency that is installed but unused"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write
                          root
                          ".echelon/foundations.json"
                          (config "Example" "\"forma\": { \"required\": true, \"version\": \"0.2.0\" }")

                      write
                          root
                          "package.json"
                          """{
  "dependencies": {
    "@echelon-foundry/design-system": "0.2.0"
  }
}"""

                      write root "web/index.html" "<button type=\"button\">Local button</button>"

                      let result = report root
                      Assert.isTrue (not result.Passed) "Unused Forma must fail"
                      let finding = result.Findings |> List.find (fun item -> item.Code = "ECHELON-FND-FORMA-003")
                      Assert.equal "forma" finding.Capability) }

          { Name = "foundation verifier requires Aegis boundary evidence"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write
                          root
                          ".echelon/foundations.json"
                          (config
                              "Example"
                              "\"aegis\": { \"required\": true, \"version\": \"1.0.0\", \"boundaryManifest\": \"aegis-boundaries.json\" }")

                      write
                          root
                          "Directory.Packages.props"
                          """<Project>
  <ItemGroup>
    <PackageVersion Include="EchelonFoundry.Aegis.Core" Version="1.0.0" />
  </ItemGroup>
</Project>"""

                      write
                          root
                          "src/App/App.fsproj"
                          """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="EchelonFoundry.Aegis.Core" />
  </ItemGroup>
</Project>"""

                      write
                          root
                          "src/App/Program.fs"
                          """module App.Program
open EchelonFoundry.Aegis
let guarded = Aegis.guard"""

                      let missing = report root
                      Assert.isTrue (not missing.Passed) "Missing Aegis boundary declaration must fail"
                      Assert.isTrue
                          (missing.Findings |> List.exists (fun item -> item.Code = "ECHELON-FND-AEGIS-004"))
                          "Expected Aegis evidence finding"

                      write root "aegis-boundaries.json" """{ "schemaVersion": 1, "boundaries": [] }"""

                      let complete = report root
                      Assert.isTrue complete.Passed $"Expected Aegis pass after boundary evidence: {complete.Findings}") }

          { Name = "foundation verifier pins Limen under its 0.7.0 package name @echelon-foundry/limen"
            Run =
              fun () ->
                  withTemp (fun root ->
                      limenRepository root "0.7.0" (Some "@echelon-foundry/limen") "0.7.0"
                      write root "package.json" (dependency "@echelon-foundry/limen" "0.7.0")
                      let limen = limenResult root
                      Assert.isTrue limen.Installed "Limen should be detected under @echelon-foundry/limen"
                      Assert.isTrue limen.Pinned "Limen 0.7.0 should be pinned under @echelon-foundry/limen"
                      Assert.isTrue (report root).Passed "Expected the foundations to pass") }

          { Name = "foundation verifier still pins Limen under the deprecated typescript-wasm-kernel name"
            Run =
              fun () ->
                  withTemp (fun root ->
                      limenRepository root "0.6.2" (Some "@echelon-foundry/typescript-wasm-kernel") "0.6.2"
                      write root "package.json" (dependency "@echelon-foundry/typescript-wasm-kernel" "0.6.2")
                      Assert.isTrue (limenResult root).Pinned "Limen 0.6.2 should be pinned under its legacy name"
                      Assert.isTrue (report root).Passed "Expected the foundations to pass") }

          { Name = "foundation verifier pins Limen from .echelon/limen.json when there is no npm dependency"
            Run =
              fun () ->
                  withTemp (fun root ->
                      limenRepository root "0.7.0" (Some "@echelon-foundry/limen") "0.7.0"
                      Assert.isTrue (limenResult root).Pinned "The manifest's installedVersion is the pin the verify workflow runs"
                      Assert.isTrue (report root).Passed "Expected the foundations to pass") }

          { Name = "foundation verifier rejects an unpinned or contradicted Limen with ECHELON-FND-LIMEN-002"
            Run =
              fun () ->
                  withTemp (fun root ->
                      let assertUnpinned because =
                          let result = report root
                          Assert.isTrue (not (limenResult root).Pinned) because
                          Assert.isTrue
                              (result.Findings |> List.exists (fun item -> item.Code = "ECHELON-FND-LIMEN-002"))
                              $"Expected ECHELON-FND-LIMEN-002 ({because})"

                      limenRepository root "0.7.0" (Some "@echelon-foundry/limen") "0.6.2"
                      assertUnpinned "a manifest recording another version is not the declared pin"

                      limenRepository root "0.7.0" (Some "@echelon-foundry/limen") "0.7.0"
                      write root "package.json" (dependency "@echelon-foundry/limen" "^0.7.0")
                      assertUnpinned "a floating npm range is not a pin, whatever the manifest says"

                      limenRepository root "0.7.0" (Some "@echelon-foundry/limen") "0.6.2"
                      write root "package.json" (dependency "@echelon-foundry/limen" "0.7.0")
                      assertUnpinned "an npm pin that contradicts the manifest is not a pin"

                      File.Delete(Path.Combine(root, "package.json"))
                      limenRepository root "0.7.0" (Some "@echelon-foundry/other") "0.7.0"
                      assertUnpinned "a manifest naming a package that is not Limen is not a Limen pin") }

          { Name = "Limen pin policy: evidence must exist and agree"
            Run =
              fun () ->
                  let manifest version = Some { Package = Some "@echelon-foundry/limen"; InstalledVersion = Some version }
                  Assert.isTrue (not (LimenPin.isPinned None None)) "no evidence is not a pin"
                  Assert.isTrue (LimenPin.isPinned (Some true) None) "a pinned dependency alone is a pin"
                  Assert.isTrue (not (LimenPin.isPinned (Some true) (Some false))) "contradicting evidence is not a pin"
                  Assert.equal (Some true) (LimenPin.manifestEvidence (Some "0.7.0") true (manifest "0.7.0"))
                  Assert.equal (Some false) (LimenPin.manifestEvidence (Some "0.7.0") true (manifest "0.6.2"))
                  Assert.equal None (LimenPin.manifestEvidence (Some "0.7.0") true None)
                  Assert.equal (Some true) (LimenPin.manifestEvidence None true None)
                  Assert.equal None (LimenPin.manifestEvidence None false None)

                  Assert.equal
                      (Some { Package = Some "@echelon-foundry/limen"; InstalledVersion = Some "0.7.0" })
                      (LimenManifestReader.parse """{ "package": "@echelon-foundry/limen", "installedVersion": "0.7.0" }""")

                  Assert.equal None (LimenManifestReader.parse "not json")
                  Assert.equal None (LimenManifestReader.parse "[]") } ]
