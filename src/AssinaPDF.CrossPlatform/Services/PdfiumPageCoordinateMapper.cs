using System;

namespace AssinaPDF.MacPrototype.Services;

public readonly record struct PdfiumViewRect(
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>
/// Converte coordenadas entre a área visual da página e o sistema PDF
/// (pontos, origem no canto inferior esquerdo).
/// </summary>
public static class PdfiumPageCoordinateMapper
{
    public static bool TryViewToPdf(
        double viewX,
        double viewY,
        double viewWidth,
        double viewHeight,
        double pageWidthPoints,
        double pageHeightPoints,
        out double pdfX,
        out double pdfY)
    {
        pdfX = 0;
        pdfY = 0;

        if (!double.IsFinite(viewX) || !double.IsFinite(viewY) ||
            !double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) ||
            !double.IsFinite(pageWidthPoints) || !double.IsFinite(pageHeightPoints) ||
            viewWidth <= 0 || viewHeight <= 0 ||
            pageWidthPoints <= 0 || pageHeightPoints <= 0 ||
            viewX < 0 || viewY < 0 || viewX > viewWidth || viewY > viewHeight)
            return false;

        pdfX = viewX / viewWidth * pageWidthPoints;
        pdfY = (viewHeight - viewY) / viewHeight * pageHeightPoints;
        return double.IsFinite(pdfX) && double.IsFinite(pdfY);
    }

    public static PdfiumViewRect PdfToViewRect(
        double left,
        double bottom,
        double right,
        double top,
        double viewWidth,
        double viewHeight,
        double pageWidthPoints,
        double pageHeightPoints)
    {
        if (!double.IsFinite(left) || !double.IsFinite(bottom) ||
            !double.IsFinite(right) || !double.IsFinite(top) ||
            !double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) ||
            !double.IsFinite(pageWidthPoints) || !double.IsFinite(pageHeightPoints) ||
            right < left || top < bottom ||
            viewWidth <= 0 || viewHeight <= 0 ||
            pageWidthPoints <= 0 || pageHeightPoints <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewWidth),
                "As dimensões e coordenadas da página devem ser válidas.");

        double x = left / pageWidthPoints * viewWidth;
        double y = (pageHeightPoints - top) / pageHeightPoints * viewHeight;
        double width = (right - left) / pageWidthPoints * viewWidth;
        double height = (top - bottom) / pageHeightPoints * viewHeight;

        return new PdfiumViewRect(x, y, width, height);
    }
}
