namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Praxis.Infrastructure.Planning

[<RequireQualifiedAccess>]
module LocalEcirAuthorityTests =
    /// A distinct process checks the actual OS file lease, not a mock port.
    let lockFixture path =
        try
            use held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            0
        with :? IOException -> 3
    let private t name run = { Name = "local ECIR authority: " + name; Run = run }
    let private now = DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero)
    let private get = function Ok value -> value | Error error -> failwith error
    let private refused = function Error _ -> () | Ok _ -> failwith "unexpected host acceptance"
    let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let private withHost run =
        if OperatingSystem.IsWindows() then printfn "Unix host fixture unavailable on Windows; adapter refuses Windows"
        else
            let directory = Path.Combine(Path.GetTempPath(), "praxis-local-authority-" + Guid.NewGuid().ToString("N"))
            let repo = Path.Combine(directory, "repo")
            let host = Path.Combine(directory, "host")
            Directory.CreateDirectory repo |> ignore
            Directory.CreateDirectory host |> ignore
            File.SetUnixFileMode(host, fileMode ||| UnixFileMode.UserExecute)
            let settings = { RepositoryRoot = repo; HostRoot = host; LockTimeout = TimeSpan.FromMilliseconds 500. }
            use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let policy =
                { Revision = "initial"; RepositoryRoot = repo; QualifiedRelease = true
                  TrustedSigners = Map.ofList [ "key-1", { Approver = "operator"; PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() } ]
                  Grants = [] }
            try run settings policy
            finally Directory.Delete(directory, true)
    let private audit previous next : EcirHostAudit =
        { Id = Guid.NewGuid().ToString("N"); Action = "revoked"; Approver = "operator"; KeyId = "key-1"
          GroupId = None; PreviousRevision = previous; NewRevision = next; OccurredAt = now; ReceiptSha256 = None }
    let private change (policy: EcirHostPolicy) next =
        { policy with Revision = next; TrustedSigners = Map.empty }
    let private childLock path =
        let start = Diagnostics.ProcessStartInfo("dotnet", UseShellExecute = false)
        start.ArgumentList.Add(typeof<TestCase>.Assembly.Location)
        start.ArgumentList.Add("--local-host-lock-fixture")
        start.ArgumentList.Add(path)
        use child = Diagnostics.Process.Start start
        if not (child.WaitForExit 5000) then child.Kill(true); failwith "host lease fixture timed out"
        child.ExitCode

    let tests =
        [ t "exact policy and public PEM survive independent store reload" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              let reloaded = FileLocalEcirAuthority.create settings (fun () -> now)
              Assert.equal policy (reloaded.Load() |> get)))
          t "provisioning never overwrites existing authority" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              store.Initialize(change policy "different") |> refused
              Assert.equal policy (store.Load() |> get)))
          t "policy and audit are published together and stale revision cannot mutate either" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              let a = audit policy.Revision "revoked"
              store.Apply(policy.Revision, change policy "revoked", a) |> get
              Assert.equal [ a ] (store.Audit() |> get)
              store.Apply(policy.Revision, change policy "stale", audit policy.Revision "stale") |> refused
              Assert.equal "revoked" (store.Load() |> get).Revision
              Assert.equal [ a ] (store.Audit() |> get)))
          t "changed revision and expiry refuse before repository callback" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              let mutable calls = 0
              let apply () = calls <- calls + 1; Ok()
              store.Authority.Commit "wrong" (now.AddMinutes 1.) apply |> refused
              store.Authority.Commit policy.Revision now apply |> refused
              store.Authority.Commit policy.Revision (now.AddMinutes 1.) apply |> get
              Assert.equal 1 calls))
          t "pending partial policy publication blocks load, mutation and dispatch" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              let pending = Path.Combine(settings.HostRoot, "policy.pending")
              File.WriteAllText(pending, "partial")
              File.SetUnixFileMode(pending, fileMode)
              store.Load() |> refused
              store.Apply(policy.Revision, change policy "new", audit policy.Revision "new") |> refused
              store.Authority.Commit policy.Revision (now.AddMinutes 1.) (fun () -> failwith "must not dispatch") |> refused))
          t "audit mismatch and repeated historical revision cannot publish" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              store.Apply(policy.Revision, change policy "new", audit policy.Revision "different") |> refused
              store.Apply(policy.Revision, change policy "new", audit policy.Revision "new") |> get
              store.Apply("new", policy, audit "new" policy.Revision) |> refused
              Assert.equal "new" (store.Load() |> get).Revision))
          t "future or backwards policy audit time cannot publish" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              let a = audit policy.Revision "new"
              store.Apply(policy.Revision, change policy "new", { a with OccurredAt = now.AddSeconds 1. }) |> refused
              store.Apply(policy.Revision, change policy "new", a) |> get
              store.Apply("new", change policy "later", { audit "new" "later" with OccurredAt = now.AddSeconds -1. }) |> refused))
          t "public permissions and linked policy files refuse" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              File.SetUnixFileMode(settings.HostRoot, fileMode ||| UnixFileMode.UserExecute ||| UnixFileMode.OtherRead)
              store.Initialize policy |> refused
              File.SetUnixFileMode(settings.HostRoot, fileMode ||| UnixFileMode.UserExecute)
              store.Initialize policy |> get
              let target = Path.Combine(settings.HostRoot, "policy.json")
              File.SetUnixFileMode(target, fileMode ||| UnixFileMode.GroupRead)
              store.Load() |> refused
              File.SetUnixFileMode(target, fileMode)
              let other = Path.Combine(settings.HostRoot, "saved.json")
              File.Move(target, other)
              File.CreateSymbolicLink(target, other) |> ignore
              store.Load() |> refused))
          t "unknown fields and duplicate JSON fields cannot replace host policy" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              let target = Path.Combine(settings.HostRoot, "policy.json")
              let original = File.ReadAllText target
              for hostile in [ original.Insert(1, "\"Extra\":true,"); original.Insert(1, "\"SchemaVersion\":\"praxis.local-ecir-policy/1\",") ] do
                  File.WriteAllText(target, hostile)
                  store.Load() |> refused))
          t "competing store instances admit only one revision update" (fun () -> withHost (fun settings policy ->
              let first = FileLocalEcirAuthority.create settings (fun () -> now)
              let second = FileLocalEcirAuthority.create settings (fun () -> now)
              first.Initialize policy |> get
              let attempts = [| Task.Run(fun () -> first.Apply(policy.Revision, change policy "a", audit policy.Revision "a"))
                                Task.Run(fun () -> second.Apply(policy.Revision, change policy "b", audit policy.Revision "b")) |]
              Task.WaitAll(attempts |> Array.map (fun task -> task :> Task))
              Assert.equal 1 (attempts |> Array.filter (fun task -> Result.isOk task.Result) |> Array.length)
              Assert.equal 1 (first.Audit() |> get |> List.length)))
          t "revocation waits for dispatch callback and invalidates subsequent dispatch" (fun () -> withHost (fun settings policy ->
              let first = FileLocalEcirAuthority.create settings (fun () -> now)
              let second = FileLocalEcirAuthority.create settings (fun () -> now)
              first.Initialize policy |> get
              use entered = new ManualResetEventSlim(false)
              use release = new ManualResetEventSlim(false)
              let commit = Task.Run(fun () -> first.Authority.Commit policy.Revision (now.AddMinutes 1.) (fun () ->
                  entered.Set()
                  if not (release.Wait 2000) then Error "test release timeout" else Ok()))
              Assert.isTrue (entered.Wait 2000) "dispatch never entered"
              let revocation = Task.Run(fun () -> second.Apply(policy.Revision, change policy "revoked", audit policy.Revision "revoked"))
              try Assert.isTrue (not (revocation.Wait 50)) "revocation bypassed held policy lease"
              finally release.Set()
              commit.Result |> get
              revocation.Result |> get
              first.Authority.Commit policy.Revision (now.AddMinutes 1.) (fun () -> failwith "stale dispatch") |> refused))
          t "host policy lease excludes a distinct process and releases afterward" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              let path = Path.Combine(settings.HostRoot, "policy.lock")
              store.Authority.Commit policy.Revision (now.AddMinutes 1.) (fun () ->
                  Assert.equal 3 (childLock path)
                  Ok()) |> get
              Assert.equal 0 (childLock path)))
          t "private signer PEM cannot enter policy persistence" (fun () -> withHost (fun settings policy ->
              use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
              let privatePolicy = { policy with TrustedSigners = Map.ofList [ "key-1", { Approver = "operator"; PublicKeyPem = key.ExportECPrivateKeyPem() } ] }
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize privatePolicy |> refused
              Assert.isTrue (not (File.Exists(Path.Combine(settings.HostRoot, "policy.pending")))) "private key persisted"
              Assert.isTrue (not (File.Exists(Path.Combine(settings.HostRoot, "policy.json")))) "private key policy persisted"))
          t "exact committed inputs, validator pins and member map survive reload" (fun () -> withHost (fun settings policy ->
              let digest = "sha256:" + String('a', 64)
              let grant =
                  { GroupId = "g"; CohortId = "c"
                    Source = { Commit = String('b', 40); ManifestPath = "manifest.json"; BlueprintPath = "blueprint.json"; SourceManifestDigest = digest }
                    BlueprintDigest = digest; Ordo = { Executable = "/host/ordo"; Sha256 = digest }
                    MemberRequirements = Map.ofList [ "WI-1", "RQ-1"; "WI-2", "RQ-2" ]; ReceiptJson = "{}" }
              let expected = { policy with Grants = [ grant ] }
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize expected |> get
              Assert.equal expected (store.Load() |> get)))
          t "clock rollback never reaches callback" (fun () -> withHost (fun settings policy ->
              let mutable clock = now
              let store = FileLocalEcirAuthority.create settings (fun () -> clock)
              store.Initialize policy |> get
              store.Apply(policy.Revision, change policy "new", audit policy.Revision "new") |> get
              clock <- now.AddSeconds -1.
              store.Authority.Commit "new" (now.AddMinutes 1.) (fun () -> failwith "clock rollback") |> refused
              Assert.equal "new" (store.Load() |> get).Revision))
          t "unqualified releases and changed qualification flags refuse" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              let unqualified = { policy with QualifiedRelease = false }
              store.Initialize unqualified |> get
              store.Authority.Commit policy.Revision (now.AddMinutes 1.) (fun () -> failwith "unqualified release") |> refused
              store.Apply(policy.Revision, change policy "qualified", audit policy.Revision "qualified") |> refused))
          t "callback failure releases the policy lease without mutating authority" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              store.Authority.Commit policy.Revision (now.AddMinutes 1.) (fun () -> Error "repository conflict") |> refused
              Assert.equal policy (store.Load() |> get)
              Assert.equal [] (store.Audit() |> get)))
          t "native recovery refusal prevents validation and staging" (fun () -> withHost (fun settings policy ->
              let store = FileLocalEcirAuthority.create settings (fun () -> now)
              store.Initialize policy |> get
              FileLocalEcirAuthority.dispatch settings store (fun () -> Error "pending native transaction") "g"
                  (fun _ -> failwith "must not validate") (fun _ -> failwith "must not stage") |> refused)) ]
