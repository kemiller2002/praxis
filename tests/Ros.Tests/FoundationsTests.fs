namespace Ros.Tests

open System
open System.IO
open Ros.Cli

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
                      Assert.isTrue complete.Passed $"Expected Aegis pass after boundary evidence: {complete.Findings}") } ]
