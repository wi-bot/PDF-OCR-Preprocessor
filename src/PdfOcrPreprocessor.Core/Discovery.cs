namespace PdfOcrPreprocessor.Core;

public sealed record SourceRoot(string Name, string Path);
public sealed record DiscoveredPdf(string Source, string FullPath, string RelativePath);
public sealed record DiscoveryIssue(string Source, string Path, string Error);
public sealed record DiscoveryResult(DiscoveredPdf[] Files, DiscoveryIssue[] Issues);
public sealed record DocumentMatch(string RelativePath, Dictionary<string, DiscoveredPdf[]> Variants, string[] MissingSources,
    bool ProviderOnly, bool Collision);

public static class Discovery
{
    public static DiscoveryResult Scan(SourceRoot source, CancellationToken cancellationToken)
    {
        var files = new List<DiscoveredPdf>();
        var issues = new List<DiscoveryIssue>();
        var root = System.IO.Path.GetFullPath(source.Path);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Directory.GetFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            issues.Add(new(source.Name, entry, "Reparse point skipped."));
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else if (System.IO.Path.GetExtension(entry).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                            files.Add(new(source.Name, entry, System.IO.Path.GetRelativePath(root, entry).Replace('/', '\\')));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        issues.Add(new(source.Name, entry, exception.Message));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(source.Name, directory, exception.Message));
            }
        }
        return new(files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), issues.ToArray());
    }

    public static DocumentMatch[] Match(IEnumerable<DiscoveredPdf> files, SourceRoot[] sources) =>
        files.GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var variants = group.GroupBy(file => file.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(variant => variant.Key, variant => variant.ToArray(), StringComparer.OrdinalIgnoreCase);
            return new DocumentMatch(group.Key, variants, sources.Where(source => !variants.ContainsKey(source.Name))
                .Select(source => source.Name).ToArray(), !variants.ContainsKey("Original"), variants.Values.Any(values => values.Length > 1));
        }).OrderBy(match => match.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();

    public static void ValidatePaths(SourceRoot[] sources, string workspace, string repository)
    {
        if (sources.Length == 0 || !sources.Any(source => source.Name.Equals("Original", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("An Original source root is required.");
        if (sources.Select(source => source.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new ArgumentException("Source names must be unique.");
        foreach (var source in sources)
        {
            if (Overlaps(source.Path, workspace) || Overlaps(source.Path, repository))
                throw new ArgumentException("Sources, repository and analysis workspace must not overlap.");
            foreach (var other in sources.Where(other => !ReferenceEquals(source, other)))
                if (Overlaps(source.Path, other.Path)) throw new ArgumentException("Source roots must not overlap.");
        }
        if (Overlaps(workspace, repository)) throw new ArgumentException("Analysis workspace must be outside the repository.");
        foreach (var path in sources.Select(source => source.Path).Append(workspace).Append(repository))
        {
            var ancestor = new DirectoryInfo(System.IO.Path.GetFullPath(path));
            while (ancestor != null)
            {
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException($"Reparse-point path not allowed: {ancestor.FullName}");
                ancestor = ancestor.Parent;
            }
        }
    }

    private static bool Overlaps(string first, string second)
    {
        var firstPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(first)) + System.IO.Path.DirectorySeparatorChar;
        var secondPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(second)) + System.IO.Path.DirectorySeparatorChar;
        return firstPath.StartsWith(secondPath, StringComparison.OrdinalIgnoreCase) || secondPath.StartsWith(firstPath, StringComparison.OrdinalIgnoreCase);
    }
}