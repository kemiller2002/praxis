namespace Praxis.Site

open System
open System.IO
open System.Text.RegularExpressions

/// The `check` command (docs/public-site.md): required files, structural HTML,
/// accessibility rules decidable from markup, relative references that resolve
/// inside the site, and the public/private boundary.
[<RequireQualifiedAccess>]
module Site =
    let requiredFiles = [ "index.html"; "assets/css/site.css"; "robots.txt"; ".nojekyll" ]

    let private exists (path: string) = File.Exists path || Directory.Exists path

    let rec private walk (directory: string) =
        Directory.GetFileSystemEntries(directory)
        |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.ofArray
        |> List.collect (fun entry ->
            let info = FileInfo(entry)

            if Directory.Exists entry && isNull info.LinkTarget then walk entry else [ entry ])

    let private textFile = Regex(@"\.(html|css|js|json|txt|svg)\z", RegexOptions.CultureInvariant)
    let private cssUrl = Regex("""url\(\s*["']?([^"')]+)["']?\s*\)""", RegexOptions.CultureInvariant)
    let private externalUrl = Regex("^(https?:|data:|#)", RegexOptions.CultureInvariant)

    let private resolve (file: string) (reference: string) =
        try
            Some(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file: string), reference)))
        with _ ->
            None

    let private resolves (file: string) (reference: string) =
        resolve file reference |> Option.map exists |> Option.defaultValue false

    /// Every problem with the site rooted at `root`, as `site/<path>: <problem>`.
    let check (root: string) =
        let missing =
            requiredFiles
            |> List.filter (fun file -> not (exists (Path.Combine(root, file))))
            |> List.map (fun file -> $"missing required file site/{file}")

        let files = if Directory.Exists root then walk root else []
        let relative file = Repository.relative root file

        let pages =
            files
            |> List.filter (fun file -> file.EndsWith(".html", StringComparison.Ordinal))
            |> List.collect (fun file ->
                let html = File.ReadAllText file

                Html.structureProblems html @ Html.accessibilityProblems html @ Html.referenceProblems html (resolves file)
                |> List.map (fun problem -> $"site/{relative file}: {problem}"))

        let cssReferences =
            files
            |> List.filter (fun file -> file.EndsWith(".css", StringComparison.Ordinal))
            |> List.collect (fun file ->
                cssUrl.Matches(File.ReadAllText file)
                |> Seq.map (fun found -> found.Groups[1].Value)
                |> Seq.filter (fun reference -> not (externalUrl.IsMatch reference))
                |> Seq.filter (fun reference -> reference.StartsWith("/", StringComparison.Ordinal) || not (resolves file reference))
                |> Seq.map (fun reference -> $"site/{relative file}: url({reference}) is root-relative or missing")
                |> List.ofSeq)

        let boundary =
            files
            |> List.filter textFile.IsMatch
            |> List.collect (fun file -> Html.boundaryProblems $"site/{relative file}" (File.ReadAllText file))

        missing @ pages @ cssReferences @ boundary
