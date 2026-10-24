namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text

/// Shared file mechanics, not OS protection. Provisioned host roots and their
/// dependency/ownership/link stability remain a qualified host responsibility.
[<RequireQualifiedAccess>]
module internal LocalHostJournalFiles =
    let noLinks path =
        let mutable current = DirectoryInfo(Path.GetFullPath path)
        let mutable safe = true
        while not (isNull current) && safe do
            safe <- current.LinkTarget = null
            current <- current.Parent
        safe && FileInfo(path).LinkTarget = null
    let rootOutside (repositoryRoot: string) (storeRoot: string) =
        let repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath repositoryRoot)
        if not (Path.IsPathFullyQualified storeRoot) then invalidOp "host journal root must be absolute"
        let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath storeRoot)
        let comparison = StringComparison.OrdinalIgnoreCase
        let prefix = if Path.EndsInDirectorySeparator repository then repository else repository + string Path.DirectorySeparatorChar
        if String.Equals(root, repository, comparison) || root.StartsWith(prefix, comparison) then invalidOp "host journal root must be outside the repository"
        if not (Directory.Exists root) || not (noLinks root) then invalidOp "host must provision a non-link journal directory"
        root
    let directory root repositoryIdentity dispatchId =
        let key = Encoding.UTF8.GetBytes(repositoryIdentity + "\u0000" + dispatchId) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
        let target = Path.Combine(root, key)
        if not (noLinks root && noLinks target) then invalidOp "host journal path contains a link"
        target
    let readBounded path =
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        let buffer = Array.zeroCreate<byte> 131073
        let mutable count = 0
        let mutable finished = false
        while count < buffer.Length && not finished do
            let read = input.Read(buffer, count, buffer.Length - count)
            if read = 0 then finished <- true else count <- count + read
        if count > 131072 then invalidOp "oversized host journal"
        UTF8Encoding(false, true).GetString(buffer, 0, count)
    let private stagingPrefix = ".praxis-staging-"
    /// A transient staging file of `publish`: a regular, unlinked file named
    /// for staging directly in a host root. Root inventories skip it; it is
    /// never read as a record (a vanished one also qualifies).
    let isStaging (path: string) =
        let name = Path.GetFileName path
        name.StartsWith(stagingPrefix, StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)
        && not (Directory.Exists path) && isNull (FileInfo(path).LinkTarget)
    module private Posix =
        [<DllImport("libc", EntryPoint = "link", SetLastError = true)>]
        extern int link([<MarshalAs(UnmanagedType.LPUTF8Str)>] string existing, [<MarshalAs(UnmanagedType.LPUTF8Str)>] string created)
    /// Give `staged` the name `path` atomically, never replacing an existing
    /// name. Unix `File.Move(.., overwrite = false)` is lstat-then-rename,
    /// which replaces a racing winner, so Unix uses link(2) (EEXIST is the
    /// collision); Windows MoveFileEx without REPLACE_EXISTING is atomic.
    let private link staged path =
        if OperatingSystem.IsWindows() then File.Move(staged, path, false)
        elif Posix.link(staged, path) <> 0 then
            match Marshal.GetLastPInvokeError() with
            | 17 -> raise (IOException("host record already exists: " + path))
            | errno -> invalidOp (sprintf "host record publication failed (errno %d)" errno)
    /// Write-once publication. The bytes are written and flushed to disk under
    /// a unique staging name in `root` (the same filesystem as `path`, which
    /// lies under it, and outside every record directory), then published
    /// with no-replace semantics, so `path` only ever names complete content
    /// and its name is never opened for writing. A Unix FileStream takes its
    /// FileShare flock only after open(O_CREAT|O_EXCL), so a reader's shared
    /// lock in that gap used to fail the creator and leave an empty record.
    /// Only an existing `path` raises IOException (a collision the caller may
    /// resolve by reading); every other failure is InvalidOperationException.
    /// The staging file is removed on every path. `staged` is a test seam
    /// observing the complete, not yet published, staging file.
    let publishWith (staged: string -> unit) root path (bytes: byte array) =
        let staging = Path.Combine(root, stagingPrefix + Guid.NewGuid().ToString("N") + ".tmp")
        try
            try
                do
                    use output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    output.Write(bytes, 0, bytes.Length)
                    output.Flush(true)
                staged staging
            with e -> raise (InvalidOperationException("host record staging failed", e))
            link staging path
        finally
            // Best effort: a staging file left by a failed unlink is never
            // read as a record and is skipped by root inventories.
            try File.Delete staging with :? IOException | :? UnauthorizedAccessException -> ()
    let publish root path bytes = publishWith ignore root path bytes
    let writeExclusiveWith staged root path content =
        let bytes = Encoding.UTF8.GetBytes(content: string)
        if bytes.Length > 131072 then invalidOp "oversized host journal"
        publishWith staged root path bytes
    let writeExclusive root path content = writeExclusiveWith ignore root path content
