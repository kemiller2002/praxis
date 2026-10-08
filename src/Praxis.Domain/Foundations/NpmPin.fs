namespace Praxis.Domain.Foundations

open System
open System.Text.RegularExpressions

/// Whether an npm dependency specifier pins an Echelon foundation to its
/// declared immutable baseline. Pure.
[<RequireQualifiedAccess>]
module NpmPin =
    /// A range, tag or branch: never an immutable baseline.
    let isFloatingSpec (spec: string) =
        let value = spec.Trim()
        value.StartsWith("^", StringComparison.Ordinal)
        || value.StartsWith("~", StringComparison.Ordinal)
        || value = "*"
        || value.Equals("latest", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(value, "(^|[#/@])main($|[/?#])", RegexOptions.IgnoreCase)

    /// The specifier names the declared commit or version, and does not float.
    let isPinned (expectedVersion: string option) (sourceCommit: string option) (spec: string option) =
        match spec with
        | None -> false
        | Some value when isFloatingSpec value -> false
        | Some value ->
            match sourceCommit, expectedVersion with
            | Some commit, _ -> value.Contains(commit, StringComparison.OrdinalIgnoreCase)
            | None, Some version ->
                value.Equals(version, StringComparison.OrdinalIgnoreCase)
                || value.Contains($"/v{version}/", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith($"#v{version}", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith($"@{version}", StringComparison.OrdinalIgnoreCase)
            | None, None -> true
