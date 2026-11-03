using System;
using System.Collections.Generic;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AssinaPDF.MacPrototype.Services;

public sealed record SignatureWidgetInfo(
    int PageIndex,
    string FieldName,
    double NormalizedX,
    double NormalizedY,
    double NormalizedWidth,
    double NormalizedHeight);

public static class PdfSignatureWidgetReader
{
    public static IReadOnlyList<SignatureWidgetInfo> Read(string filePath)
    {
        var found = new List<SignatureWidgetInfo>();

        using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);

        for (int pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
        {
            var page = document.Pages[pageIndex];
            if (!page.HasAnnotations)
                continue;

            double pageWidth = page.Width.Point;
            double pageHeight = page.Height.Point;
            if (pageWidth <= 0 || pageHeight <= 0)
                continue;

            foreach (var item in page.Annotations)
            {
                if (item is not PdfDictionary annotation)
                    continue;

                if (!string.Equals(
                    annotation.Elements.GetName("/Subtype"),
                    "/Widget",
                    StringComparison.Ordinal))
                    continue;

                var field = annotation;
                string fieldType = string.Empty;
                string fieldName = string.Empty;

                // /FT and /T may be inherited from one or more parent fields.
                for (int depth = 0; depth < 16 && field != null; depth++)
                {
                    if (string.IsNullOrEmpty(fieldType))
                        fieldType = field.Elements.GetName("/FT");

                    if (string.IsNullOrEmpty(fieldName))
                        fieldName = field.Elements.GetString("/T", false);

                    field = field.Elements.GetDictionary("/Parent");
                }

                if (!string.Equals(fieldType, "/Sig", StringComparison.Ordinal))
                    continue;

                var rect = annotation.Elements.GetRectangle("/Rect", false);
                if (rect == null)
                    continue;

                double left = Math.Min(rect.X1, rect.X2);
                double right = Math.Max(rect.X1, rect.X2);
                double bottom = Math.Min(rect.Y1, rect.Y2);
                double top = Math.Max(rect.Y1, rect.Y2);
                double width = right - left;
                double height = top - bottom;

                // Zero-sized widgets are intentionally invisible in the PDF.
                if (width < 1 || height < 1)
                    continue;

                double x = Math.Clamp(left / pageWidth, 0, 1);
                double y = Math.Clamp((pageHeight - top) / pageHeight, 0, 1);
                double normalizedWidth = Math.Clamp(width / pageWidth, 0, 1 - x);
                double normalizedHeight = Math.Clamp(height / pageHeight, 0, 1 - y);

                if (normalizedWidth <= 0 || normalizedHeight <= 0)
                    continue;

                found.Add(new SignatureWidgetInfo(
                    pageIndex,
                    string.IsNullOrWhiteSpace(fieldName) ? "Assinatura digital" : fieldName,
                    x,
                    y,
                    normalizedWidth,
                    normalizedHeight));
            }
        }

        return found;
    }
}
