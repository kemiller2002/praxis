namespace Praxis.Application.Work

open System
open System.Security.Cryptography
open System.Text
open Praxis.Domain.Execution
open Praxis.Domain.Work

/// Operator-selected settings of the Linux namespace profile. They come only
/// from the host configuration, never from the repository or worker output.
type LinuxContainmentSettings =
    { /// Absolute host paths bound read-only into the worker's minimal root.
      ReadOnlyPaths: string list
      /// The only environment variable names a contained worker may receive.
      EnvironmentAllowlist: Set<string>
      MaxProcesses: int
      MaxOpenFiles: int
      MaxFileBytes: int64
      ScratchMegabytes: int }

/// The pinned host tools the confined launch executes, digested by the host.
type LinuxContainmentTools =
    { Unshare: ContainmentTool
      Shell: ContainmentTool
      Prlimit: ContainmentTool
      Setpriv: ContainmentTool
      /// Utilities the fixed setup script runs inside the namespaces.
      Support: ContainmentTool list }

/// Host-provisioned locations for one confined launch. `Sealed` paths (host
/// roots, the repository, credential stores) may not be bound or contain a
/// bind; `Protected` paths (home directories, `/etc`, `/`) may not be bound
/// whole or by an ancestor, though a narrower subpath may be.
type ContainedLaunchRequest =
    { Stage: string
      WorkerRoot: string
      Sealed: string list
      Protected: string list }

/// What the host observes about itself at launch time. The gate compares it
/// with a stored qualification; nothing here is reported by a worker.
type ContainmentHostFacts =
    { OperatingSystem: string
      Kernel: string
      Architecture: string
      ProfileDigest: string
      Tools: ContainmentTool list }

/// Linux confined launch: `unshare` user/net/mount/pid/ipc/uts namespaces, a
/// fixed setup script that pivots into a minimal root of read-only binds plus
/// the writable worker directory, then `prlimit` and `setpriv` (no_new_privs,
/// every capability set cleared, locked securebits) before the worker runs.
/// The host builds every argument; the worker supplies none of them.
[<RequireQualifiedAccess>]
module LinuxContainment =
    [<Literal>]
    let Host = "praxis-linux-namespaces"

    [<Literal>]
    let Profile = "linux-userns-pivot/1"

    let Mechanism =
        "unshare(user,net,mount,pid,ipc,uts,kill-child)+pivot_root(read-only binds)+prlimit+setpriv(no_new_privs,caps=-all,securebits locked)"

    /// Directories the setup script searches; the host resolves the same.
    let searchPath = [ "/usr/sbin"; "/usr/bin"; "/sbin"; "/bin" ]

    /// Utilities the setup script runs; each is pinned by digest.
    let supportTools = [ "mount"; "umount"; "pivot_root"; "mkdir"; "ln"; "readlink"; "rmdir" ]

    /// Credential and session stores under a home directory. They are sealed.
    let homeCredentials =
        [ ".ssh"; ".gnupg"; ".aws"; ".azure"; ".config"; ".docker"; ".kube"; ".netrc"; ".git-credentials"
          ".gitconfig"; ".npmrc"; ".pypirc"; ".nuget"; ".ccr"; ".claude"; ".codex"; ".gemini"
          ".local/share/keyrings"; ".password-store"; ".vault-token" ]

    /// System locations that are never bound into a worker root.
    let systemSealed =
        [ "/proc"; "/sys"; "/dev"; "/run"; "/var/run"; "/boot"; "/etc/shadow"; "/etc/shadow-"; "/etc/gshadow"
          "/etc/gshadow-"; "/etc/sudoers"; "/etc/sudoers.d"; "/etc/ssh"; "/etc/security"; "/etc/ssl/private" ]

    /// System locations that may not be bound whole or through an ancestor.
    let systemProtected = [ "/"; "/etc"; "/var"; "/home"; "/root"; "/tmp" ]

    /// Names a shell adds to its own environment; they carry no host secret.
    let shellIntrinsic = Set.ofList [ "PWD"; "OLDPWD"; "SHLVL"; "_" ]

    /// The fixed setup script. It is a constant of this program, pinned by
    /// digest in every qualification; paths reach it only as arguments.
    let setupScript =
        String.concat
            "\n"
            [ "set -euf"
              "case ${PATH+set} in set) praxis_path=$PATH; praxis_has_path=1;; *) praxis_has_path=0;; esac"
              "PATH=/usr/sbin:/usr/bin:/sbin:/bin"
              "umask 077"
              "stage=$1; work=$2; binds=$3; scratch=$4"
              "shift 4"
              "[ \"${1-}\" = \"--\" ] || exit 125"
              "shift"
              "mount --make-rprivate /"
              "mount -t tmpfs -o mode=0755,nosuid,nodev,size=16m praxis-root \"$stage\""
              "praxis_ifs=$IFS"
              "IFS='"
              "'"
              "for src in $binds; do"
              "  IFS=$praxis_ifs"
              "  if [ ! -e \"$src\" ] && [ ! -L \"$src\" ]; then continue; fi"
              "  parent=${src%/*}"
              "  if [ -n \"$parent\" ]; then mkdir -p \"$stage$parent\"; fi"
              "  if [ -L \"$src\" ]; then"
              "    ln -s \"$(readlink \"$src\")\" \"$stage$src\""
              "  else"
              "    if [ -d \"$src\" ]; then mkdir -p \"$stage$src\"; else : > \"$stage$src\"; fi"
              "    mount --bind \"$src\" \"$stage$src\""
              "    mount -o remount,bind,ro,nosuid,nodev \"$stage$src\""
              "  fi"
              "done"
              "IFS=$praxis_ifs"
              "mkdir -p \"$stage/proc\" \"$stage/tmp\" \"$stage/dev\" \"$stage/.praxis-old-root\""
              "mount -t proc -o nosuid,nodev,noexec proc \"$stage/proc\""
              "mount -t tmpfs -o \"nosuid,nodev,mode=1777,size=${scratch}m\" praxis-scratch \"$stage/tmp\""
              "for node in null zero random urandom; do"
              "  : > \"$stage/dev/$node\""
              "  mount --bind \"/dev/$node\" \"$stage/dev/$node\""
              "done"
              "mkdir -p \"$stage$work\""
              "mount --bind \"$work\" \"$stage$work\""
              "mount -o remount,bind,nosuid,nodev \"$stage$work\""
              "cd \"$stage\""
              "pivot_root . .praxis-old-root"
              "umount -l /.praxis-old-root"
              "rmdir /.praxis-old-root"
              "mount -o remount,ro,bind,nosuid,nodev /"
              "echo 0 > /proc/sys/user/max_user_namespaces"
              "echo praxis-worker > /proc/sys/kernel/hostname"
              "cd \"$work\""
              "if [ \"$praxis_has_path\" = 1 ]; then PATH=$praxis_path; else unset PATH; fi"
              "exec \"$@\"" ]

    let digestText (text: string) =
        "sha256:" + (Encoding.UTF8.GetBytes text |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

    let setupScriptDigest = digestText setupScript

    let private framed (values: string list) =
        values |> List.map (fun value -> string (Encoding.UTF8.GetByteCount value) + ":" + value) |> String.concat "|" |> digestText

    let allTools (tools: LinuxContainmentTools) = [ tools.Unshare; tools.Shell; tools.Prlimit; tools.Setpriv ] @ tools.Support

    /// Everything that shapes the boundary: profile, scripts, settings and
    /// the digest of every tool. Any change requires a new qualification.
    let profileDigest (settings: LinuxContainmentSettings) (tools: LinuxContainmentTools) probeScriptDigest =
        framed (
            [ "praxis.containment-profile/1"; Profile; setupScriptDigest; probeScriptDigest ]
            @ (settings.ReadOnlyPaths |> List.sort |> List.map (fun p -> "ro=" + p))
            @ (settings.EnvironmentAllowlist |> Set.toList |> List.map (fun name -> "env=" + name))
            @ [ sprintf "nproc=%d" settings.MaxProcesses
                sprintf "nofile=%d" settings.MaxOpenFiles
                sprintf "fsize=%d" settings.MaxFileBytes
                sprintf "scratch=%d" settings.ScratchMegabytes ]
            @ (allTools tools |> List.map (fun t -> sprintf "tool=%s:%s:%s" t.Name t.Path t.Digest) |> List.sort)
        )

    // ------------------------------------------------------------- paths

    /// A canonical absolute POSIX path: no `.`/`..`/empty segment, no
    /// trailing separator, no control, glob or newline characters.
    let isCanonical (path: string) =
        not (isNull path) && path.Length > 0 && path.Length <= 1024 && path.StartsWith '/'
        && (path = "/" || not (path.EndsWith '/'))
        && not (path |> Seq.exists (fun c -> Char.IsControl c || c = '*' || c = '?' || c = '[' || c = '\\'))
        && (path = "/" || path.Substring(1).Split('/') |> Array.forall (fun s -> s <> "" && s <> "." && s <> ".."))

    /// `ancestor` equals `path` or contains it.
    let covers (ancestor: string) (path: string) =
        ancestor = "/" || path = ancestor || path.StartsWith(ancestor + "/", StringComparison.Ordinal)

    let private overlaps a b = covers a b || covers b a

    let private check condition message = if condition then Ok() else Error message

    let private all (checks: Result<unit, string> list) =
        match checks |> List.choose (function Error e -> Some e | Ok() -> None) with
        | [] -> Ok()
        | errors -> Error(String.concat "; " errors)

    /// The read boundary: no bind exposes a sealed path, contains a sealed or
    /// protected path, or overlaps the writable worker directory.
    let checkBinds (settings: LinuxContainmentSettings) (request: ContainedLaunchRequest) =
        let binds = settings.ReadOnlyPaths
        all
            [ check (not binds.IsEmpty && binds.Length <= 32 && binds.Length = (Set.ofList binds).Count) "read-only paths must be 1-32 distinct entries"
              yield!
                  binds
                  |> List.map (fun bind ->
                      if not (isCanonical bind) then Error("read-only path is not canonical: " + bind)
                      else
                          match request.Sealed |> List.tryFind (fun s -> overlaps s bind) with
                          | Some sealedPath -> Error(sprintf "read-only path %s overlaps sealed host path %s" bind sealedPath)
                          | None ->
                              match request.Protected |> List.tryFind (fun p -> covers bind p) with
                              | Some protectedPath -> Error(sprintf "read-only path %s exposes protected path %s" bind protectedPath)
                              | None -> Ok()) ]

    let private checkWorker (settings: LinuxContainmentSettings) (request: ContainedLaunchRequest) (work: string) =
        all
            [ check (isCanonical work) "worker directory is not canonical"
              check (isCanonical request.WorkerRoot && work <> request.WorkerRoot && covers request.WorkerRoot work) "worker directory must be inside the host checkout root"
              check (isCanonical request.Stage && request.Stage <> request.WorkerRoot && covers request.WorkerRoot request.Stage && not (overlaps request.Stage work))
                  "staging root must be a separate directory inside the host checkout root"
              check (not (settings.ReadOnlyPaths |> List.exists (overlaps work))) "worker directory overlaps a read-only path"
              check (not ((request.Sealed |> List.filter ((<>) request.WorkerRoot)) |> List.exists (overlaps work))) "worker directory overlaps a sealed host path"
              check (not (request.Protected |> List.exists (covers work))) "worker directory contains a protected path" ]

    let private checkEnvironment (settings: LinuxContainmentSettings) (environment: Map<string, string>) =
        match environment |> Map.keys |> Seq.filter (fun name -> not (settings.EnvironmentAllowlist.Contains name)) |> Seq.toList with
        | [] -> Ok()
        | names -> Error("environment variables outside the containment allowlist: " + String.concat ", " (List.sort names))

    let checkSettings (settings: LinuxContainmentSettings) =
        all
            [ check (settings.MaxProcesses >= 8 && settings.MaxProcesses <= 65536) "maxProcesses must be 8-65536"
              check (settings.MaxOpenFiles >= 16 && settings.MaxOpenFiles <= 1048576) "maxOpenFiles must be 16-1048576"
              check (settings.MaxFileBytes >= 4096L && settings.MaxFileBytes <= 68719476736L) "maxFileBytes must be 4096-68719476736"
              check (settings.ScratchMegabytes >= 1 && settings.ScratchMegabytes <= 65536) "scratchMegabytes must be 1-65536"
              check (settings.EnvironmentAllowlist.Count <= 32
                     && settings.EnvironmentAllowlist |> Set.forall (fun n -> n.Length > 0 && n.Length <= 128 && n |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_')))
                  "environment allowlist entries must be 1-128 letters, digits or underscores (at most 32)" ]

    /// The confined process specification. The worker's own pinned executable,
    /// arguments, working directory, input and budgets are preserved; it must
    /// lie inside a read-only path, because nothing else is visible.
    let wrap (settings: LinuxContainmentSettings) (tools: LinuxContainmentTools) (request: ContainedLaunchRequest) (spec: LocalWorkerProcessSpec) =
        let work = spec.WorkingDirectory
        all
            [ checkSettings settings
              checkBinds settings request
              checkWorker settings request work
              checkEnvironment settings spec.Environment
              check (isCanonical spec.Executable && settings.ReadOnlyPaths |> List.exists (fun bind -> covers bind spec.Executable))
                  "worker executable must be inside a read-only path of the containment profile"
              check (allTools tools |> List.forall (fun t -> isCanonical t.Path && LocalAgentHandoff.isDigest t.Digest)) "containment tools must be pinned canonical paths" ]
        |> Result.bind (fun () ->
            let arguments =
                [ "--user"; "--map-root-user"; "--net"; "--mount"; "--pid"; "--ipc"; "--uts"; "--fork"; "--kill-child"; "--"
                  tools.Shell.Path; "-c"; setupScript; "praxis-contain"
                  request.Stage; work; String.concat "\n" settings.ReadOnlyPaths; string settings.ScratchMegabytes; "--"
                  tools.Prlimit.Path; sprintf "--nproc=%d" settings.MaxProcesses; sprintf "--nofile=%d" settings.MaxOpenFiles
                  sprintf "--fsize=%d" settings.MaxFileBytes; "--core=0"; "--"
                  tools.Setpriv.Path; "--no-new-privs"; "--inh-caps=-all"; "--ambient-caps=-all"; "--bounding-set=-all"
                  "--securebits=+noroot,+noroot_locked,+no_setuid_fixup,+no_setuid_fixup_locked,+keep_caps_locked"; "--"
                  spec.Executable ]
                @ spec.Arguments
            if arguments.Length > 64 then Error "confined argv exceeds 64 arguments; reduce worker arguments or read-only paths"
            elif arguments |> List.exists (fun a -> a.Length > 4096) then Error "a confined argument exceeds 4096 characters"
            else Ok { spec with Executable = tools.Unshare.Path; ExecutableDigest = tools.Unshare.Digest; Arguments = arguments })

    // -------------------------------------------------------------- gate

    /// Admits a launch only when a stored qualification for this exact
    /// profile, tool closure, kernel and platform shows every containment
    /// dimension enforced by passing probes, and is no older than `maxAge`.
    let admit (facts: ContainmentHostFacts) (now: DateTimeOffset) (maxAge: TimeSpan) (qualification: ContainmentQualification) =
        let restrictions = LocalContainment.restrictions qualification
        let shortfall =
            restrictions |> List.filter (fun r -> r.Status <> RestrictionStatus.Enforced)
            |> List.map (fun r -> sprintf "%s is %s" r.Dimension (RestrictionStatus.toWire r.Status))
        let toolSet (tools: ContainmentTool list) = tools |> List.map (fun t -> t.Name, t.Path, t.Digest) |> Set.ofList
        all
            [ check (facts.OperatingSystem = "linux") ("containment is unsupported and unqualified on " + facts.OperatingSystem)
              check (qualification.Host = Host && qualification.Profile = Profile) "qualification is for a different containment mechanism"
              check (qualification.OperatingSystem = facts.OperatingSystem && qualification.Architecture = facts.Architecture) "qualification was recorded on a different platform"
              check (qualification.Kernel = facts.Kernel) "kernel changed since qualification"
              check (qualification.ProfileDigest = facts.ProfileDigest) "containment profile, scripts or settings changed since qualification"
              check (toolSet qualification.Tools = toolSet facts.Tools) "containment tool closure changed since qualification"
              check (qualification.QualifiedAt <= now.AddMinutes 1.0 && now - qualification.QualifiedAt <= maxAge) "qualification is stale or from the future"
              check shortfall.IsEmpty ("containment is not qualified: " + String.concat ", " shortfall) ]
