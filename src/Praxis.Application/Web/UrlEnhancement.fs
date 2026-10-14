namespace Praxis.Application.Web

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// The one browser script Praxis's web and hub UIs load (DF-ROS-2026-A058):
/// `url-state.js`, a progressive enhancement that replaces the history entry
/// on a filter refinement (SAF-URL-3) and copies "Link to this view" in one
/// action (SAF-URL-10). It is embedded in the assembly, served from the UI's
/// own origin, pinned by its Subresource Integrity digest, and allowed by a
/// Content-Security-Policy that admits no inline script, no eval and no
/// network connection. Pure apart from reading the embedded resource once.
[<RequireQualifiedAccess>]
module UrlEnhancement =
    /// Where the UIs serve the script. It is an asset, not a view, so it is
    /// not in the route inventory.
    [<Literal>]
    let Path = "/url-state.js"

    /// The script's exact bytes, as embedded at build time.
    let bytes: byte array =
        use stream = typeof<WorkFilter>.Assembly.GetManifestResourceStream "url-state.js"
        use buffer = new MemoryStream()
        stream.CopyTo buffer
        buffer.ToArray()

    let source = Encoding.UTF8.GetString bytes

    /// The Subresource Integrity value the page pins the script to.
    let integrity = "sha256-" + Convert.ToBase64String(SHA256.HashData bytes)

    /// The element every UI page carries in its head. `defer` runs it after
    /// the document is parsed; the page is complete without it.
    let scriptTag = $"<script src=\"{Path}\" integrity=\"{integrity}\" defer></script>"

    /// Scripts only from the UI's own origin (the integrity attribute pins
    /// which), no inline script or `eval` (neither keyword is granted), no
    /// network connection from script, and no DOM sink that takes strings.
    let contentSecurityPolicy =
        String.concat
            "; "
            [ "default-src 'self'"
              "script-src 'self'"
              "connect-src 'none'"
              "img-src 'self' data:"
              "font-src 'self' data:"
              "object-src 'none'"
              "base-uri 'none'"
              "form-action 'self'"
              "frame-ancestors 'none'"
              "require-trusted-types-for 'script'"
              "trusted-types 'none'" ]
