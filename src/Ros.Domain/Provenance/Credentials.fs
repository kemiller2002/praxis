namespace Ros.Domain.Provenance

open System.Text.RegularExpressions

/// Provenance identifies an execution; it never carries authentication
/// material (RQ-ROS-2026-A013). This recognizes the unambiguous shapes of
/// common credentials so a value that would leak one is refused rather than
/// recorded. It is a guard against accidents, not a secret scanner: a value
/// it does not recognize is not thereby proven safe.
[<RequireQualifiedAccess>]
module Credentials =
    let private patterns =
        [ @"\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{16,}"
          @"\bgh[pousr]_[A-Za-z0-9]{20,}"
          @"\bgithub_pat_[A-Za-z0-9_]{20,}"
          @"\bxox[abposr]-[A-Za-z0-9-]{10,}"
          @"\bAKIA[0-9A-Z]{16}\b"
          @"\bAIza[0-9A-Za-z_-]{30,}"
          @"-----BEGIN [A-Z ]*PRIVATE KEY-----"
          @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{16,}"
          @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"
          @"(?i)\b(?:api[_-]?key|access[_-]?token|secret|password|passwd)\s*[=:]\s*\S{8,}" ]
        |> List.map (fun pattern -> Regex(pattern, RegexOptions.CultureInvariant))

    let looksLikeCredential (value: string) =
        not (isNull value) && patterns |> List.exists (fun pattern -> pattern.IsMatch value)
