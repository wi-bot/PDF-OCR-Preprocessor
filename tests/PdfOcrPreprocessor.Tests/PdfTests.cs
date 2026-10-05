using PdfOcrPreprocessor.Core;
using PdfOcrPreprocessor.Desktop.Pdf;
using SkiaSharp;
using Xunit;

namespace PdfOcrPreprocessor.Tests;

public sealed class PdfTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PdfOcrPhase0Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(90, 1, false)]
    [InlineData(180, 1, false)]
    [InlineData(270, 1, false)]
    [InlineData(0, 2, true)]
    [InlineData(90, 2, true)]
    [InlineData(180, 2, true)]
    [InlineData(270, 2, true)]
    public void SyntheticGlyphOverlayMatchesRenderedInk(int rotation, double unit, bool crop)
    {
        var path = SyntheticPdf.Write(directory, rotation, unit, crop);
        var evidence = new PdfPigExtractor().Extract(path, Path.GetFileName(path), "Synthetic", 1, default);
        var render = new PdfiumRenderer().Render(path, 1, 144, default);
        Assert.Contains("1,985,420.00", evidence.Text);
        Assert.NotEmpty(evidence.Words);
        using var bitmap = SKBitmap.Decode(render.Png);
        foreach (var glyph in evidence.Glyphs.Where(glyph => !string.IsNullOrWhiteSpace(glyph.Text)))
        {
        var left = (int)Math.Floor(glyph.DisplayQuad.Min(point => point.X) / evidence.Geometry.DisplayWidth * bitmap.Width);
        var top = (int)Math.Floor(glyph.DisplayQuad.Min(point => point.Y) / evidence.Geometry.DisplayHeight * bitmap.Height);
        var right = (int)Math.Ceiling(glyph.DisplayQuad.Max(point => point.X) / evidence.Geometry.DisplayWidth * bitmap.Width);
        var bottom = (int)Math.Ceiling(glyph.DisplayQuad.Max(point => point.Y) / evidence.Geometry.DisplayHeight * bitmap.Height);
        var dark = 0;
        for (var row = Math.Max(0, top - 2); row < Math.Min(bitmap.Height, bottom + 2); row++)
        for (var column = Math.Max(0, left - 2); column < Math.Min(bitmap.Width, right + 2); column++)
            if (bitmap.GetPixel(column, row).Red < 128) dark++;
        Assert.True(dark > 1, $"No ink near glyph {glyph.Index}: rotation={rotation}, unit={unit}, crop={crop}; box={left},{top},{right},{bottom}; page={bitmap.Width}x{bitmap.Height}");
        }
        Assert.Equal(evidence.Geometry.DisplayWidth, render.DisplayWidthPoints, 2);
        Assert.Equal(evidence.Geometry.DisplayHeight, render.DisplayHeightPoints, 2);
    }

    [Fact]
    public void RenderDpiLimitsAndCancellationAreExplicit()
    {
        var path = SyntheticPdf.Write(directory, text: "");
        var renderer = new PdfiumRenderer();
        var small = renderer.Render(path, 1, 72, default);
        var large = renderer.Render(path, 1, 144, default);
        Assert.Equal(small.PixelWidth * 2, large.PixelWidth);
        Assert.Equal(small.PixelHeight * 2, large.PixelHeight);
        Assert.Empty(new PdfPigExtractor().Extract(path, "synthetic.pdf", "Synthetic", 1, default).Glyphs);
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Render(path, 1, 600, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.Render(path, 1, 72, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new PdfPigExtractor().Extract(path, "synthetic.pdf", "Synthetic", 1, cancellation.Token));
    }

    [Fact]
    public void InvisibleSyntheticTextIsExtractedButNotPainted()
    {
        var path = SyntheticPdf.Write(directory, invisible: true);
        var evidence = new PdfPigExtractor().Extract(path, Path.GetFileName(path), "Synthetic", 1, default);
        Assert.NotEmpty(evidence.Glyphs);
        using var bitmap = SKBitmap.Decode(new PdfiumRenderer().Render(path, 1, 72, default).Png);
        Assert.All(bitmap.Pixels, pixel => Assert.Equal(SKColors.White, pixel));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}