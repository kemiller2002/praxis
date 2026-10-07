namespace Ros.Infrastructure.Json

open System
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// JSON text and hashing primitives shared by every real effect that must
/// reproduce one of production's own content-addressed IDs (`tools/
/// ros_cli.mjs`'s event `eventId`, `tools/ros_telemetry.mjs`'s
/// `measurementId`/execution ID). Both are plain SHA-256 hex digests of a
/// `JSON.stringify`-equivalent string, so the only thing that must match
/// exactly is the JSON text: compact (no indentation) and escaped the same
/// minimal way `JSON.stringify` escapes (never HTML-safe).
[<RequireQualifiedAccess>]
module CanonicalJson =
    let private serializerOptions =
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    /// `JSON.stringify(node)`: compact, JS-compatible escaping, and -- unlike
    /// a "canonical JSON" scheme -- no key reordering. The caller is
    /// responsible for inserting object keys in the exact order production's
    /// own object literal (plus any spread) would, since `JSON.stringify`
    /// itself never reorders keys.
    let serializeCompact (node: JsonNode) : string = node.ToJsonString(serializerOptions)

    /// `crypto.createHash("sha256").update(text).digest("hex").slice(0, length)`.
    let sha256HexPrefix (length: int) (text: string) : string =
        let hex =
            text
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()

        hex.Substring(0, length)

    /// `stable(value)` (`tools/ros_telemetry.mjs`): recursively rebuilds a
    /// node with every object's own keys sorted (ordinally, matching
    /// production's plain `Object.keys(value).sort()` for realistic JSON
    /// key names), leaving array order and every scalar untouched. Unlike
    /// this migration's other content-addressed digests (`recordLifecycle`'s
    /// event, `derivedMetricNode`'s metric), which hand-build a flat,
    /// already-sorted object literal because their own field set is fixed
    /// and shallow, this is for digesting genuinely arbitrary,
    /// caller-supplied JSON (a raw telemetry payload, an ingested event or
    /// quality signal, a metric's own `dimensions`/`pricing`) where the key
    /// set and nesting depth are not known in advance. Always rebuilds
    /// fresh nodes (via `DeepClone` for scalars) since a `JsonNode` can only
    /// ever be attached to one parent.
    let rec stabilize (node: JsonNode) : JsonNode =
        match node with
        | null -> null
        | :? JsonArray as array ->
            let result = JsonArray()
            for item in array do
                result.Add(stabilize item)

            result
        | :? JsonObject as obj ->
            let result = JsonObject()
            let keys = obj |> Seq.map (fun entry -> entry.Key) |> List.ofSeq |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

            for key in keys do
                result[key] <- stabilize obj[key]

            result
        | _ -> node.DeepClone()

    /// `digest(value, length)`: `stable()` followed by `serializeCompact`
    /// and `sha256HexPrefix`, for digesting arbitrary (not hand-sorted)
    /// JSON directly.
    let contentDigest (length: int) (node: JsonNode) : string =
        sha256HexPrefix length (serializeCompact (stabilize node))
