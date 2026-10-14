namespace Praxis.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading

/// A headless Chrome or Chromium driven over the Chrome DevTools Protocol with
/// nothing but the BCL: no Selenium, no Playwright, no Node (RQ-ROS-2026-A024).
/// The browser suite (`Praxis.Tests --suite browser`) uses it to prove the
/// approved `url-state.js` enhancement and its script-free fallback
/// (DF-ROS-2026-A058). The browser is `PRAXIS_TEST_BROWSER`, or the first
/// Chrome or Chromium found in the usual places; none found is a failure.
[<RequireQualifiedAccess>]
module BrowserHarness =
    let private candidates =
        [ Environment.GetEnvironmentVariable "PRAXIS_TEST_BROWSER"
          "/usr/bin/google-chrome"
          "/usr/bin/google-chrome-stable"
          "/usr/bin/chromium"
          "/usr/bin/chromium-browser"
          "/root/bin/chromium"
          "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
          @"C:\Program Files\Google\Chrome\Application\chrome.exe"
          @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" ]

    let executable () =
        match candidates |> List.tryFind (fun path -> not (String.IsNullOrWhiteSpace path) && File.Exists path) with
        | Some path -> path
        | None -> failwith "no Chrome or Chromium found: set PRAXIS_TEST_BROWSER to a Chrome executable to run the browser suite"

/// Building and reading DevTools protocol JSON.
[<RequireQualifiedAccess>]
module Cdp =
    let args (pairs: (string * JsonNode) list) =
        let message = JsonObject()
        pairs |> List.iter (fun (name, value) -> message[name] <- value)
        message

    let num (value: int) : JsonNode = JsonValue.Create value
    let real (value: float) : JsonNode = JsonValue.Create value
    let str (value: string) : JsonNode = JsonValue.Create value
    let flag (value: bool) : JsonNode = JsonValue.Create value

    let at (node: JsonNode) (path: string list) =
        path |> List.fold (fun (current: JsonNode) (key: string) -> if isNull current then null else current[key]) node

    let int node path = (at node path).GetValue<int>()
    let text node path = match at node path with | null -> "" | value -> value.GetValue<string>()
    let strings node path = (at node path).AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> List.ofSeq

/// One page target, attached with a flat session.
type BrowserPage(browser: Browser, session: string) =
    member _.Send(methodName: string, ?parameters: JsonObject) = browser.Send(methodName, defaultArg parameters (JsonObject()), session)

    /// Every event this page has received so far, oldest first.
    member _.Events() : (string * JsonNode) list = browser.Events session

    /// Waits until an event named `methodName` arrives after `seen` events.
    member this.WaitForEvent(methodName: string, seen: int) =
        let deadline = DateTime.UtcNow.AddSeconds 30.0

        let rec poll () =
            let events = this.Events()

            match events |> List.skip (min seen events.Length) |> List.tryFind (fun (name, _) -> name = methodName) with
            | Some(_, payload) -> payload
            | None when DateTime.UtcNow > deadline -> failwith $"no {methodName} within 30 seconds"
            | None ->
                Thread.Sleep 50
                poll ()

        poll ()

    /// Runs `action`, then waits for the next page load it caused.
    member this.AndWaitForLoad(action: unit -> unit) =
        let seen = this.Events().Length
        action ()
        this.WaitForEvent("Page.loadEventFired", seen) |> ignore

    member this.Navigate(url: string) =
        this.AndWaitForLoad(fun () -> this.Send("Page.navigate", Cdp.args [ "url", Cdp.str url ]) |> ignore)

    /// The node matching `selector` in the current document, if any.
    member this.Query(selector: string) =
        let document = this.Send("DOM.getDocument", Cdp.args [ "depth", Cdp.num 0 ])
        let found = this.Send("DOM.querySelector", Cdp.args [ "nodeId", Cdp.num (Cdp.int document [ "root"; "nodeId" ]); "selector", Cdp.str selector ])

        match Cdp.int found [ "nodeId" ] with
        | 0 -> None
        | node -> Some node

    member this.Node(selector: string) =
        this.Query selector |> Option.defaultWith (fun () -> failwith $"no element matches {selector}")

    member private this.OnNode(methodName: string, selector: string) =
        this.Send(methodName, Cdp.args [ "nodeId", Cdp.num (this.Node selector) ])

    member this.Attributes(selector: string) =
        let flat = Cdp.strings (this.OnNode("DOM.getAttributes", selector)) [ "attributes" ] |> Array.ofList
        [ for index in 0..2 .. flat.Length - 2 -> flat[index], flat[index + 1] ]

    member this.OuterHtml(selector: string) =
        Cdp.text (this.OnNode("DOM.getOuterHTML", selector)) [ "outerHTML" ]

    /// Sets an attribute through the DevTools DOM, which works with page
    /// script disabled: a pristine field's `value` attribute is its value.
    member this.SetAttribute(selector: string, name: string, value: string) =
        this.Send("DOM.setAttributeValue", Cdp.args [ "nodeId", Cdp.num (this.Node selector); "name", Cdp.str name; "value", Cdp.str value ]) |> ignore

    /// Whether the element is rendered (a `hidden` element has no box).
    member this.IsRendered(selector: string) =
        try
            this.OnNode("DOM.getBoxModel", selector) |> ignore
            true
        with _ ->
            false

    /// A real mouse click at the element's centre: what a person does.
    member this.Click(selector: string) =
        this.OnNode("DOM.scrollIntoViewIfNeeded", selector) |> ignore
        let quad = Cdp.at (this.OnNode("DOM.getBoxModel", selector)) [ "model"; "content" ] |> fun node -> node.AsArray() |> Seq.map (fun item -> item.GetValue<float>()) |> Array.ofSeq
        let x = (quad[0] + quad[2] + quad[4] + quad[6]) / 4.0
        let y = (quad[1] + quad[3] + quad[5] + quad[7]) / 4.0

        for kind in [ "mousePressed"; "mouseReleased" ] do
            this.Send("Input.dispatchMouseEvent", Cdp.args [ "type", Cdp.str kind; "x", Cdp.real x; "y", Cdp.real y; "button", Cdp.str "left"; "clickCount", Cdp.num 1 ]) |> ignore

    /// The session history the browser keeps: (current index, entry URLs).
    member this.History() =
        let history = this.Send "Page.getNavigationHistory"
        let entries = (Cdp.at history [ "entries" ]).AsArray() |> Seq.map (fun entry -> Cdp.text entry [ "url" ]) |> List.ofSeq
        Cdp.int history [ "currentIndex" ], entries

    /// Evaluates an expression in the page (DevTools, not page script) and
    /// awaits a promise result.
    member this.Evaluate(expression: string) =
        let result = this.Send("Runtime.evaluate", Cdp.args [ "expression", Cdp.str expression; "awaitPromise", Cdp.flag true; "returnByValue", Cdp.flag true ])

        match result["exceptionDetails"] with
        | null -> Cdp.at result [ "result"; "value" ]
        | details -> failwith $"evaluation failed: {details.ToJsonString()}"

    /// Console errors, uncaught exceptions and security (CSP) reports. The
    /// browser's own `/favicon.ico` probe (the UIs have none) is not a page problem.
    member this.Problems() =
        this.Events()
        |> List.choose (fun (name: string, payload: JsonNode) ->
            match name with
            | "Runtime.exceptionThrown" -> Some(payload.ToJsonString())
            | "Log.entryAdded" when not ((Cdp.text payload [ "entry"; "url" ]).EndsWith "/favicon.ico") && (Cdp.text payload [ "entry"; "level" ] = "error" || Cdp.text payload [ "entry"; "source" ] = "security") ->
                Some(Cdp.text payload [ "entry"; "text" ] + " " + Cdp.text payload [ "entry"; "url" ])
            | _ -> None)

/// A launched browser and its DevTools connection; `Dispose` stops it.
and Browser private (child: Process, profile: string, socket: ClientWebSocket) =
    let mutable next = 0
    let pending = Dictionary<int, JsonNode>()
    let events = List<string * string * JsonNode>()
    let gate = obj ()

    let receive () =
        let buffer = Array.zeroCreate<byte> 1_048_576

        try
            while socket.State = WebSocketState.Open do
                use message = new MemoryStream()
                let mutable finished = false

                while not finished do
                    let part = socket.ReceiveAsync(ArraySegment buffer, CancellationToken.None).Result
                    message.Write(buffer, 0, part.Count)
                    finished <- part.EndOfMessage || part.MessageType = WebSocketMessageType.Close

                if message.Length > 0L then
                    let node = JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))

                    lock gate (fun () ->
                        match node["id"] with
                        | null ->
                            let session = match node["sessionId"] with | null -> "" | value -> value.GetValue<string>()
                            events.Add(session, node["method"].GetValue<string>(), node["params"])
                        | id -> pending[id.GetValue<int>()] <- node)
        with _ ->
            ()

    let reader = Thread(receive, IsBackground = true)
    do reader.Start()

    static member Launch() =
        let profile = Path.Combine(Path.GetTempPath(), "praxis-browser-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory profile |> ignore
        let startInfo = ProcessStartInfo(BrowserHarness.executable ())
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardError <- true
        startInfo.RedirectStandardOutput <- true

        [ "--headless=new"; "--remote-debugging-port=0"; $"--user-data-dir={profile}"; "--no-first-run"; "--no-default-browser-check"
          "--no-sandbox"; "--disable-gpu"; "--disable-extensions"; "--window-size=1280,2400"; "about:blank" ]
        |> List.iter startInfo.ArgumentList.Add

        let child = Process.Start startInfo
        child.BeginOutputReadLine()
        let endpoint = TaskCompletionSourceSlim()
        let listening = Regex("DevTools listening on (ws://\\S+)")

        child.ErrorDataReceived.Add(fun line ->
            if not (isNull line.Data) then
                let found = listening.Match line.Data
                if found.Success then endpoint.Set found.Groups[1].Value)

        child.BeginErrorReadLine()

        match endpoint.Wait(TimeSpan.FromSeconds 60.0) with
        | None ->
            child.Kill true
            failwith "the browser did not open its DevTools endpoint within 60 seconds"
        | Some url ->
            let socket = new ClientWebSocket()
            socket.Options.KeepAliveInterval <- TimeSpan.Zero
            socket.ConnectAsync(Uri url, CancellationToken.None).Wait()
            new Browser(child, profile, socket)

    member _.Send(methodName: string, parameters: JsonObject, session: string) : JsonNode =
        let id = Interlocked.Increment &next
        let message = Cdp.args [ "id", Cdp.num id; "method", Cdp.str methodName; "params", parameters :> JsonNode ]
        if session <> "" then message["sessionId"] <- Cdp.str session
        let bytes = Encoding.UTF8.GetBytes(message.ToJsonString())
        lock socket (fun () -> socket.SendAsync(ArraySegment bytes, WebSocketMessageType.Text, true, CancellationToken.None).Wait())
        let deadline = DateTime.UtcNow.AddSeconds 30.0

        let rec await () =
            match lock gate (fun () -> match pending.TryGetValue id with | true, reply -> pending.Remove id |> ignore; Some reply | _ -> None) with
            | Some reply ->
                match reply["error"] with
                | null -> reply["result"]
                | error -> failwith $"{methodName} failed: {error.ToJsonString()}"
            | None when DateTime.UtcNow > deadline -> failwith $"{methodName} had no reply within 30 seconds"
            | None ->
                Thread.Sleep 10
                await ()

        await ()

    member _.Events(session: string) =
        lock gate (fun () -> events |> Seq.filter (fun (owner, _, _) -> owner = session) |> Seq.map (fun (_, name, payload) -> name, payload) |> List.ofSeq)

    /// A fresh page, with the Page, DOM, Runtime and Log domains enabled.
    /// `scripts = false` disables page script for the page: the no-JS case.
    member this.NewPage(scripts: bool) =
        let target = Cdp.text (this.Send("Target.createTarget", Cdp.args [ "url", Cdp.str "about:blank" ], "")) [ "targetId" ]
        let attached = this.Send("Target.attachToTarget", Cdp.args [ "targetId", Cdp.str target; "flatten", Cdp.flag true ], "")
        let page = BrowserPage(this, Cdp.text attached [ "sessionId" ])

        for domain in [ "Page.enable"; "DOM.enable"; "Runtime.enable"; "Log.enable" ] do
            page.Send domain |> ignore

        page.Send("Emulation.setFocusEmulationEnabled", Cdp.args [ "enabled", Cdp.flag true ]) |> ignore

        if not scripts then
            page.Send("Emulation.setScriptExecutionDisabled", Cdp.args [ "value", Cdp.flag true ]) |> ignore

        page

    /// Lets pages from `origin` read and write the clipboard.
    member this.GrantClipboard(origin: string) =
        let permissions = JsonArray(Cdp.str "clipboardReadWrite", Cdp.str "clipboardSanitizedWrite")
        this.Send("Browser.grantPermissions", Cdp.args [ "origin", Cdp.str origin; "permissions", permissions :> JsonNode ], "") |> ignore

    interface IDisposable with
        member _.Dispose() =
            try
                try
                    socket.Abort()
                    socket.Dispose()
                finally
                    if not child.HasExited then
                        child.Kill true
                        child.WaitForExit 10000 |> ignore

                    child.Dispose()
            finally
                try
                    Directory.Delete(profile, true)
                with _ ->
                    ()

/// A one-shot value set from another thread.
and private TaskCompletionSourceSlim() =
    let signal = new ManualResetEventSlim(false)
    let mutable value = ""

    member _.Set(text: string) =
        if not signal.IsSet then
            value <- text
            signal.Set()

    member _.Wait(timeout: TimeSpan) = if signal.Wait timeout then Some value else None
