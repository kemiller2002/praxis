namespace Praxis.Infrastructure.Foundations

open System
open System.IO
open System.Text.Json
open Praxis.Domain.Foundations

/// Reads a repository's reliability applicability declaration
/// (`.echelon/reliability.json`, `echelon.reliability/v1`, RQ-ROS-2026-A038).
/// `parse` is pure; `read` and `fileExists` are the only I/O.
[<RequireQualifiedAccess>]
module ReliabilityDeclarationReader =
    let private property (name: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            match element.TryGetProperty name with
            | true, value -> Some value
            | _ -> None
        | _ -> None

    let private kind (value: JsonElement) = value.ValueKind.ToString().ToLowerInvariant()

    /// A string property, and a problem when the property is present but not a string.
    let private text (owner: string) name element =
        match property name element with
        | None -> None, []
        | Some value when value.ValueKind = JsonValueKind.String -> (value.GetString() |> Option.ofObj), []
        | Some value -> None, [ $"{owner}{name} must be a string, not {kind value}" ]

    let private unknown (owner: string) (allowed: string list) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.map _.Name
            |> Seq.filter (fun name -> not (List.contains name allowed))
            |> Seq.map (fun name -> $"{owner}has unknown property '{name}'")
            |> List.ofSeq
        | _ -> []

    let private objectProblems (owner: string) (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Object then [] else [ $"{owner}must be an object, not {kind element}" ]

    /// An array property's items, and a problem when it is present but not an array.
    let private items (owner: string) name element =
        match property name element with
        | None -> [], []
        | Some value when value.ValueKind = JsonValueKind.Array -> List.ofSeq (value.EnumerateArray()), []
        | Some value -> [], [ $"{owner}{name} must be an array, not {kind value}" ]

    let private tenet (key: string) (element: JsonElement) : ReliabilityTenet * string list =
        let owner = $"tenets.{key}."
        let applicability, p1 = text owner "applicability" element
        let reason, p2 = text owner "reason" element

        { Applicability = applicability; Reason = reason },
        objectProblems owner element @ unknown owner [ "applicability"; "reason" ] element @ p1 @ p2

    let private evidence (owner: string) (element: JsonElement) : ReliabilityEvidence * string list =
        let evidenceClass, p1 = text owner "class" element
        let reference, p2 = text owner "ref" element

        { Class = evidenceClass; Ref = reference },
        objectProblems owner element @ unknown owner [ "class"; "ref" ] element @ p1 @ p2

    let private family (index: int) (element: JsonElement) : ReliabilityFamily * string list =
        let owner = $"families[{index}]."
        let name, p1 = text owner "family" element
        let state, p2 = text owner "state" element
        let entries, p3 = items owner "evidence" element
        let parsed = entries |> List.mapi (fun i entry -> evidence $"{owner}evidence[{i}]." entry)

        { Family = name
          State = state
          Evidence = parsed |> List.map fst },
        objectProblems owner element
        @ unknown owner [ "family"; "state"; "evidence" ] element
        @ p1 @ p2 @ p3 @ (parsed |> List.collect snd)

    let private exceptionEntry (index: int) (element: JsonElement) : ReliabilityException * string list =
        let owner = $"exceptions[{index}]."
        let field name = text owner name element
        let requirement, p1 = field "requirement"
        let rationale, p2 = field "rationale"
        let controls, p3 = field "compensatingControls"
        let approver, p4 = field "approver"
        let decision, p5 = field "decision"
        let expires, p6 = field "expires"

        { Requirement = requirement
          Rationale = rationale
          CompensatingControls = controls
          Approver = approver
          Decision = decision
          Expires = expires },
        objectProblems owner element
        @ unknown owner [ "requirement"; "rationale"; "compensatingControls"; "approver"; "decision"; "expires" ] element
        @ p1 @ p2 @ p3 @ p4 @ p5 @ p6

    let private dependency (index: int) (element: JsonElement) : ReliabilityDependency * string list =
        let owner = $"dependencies[{index}]."
        let capability, p1 = text owner "capability" element
        let pin, p2 = text owner "pin" element

        { Capability = capability; Pin = pin },
        objectProblems owner element @ unknown owner [ "capability"; "pin" ] element @ p1 @ p2

    let private topLevel =
        [ "schema"; "systemClass"; "tenets"; "families"; "exceptions"; "designEvidence"; "migrationPlan"; "dependencies" ]

    let private document (root: JsonElement) : ReliabilityDeclarationDocument =
        let schema, p1 = text "" "schema" root
        let systemClass, p2 = text "" "systemClass" root

        let tenetsElement = property "tenets" root

        let tenetProblems =
            match tenetsElement with
            | None -> [ "is missing tenets" ]
            | Some value -> objectProblems "tenets " value @ unknown "tenets " ReliabilityDeclaration.tenetKeys value

        let tenets =
            ReliabilityDeclaration.tenetKeys
            |> List.map (fun key ->
                match tenetsElement |> Option.bind (property key) with
                | None -> (key, None), []
                | Some value ->
                    let parsed, problems = tenet key value
                    (key, Some parsed), problems)

        let familyItems, p3 = items "" "families" root
        let families = familyItems |> List.mapi family
        let exceptionItems, p4 = items "" "exceptions" root
        let exceptions = exceptionItems |> List.mapi exceptionEntry
        let dependencyItems, p5 = items "" "dependencies" root
        let dependencies = dependencyItems |> List.mapi dependency

        let designElement = property "designEvidence" root
        let design name = designElement |> Option.map (text "designEvidence." name) |> Option.defaultValue (None, [])
        let decision, d1 = design "decision"
        let serviceLevels, d2 = design "serviceLevels"
        let failureModes, d3 = design "failureModes"

        let designProblems =
            match designElement with
            | None -> []
            | Some value -> objectProblems "designEvidence " value @ unknown "designEvidence " [ "decision"; "serviceLevels"; "failureModes" ] value @ d1 @ d2 @ d3

        let migrationPlan, p6 = text "" "migrationPlan" root

        { Schema = schema
          SystemClass = systemClass
          Tenets = tenets |> List.map fst
          Families = families |> List.map fst
          Exceptions = exceptions |> List.map fst
          DesignEvidence =
            { Decision = decision
              ServiceLevels = serviceLevels
              FailureModes = failureModes }
          MigrationPlan = migrationPlan
          Dependencies = dependencies |> List.map fst
          ShapeProblems =
            unknown "" topLevel root
            @ p1 @ p2 @ tenetProblems
            @ (tenets |> List.collect snd)
            @ p3 @ (families |> List.collect snd)
            @ p4 @ (exceptions |> List.collect snd)
            @ p5 @ (dependencies |> List.collect snd)
            @ designProblems @ p6 }

    /// Pure: the declaration text, or None when the file is absent.
    let parse (json: string option) : ReliabilityInput =
        match json with
        | None -> DeclarationAbsent
        | Some content ->
            try
                use parsed = JsonDocument.Parse content

                if parsed.RootElement.ValueKind <> JsonValueKind.Object then
                    DeclarationUnreadable $"the document is a JSON {kind parsed.RootElement}, not an object"
                else
                    DeclarationParsed(document parsed.RootElement)
            with :? JsonException as error ->
                DeclarationUnreadable $"not valid JSON ({error.Message})"

    let private resolve (root: string) (relativePath: string) =
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))

    /// The declaration text under `root`, or None when it is absent.
    let read (root: string) : string option =
        let path = resolve root ReliabilityDeclaration.RelativePath
        if File.Exists path then Some(File.ReadAllText path) else None

    /// Whether a repository-relative path exists under `root` (file or directory).
    let fileExists (root: string) (relativePath: string) =
        let path = resolve root relativePath
        File.Exists path || Directory.Exists path
