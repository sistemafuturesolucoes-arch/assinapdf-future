using System;

namespace AssinaPDF.MacPrototype.Services;

public readonly record struct PdfiumViewRect(
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>
/// Converte coordenadas entre a área visual renderizada e o sistema PDF
/// (pontos, origem no canto inferior esquerdo), incluindo /Rotate da página.
/// A rotação segue a enumeração do PDFium: 0, 1, 2 ou 3 quartos de volta
/// no sentido horário.
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
        out double pdfY,
        int rotation = 0)
    {
        pdfX = 0;
        pdfY = 0;

        if (!double.IsFinite(viewX) || !double.IsFinite(viewY) ||
            !double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) ||
            !double.IsFinite(pageWidthPoints) || !double.IsFinite(pageHeightPoints) ||
            viewWidth <= 0 || viewHeight <= 0 ||
            pageWidthPoints <= 0 || pageHeightPoints <= 0 ||
            viewX < 0 || viewY < 0 || viewX > viewWidth || viewY > viewHeight ||
            rotation is < 0 or > 3)
            return false;

        double u = viewX / viewWidth;
        double v = viewY / viewHeight;

        (pdfX, pdfY) = rotation switch
        {
            0 => (u * pageWidthPoints, (1 - v) * pageHeightPoints),
            1 => (v * pageWidthPoints, u * pageHeightPoints),
            2 => ((1 - u) * pageWidthPoints, v * pageHeightPoints),
            3 => ((1 - v) * pageWidthPoints, (1 - u) * pageHeightPoints),
            _ => (0, 0)
        };

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
        double pageHeightPoints,
        int rotation = 0)
    {
        if (!double.IsFinite(left) || !double.IsFinite(bottom) ||
            !double.IsFinite(right) || !double.IsFinite(top) ||
            !double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) ||
            !double.IsFinite(pageWidthPoints) || !double.IsFinite(pageHeightPoints) ||
            right < left || top < bottom ||
            viewWidth <= 0 || viewHeight <= 0 ||
            pageWidthPoints <= 0 || pageHeightPoints <= 0 ||
            rotation is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(viewWidth),
                "As dimensões, coordenadas e rotação da página devem ser válidas.");

        double x, y, width, height;
        switch (rotation)
        {
            case 0:
                x = left / pageWidthPoints * viewWidth;
                y = (pageHeightPoints - top) / pageHeightPoints * viewHeight;
                width = (right - left) / pageWidthPoints * viewWidth;
                height = (top - bottom) / pageHeightPoints * viewHeight;
                break;
            case 1:
                x = bottom / pageHeightPoints * viewWidth;
                y = left / pageWidthPoints * viewHeight;
                width = (top - bottom) / pageHeightPoints * viewWidth;
                height = (right - left) / pageWidthPoints * viewHeight;
                break;
            case 2:
                x = (pageWidthPoints - right) / pageWidthPoints * viewWidth;
                y = bottom / pageHeightPoints * viewHeight;
                width = (right - left) / pageWidthPoints * viewWidth;
                height = (top - bottom) / pageHeightPoints * viewHeight;
                break;
            default:
                x = (pageHeightPoints - top) / pageHeightPoints * viewWidth;
                y = (pageWidthPoints - right) / pageWidthPoints * viewHeight;
                width = (top - bottom) / pageHeightPoints * viewWidth;
                height = (right - left) / pageWidthPoints * viewHeight;
                break;
        }

        return new PdfiumViewRect(x, y, width, height);
    }
}
