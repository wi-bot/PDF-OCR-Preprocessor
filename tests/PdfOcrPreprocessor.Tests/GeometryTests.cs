using PdfOcrPreprocessor.Core;
using Xunit;

namespace PdfOcrPreprocessor.Tests;

public class GeometryTests
{
    [Theory]
    [InlineData(0, 20, 160, 200, 200)]
    [InlineData(90, 40, 20, 200, 200)]
    [InlineData(180, 180, 40, 200, 200)]
    [InlineData(270, 160, 180, 200, 200)]
    public void CropScaleAndQuarterTurns(int rotation, double expectedX, double expectedY, double width, double height)
    {
        var geometry = new PageGeometry(new(0, 0, 200, 300), new(10, 20, 110, 120), 2, rotation);
        Assert.Equal(new PointD(expectedX, expectedY), geometry.ToDisplay(new(20, 40)));
        Assert.Equal(width, geometry.DisplayWidth);
        Assert.Equal(height, geometry.DisplayHeight);
    }
}