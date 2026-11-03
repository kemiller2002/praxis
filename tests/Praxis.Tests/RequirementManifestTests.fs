module Praxis.Tests.RequirementManifestTests

open System.Text
open Praxis.Application.Requirements
open Praxis.Contracts.Requirements
open Praxis.Domain.Requirements

let private commit = "0123456789abcdef0123456789abcdef01234567"

let private requirement id name : CatalogRequirement =
    { Id = id
      Kind = "ui-field"
      Screen = "investigation-start"
      Name = name
      Component = Some "text-input"
      Required = true
      Visible = true
      Editable = true
      Rules = { MinLength = Some 1; MaxLength = Some 200; Pattern = None; Options = [] }
      Verification = [ "mapped"; "exists"; "visible"; "editable"; "enter"; "validation"; "save"; "reload" ]
      Sources = [ { Document = "docs/SCREEN-CONTRACTS.md"; Requirement = "S01" } ]
      DependsOn = []
      AppliesWhen = []
      Instances = None }

let private catalog requirements : Catalog =
    { CatalogId = "CAT-TEST"
      IdPattern = "^INV-\\d{3}$"
      Status = "proposed"
      Screens = [ { Id = "investigation-start"; Route = Some "#/investigations/new" } ]
      Facts = [ "ai-provider-configured" ]
      Requirements = requirements
      Retired = [] }

let private valid =
    catalog
        [ requirement "INV-002" "Problem statement"
          { requirement "INV-001" "Title" with Required = false; Rules = { MinLength = None; MaxLength = Some 200; Pattern = None; Options = [] } } ]

let private codes (findings: CatalogFinding list) =
    findings |> List.filter (fun f -> f.Severity = CatalogError) |> List.map _.Code |> List.distinct

let private rejectedWith (code: string) (input: Catalog) (mappings: ImplementationMapping list) =
    match RequirementCatalog.compile input "hash" commit mappings with
    | Ok _ -> failwith $"expected the compiler to reject the catalog with '{code}'"
    | Error findings -> Assert.isTrue (List.contains code (codes findings)) $"expected '{code}' among {codes findings}"

let private render input =
    match RequirementCatalog.compile input "hash" commit [] with
    | Ok(manifest, _) -> RequirementManifestJson.renderManifest manifest
    | Error findings -> failwith $"unexpected rejection: {RequirementManifestJson.renderFindingsText findings}"

let private catalogJson =
    """{
  "schema": "praxis.requirement-catalog/1",
  "catalogId": "CAT-TEST",
  "idPattern": "^INV-\\d{3}$",
  "status": "proposed",
  "screens": [ { "id": "investigation-start", "route": "#/investigations/new" } ],
  "requirements": [
    { "id": "INV-001", "type": "ui-field", "screen": "investigation-start", "name": "Problem statement",
      "component": "textarea", "required": true, "visible": true, "editable": true,
      "validation": { "minLength": 1, "maxLength": 4000 },
      "verification": [ "reload", "mapped", "exists", "visible", "editable", "enter", "validation", "save" ],
      "sources": [ { "document": "docs/SCREEN-CONTRACTS.md", "requirement": "S01" } ] }
  ]
}"""

let private memoryInputs (files: Map<string, string>) : RequirementManifest.Inputs =
    { ReadFile =
        fun path ->
            match files.TryFind path with
            | Some text -> Ok(Encoding.UTF8.GetBytes text)
            | None -> Error "not found" }

let private compileText files =
    match RequirementManifest.compile (memoryInputs files) { Catalog = "catalog.json"; Mappings = None; SourceCommit = commit; Previous = None } with
    | Ok(RequirementManifest.Compiled(manifest, _)) -> manifest
    | other -> failwith $"unexpected outcome {other}"

let tests: TestCase list =
    [ { Name = "requirement manifest: identical catalogs compile to byte-identical manifests"
        Run = fun () -> Assert.equal (render valid) (render valid) }
      { Name = "requirement manifest: requirement and obligation order is canonical whatever the catalog order"
        Run =
          fun () ->
              let reversed = { valid with Requirements = List.rev valid.Requirements }
              Assert.equal (render valid) (render reversed) }
      { Name = "requirement manifest: obligation IDs are requirement/level/check and carry the mandatory flag"
        Run =
          fun () ->
              match RequirementCatalog.compile valid "hash" commit [] with
              | Error _ -> failwith "rejected"
              | Ok(manifest, _) ->
                  let statement = manifest.Requirements |> List.find (fun r -> r.Id = "INV-002")
                  Assert.equal [ V1; V2; V3; V4; V5 ] statement.Levels
                  Assert.isTrue (statement.Obligations |> List.exists (fun o -> o.Id = "INV-002/V3/editable" && o.Mandatory)) "editable obligation"
                  let title = manifest.Requirements |> List.find (fun r -> r.Id = "INV-001")
                  Assert.isTrue (title.Obligations |> List.forall (fun o -> not o.Mandatory)) "optional requirement obligations are not mandatory" }
      { Name = "requirement manifest: renaming a label keeps every requirement and obligation ID"
        Run =
          fun () ->
              let renamed =
                  { valid with Requirements = valid.Requirements |> List.map (fun r -> if r.Id = "INV-002" then { r with Name = "What happened?" } else r) }

              let ids (c: Catalog) =
                  match RequirementCatalog.compile c "hash" commit [] with
                  | Ok(m, _) -> m.Requirements |> List.collect (fun r -> r.Id :: (r.Obligations |> List.map _.Id))
                  | Error _ -> failwith "rejected"

              Assert.equal (ids valid) (ids renamed)
              Assert.isTrue (render valid <> render renamed) "the content hash still changes with the label" }
      { Name = "requirement manifest: the manifest records the source commit, catalog hash and its own content hash"
        Run =
          fun () ->
              let text = render valid
              Assert.isTrue (text.Contains $"\"commit\": \"{commit}\"") "source commit"
              Assert.isTrue (text.Contains "\"catalogSha256\": \"hash\"") "catalog hash"
              match RequirementManifestJson.decodeManifestSummary text with
              | Ok summary -> Assert.equal summary.ContentHash summary.RecomputedHash
              | Error reason -> failwith reason }
      { Name = "requirement manifest: an unmapped requirement keeps every obligation (the application cannot shrink its obligations)"
        Run =
          fun () ->
              match RequirementCatalog.compile valid "hash" commit [ { RequirementId = "INV-001"; Location = "web/index.html" } ] with
              | Error _ -> failwith "rejected"
              | Ok(manifest, _) ->
                  Assert.equal 2 manifest.Requirements.Length
                  let unmapped = manifest.Requirements |> List.find (fun r -> r.Id = "INV-002")
                  Assert.equal [] unmapped.Mappings
                  Assert.equal 8 unmapped.Obligations.Length }
      { Name = "requirement manifest: duplicate IDs are rejected"
        Run = fun () -> rejectedWith "duplicate-id" (catalog [ requirement "INV-001" "A"; requirement "INV-001" "B" ]) [] }
      { Name = "requirement manifest: a requirement without an ID is rejected"
        Run = fun () -> rejectedWith "missing-id" (catalog [ requirement "" "A" ]) [] }
      { Name = "requirement manifest: an ID outside the catalog pattern is rejected"
        Run = fun () -> rejectedWith "invalid-id" (catalog [ requirement "INV-1" "A" ]) [] }
      { Name = "requirement manifest: an undeclared screen, dependency or fact is an invalid reference"
        Run =
          fun () ->
              rejectedWith "invalid-reference" (catalog [ { requirement "INV-001" "A" with Screen = "nowhere" } ]) []
              rejectedWith "invalid-reference" (catalog [ { requirement "INV-001" "A" with DependsOn = [ "INV-404" ] } ]) []
              rejectedWith "invalid-reference" (catalog [ { requirement "INV-001" "A" with AppliesWhen = [ "undeclared", true ] } ]) [] }
      { Name = "requirement manifest: a requirement with no verification obligation is rejected"
        Run = fun () -> rejectedWith "missing-verification" (catalog [ { requirement "INV-001" "A" with Verification = [] } ]) [] }
      { Name = "requirement manifest: unknown verification types, and checks that do not fit the requirement type, are unsupported"
        Run =
          fun () ->
              rejectedWith "unsupported-verification" (catalog [ { requirement "INV-001" "A" with Verification = [ "exists"; "looks-right" ] } ]) []
              rejectedWith "unsupported-verification" (catalog [ { requirement "INV-001" "A" with Kind = "action"; Editable = false; Verification = [ "exists"; "enter" ] } ]) [] }
      { Name = "requirement manifest: an unsupported requirement type is rejected"
        Run = fun () -> rejectedWith "unsupported-requirement-type" (catalog [ { requirement "INV-001" "A" with Kind = "widget" } ]) [] }
      { Name = "requirement manifest: conflicting requirements are rejected"
        Run =
          fun () ->
              rejectedWith "conflicting-requirement" (catalog [ { requirement "INV-001" "A" with Rules = { MinLength = Some 10; MaxLength = Some 5; Pattern = None; Options = [] } } ]) []
              rejectedWith "conflicting-requirement" (catalog [ { requirement "INV-001" "A" with Editable = false } ]) []
              rejectedWith "conflicting-requirement" (catalog [ requirement "INV-001" "Title"; requirement "INV-002" "title" ]) []
              rejectedWith "conflicting-requirement" (catalog [ { requirement "INV-001" "A" with Verification = [ "mapped"; "select" ] } ]) [] }
      { Name = "requirement manifest: an implementation mapping naming no requirement is orphaned"
        Run = fun () -> rejectedWith "orphaned-mapping" valid [ { RequirementId = "INV-999"; Location = "web/index.html" } ] }
      { Name = "requirement manifest: a retired ID cannot be reused, and a retirement must name its authority"
        Run =
          fun () ->
              rejectedWith "retired-id-reused" { valid with Retired = [ { Id = "INV-001"; Reason = "merged"; Authority = "DEC-1"; ReplacedBy = None } ] } []
              rejectedWith "unauthorised-retirement" { valid with Retired = [ { Id = "INV-009"; Reason = "merged"; Authority = ""; ReplacedBy = None } ] } [] }
      { Name = "requirement manifest: a requirement must trace to an authoritative source"
        Run = fun () -> rejectedWith "missing-source" (catalog [ { requirement "INV-001" "A" with Sources = [] } ]) [] }
      { Name = "requirement manifest: a full Git SHA is required as the source commit"
        Run =
          fun () ->
              match RequirementCatalog.compile valid "hash" "HEAD" [] with
              | Ok _ -> failwith "accepted a symbolic commit"
              | Error findings -> Assert.isTrue (List.contains "invalid-source-commit" (codes findings)) "invalid-source-commit" }
      { Name = "requirement manifest: deleting a requirement without a retirement record is rejected against the previous manifest"
        Run =
          fun () ->
              let later = catalog [ requirement "INV-002" "Problem statement" ]
              Assert.equal [ "removed-without-retirement" ] (RequirementCatalog.removedWithoutRetirement [ "INV-001"; "INV-002" ] later |> List.map _.Code)
              let retired = { later with Retired = [ { Id = "INV-001"; Reason = "folded into INV-002"; Authority = "DEC-1"; ReplacedBy = Some "INV-002" } ] }
              Assert.empty (RequirementCatalog.removedWithoutRetirement [ "INV-001"; "INV-002" ] retired) }
      { Name = "requirement manifest: decoding refuses another schema and strictly types every member"
        Run =
          fun () ->
              Assert.isTrue (RequirementManifestJson.decodeCatalog (catalogJson.Replace("requirement-catalog/1", "requirement-catalog/2")) |> Result.isError) "schema"
              Assert.isTrue (RequirementManifestJson.decodeCatalog (catalogJson.Replace("\"required\": true", "\"required\": \"yes\"")) |> Result.isError) "boolean"
              Assert.isTrue (RequirementManifestJson.decodeCatalog catalogJson |> Result.isOk) "valid" }
      { Name = "requirement manifest: CRLF and LF checkouts of one catalog compile to the same manifest"
        Run =
          fun () ->
              let lf = compileText (Map [ "catalog.json", catalogJson ])
              let crlf = compileText (Map [ "catalog.json", catalogJson.Replace("\n", "\r\n") ])
              Assert.equal lf crlf }
      { Name = "requirement manifest: check reports current for a freshly compiled manifest"
        Run =
          fun () ->
              let manifest = compileText (Map [ "catalog.json", catalogJson ])

              match RequirementManifest.check (memoryInputs (Map [ "catalog.json", catalogJson; "manifest.json", manifest ])) "catalog.json" "manifest.json" None with
              | Ok(RequirementManifest.Current _) -> ()
              | other -> failwith $"expected current, got {other}" }
      { Name = "requirement manifest: changing a requirement without regenerating the manifest is detected as stale"
        Run =
          fun () ->
              let manifest = compileText (Map [ "catalog.json", catalogJson ])
              let changed = catalogJson.Replace("\"maxLength\": 4000", "\"maxLength\": 2000")

              match RequirementManifest.check (memoryInputs (Map [ "catalog.json", changed; "manifest.json", manifest ])) "catalog.json" "manifest.json" None with
              | Ok(RequirementManifest.Stale problems) ->
                  Assert.isTrue (problems |> List.exists (function RequirementManifest.CatalogChanged _ -> true | _ -> false)) "catalog-changed"
                  Assert.isTrue (List.contains RequirementManifest.ContentDiffers problems) "content-differs"
              | other -> failwith $"expected stale, got {other}" }
      { Name = "requirement manifest: hand-editing a compiled manifest is detected as tampering"
        Run =
          fun () ->
              let manifest = compileText (Map [ "catalog.json", catalogJson ])
              let edited = manifest.Replace("\"mandatory\": true", "\"mandatory\": false")

              match RequirementManifest.check (memoryInputs (Map [ "catalog.json", catalogJson; "manifest.json", edited ])) "catalog.json" "manifest.json" None with
              | Ok(RequirementManifest.Stale problems) ->
                  Assert.isTrue (problems |> List.exists (function RequirementManifest.Tampered _ -> true | _ -> false)) "tampered"
              | other -> failwith $"expected stale, got {other}" } ]
