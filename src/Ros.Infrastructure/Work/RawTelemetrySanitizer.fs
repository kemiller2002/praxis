namespace Ros.Infrastructure.Work

open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// Shared raw-provider boundary used by native ingestion and runtime-free
/// envelope reconciliation. Sensitive values are never copied into canonical
/// telemetry merely because a provider supplied an otherwise valid object.
[<RequireQualifiedAccess>]
module RawTelemetrySanitizer =
    let private redactedKeyPattern =
        Regex(
            @"^(?:authorization|cookie|set-cookie|password|passwd|secret|credential|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|prompt|prompts|messages?|content|tool[_-]?input|tool[_-]?response|request|response|stdout|stderr|command|full[_-]?command|transcript[_-]?path|cwd|current[_-]?dir|project[_-]?dir|workspace[_-]?path|file[_-]?path|email|user\.email)$",
            RegexOptions.IgnoreCase
        )

    let private sensitiveSegmentPattern =
        Regex(
            @"(?:^|[._-])(?:authorization|password|passwd|secret|credential|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|email)(?:$|[._-])",
            RegexOptions.IgnoreCase
        )

    let private maxRawStringLength = 2048

    let rec sanitizeInto (value: JsonNode) (currentPath: string) (redactions: ResizeArray<string>) : JsonNode =
        match value with
        | null -> null
        | :? JsonArray as array ->
            let result = JsonArray()
            array |> Seq.iteri (fun index item -> result.Add(sanitizeInto item $"{currentPath}[{index}]" redactions))
            result
        | :? JsonObject as obj ->
            let result = JsonObject()

            for entry in obj |> Seq.toList do
                let childPath = $"{currentPath}.{entry.Key}"

                if redactedKeyPattern.IsMatch(entry.Key) || sensitiveSegmentPattern.IsMatch(entry.Key) then
                    result[entry.Key] <- JsonValue.Create "[REDACTED_BY_ROS]"
                    redactions.Add childPath
                else
                    result[entry.Key] <- sanitizeInto entry.Value childPath redactions

            result
        | :? JsonValue as leaf when leaf.GetValueKind() = JsonValueKind.String ->
            let text = leaf.GetValue<string>()

            if text.Length > maxRawStringLength then
                JsonValue.Create $"[TRUNCATED_BY_ROS length={text.Length}]"
            else
                JsonValue.Create text
        | _ -> value.DeepClone()

    let rec private leafPathsInto (value: JsonNode) (prefix: string) (result: ResizeArray<string>) =
        match value with
        | :? JsonArray as array -> array |> Seq.iteri (fun index item -> leafPathsInto item $"{prefix}[{index}]" result)
        | :? JsonObject as obj ->
            for entry in obj do
                leafPathsInto entry.Value $"{prefix}.{entry.Key}" result
        | _ -> result.Add(Regex.Replace(prefix, @"\[\d+\]", "[]"))

    let leafPaths (value: JsonNode) : string list =
        let result = ResizeArray<string>()
        leafPathsInto value "$" result
        result |> Seq.distinct |> Seq.sortWith (fun left right -> System.String.CompareOrdinal(left, right)) |> Seq.toList
