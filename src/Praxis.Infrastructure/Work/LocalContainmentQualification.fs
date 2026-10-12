namespace Praxis.Infrastructure.Work

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work

/// What the host observed around one probe launch. In-sandbox reports are
/// keyed by probe id; host observations never come from worker output.
type ContainmentProbeObservations =
    { Reports: Map<string, string>
      Completed: bool
      Output: string
      /// Write targets whose host state changed (exists/content).
      ChangedTargets: Set<string>
      /// The written-inside file holds the nonce on the host.
      InsideWritten: bool
      /// Planted secrets (by probe id) that appeared in worker output.
      LeakedSecrets: Set<string>
      LoopbackAccepted: bool
      SentinelAlive: bool
      /// The adapter refused a spec with a non-allowlisted variable.
      NonAllowlistedRefused: bool
      AllowedEnvironment: Set<string>
      Limits: int * int }

/// The real probe suite: it plants secrets and targets in the host roots,
/// starts a sentinel host process and a loopback listener, launches a fixed
/// probe script through the confined adapter and the real supervisor, and
/// judges every probe from host observations plus the in-sandbox report.
[<RequireQualifiedAccess>]
module LocalContainmentQualification =
    /// The fixed probe script (bash). Paths and targets arrive as arguments.
    let probeScript =
        String.concat
            "\n"
            [ "nonce=$1; writes=$2; reads=$3; sibling=$4; port=$5; spid=$6; sname=$7; usr_file=$8; creds=$9"
              "report() { printf 'PROBE\\t%s\\t%s\\n' \"$1\" \"$2\"; }"
              "try_write() { if ( printf '%s\\n' \"$nonce\" >> \"$1\" ) 2>/dev/null; then echo written; else echo denied; fi; }"
              "try_read() { local c; if c=$(cat -- \"$1\" 2>/dev/null); then printf 'read:%s' \"$c\"; else echo denied; fi; }"
              "names=$(compgen -e | sort | tr '\\n' ','); report env.allowlist \"${names%,}\""
              "if printf '%s' \"$nonce\" > \"probe-$nonce\" 2>/dev/null; then report fs.write-inside allowed; else report fs.write-inside denied; fi"
              "while IFS=' ' read -r id path; do [ -n \"$id\" ] && report \"$id\" \"$(try_write \"$path\")\"; done <<< \"$writes\""
              "while IFS=' ' read -r id path; do [ -n \"$id\" ] && report \"$id\" \"$(try_read \"$path\")\"; done <<< \"$reads\""
              "signer=$(printf '%s\\n' \"$reads\" | while IFS=' ' read -r id path; do [ \"$id\" = cred.read-signer ] && printf '%s' \"$path\"; done)"
              "policy=$(printf '%s\\n' \"$writes\" | while IFS=' ' read -r id path; do [ \"$id\" = fs.write-policy ] && printf '%s' \"$path\"; done)"
              "ln -s \"$policy\" \"link-policy-$nonce\" 2>/dev/null; ln -s \"$sibling\" \"link-dir-$nonce\" 2>/dev/null; ln -s \"$signer\" \"link-signer-$nonce\" 2>/dev/null"
              "report fs.symlink-escape \"$(try_write \"link-policy-$nonce\"),$(try_write \"link-dir-$nonce/escape-$nonce\")\""
              "report cred.read-via-symlink \"$(try_read \"link-signer-$nonce\")\""
              "visible=''; while IFS= read -r c; do [ -n \"$c\" ] && [ -e \"$c\" ] && visible=\"$visible $c\"; done <<< \"$creds\""
              "report cred.host-credentials \"${visible:-absent}\""
              "if (exec 3<>\"/dev/tcp/127.0.0.1/$port\") 2>/dev/null; then report net.loopback-host connected; else report net.loopback-host denied; fi"
              "if timeout 5 bash -c 'exec 3<>/dev/tcp/1.1.1.1/443' 2>/dev/null; then report net.external connected; else report net.external denied; fi"
              "ifs=''; while IFS=: read -r name rest; do case $name in *'|'*) ;; *) ifs=\"$ifs${name// /},\";; esac; done < /proc/net/dev; report net.interfaces \"${ifs%,}\""
              "scan() { local found=isolated d a0; for d in /proc/[0-9]*; do a0=''; IFS= read -r -d '' a0 < \"$d/cmdline\" 2>/dev/null; [ \"$a0\" = \"$sname\" ] && found=visible; done; echo \"$found\"; }"
              "report proc.host-visibility \"$(scan)\""
              "export sname; export -f scan; child=$(bash -c scan 2>/dev/null); report proc.subprocess-isolated \"${child:-not-run}\""
              "if kill -KILL \"$spid\" 2>/dev/null; then report proc.signal-host delivered; else report proc.signal-host denied; fi"
              "if [ -e \"/proc/$spid\" ]; then if e=$(tr '\\0' '\\n' < \"/proc/$spid/environ\" 2>/dev/null); then report proc.ptrace-host \"read:$e\"; else report proc.ptrace-host denied; fi; else report proc.ptrace-host absent; fi"
              "st=$(grep -E '^(NoNewPrivs|CapInh|CapPrm|CapEff|CapBnd|CapAmb):' /proc/self/status | tr -s '\\t ' '=' | tr '\\n' ';'); report proc.no-new-privs \"$st\""
              "if command -v su >/dev/null 2>&1; then if su -c true root </dev/null >/dev/null 2>&1; then report proc.setuid escalated; else report proc.setuid denied; fi; else report proc.setuid untested; fi"
              "r1=denied; mount -o remount,rw / 2>/dev/null && r1=remounted; r2=denied; mount -o remount,rw /usr 2>/dev/null && r2=remounted"
              "report proc.remount-rw \"$r1,$r2,$(try_write \"$usr_file\")\""
              "if unshare -U -r true 2>/dev/null; then report proc.nested-userns allowed; else report proc.nested-userns denied; fi"
              "report proc.rlimits \"nproc=$(ulimit -u),nofile=$(ulimit -n)\""
              "printf 'PROBE-END\\t%s\\n' \"$nonce\"" ]

    let probeScriptDigest = LinuxContainment.digestText probeScript

    let private dimension (id: string) =
        match id.Split('.').[0] with
        | "fs" -> "filesystem"
        | "cred" -> "credential"
        | "net" -> "network"
        | "proc" -> "process"
        | _ -> "environment"

    /// Every probe and what it must show. Write targets are judged by the
    /// host's own before/after snapshot, reads by planted-secret leakage.
    let writeProbes =
        [ "fs.write-repository"; "fs.write-policy"; "fs.write-signer"; "fs.write-journal"; "fs.write-spool"; "fs.write-submissions"
          "fs.write-evidence"; "fs.write-host-config"; "fs.write-sibling-worktree"; "fs.write-host-tmp" ]

    let readProbes = [ "cred.read-policy"; "cred.read-signer"; "cred.read-journal"; "cred.read-spool"; "cred.read-submissions"; "cred.read-evidence"; "cred.read-host-config" ]

    let probeIds =
        [ "fs.write-inside" ] @ writeProbes @ [ "fs.symlink-escape" ] @ readProbes
        @ [ "cred.read-via-symlink"; "cred.host-credentials"; "net.loopback-host"; "net.external"; "net.interfaces"
            "proc.host-visibility"; "proc.subprocess-isolated"; "proc.signal-host"; "proc.ptrace-host"; "proc.no-new-privs"
            "proc.setuid"; "proc.remount-rw"; "proc.nested-userns"; "proc.rlimits"; "env.allowlist"; "env.non-allowlisted-refused" ]

    let private expected id =
        match id with
        | "fs.write-inside" -> "allowed"
        | "env.allowlist" -> "only allowlisted names"
        | "env.non-allowlisted-refused" -> "adapter refuses"
        | "net.interfaces" -> "lo only"
        | "proc.rlimits" -> "profile limits"
        | "proc.no-new-privs" -> "NoNewPrivs=1, all capability sets 0"
        | "proc.host-visibility" | "proc.subprocess-isolated" -> "isolated"
        | _ when id.StartsWith "fs.write-" || id = "fs.symlink-escape" -> "no host effect"
        | _ when id.StartsWith "cred." -> "denied (no planted secret in output)"
        | _ -> "denied"

    /// Pure verdicts. A probe passes only when the in-sandbox report exists
    /// and the host observation agrees; a host-observed violation fails even
    /// without a report; otherwise the result is unknown, never a pass.
    let judge (observations: ContainmentProbeObservations) =
        let reported id = if observations.Completed then observations.Reports |> Map.tryFind id else None
        let verdict id =
            let report = reported id
            let held condition = if condition then ContainmentProbeOutcome.Pass else ContainmentProbeOutcome.Fail
            let outcome, detail =
                match id, report with
                | _, _ when id.StartsWith "fs.write-" && id <> "fs.write-inside" && observations.ChangedTargets.Contains id ->
                    ContainmentProbeOutcome.Fail, "host target changed"
                | "fs.symlink-escape", _ when observations.ChangedTargets.Contains id -> ContainmentProbeOutcome.Fail, "host target changed through a link"
                | _, _ when id.StartsWith "cred.read-" && observations.LeakedSecrets.Contains id -> ContainmentProbeOutcome.Fail, "planted secret appeared in worker output"
                | "net.loopback-host", _ when observations.LoopbackAccepted -> ContainmentProbeOutcome.Fail, "host listener accepted a connection"
                | "proc.signal-host", _ when not observations.SentinelAlive -> ContainmentProbeOutcome.Fail, "host sentinel process died"
                | "env.non-allowlisted-refused", _ -> held observations.NonAllowlistedRefused, "static adapter check"
                | _, None -> ContainmentProbeOutcome.Unknown, "no in-sandbox report"
                | "fs.write-inside", Some r -> held (r = "allowed" && observations.InsideWritten), "host read back the nonce"
                | _, Some r when id.StartsWith "fs.write-" -> ContainmentProbeOutcome.Pass, (if r = "written" then "write landed only in the private namespace" else "host unchanged")
                | "fs.symlink-escape", Some _ -> ContainmentProbeOutcome.Pass, "host unchanged"
                | "cred.host-credentials", Some r -> held (r = "absent"), "in-sandbox observation"
                | _, Some r when id.StartsWith "cred.read-" -> held (r = "denied"), "no planted secret in output"
                | "net.loopback-host", Some r -> held (r = "denied"), "host listener accepted nothing"
                | "net.external", Some r -> held (r = "denied"), "in-sandbox observation"
                | "net.interfaces", Some r -> held (r = "lo"), "in-sandbox observation"
                | ("proc.host-visibility" | "proc.subprocess-isolated"), Some r -> held (r = "isolated"), "host sentinel not visible"
                | "proc.signal-host", Some r -> held (r = "denied"), "host sentinel alive"
                | "proc.ptrace-host", Some r -> held (r = "absent" || r = "denied"), "in-sandbox observation"
                | "proc.no-new-privs", Some r ->
                    let fields = r.Split(';', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                    let caps = fields |> List.filter (fun f -> f.StartsWith "Cap")
                    held (List.contains "NoNewPrivs:=1" fields && caps.Length = 5 && caps |> List.forall (fun f -> f.EndsWith "=0000000000000000")),
                    "in-sandbox /proc/self/status"
                | "proc.setuid", Some "untested" -> ContainmentProbeOutcome.Unknown, "no setuid binary to try"
                | "proc.setuid", Some r -> held (r = "denied"), "setuid su under no_new_privs"
                | "proc.remount-rw", Some r -> held (r.StartsWith "denied,denied,"), "host /usr unchanged"
                | "proc.nested-userns", Some r -> held (r = "denied"), "in-sandbox observation"
                | "proc.rlimits", Some r ->
                    let processes, files = observations.Limits
                    held (r = sprintf "nproc=%d,nofile=%d" processes files), "in-sandbox ulimit"
                | "env.allowlist", Some r ->
                    let names = r.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
                    held (Set.isSubset names (Set.union observations.AllowedEnvironment LinuxContainment.shellIntrinsic)), "names only, values never reported"
                | _, Some _ -> ContainmentProbeOutcome.Unknown, "unrecognized probe"
            let observed =
                match report with
                | Some r when id.StartsWith "cred.read-" && r.StartsWith "read:" -> "read"
                | Some r when id = "proc.ptrace-host" && r.StartsWith "read:" -> "read"
                | Some r -> (if r.Length > 256 then r.Substring(0, 256) else r)
                | None -> (if id = "env.non-allowlisted-refused" then (if observations.NonAllowlistedRefused then "refused" else "accepted") else "unobserved")
            { Id = id; Dimension = dimension id; Expected = expected id; Observed = observed; Outcome = outcome; Detail = detail }
        probeIds |> List.map verdict

    // -------------------------------------------------------------- effects

    let private sha256File (path: string) =
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        (SHA256.HashData input |> Convert.ToHexString).ToLowerInvariant()

    let private snapshot (path: string) =
        if File.Exists path then Some(sha256File path)
        elif Directory.Exists path then Some "directory"
        else None

    let private secret () = "praxis-secret-" + Guid.NewGuid().ToString("N")

    let private parseReports (output: string) =
        output.Split('\n')
        |> Array.choose (fun line ->
            match line.Split('\t') with
            | [| "PROBE"; id; observed |] -> Some(id, observed)
            | _ -> None)
        |> Array.fold (fun (reports: Map<string, string>) (id, observed) -> if reports.ContainsKey id then reports else reports.Add(id, observed)) Map.empty

    /// Runs the suite for real. Every planted file, sentinel and listener is
    /// removed afterwards; a host target that changed is reported, then
    /// restored when it was created by the probe.
    let run (settings: LinuxContainmentSettings) (tools: LinuxContainmentTools) (paths: ContainmentHostPaths) (hostConfigDirectory: string) =
        let nonce = Guid.NewGuid().ToString("N").Substring(0, 16)
        let checkouts = Path.TrimEndingDirectorySeparator(Path.GetFullPath paths.CheckoutRoot)
        let work = Path.Combine(checkouts, "praxis-qualify-" + nonce)
        let sibling = Path.Combine(checkouts, "praxis-sibling-" + nonce)
        let root name = paths.HostRoots |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd
        let inRoot name file = root name |> Option.map (fun r -> Path.Combine(r, file))
        let planted =
            [ "cred.read-policy", inRoot "policy" ("praxis-containment-probe-" + nonce + ".policy")
              "cred.read-signer", inRoot "policy" ("praxis-containment-probe-" + nonce + ".key")
              "cred.read-journal", inRoot "journal" ("praxis-containment-probe-" + nonce + ".secret")
              "cred.read-spool", inRoot "spool" ("praxis-containment-probe-" + nonce + ".secret")
              "cred.read-submissions", inRoot "submissions" ("praxis-containment-probe-" + nonce + ".secret")
              "cred.read-evidence", inRoot "evidence" ("praxis-containment-probe-" + nonce + ".secret")
              "cred.read-host-config", Some(Path.Combine(hostConfigDirectory, "praxis-containment-probe-" + nonce + ".secret")) ]
            |> List.choose (fun (id, path) -> path |> Option.map (fun p -> id, p, secret ()))
        let file id = planted |> List.tryFind (fun (i, _, _) -> i = id) |> Option.map (fun (_, p, _) -> p)
        let fresh name = "praxis-containment-probe-" + nonce + "." + name
        let writes =
            [ "fs.write-repository", Some(Path.Combine(paths.RepositoryRoot, fresh "write"))
              "fs.write-policy", file "cred.read-policy"
              "fs.write-signer", file "cred.read-signer"
              "fs.write-journal", file "cred.read-journal"
              "fs.write-spool", file "cred.read-spool"
              "fs.write-submissions", file "cred.read-submissions"
              "fs.write-evidence", file "cred.read-evidence"
              "fs.write-host-config", file "cred.read-host-config"
              "fs.write-sibling-worktree", Some(Path.Combine(sibling, fresh "write"))
              "fs.write-host-tmp", Some(Path.Combine(Path.GetTempPath(), fresh "write")) ]
            |> List.choose (fun (id, path) -> path |> Option.map (fun p -> id, p))
        let usrFile = "/usr/" + fresh "write"
        let escapeFile = Path.Combine(sibling, "escape-" + nonce)
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        let credentials =
            (LinuxContainment.homeCredentials |> List.map (fun item -> Path.Combine(home, item)))
            @ [ "/etc/shadow"; "/etc/gshadow"; "/etc/sudoers"; "/etc/ssh" ]
            |> List.filter (fun p -> File.Exists p || Directory.Exists p)
        let listener = new TcpListener(IPAddress.Loopback, 0)
        let mutable accepted = false
        let mutable stage = None
        let sentinelName = "praxis-sentinel-" + nonce
        let sentinelSecret = secret ()
        let sentinel =
            let info = ProcessStartInfo("/bin/bash", [ "-c"; "exec -a \"$0\" sleep 600"; sentinelName ])
            info.UseShellExecute <- false
            info.Environment["PRAXIS_SENTINEL_SECRET"] <- sentinelSecret
            Process.Start info
        try
            Directory.CreateDirectory work |> ignore
            Directory.CreateDirectory sibling |> ignore
            for _, path, value in planted do File.WriteAllText(path, value)
            let before = writes |> List.map (fun (id, path) -> id, snapshot path) |> Map.ofList
            let symlinkBefore = (file "cred.read-policy" |> Option.bind snapshot), snapshot escapeFile
            listener.Start()
            let port = (listener.LocalEndpoint :?> IPEndPoint).Port
            let acceptTask = listener.AcceptTcpClientAsync()
            Thread.Sleep 200
            let bash = "/usr/bin/bash"
            let bashDigest = if File.Exists bash then "sha256:" + sha256File bash else ""
            let environment =
                [ "PATH", "/usr/bin:/bin"; "LANG", "C.UTF-8"; "HOME", work; "TMPDIR", "/tmp" ]
                |> List.filter (fun (name, _) -> settings.EnvironmentAllowlist.Contains name) |> Map.ofList
            let spec : LocalWorkerProcessSpec =
                { Executable = bash; ExecutableDigest = bashDigest
                  Arguments =
                    [ "-c"; probeScript; "praxis-probe"; nonce
                      writes |> List.map (fun (id, path) -> id + " " + path) |> String.concat "\n"
                      planted |> List.map (fun (id, path, _) -> id + " " + path) |> String.concat "\n"
                      sibling; string port; string sentinel.Id; sentinelName; usrFile; String.concat "\n" credentials ]
                  WorkingDirectory = work; Environment = environment; Input = ""
                  TimeoutMilliseconds = 60000; MaxOutputBytes = 65536 }
            let refusedSecret =
                LinuxContainmentHost.createStage checkouts
                |> Result.bind (fun s ->
                    try LinuxContainmentHost.confine settings tools paths s { spec with Environment = spec.Environment.Add("PRAXIS_PROBE_SECRET", sentinelSecret) }
                    finally LinuxContainmentHost.removeStage s)
                |> function
                    | Error reason -> reason.Contains "outside the containment allowlist"
                    | Ok _ -> false
            let confined =
                LinuxContainmentHost.createStage checkouts
                |> Result.bind (fun s -> stage <- Some s; LinuxContainmentHost.confine settings tools paths s spec)
            let observation =
                match confined with
                | Error reason -> Error reason
                | Ok confinedSpec -> Ok((LocalWorkerProcess.run confinedSpec (fun _ -> Ok()) CancellationToken.None).GetAwaiter().GetResult())
            accepted <- acceptTask.Wait 300
            let output, completed =
                match observation with
                | Ok o ->
                    let text = o.StandardOutput + "\n" + o.StandardError
                    text, (o.Outcome = LocalWorkerProcessOutcome.Exited 0 && o.StandardOutput.Contains("PROBE-END\t" + nonce))
                | Error reason -> reason, false
            let changed =
                [ for id, path in writes do if snapshot path <> before[id] then yield id
                  if (file "cred.read-policy" |> Option.bind snapshot, snapshot escapeFile) <> symlinkBefore then yield "fs.symlink-escape"
                  if File.Exists usrFile then yield "proc.remount-rw" ]
                |> Set.ofList
            let observations =
                { Reports = (match observation with Ok o -> parseReports o.StandardOutput | Error _ -> Map.empty)
                  Completed = completed
                  Output = output
                  ChangedTargets = changed
                  InsideWritten = (let p = Path.Combine(work, "probe-" + nonce) in File.Exists p && File.ReadAllText p = nonce)
                  LeakedSecrets =
                    (planted |> List.filter (fun (_, _, value) -> output.Contains value) |> List.map (fun (id, _, _) -> id))
                    @ (planted |> List.filter (fun (id, _, value) -> id = "cred.read-signer" && output.Contains value) |> List.map (fun _ -> "cred.read-via-symlink"))
                    |> Set.ofList
                  LoopbackAccepted = accepted
                  SentinelAlive = not sentinel.HasExited
                  NonAllowlistedRefused = refusedSecret && Result.isOk confined
                  AllowedEnvironment = settings.EnvironmentAllowlist
                  Limits = settings.MaxProcesses, settings.MaxOpenFiles }
            let probes =
                judge observations
                |> List.map (fun p ->
                    if p.Id = "proc.remount-rw" && changed.Contains "proc.remount-rw" then { p with Outcome = ContainmentProbeOutcome.Fail; Detail = "host /usr changed" }
                    elif p.Id = "proc.ptrace-host" && output.Contains sentinelSecret then { p with Outcome = ContainmentProbeOutcome.Fail; Detail = "host process environment leaked" }
                    else p)
            let failure =
                match observation with
                | Error reason -> Some("confined launch refused: " + reason)
                | Ok o when not completed -> Some(sprintf "confined launch did not complete (%A): %s" o.Outcome (if o.StandardError.Length > 512 then o.StandardError.Substring(0, 512) else o.StandardError))
                | Ok _ -> None
            Ok(probes, failure)
        finally
            (try listener.Stop() with _ -> ())
            (try if not sentinel.HasExited then sentinel.Kill true with _ -> ())
            sentinel.Dispose()
            for _, path, _ in planted do (try File.Delete path with _ -> ())
            for id, path in writes do
                if id = "fs.write-repository" || id = "fs.write-host-tmp" || id = "fs.write-sibling-worktree" then (try File.Delete path with _ -> ())
            (try File.Delete usrFile with _ -> ())
            for directory in [ work; sibling ] do (try Directory.Delete(directory, true) with _ -> ())
            stage |> Option.iter LinuxContainmentHost.removeStage

    /// A full qualification record for the current host and profile.
    let qualify (settings: LinuxContainmentSettings) (paths: ContainmentHostPaths) hostConfigDirectory (now: unit -> DateTimeOffset) =
        let unsupported =
            [ "seccomp syscall filtering (no pinned seccomp tool; setpriv has none)"
              "distinct host uid: the worker runs as the invoking uid mapped to namespace root, without capabilities"
              "cgroup memory/CPU accounting (prlimit bounds processes, files and file size only)"
              "Git commits from a linked worktree (its common .git directory is sealed)" ]
        match LinuxContainmentHost.resolveTools () with
        | Error reason ->
            let record =
                LocalContainment.unsupported LinuxContainment.Host (LinuxContainmentHost.operatingSystem ()) (LinuxContainmentHost.kernel ())
                    (LinuxContainmentHost.architecture ()) (reason :: unsupported) (now ())
            record, Some reason
        | Ok tools ->
            let facts = LinuxContainmentHost.facts settings tools probeScriptDigest
            let probes, failure =
                match LinuxContainment.checkSettings settings with
                | Error reason -> probeIds |> List.map (fun id -> { Id = id; Dimension = dimension id; Expected = expected id; Observed = "unobserved"; Outcome = ContainmentProbeOutcome.Unknown; Detail = reason }), Some reason
                | Ok() ->
                    match (try run settings tools paths hostConfigDirectory with e -> Error e.Message) with
                    | Ok(probes, failure) -> probes, failure
                    | Error reason -> probeIds |> List.map (fun id -> { Id = id; Dimension = dimension id; Expected = expected id; Observed = "unobserved"; Outcome = ContainmentProbeOutcome.Unknown; Detail = reason }), Some reason
            { Host = LinuxContainment.Host
              Profile = LinuxContainment.Profile
              ProfileDigest = facts.ProfileDigest
              Mechanism = LinuxContainment.Mechanism
              OperatingSystem = facts.OperatingSystem
              Kernel = facts.Kernel
              Architecture = facts.Architecture
              Tools = facts.Tools
              Probes = probes
              Unsupported = unsupported
              QualifiedAt = now () },
            failure

    /// The launch gate. A contained worker starts only when this host's
    /// current platform, kernel, tool closure and profile match a stored
    /// qualification of the whole current probe suite with every dimension
    /// enforced. Unsupported platforms and missing tools refuse.
    let admit (settings: LinuxContainmentSettings) (evidencePath: string) (maxAge: TimeSpan) (now: DateTimeOffset) =
        if LinuxContainmentHost.operatingSystem () <> "linux" then
            Error("containment is unsupported and unqualified on " + LinuxContainmentHost.operatingSystem ())
        else
            LinuxContainmentHost.resolveTools ()
            |> Result.bind (fun tools ->
                LinuxContainmentHost.readEvidence evidencePath
                |> Result.bind (fun qualification ->
                    if (qualification.Probes |> List.map _.Id) <> probeIds then Error "qualification does not cover the current probe suite"
                    else
                        LinuxContainment.admit (LinuxContainmentHost.facts settings tools probeScriptDigest) now maxAge qualification
                        |> Result.map (fun () -> tools, qualification)))
