namespace Praxis.Infrastructure.Work

open System
open System.IO
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
    let writeExclusive path content =
        let created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
        try
            use output = created
            let bytes = Encoding.UTF8.GetBytes(content: string)
            if bytes.Length > 131072 then invalidOp "oversized host journal"
            output.Write(bytes, 0, bytes.Length)
            output.Flush(true)
        with e ->
            // Only failure to open exclusively may be interpreted as collision.
            // Once created, a failed write/flush must remain a failure even if
            // a subsequent reader could decode the bytes left on disk.
            raise (InvalidOperationException("host journal write failed after exclusive creation", e))
