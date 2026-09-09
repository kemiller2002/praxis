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
