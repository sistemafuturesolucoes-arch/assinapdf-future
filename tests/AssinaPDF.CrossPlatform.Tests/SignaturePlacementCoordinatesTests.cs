using AssinaPDF.MacPrototype;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class SignaturePlacementCoordinatesTests
{
    [Theory]
    [InlineData(0, 10, 140)]
    [InlineData(1, 20, 20)]
    [InlineData(2, 70, 40)]
    [InlineData(3, 70, 140)]
    public void GetPdfOrigin_ConvertsVisibleSelectionForEveryPageRotation(
        int rotation,
        double expectedX,
        double expectedY)
    {
        var placement = new SignaturePlacement(
            PageIndex: 0,
            NormalizedX: 0.1,
            NormalizedY: 0.2,
            NormalizedWidth: 0.2,
            NormalizedHeight: 0.1,
            PageWidthPoints: 100,
            PageHeightPoints: 200,
            PageRotation: rotation);

        var origin = placement.GetPdfOrigin();

        Assert.Equal(expectedX, origin.X, 6);
        Assert.Equal(expectedY, origin.Y, 6);
    }

    [Fact]
    public void GetPdfOrigin_RejectsInvalidNormalizedBounds()
    {
        var placement = new SignaturePlacement(
            PageIndex: 0,
            NormalizedX: 0.9,
            NormalizedY: 0.2,
            NormalizedWidth: 0.2,
            NormalizedHeight: 0.1,
            PageWidthPoints: 100,
            PageHeightPoints: 200,
            PageRotation: 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => placement.GetPdfOrigin());
    }
}
