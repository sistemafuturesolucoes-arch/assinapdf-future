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
            Assert.Equal(792, rendered.PageWidthPoints, 1);
            Assert.Equal(612, rendered.PageHeightPoints, 1);
            Assert.Equal(612, rendered.OriginalPageWidthPoints, 1);
            Assert.Equal(792, rendered.OriginalPageHeightPoints, 1);
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
                var firstPage = document.Pages.Add();
                firstPage.SetPageSize(612, 792);

                var secondPage = document.Pages.Add();
                secondPage.SetPageSize(300, 500);
                secondPage.RotateDegrees = 270;

                var thirdPage = document.Pages.Add();
                thirdPage.SetPageSize(842, 595);

                document.Save(path);
            }

            using var pdfium = new PdfiumDocument(path);
            Assert.Equal(3, pdfium.PageCount);

            var firstRendered = pdfium.RenderPage(0, 0.25f);
            var secondRendered = pdfium.RenderPage(1, 0.25f);
            var thirdRendered = pdfium.RenderPage(2, 0.25f);

            Assert.Equal(0, firstRendered.PageRotation);
            Assert.Equal(3, secondRendered.PageRotation);
            Assert.Equal(0, thirdRendered.PageRotation);

            Assert.True(firstRendered.Height > firstRendered.Width);
            Assert.True(secondRendered.Width > secondRendered.Height);
            Assert.True(thirdRendered.Width > thirdRendered.Height);

            Assert.Equal(612, firstRendered.OriginalPageWidthPoints, 1);
            Assert.Equal(792, firstRendered.OriginalPageHeightPoints, 1);
            Assert.Equal(500, secondRendered.PageWidthPoints, 1);
            Assert.Equal(300, secondRendered.PageHeightPoints, 1);
            Assert.Equal(300, secondRendered.OriginalPageWidthPoints, 1);
            Assert.Equal(500, secondRendered.OriginalPageHeightPoints, 1);
            Assert.Equal(842, thirdRendered.OriginalPageWidthPoints, 1);
            Assert.Equal(595, thirdRendered.OriginalPageHeightPoints, 1);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
