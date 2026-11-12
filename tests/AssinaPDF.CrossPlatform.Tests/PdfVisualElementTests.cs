using Aspose.Pdf;
using Aspose.Pdf.Annotations;
using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfVisualElementTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZ1sAAAAASUVORK5CYII=";

    [Fact]
    public void ImageAndEditorRectangle_CanBeInsertedMovedResizedAndDeleted()
    {
        string imagePath = Path.Combine(
            Path.GetTempPath(), $"assinapdf-image-{Guid.NewGuid():N}.png");
        string pdfPath = Path.Combine(
            Path.GetTempPath(), $"assinapdf-elements-{Guid.NewGuid():N}.pdf");

        try
        {
            File.WriteAllBytes(imagePath, Convert.FromBase64String(OnePixelPng));

            using (var document = new Document())
            {
                var page = document.Pages.Add();
                page.SetPageSize(612, 792);

                var rectangle = new SquareAnnotation(
                    page, new Rectangle(20, 30, 120, 130))
                {
                    Name = "FutureEditorRectangle-Test",
                    Title = "AssinaPDF",
                    Subject = "AssinaPDF Editor Rectangle",
                    Color = Color.FromRgb(0.42, 0.29, 0.75),
                    InteriorColor = Color.FromRgb(0.88, 0.84, 0.96),
                    Opacity = 0.22
                };
                rectangle.UpdateAppearances();
                page.Annotations.Add(rectangle);

                var image = new ImageStamp(imagePath)
                {
                    X = 40,
                    Y = 50,
                    DisplayWidth = 100,
                    DisplayHeight = 80,
                    Quality = 95,
                    Opacity = 1.0,
                    Background = false
                };
                page.AddStamp(image);
                document.Save(pdfPath);
            }

            using (var editable = Document.Open(pdfPath))
            {
                var page = editable.Pages[1];
                var rectangle = Assert.Single(page.Annotations
                    .OfType<SquareAnnotation>()
                    .Where(item => item.Name == "FutureEditorRectangle-Test"));
                rectangle.Rect = new Rectangle(140, 150, 280, 250);
                rectangle.UpdateAppearances();

                var absorber = new ImagePlacementAbsorber();
                page.Accept(absorber);
                var placement = Assert.Single(absorber.ImagePlacements);
                using var imageData = new MemoryStream();
                placement.Save(imageData);
                Assert.NotEmpty(imageData.ToArray());
                placement.Hide();

                using var imageStream = new MemoryStream(imageData.ToArray(), writable: false);
                page.AddStamp(new ImageStamp(imageStream)
                {
                    X = 160,
                    Y = 170,
                    DisplayWidth = 120,
                    DisplayHeight = 96,
                    Quality = 95,
                    Opacity = 1.0,
                    Background = false
                });

                editable.Save(pdfPath);
            }

            using (var reopened = Document.Open(pdfPath))
            {
                var page = reopened.Pages[1];
                var rectangle = Assert.Single(page.Annotations
                    .OfType<SquareAnnotation>()
                    .Where(item => item.Name == "FutureEditorRectangle-Test"));
                Assert.Equal(140, rectangle.Rect!.LLX, 1);
                Assert.Equal(150, rectangle.Rect.LLY, 1);
                Assert.Equal(280, rectangle.Rect.URX, 1);
                Assert.Equal(250, rectangle.Rect.URY, 1);

                var absorber = new ImagePlacementAbsorber();
                page.Accept(absorber);
                var placement = Assert.Single(absorber.ImagePlacements);
                Assert.InRange(Math.Abs(placement.Rectangle.LLX - 160), 0, 2);
                Assert.InRange(Math.Abs(placement.Rectangle.LLY - 170), 0, 2);
                Assert.InRange(Math.Abs(placement.Rectangle.URX - 280), 0, 2);
                Assert.InRange(Math.Abs(placement.Rectangle.URY - 266), 0, 2);

                page.Annotations.Delete(rectangle);
                reopened.Save(pdfPath);
            }

            using var finalDocument = Document.Open(pdfPath);
            Assert.Empty(finalDocument.Pages[1].Annotations
                .OfType<SquareAnnotation>()
                .Where(item => item.Name == "FutureEditorRectangle-Test"));
        }
        finally
        {
            if (File.Exists(imagePath))
                File.Delete(imagePath);
            if (File.Exists(pdfPath))
                File.Delete(pdfPath);
        }
    }
}
