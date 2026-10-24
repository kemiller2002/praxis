namespace Praxis.Domain.Work

open System
open System.Security.Cryptography
open System.Text

/// The minimal authority contract an implementation agent's brief carries
/// (VAC-002-P3-01 R-PRAX-1, owner decision OD-6). It restates existing
/// policy (cohort-based-engineering principle 3, VAC-CBE-001-R2); it is not a
/// demonstrated reliability control (P3: 3/18 vs 7/18, p = 0.125,
/// inconclusive). The wording is frozen: a paraphrase needs a new decision.
/// Pure: reading the brief is an effect at the edge.
[<RequireQualifiedAccess>]
module AuthorityContract =
    /// The frozen four sentences, byte for byte, as one line.
    [<Literal>]
    let Text =
        "Implement only behavior supported by requirements or accepted decisions. Do not invent exceptions, precedence rules, or business policies. Record unsupported interpretations as unresolved questions. Do not treat your own proposed decisions as authorized."

    /// SHA-256 of `Text` (UTF-8, no trailing newline), as frozen by P3.
    [<Literal>]
    let Sha256 = "2af405073b299f562a8321feac875d2b9a9ae6d94171475a59a60a416784862d"

    /// The frozen contract's provenance label.
    [<Literal>]
    let Version = "VAC-002-P3"

    /// The short note that precedes the contract in a generated brief.
    [<Literal>]
    let Note =
        "_Existing policy (cohort-based-engineering principle 3, VAC-CBE-001-R2; owner decision OD-6), not a demonstrated reliability control (VAC-002 P3: 3/18 vs 7/18, p = 0.125, inconclusive)._"

    /// The heading the contract is rendered under.
    [<Literal>]
    let Heading = "## Authority"

    /// What a brief carries. `Present` names the digest of the contract text
    /// actually found, which differs from `Sha256` when the brief holds a
    /// different wording; `Absent` is never a digest.
    type Observation =
        | Present of sha256: string
        | Absent of reason: string

    let digest (text: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString |> fun hex -> hex.ToLowerInvariant()

    let isFrozen (sha256: string) = sha256 = Sha256

    /// The contract text of a Markdown brief: the first block quote of its
    /// `## Authority` section, quote markers removed and lines joined by one
    /// space. None when the brief has no such section or no quote in it.
    let find (brief: string) : string option =
        let lines = brief.Replace("\r\n", "\n").Split('\n') |> Array.toList
        let isHeading (line: string) = line.StartsWith "# " || line.StartsWith "## "
        let unquote (line: string) = line.TrimStart().Substring(1).Trim()
        let isQuote (line: string) = line.TrimStart().StartsWith ">"

        match lines |> List.skipWhile (fun line -> line.TrimEnd() <> Heading) with
        | [] -> None
        | _ :: rest ->
            let quote =
                rest
                |> List.takeWhile (isHeading >> not)
                |> List.skipWhile (isQuote >> not)
                |> List.takeWhile isQuote
                |> List.map unquote
                |> List.filter (String.IsNullOrWhiteSpace >> not)

            if quote.IsEmpty then None else Some(String.Join(" ", quote))

    /// The observation for a brief that may not exist.
    let observe (brief: string option) : Observation =
        match brief with
        | None -> Absent "brief-missing"
        | Some text ->
            match find text with
            | Some contract -> Present(digest contract)
            | None -> Absent "no-authority-contract"
