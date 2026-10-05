namespace PdfOcrPreprocessor.Core;

public sealed record PointD(double X, double Y);
public sealed record BoxD(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

public sealed record PageGeometry(BoxD MediaBox, BoxD CropBox, double UserUnit, int Rotation)
{
    public double DisplayWidth => (Rotation is 90 or 270 ? CropBox.Height : CropBox.Width) * UserUnit;
    public double DisplayHeight => (Rotation is 90 or 270 ? CropBox.Width : CropBox.Height) * UserUnit;

    public PointD ToDisplay(PointD raw)
    {
        var horizontal = (raw.X - CropBox.Left) * UserUnit;
        var vertical = (raw.Y - CropBox.Bottom) * UserUnit;
        return Rotation switch
        {
            0 => new(horizontal, DisplayHeight - vertical),
            90 => new(vertical, horizontal),
            180 => new(DisplayWidth - horizontal, vertical),
            270 => new(DisplayWidth - vertical, DisplayHeight - horizontal),
            _ => throw new ArgumentOutOfRangeException(nameof(Rotation))
        };
    }
}

public sealed record GlyphEvidence(int Index, string Text, PointD[] ExtractorQuad, PointD[] DisplayQuad,
    PointD BaselineStart, PointD BaselineEnd);
public sealed record WordEvidence(string Text, int[] GlyphIndices, int Line);
public sealed record PageEvidence(string FullPath, string RelativePath, string Source, int PageNumber,
    int PageCount, PageGeometry Geometry, string CoordinateConvention, string Text,
    GlyphEvidence[] Glyphs, WordEvidence[] Words, int ImageCount, string[] Warnings);
public sealed record RenderedPage(byte[] Png, int PixelWidth, int PixelHeight, int RequestedDpi,
    double DisplayWidthPoints, double DisplayHeightPoints);

public interface IPdfTextExtractor
{
    int GetPageCount(string path);
    PageEvidence Extract(string path, string relativePath, string source, int page, CancellationToken cancellationToken);
}

public interface IPageRenderer
{
    RenderedPage Render(string path, int page, int dpi, CancellationToken cancellationToken);
}