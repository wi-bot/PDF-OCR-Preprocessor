using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using PdfOcrPreprocessor.Core;
using PdfOcrPreprocessor.Desktop.Pdf;

namespace PdfOcrPreprocessor.Desktop;

public sealed record ProofOptions(SourceRoot[] Sources, string Workspace, string Repository, string? Samples, bool Proof, bool InventoryOnly)
{
    public static ProofOptions Parse(string[] arguments)
    {
        var sources = new List<SourceRoot>();
        var original = @"C:\EnronDataset";
        var workspace = @"C:\EnronAnalysis\Phase0";
        string? repository = null;
        string? samples = null;
        var proof = false;
        var inventoryOnly = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            var option = arguments[index];
            string Value() => ++index < arguments.Length ? arguments[index] : throw new ArgumentException($"Missing value for {option}");
            switch (option)
            {
                case "--root": original = Value(); break;
                case "--workspace": workspace = Value(); break;
                case "--repository": repository = Value(); break;
                case "--samples": samples = Value(); break;
                case "--provider":
                    var parts = Value().Split('=', 2);
                    if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) throw new ArgumentException("Use --provider Name=Root");
                    sources.Add(new(parts[0], Path.GetFullPath(parts[1])));
                    break;
                case "--proof": proof = true; break;
                case "--inventory-only": proof = inventoryOnly = true; break;
                default: throw new ArgumentException($"Unknown argument: {option}");
            }
        }
        sources.Insert(0, new("Original", Path.GetFullPath(original)));
        repository ??= FindRepository();
        Discovery.ValidatePaths(sources.ToArray(), workspace, repository);
        return new(sources.ToArray(), Path.GetFullPath(workspace), Path.GetFullPath(repository), samples, proof, inventoryOnly);
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
        throw new ArgumentException("Launch from the repository or provide --repository with its actual path.");
    }
}

public sealed record InventoryFile(DiscoveredPdf File, int? PageCount, string? Sha256, string? Error);
public sealed record Inventory(InventoryFile[] Files, DiscoveryIssue[] Issues, DocumentMatch[] Matches);
public sealed record SelectedPage(string RelativePath, int PageNumber, string Reason);
public sealed record PageResult(string Source, string RelativePath, int PageNumber, string ArtifactPrefix,
    string? HashBefore, string? HashAfter, bool? Unchanged, int? GlyphCount, int? ImageCount, long ElapsedMilliseconds,
    string Extraction, string Rendering, string Persistence, string[] Errors);

public static class ProofRunner
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Inventory Discover(ProofOptions options, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var extractor = new PdfPigExtractor();
        var scans = options.Sources.Select(source => Discovery.Scan(source, cancellationToken)).ToArray();
        var files = scans.SelectMany(scan => scan.Files).ToArray();
        var inventory = new List<InventoryFile>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report($"Inspecting {inventory.Count + 1}/{files.Length}: {file.Source} / {file.RelativePath}");
            int? count = null;
            string? hash = null;
            string? error = null;
            try
            {
                hash = Hash(file.FullPath);
                count = extractor.GetPageCount(file.FullPath);
            }
            catch (Exception exception) when (Recoverable(exception)) { error = exception.ToString(); }
            inventory.Add(new(file, count, hash, error));
        }
        return new(inventory.ToArray(), scans.SelectMany(scan => scan.Issues).ToArray(), Discovery.Match(files, options.Sources));
    }

    public static string Run(ProofOptions options, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var output = Path.Combine(options.Workspace, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(output);
        var stopwatch = Stopwatch.StartNew();
        void Log(string message)
        {
            progress.Report(message);
            File.AppendAllText(Path.Combine(output, "diagnostics.log"), $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        var inventory = Discover(options, new InlineProgress(Log), cancellationToken);
        WriteJson(Path.Combine(output, "inventory.json"), inventory);
        WriteJson(Path.Combine(output, "environment.json"), EnvironmentEvidence());
        WriteJson(Path.Combine(output, "configuration.json"), options);
        if (options.InventoryOnly) return output;
        var selected = options.Samples == null ? SelectPages(inventory) :
            JsonSerializer.Deserialize<SelectedPage[]>(File.ReadAllText(options.Samples), JsonOptions) ?? throw new InvalidDataException("Empty sample selection.");
        WriteJson(Path.Combine(output, "selected-pages.json"), selected);
        var results = new List<PageResult>();
        var comparisons = new List<object>();
        var extractor = new PdfPigExtractor();
        var renderer = new PdfiumRenderer();
        var wasCancelled = false;
        string? sqliteVersion = null;
        try
        {
            foreach (var sample in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matching = inventory.Files.Where(file => file.File.RelativePath.Equals(sample.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matching.Length == 0) throw new InvalidDataException($"Selected relative path not discovered: {sample.RelativePath}");
                var pageEvidence = new List<(InventoryFile File, PageEvidence Evidence)>();
                foreach (var file in matching)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var prefix = $"page-{results.Count + 1:000}";
                    Log($"Processing {prefix}: {file.File.Source} / {sample.RelativePath} / page {sample.PageNumber}");
                    var elapsed = Stopwatch.StartNew();
                    var errors = new List<string>();
                    string? before = null;
                    string? after = null;
                    PageEvidence? evidence = null;
                    var extraction = "NotTested";
                    var rendering = "NotTested";
                    var persistence = "NotTested";
                    try
                    {
                        before = Hash(file.File.FullPath);
                        if (before != file.Sha256) throw new InvalidDataException("Source changed since inventory.");
                        evidence = extractor.Extract(file.File.FullPath, sample.RelativePath, file.File.Source, sample.PageNumber, cancellationToken);
                        extraction = evidence.Glyphs.Length == 0 ? "PassedNoText" : "PassedTextPresent";
                        WriteJson(Path.Combine(output, prefix + ".json"), evidence);
                    }
                    catch (Exception exception) when (Recoverable(exception)) { extraction = "Failed"; errors.Add($"Extraction: {exception}"); }
                    try
                    {
                        var image = renderer.Render(file.File.FullPath, sample.PageNumber, 144, cancellationToken);
                        File.WriteAllBytes(Path.Combine(output, prefix + ".png"), image.Png);
                        WriteJson(Path.Combine(output, prefix + "-render.json"), new { image.PixelWidth, image.PixelHeight, image.RequestedDpi,
                            image.DisplayWidthPoints, image.DisplayHeightPoints, Annotations = true, FormFill = false, ExtraRotation = 0,
                            PngSha256 = Convert.ToHexString(SHA256.HashData(image.Png)) });
                        rendering = "Passed";
                    }
                    catch (Exception exception) when (Recoverable(exception)) { rendering = "Failed"; errors.Add($"Rendering: {exception}"); }
                    if (evidence != null)
                    {
                        try
                        {
                            sqliteVersion = ProofStore.RoundTrip(Path.Combine(output, "smoke.db"), evidence);
                            persistence = "Passed";
                        }
                        catch (Exception exception) when (Recoverable(exception)) { persistence = "Failed"; errors.Add($"Persistence: {exception}"); }
                    }
                    try { after = Hash(file.File.FullPath); }
                    catch (Exception exception) when (Recoverable(exception)) { errors.Add($"HashAfter: {exception}"); }
                    if (before != null && after != null && before != after) errors.Add("Source hash changed during processing; evidence is stale.");
                    if (evidence != null && before == after && before != null) pageEvidence.Add((file, evidence));
                    results.Add(new(file.File.Source, sample.RelativePath, sample.PageNumber, prefix, before, after,
                        before == null || after == null ? null : before == after, evidence?.Glyphs.Length, evidence?.ImageCount,
                        elapsed.ElapsedMilliseconds, extraction, rendering, persistence, errors.ToArray()));
                    WriteJson(Path.Combine(output, "page-results.json"), results);
                    if (errors.Count > 0) Log($"Failure {prefix}: extraction={extraction}; rendering={rendering}; persistence={persistence}; see page-results.json.");
                }
                var providers = options.Sources.Where(source => source.Name != "Original").ToArray();
                var originalCount = matching.FirstOrDefault(item => item.File.Source == "Original")?.PageCount;
                ComparisonInput Input(SourceRoot source)
                {
                    var variants = matching.Where(item => item.File.Source == source.Name).ToArray();
                    if (variants.Length == 0) return new(null, inventory.Issues.Any(issue => issue.Source == source.Name) ? "SourceDiscoveryIncomplete" : "Missing");
                    if (variants.Length > 1) return new(null, "Collision");
                    var variant = variants[0];
                    if (variant.Error != null) return new(null, "InaccessibleOrInvalid");
                    if (originalCount == null || originalCount != variant.PageCount) return new(null, "PageCorrespondenceBlocked", variant.PageCount ?? 0);
                    var extracted = pageEvidence.FirstOrDefault(item => item.File.File.Source == source.Name).Evidence;
                    return extracted == null ? new(null, "ExtractionFailedOrStale") : new(extracted.Text, PageCount: extracted.PageCount, FileHash: variant.Sha256);
                }
                for (var leftIndex = 0; leftIndex < providers.Length; leftIndex++)
                for (var rightIndex = leftIndex + 1; rightIndex < providers.Length; rightIndex++)
                {
                    var left = providers[leftIndex];
                    var right = providers[rightIndex];
                    comparisons.Add(new { sample.RelativePath, sample.PageNumber, Left = left.Name, Right = right.Name,
                        Result = Comparison.Compare(Input(left), Input(right), cancellationToken) });
                }
            }
        }
        catch (OperationCanceledException) { wasCancelled = true; Log("Cancelled between cooperative processing boundaries."); }
        WriteJson(Path.Combine(output, "comparisons.json"), comparisons);
        WriteJson(Path.Combine(output, "summary.json"), new
        {
            WasCancelled = wasCancelled,
            DiscoveredFiles = inventory.Files.Length,
            OpenedFiles = inventory.Files.Count(file => file.PageCount != null),
            TotalEnumeratedPages = inventory.Files.Sum(file => file.PageCount ?? 0),
            UniqueFileHashes = inventory.Files.Where(file => file.Sha256 != null).Select(file => file.Sha256).Distinct().Count(),
            DiscoveryFailures = inventory.Issues.Length,
            OpenFailures = inventory.Files.Count(file => file.Error != null),
            SelectedPages = selected.Length, ProcessedPages = results.Count,
            RenderedPages = results.Count(result => result.Rendering == "Passed"),
            ExtractedTextPages = results.Count(result => result.Extraction == "PassedTextPresent"),
            NoTextPages = results.Count(result => result.Extraction == "PassedNoText"),
            PersistencePassed = results.Count(result => result.Persistence == "Passed"),
            SqliteEngineVersion = sqliteVersion,
            AllProcessedHashesUnchanged = results.Count > 0 && results.All(result => result.Unchanged == true),
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            Providers = options.Sources.Where(source => source.Name != "Original").Select(source => source.Name).ToArray(),
            RealProviderValidation = comparisons.Count == 0 ? "PENDING: no eligible real provider pair tested" : "See comparisons; visual review pending",
            Geometry = "Synthetic pixel tests separate; corpus overlays require visual review. No registration.",
            Limitations = "In-process; cancellation cooperative; native crash or hang can terminate or block application. No automatic acceptance."
        });
        WriteJson(Path.Combine(output, "loaded-modules.json"), Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(module => module.ModuleName.Contains("pdfium", StringComparison.OrdinalIgnoreCase) || module.ModuleName.Contains("skia", StringComparison.OrdinalIgnoreCase)
                || module.ModuleName.Contains("sqlite", StringComparison.OrdinalIgnoreCase) || module.ModuleName == "coreclr.dll")
            .Select(module => new { module.ModuleName, module.FileName, module.FileVersionInfo.FileVersion, Sha256 = Hash(module.FileName) }).ToArray());
        Log($"Completed proof artifacts: {output}");
        return output;
    }

    public static SelectedPage[] SelectPages(Inventory inventory)
    {
        var selected = new List<SelectedPage>();
        var originals = inventory.Files.Where(file => file.File.Source == "Original" && file.PageCount > 0);
        foreach (var batch in originals.GroupBy(file => file.File.RelativePath.Split('\\')[0]).OrderBy(batch => batch.Key))
        {
            var files = batch.OrderBy(file => file.File.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var index in new[] { 0, files.Length / 3, 2 * files.Length / 3, files.Length - 1 }.Distinct())
                selected.Add(new(files[index].File.RelativePath, 1, "Deterministic batch-stratified candidate; visual classification pending."));
            var multiple = files.FirstOrDefault(file => file.PageCount > 1);
            if (multiple != null) selected.Add(new(multiple.File.RelativePath, 2, "Second-page candidate for attachment/thread review; classification pending."));
        }
        return selected.Take(25).ToArray();
    }

    public static object EnvironmentEvidence()
    {
        using var registry = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return new { CapturedUtc = DateTime.UtcNow, OS = RuntimeInformation.OSDescription,
            Edition = registry?.GetValue("EditionID"), DisplayVersion = registry?.GetValue("DisplayVersion"),
            Build = registry?.GetValue("CurrentBuild"), Revision = registry?.GetValue("UBR"),
            Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            SdkPin = "10.0.401 (build configuration; runtime execution alone does not establish SDK installation)",
            PdfPig = typeof(UglyToad.PdfPig.PdfDocument).Assembly.GetName().Version?.ToString(),
            PdfToImage = typeof(PDFtoImage.Conversion).Assembly.GetName().Version?.ToString(),
            Skia = typeof(SkiaSharp.SKBitmap).Assembly.GetName().Version?.ToString(),
            Sqlite = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName().Version?.ToString() };
    }

    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static void WriteJson<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    public static bool Recoverable(Exception exception) => exception is not OperationCanceledException and not OutOfMemoryException;
}

public sealed class InlineProgress(Action<string> action) : IProgress<string>
{
    public void Report(string value) => action(value);
}