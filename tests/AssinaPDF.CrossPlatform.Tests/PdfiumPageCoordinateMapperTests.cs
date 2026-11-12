using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfiumPageCoordinateMapperTests
{
    [Fact]
    public void TryViewToPdf_ConvertsCoordinatesAndInvertsVerticalAxis()
    {
        bool converted = PdfiumPageCoordinateMapper.TryViewToPdf(
            viewX: 250,
            viewY: 100,
            viewWidth: 500,
            viewHeight: 700,
            pageWidthPoints: 600,
            pageHeightPoints: 840,
            out double pdfX,
            out double pdfY);

        Assert.True(converted);
        Assert.Equal(300, pdfX, 6);
        Assert.Equal(720, pdfY, 6);
    }

    [Fact]
    public void PdfToViewRect_MapsPdfBoundsToVisualRectangle()
    {
        var rect = PdfiumPageCoordinateMapper.PdfToViewRect(
            left: 60,
            bottom: 84,
            right: 180,
            top: 168,
            viewWidth: 600,
            viewHeight: 840,
            pageWidthPoints: 600,
            pageHeightPoints: 840);

        Assert.Equal(60, rect.X, 6);
        Assert.Equal(672, rect.Y, 6);
        Assert.Equal(120, rect.Width, 6);
        Assert.Equal(84, rect.Height, 6);
    }

    [Theory]
    [InlineData(0, 25, 150, 25, 50)]
    [InlineData(1, 25, 100, 100, 25)]
    [InlineData(2, 25, 150, 75, 150)]
    [InlineData(3, 25, 100, 100, 75)]
    public void TryViewToPdf_MapsAllPageRotations(
        int rotation, double expectedX, double expectedY,
        double viewX, double viewY)
    {
        double viewWidth = rotation is 1 or 3 ? 200 : 100;
        double viewHeight = rotation is 1 or 3 ? 100 : 200;

        bool converted = PdfiumPageCoordinateMapper.TryViewToPdf(
            viewX, viewY, viewWidth, viewHeight, 100, 200,
            out double pdfX, out double pdfY, rotation);

        Assert.True(converted);
        Assert.Equal(expectedX, pdfX, 6);
        Assert.Equal(expectedY, pdfY, 6);
    }

    [Theory]
    [InlineData(0, 10, 120, 30, 60)]
    [InlineData(1, 20, 10, 60, 30)]
    [InlineData(2, 60, 20, 30, 60)]
    [InlineData(3, 120, 60, 60, 30)]
    public void PdfToViewRect_MapsBoundsForAllPageRotations(
        int rotation, double expectedX, double expectedY,
        double expectedWidth, double expectedHeight)
    {
        double viewWidth = rotation is 1 or 3 ? 200 : 100;
        double viewHeight = rotation is 1 or 3 ? 100 : 200;

        var rect = PdfiumPageCoordinateMapper.PdfToViewRect(
            10, 20, 40, 80, viewWidth, viewHeight, 100, 200, rotation);

        Assert.Equal(expectedX, rect.X, 6);
        Assert.Equal(expectedY, rect.Y, 6);
        Assert.Equal(expectedWidth, rect.Width, 6);
        Assert.Equal(expectedHeight, rect.Height, 6);
    }

    [Fact]
    public void TryViewToPdf_ReturnsFalseForPointsOutsidePage()
    {
        bool converted = PdfiumPageCoordinateMapper.TryViewToPdf(
            viewX: -1,
            viewY: 20,
            viewWidth: 500,
            viewHeight: 700,
            pageWidthPoints: 600,
            pageHeightPoints: 840,
            out _,
            out _);

        Assert.False(converted);
    }
}
