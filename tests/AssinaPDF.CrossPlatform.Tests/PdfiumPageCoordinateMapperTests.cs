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
