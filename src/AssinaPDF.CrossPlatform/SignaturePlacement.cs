namespace AssinaPDF.MacPrototype;

public sealed record SignaturePlacement(
    int PageIndex,
    double NormalizedX,
    double NormalizedY,
    double NormalizedWidth,
    double NormalizedHeight,
    float PageWidthPoints,
    float PageHeightPoints,
    int PageRotation = 0);


public static class SignaturePlacementCoordinates
{
    public static (double X, double Y) GetPdfOrigin(this SignaturePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.PageWidthPoints <= 0 || placement.PageHeightPoints <= 0 ||
            placement.PageRotation is < 0 or > 3 ||
            placement.NormalizedX < 0 || placement.NormalizedY < 0 ||
            placement.NormalizedWidth <= 0 || placement.NormalizedHeight <= 0 ||
            placement.NormalizedX + placement.NormalizedWidth > 1 ||
            placement.NormalizedY + placement.NormalizedHeight > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement), "A posição e as dimensões da assinatura precisam ser válidas.");
        }

        double displayWidth = placement.PageRotation is 1 or 3
            ? placement.PageHeightPoints
            : placement.PageWidthPoints;
        double displayHeight = placement.PageRotation is 1 or 3
            ? placement.PageWidthPoints
            : placement.PageHeightPoints;

        double viewLeft = placement.NormalizedX * displayWidth;
        double viewTop = placement.NormalizedY * displayHeight;
        double viewRight = (placement.NormalizedX + placement.NormalizedWidth) * displayWidth;
        double viewBottom = (placement.NormalizedY + placement.NormalizedHeight) * displayHeight;

        if (!AssinaPDF.MacPrototype.Services.PdfiumPageCoordinateMapper.TryViewToPdf(
                viewLeft, viewTop, displayWidth, displayHeight,
                placement.PageWidthPoints, placement.PageHeightPoints,
                out double firstX, out double firstY, placement.PageRotation) ||
            !AssinaPDF.MacPrototype.Services.PdfiumPageCoordinateMapper.TryViewToPdf(
                viewRight, viewBottom, displayWidth, displayHeight,
                placement.PageWidthPoints, placement.PageHeightPoints,
                out double secondX, out double secondY, placement.PageRotation))
        {
            throw new InvalidOperationException(
                "Não foi possível converter a posição da assinatura para coordenadas PDF.");
        }

        return (Math.Min(firstX, secondX), Math.Min(firstY, secondY));
    }
}
