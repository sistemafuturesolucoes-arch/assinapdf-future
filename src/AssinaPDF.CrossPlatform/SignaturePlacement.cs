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
            placement.NormalizedX + placement.NormalizedWidth > 1.000001 ||
            placement.NormalizedY + placement.NormalizedHeight > 1.000001)
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

        double normalizedLeft = Math.Clamp(placement.NormalizedX, 0, 1);
        double normalizedTop = Math.Clamp(placement.NormalizedY, 0, 1);
        double normalizedRight = Math.Clamp(
            placement.NormalizedX + placement.NormalizedWidth, 0, 1);
        double normalizedBottom = Math.Clamp(
            placement.NormalizedY + placement.NormalizedHeight, 0, 1);
        double viewLeft = normalizedLeft * displayWidth;
        double viewTop = normalizedTop * displayHeight;
        double viewRight = normalizedRight * displayWidth;
        double viewBottom = normalizedBottom * displayHeight;

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
