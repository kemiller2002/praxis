namespace Ros.Domain.Work

open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module WorkItemId =
    let isValid (value: string) =
        Regex.IsMatch(value, "^[A-Z][A-Z0-9_-]*-[A-Z0-9][A-Z0-9_-]*$")
