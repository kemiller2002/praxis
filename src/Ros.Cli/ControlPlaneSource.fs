namespace Ros.Cli

open System.Text
open System.Text.Json.Nodes
open Ros.Contracts.Work
open Ros.Domain.Work

/// Attributes every control-plane response to the durable state it was
/// derived from (PRX-CTL-004, PRX-CTL-008): the `Praxis-*` headers on every
/// response, and a `source` object on every versioned JSON document. The
/// hosts keep nothing between requests, so the identity is read per request.
[<RequireQualifiedAccess>]
module ControlPlaneSource =
    /// Re-reads of a GET whose state changed while it was answered.
    let readAttempts = 3

    let private isJson (response: HttpResponseData) =
        response.ContentType.StartsWith("application/json", System.StringComparison.OrdinalIgnoreCase)

    let private isVersionedDocument (node: JsonObject) =
        match node["contract"] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, contract -> contract.StartsWith("praxis.", System.StringComparison.Ordinal)
            | _ -> false
        | _ -> false

    /// A versioned JSON document gains `source`; any other body is returned
    /// byte for byte (legacy routes keep their exact shape).
    let private withSourceField (identity: StateIdentity) (stable: bool) (response: HttpResponseData) : HttpResponseData =
        if not (isJson response) then
            response
        else
            match (try JsonNode.Parse(Encoding.UTF8.GetString response.Body) with _ -> null) with
            | :? JsonObject as document when isVersionedDocument document ->
                document["source"] <- StateIdentityJson.sourceNode identity stable
                { response with Body = UTF8Encoding(false).GetBytes(HttpMessages.renderJson document) }
            | _ -> response

    /// A header value is printable ASCII; anything else (a repository named
    /// after a non-ASCII directory) is percent-encoded rather than dropped.
    let headerValue (value: string) =
        if value |> Seq.forall (fun character -> character >= ' ' && character <= '~') then
            value
        else
            System.Uri.EscapeDataString value

    /// Pure: a response attributed to the identity it was derived from.
    let attribute (attributed: StateAttributed<HttpResponseData>) : HttpResponseData =
        let response = withSourceField attributed.Identity attributed.Stable attributed.Value

        let headers =
            StateIdentityJson.headers attributed.Identity attributed.Stable
            |> List.map (fun (name, value) -> name, headerValue value)

        { response with Headers = response.Headers @ headers }

    /// Wraps a host's handler. A GET is a pure read of the records, so it is
    /// re-read until the identity before and after it agree. Any other
    /// method runs exactly once (mutations go through the CLI); its answer is
    /// attributed to the identity observed after it, stable when two
    /// successive observations agree.
    let serve (identify: unit -> StateIdentity) (handler: HttpRequestData -> HttpResponseData) (request: HttpRequestData) : HttpResponseData =
        match request.Method with
        | "GET"
        | "HEAD" -> StateIdentity.stable identify (fun () -> handler request) readAttempts |> attribute
        | _ ->
            let response = handler request
            StateIdentity.stable identify (fun () -> response) readAttempts |> attribute
