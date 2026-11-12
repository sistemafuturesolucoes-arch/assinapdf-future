using Aspose.Pdf;
using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfiumPageRenderingTests
{
    [Fact]
    public void RenderPage_RespectsIntrinsicRotationAndKeepsOriginalPdfCoordinates()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"assinapdf-rotation-{Guid.NewGuid():N}.pdf");

        try
        {
            using (var document = new Document())
            {
                var page = document.Pages.Add();
                page.SetPageSize(612, 792);
                page.RotateDegrees = 90;

                document.Save(path);
            }

            using var pdfium = new PdfiumDocument(path);
            Assert.Equal(1, pdfium.PageCount);

            RenderedPdfPage rendered = pdfium.RenderPage(0, 0.5f);

            Assert.Equal(1, rendered.PageRotation);
            Assert.Equal(612, rendered.PageWidthPoints, 1);
            Assert.Equal(792, rendered.PageHeightPoints, 1);
            Assert.True(rendered.Width > rendered.Height,
                "Uma página retrato rotacionada 90 graus deve ser renderizada em orientação paisagem.");
            Assert.True(rendered.Pixels.Length >= rendered.Stride * rendered.Height);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void RenderPage_PreservesEveryPageInMultiPageDocuments()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"assinapdf-multipage-{Guid.NewGuid():N}.pdf");

        try
        {
            using (var document = new Document())
            {
                var first = document.Pages.Add();
                first.SetPageSize(612, 792);

                var second = document.Pages.Add();
                second.SetPageSize(300, 500);
                second.RotateDegrees = 270;

                var third = document.Pages.Add();
                third.SetPageSize(842, 595);

                document.Save(path);
            }

            using var pdfium = new PdfiumDocument(path);
            Assert.Equal(3, pdfium.PageCount);

            var first = pdfium.RenderPage(0, 0.25f);
            var second = pdfium.RenderPage(1, 0.25f);
            var third = pdfium.RenderPage(2, 0.25f);

            Assert.Equal(0, first.PageRotation);
            Assert.Equal(3, second.PageRotation);
            Assert.Equal(0, third.PageRotation);

            Assert.True(first.Height > first.Width);
            Assert.True(second.Width > second.Height);
            Assert.True(third.Width > third.Height);

            Assert.Equal(612, first.PageWidthPoints, 1);
            Assert.Equal(792, first.PageHeightPoints, 1);
            Assert.Equal(300, second.PageWidthPoints, 1);
            Assert.Equal(500, second.PageHeightPoints, 1);
            Assert.Equal(842, third.PageWidthPoints, 1);
            Assert.Equal(595, third.PageHeightPoints, 1);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
