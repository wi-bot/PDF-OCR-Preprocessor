using PdfOcrPreprocessor.Core;
using PdfOcrPreprocessor.Desktop;
using PdfOcrPreprocessor.Desktop.Pdf;
using Xunit;

namespace PdfOcrPreprocessor.Tests;

public sealed class FoundationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PdfOcrPhase0Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FinancialDifferenceSurvivesHighAgreement()
    {
        var prefix = string.Concat(Enumerable.Repeat("same text ", 200));
        var result = Comparison.Compare(new(prefix + "$1,985,420.00"), new(prefix + "$1,965,420.00"));
        Assert.True(result.CharacterAgreement > .99);
        Assert.False(result.Equal);
        Assert.Equal(2, result.NumericDateDifferences.Length);
        Assert.Contains(result.NumericDateDifferences, value => value.Lexeme == "$1,965,420.00");
    }

    [Theory]
    [InlineData("", "", "NoTextualEvidence")]
    [InlineData("", "text", "OneSidedText")]
    [InlineData("text", "text", "Compared")]
    public void EmptyOutputsAreNotConfidence(string left, string right, string state) =>
        Assert.Equal(state, Comparison.Compare(new(left), new(right)).State);

    [Fact]
    public void MissingErrorMismatchAndDuplicateEvidenceAreExplicit()
    {
        Assert.Equal("PageCountMismatch", Comparison.Compare(new("x", PageCount: 2), new("x")).State);
        Assert.Equal("Missing", Comparison.Compare(new(null), new("x")).State);
        Assert.StartsWith("NotComparable", Comparison.Compare(new("", "Error"), new("x")).State);
        Assert.Contains(Comparison.Compare(new("x", FileHash: "same"), new("x", FileHash: "same")).Notes, note => note.Contains("duplicate evidence"));
        Assert.NotEmpty(Comparison.Compare(new("001 -12.30 (5.00) 03/04/2006"), new("1 12.30 5.00 04/03/2006")).NumericDateDifferences);
        var reordered = Comparison.Compare(new("alpha beta"), new("beta alpha"));
        Assert.Equal(1d, reordered.TokenAgreement);
        Assert.True(reordered.CharacterAgreement < 1);
    }

    [Fact]
    public void DiscoveryIsRecursiveAndMatchingDoesNotUseBasenames()
    {
        var root = Path.Combine(directory, "original");
        Directory.CreateDirectory(Path.Combine(root, "Batch", "PDF"));
        File.WriteAllText(Path.Combine(root, "Batch", "PDF", "4483.001.PDF"), "synthetic discovery only");
        File.WriteAllText(Path.Combine(root, "manifest.csv"), "not a pdf");
        var scan = Discovery.Scan(new("Original", root), default);
        Assert.Single(scan.Files);
        Assert.Equal("Batch\\PDF\\4483.001.PDF", scan.Files[0].RelativePath);
        var provider = new DiscoveredPdf("SyntheticProvider", "unused", "Other\\4483.001.PDF");
        var matches = Discovery.Match(scan.Files.Append(provider), [new("Original", root), new("SyntheticProvider", "elsewhere")]);
        Assert.Equal(2, matches.Length);
        Assert.Single(matches, match => match.ProviderOnly);
        var collision = Discovery.Match([scan.Files[0], scan.Files[0] with { RelativePath = "BATCH\\PDF\\4483.001.pdf" }], [new("Original", root)]);
        Assert.True(collision[0].Collision);
        Assert.Empty(Discovery.Match(scan.Files, [new("Original", root)])[0].MissingSources);
    }

    [Fact]
    public void UnsafePathsAndMissingRootAreReported()
    {
        Assert.Throws<ArgumentException>(() => Discovery.ValidatePaths([new("Original", directory)], Path.Combine(directory, "output"), "C:\\Repo"));
        Assert.Single(Discovery.Scan(new("Original", Path.Combine(directory, "absent")), default).Issues);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Discovery.Scan(new("Original", directory), cancellation.Token));
    }

    [Fact]
    public void SqlitePreservesSyntheticEvidenceAndGeometry()
    {
        var path = SyntheticPdf.Write(directory);
        var evidence = new PdfPigExtractor().Extract(path, "batch\\001.PDF", "Synthetic", 1, default);
        Assert.NotEmpty(ProofStore.RoundTrip(Path.Combine(directory, "smoke.db"), evidence));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}