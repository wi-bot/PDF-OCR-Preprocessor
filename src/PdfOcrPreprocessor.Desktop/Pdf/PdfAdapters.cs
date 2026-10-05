using PdfOcrPreprocessor.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.Tokens;

namespace PdfOcrPreprocessor.Desktop.Pdf;

public sealed class PdfPigExtractor : IPdfTextExtractor
{
    public int GetPageCount(string path)
    {
        using var document = PdfDocument.Open(path);
        return document.NumberOfPages;
    }

    public PageEvidence Extract(string path, string relativePath, string source, int pageNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(path);
        var page = document.GetPage(pageNumber);
        var unit = page.Dictionary.TryGet(NameToken.Create("UserUnit"), out var token) && token is NumericToken numeric
            ? numeric.Double : 1;
        if (!double.IsFinite(unit) || unit <= 0) throw new InvalidDataException("Invalid UserUnit.");
        var geometry = new PageGeometry(Box(page.MediaBox.Bounds), Box(page.CropBox.Bounds), unit, page.Rotation.Value);
        if (page.Letters.Count > 200_000) throw new InvalidDataException("Page exceeds proof glyph limit (200000).");
        var letters = page.Letters;
        PointD Display(PdfPoint point) => new(point.X * unit, (page.Height - point.Y) * unit);
        var glyphs = letters.Select((letter, index) =>
        {
            var rectangle = letter.BoundingBox;
            var points = new[] { rectangle.BottomLeft, rectangle.BottomRight, rectangle.TopRight, rectangle.TopLeft };
            return new GlyphEvidence(index, letter.Value, points.Select(Point).ToArray(), points.Select(Display).ToArray(),
                Point(letter.StartBaseLine), Point(letter.EndBaseLine));
        }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var words = new List<WordEvidence>();
        var indexByLetter = new Dictionary<UglyToad.PdfPig.Content.Letter, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < letters.Count; index++) indexByLetter[letters[index]] = index;
        try
        {
            var extractedWords = page.GetWords(NearestNeighbourWordExtractor.Instance).ToArray();
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(extractedWords);
            var lineNumber = 0;
            foreach (var block in blocks.OrderByDescending(block => block.BoundingBox.Top).ThenBy(block => block.BoundingBox.Left))
            foreach (var line in block.TextLines)
            {
                foreach (var word in line.Words)
                    words.Add(new(word.Text, word.Letters.Select(letter => indexByLetter[letter]).ToArray(), lineNumber));
                lineNumber++;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            warnings.Add($"Grouping failed: {exception.GetType().Name}: {exception.Message}");
        }
        var text = words.Count == 0 ? string.Concat(glyphs.Select(glyph => glyph.Text))
            : string.Join("\n", words.GroupBy(word => word.Line).Select(line => string.Join(" ", line.Select(word => word.Text))));
        return new(path, relativePath, source, pageNumber, document.NumberOfPages, geometry,
            "PdfPig 0.1.16 transformed page space, bottom-left; DisplayQuad: top-left points with UserUnit scale. No cross-file registration.",
            text, glyphs, words.ToArray(), page.NumberOfImages, warnings.ToArray());
    }

    private static PointD Point(PdfPoint point) => new(point.X, point.Y);
    private static BoxD Box(PdfRectangle rectangle) => new(rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top);
}

public sealed class PdfiumRenderer : IPageRenderer
{
    public const long MaxPixels = 20_000_000;

    public RenderedPage Render(string path, int page, int dpi, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dpi is < 36 or > 300) throw new ArgumentOutOfRangeException(nameof(dpi));
        using var stream = File.OpenRead(path);
        var size = PDFtoImage.Conversion.GetPageSize(stream, page - 1);
        using var metadataDocument = PdfDocument.Open(path);
        var metadata = metadataDocument.GetPage(page);
        var unit = metadata.Dictionary.TryGet(NameToken.Create("UserUnit"), out var token) && token is NumericToken numeric
            ? numeric.Double : 1;
        if (!double.IsFinite(unit) || unit <= 0) throw new InvalidDataException("Invalid UserUnit.");
        var width = Math.Ceiling(size.Width * unit * dpi / 72d);
        var height = Math.Ceiling(size.Height * unit * dpi / 72d);
        if (!double.IsFinite(width * height) || width <= 0 || height <= 0 || width * height > MaxPixels)
            throw new InvalidDataException("Render exceeds proof pixel limit or has invalid dimensions.");
        using var renderStream = File.OpenRead(path);
        using var bitmap = PDFtoImage.Conversion.ToImage(renderStream, page: page - 1,
            options: new PDFtoImage.RenderOptions(Dpi: dpi, Width: (int)width, Height: (int)height, WithAnnotations: true));
        cancellationToken.ThrowIfCancellationRequested();
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        if (encoded.Size > 80_000_000) throw new InvalidDataException("Encoded render exceeds 80 MB limit.");
        return new(encoded.ToArray(), bitmap.Width, bitmap.Height, dpi, size.Width * unit, size.Height * unit);
    }
}