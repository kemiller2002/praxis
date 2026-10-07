namespace Praxis.Infrastructure.Execution

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Praxis.Domain.Execution

/// The governing Ordo execution contract an execution starts from
/// (PRAXIS-FND-02; PRX-BND-001, PRX-SEQ-003, PRX-VER-010). The role, the
/// semantic mutation boundary, the evaluator closure and the human-only
/// transitions come from an `ordo.execution-contract/1` document read and
/// validated by Ordo.Core, never from a Praxis-local rule. Operator flags are
/// assembled into the same document and validated the same way, and the
/// execution ledger records which source supplied the contract and its digest.
type GoverningContract =
    { Role: ExecutionRole
      Boundary: MutationBoundary
      /// The evaluator closure as KIND=REFERENCE; the host observes digests.
      Evaluator: (string * string) list
      HumanOnly: string list
      /// `contract-file:<path>` or `operator-flags`.
      Source: string
      Sha256: string }

[<RequireQualifiedAccess>]
module ExecutionContracts =
    /// Flags that a contract file supplies and that therefore may not also be given.
    let contractFlags = [ "--role"; "--scope"; "--allow"; "--evaluator"; "--human-only" ]

    let private sha256 (text: string) = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

    let private ofOrdo (source: string) (text: string) : Result<GoverningContract, string> =
        match Ordo.Core.ExecutionContract.ExecutionContract.parse text with
        | Error error -> Error("the execution contract is invalid: " + Ordo.Core.ExecutionContract.ContractError.describe error)
        | Ok contract ->
            let roleName = Ordo.Core.ExecutionRole.ExecutionRole.toWire contract.Role

            match ExecutionRole.tryParse roleName with
            | None -> Error $"this host cannot run role '{roleName}'"
            | Some role ->
                let scope = Ordo.Core.MutationBoundary.SemanticScope.toWire

                Ok
                    { Role = role
                      Boundary =
                        { Scopes = contract.Boundary.Scopes |> List.map scope
                          Projections = contract.Boundary.Projections |> List.map (fun p -> { Scope = scope p.Scope; Patterns = p.Patterns })
                          EvaluatorReferences = contract.Boundary.EvaluatorReferences }
                      Evaluator = contract.Evaluator |> List.map (fun r -> Ordo.Core.Evaluator.EvaluatorInputKind.toWire r.Kind, r.Reference)
                      HumanOnly = contract.HumanOnly
                      Source = source
                      Sha256 = sha256 text }

    /// Read a contract file (relative to the repository root).
    let fromFile (root: string) (path: string) =
        let full = if Path.IsPathRooted path then path else Path.Combine(root, path)

        if not (File.Exists full) then
            Error $"execution contract not found: {path}"
        else
            ofOrdo $"contract-file:{path.Replace('\\', '/')}" (File.ReadAllText full)

    let private quote (value: string) = Text.Json.JsonSerializer.Serialize value
    let private array (items: string list) = "[" + String.Join(",", items |> List.map quote) + "]"

    /// Assemble the operator's flags into an `ordo.execution-contract/1`
    /// document so Ordo validates them exactly as it validates a contract file.
    let fromFlags (role: string option) (scopes: string list) (allows: string list) (evaluators: string list) (humanOnly: string list) =
        let pairs (flag: string) (separator: string -> int) (specs: string list) =
            specs
            |> List.map (fun spec ->
                match separator spec with
                | -1 -> Error $"{flag} expects A=B, got '{spec}'"
                | i -> Ok(spec.Substring(0, i), spec.Substring(i + 1)))
            |> List.fold (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> x :: xs))) (Ok [])
            |> Result.map List.rev

        match role with
        | None -> Error "--role specification|implementation|verification|review|integration|administration is required"
        | Some roleName ->
            pairs "--allow" (fun s -> s.LastIndexOf '=') allows
            |> Result.bind (fun projections ->
                pairs "--evaluator" (fun s -> s.IndexOf '=') evaluators
                |> Result.bind (fun closure ->
                    let projectionJson =
                        projections
                        |> List.groupBy fst
                        |> List.map (fun (scope, ps) -> $"{{\"scope\":{quote scope},\"patterns\":{array (ps |> List.map snd)}}}")
                        |> String.concat ","

                    let evaluatorJson =
                        closure
                        |> List.map (fun (kind, reference) -> $"{{\"kind\":{quote kind},\"reference\":{quote (reference.Replace('\\', '/'))}}}")
                        |> String.concat ","

                    let document =
                        $"{{\"schema\":\"ordo.execution-contract/1\",\"role\":{quote (roleName.Trim().ToLowerInvariant())},\"boundary\":{{\"scopes\":{array scopes},\"projections\":[{projectionJson}],\"evaluatorReferences\":[]}},\"evaluator\":[{evaluatorJson}],\"humanOnly\":{array humanOnly}}}"

                    ofOrdo "operator-flags" document))

    /// The governing contract for an execution start: the contract file when
    /// one is named (refusing any flag a contract supplies), else the
    /// operator's flags assembled into the same Ordo contract.
    let resolve (root: string) (contractFile: string option) (suppliedFlags: string list) role scopes allows evaluators humanOnly =
        match contractFile, suppliedFlags |> List.filter (fun flag -> List.contains flag contractFlags) with
        | Some path, [] -> fromFile root path
        | Some _, flags -> Error $"""--contract supplies the role, boundary, evaluator and human-only transitions; remove {String.Join(", ", flags)}"""
        | None, _ -> fromFlags role scopes allows evaluators humanOnly
