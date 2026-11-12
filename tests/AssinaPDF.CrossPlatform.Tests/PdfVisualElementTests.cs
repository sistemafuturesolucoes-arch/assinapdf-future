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
        string editedPath = Path.Combine(
            Path.GetTempPath(), $"assinapdf-elements-edited-{Guid.NewGuid():N}.pdf");
        string finalPath = Path.Combine(
            Path.GetTempPath(), $"assinapdf-elements-final-{Guid.NewGuid():N}.pdf");

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
                    Opacity = 0.22,
                    Flags = AnnotationFlags.Print
                };
                page.Annotations.Add(rectangle);
                rectangle.UpdateAppearances();

                var ellipse = new CircleAnnotation(
                    page, new Rectangle(180, 200, 280, 300))
                {
                    Name = "FutureEditorEllipse-Test",
                    Title = "AssinaPDF",
                    Subject = "AssinaPDF Editor Ellipse",
                    Color = Color.FromRgb(0.42, 0.29, 0.75),
                    InteriorColor = Color.FromRgb(0.88, 0.84, 0.96),
                    Opacity = 0.22,
                    Flags = AnnotationFlags.Print
                };
                page.Annotations.Add(ellipse);
                ellipse.UpdateAppearances();

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

                var ellipse = Assert.Single(page.Annotations
                    .OfType<CircleAnnotation>()
                    .Where(item => item.Name == "FutureEditorEllipse-Test"));
                ellipse.Rect = new Rectangle(300, 320, 440, 420);
                ellipse.UpdateAppearances();

                var absorber = new ImagePlacementAbsorber();
                page.Accept(absorber);
                var placement = Assert.Single(absorber.ImagePlacements);
                Assert.NotNull(placement.Image);
                byte[] imageData = placement.Image!.ToPng();
                Assert.NotEmpty(imageData);
                placement.Hide();

                using var imageStream = new MemoryStream(imageData, writable: false);
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

                editable.Save(editedPath);
            }
            File.Move(editedPath, pdfPath, overwrite: true);

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

                var ellipse = Assert.Single(page.Annotations
                    .OfType<CircleAnnotation>()
                    .Where(item => item.Name == "FutureEditorEllipse-Test"));
                Assert.Equal(300, ellipse.Rect!.LLX, 1);
                Assert.Equal(320, ellipse.Rect.LLY, 1);
                Assert.Equal(440, ellipse.Rect.URX, 1);
                Assert.Equal(420, ellipse.Rect.URY, 1);

                var absorber = new ImagePlacementAbsorber();
                page.Accept(absorber);
                var placement = Assert.Single(absorber.ImagePlacements);
                Assert.InRange(Math.Abs(placement.Rectangle.LLX - 160), 0, 2);
                Assert.InRange(Math.Abs(placement.Rectangle.LLY - 170), 0, 2);
                Assert.InRange(Math.Abs(placement.Rectangle.URX - 280), 0, 2);
                Assert.InRange(Math.Abs(placement.Rectangle.URY - 266), 0, 2);

                page.Annotations.Delete(rectangle);
                page.Annotations.Delete(ellipse);
                reopened.Save(finalPath);
            }
            File.Move(finalPath, pdfPath, overwrite: true);

            using var finalDocument = Document.Open(pdfPath);
            Assert.Empty(finalDocument.Pages[1].Annotations
                .OfType<SquareAnnotation>()
                .Where(item => item.Name == "FutureEditorRectangle-Test"));
            Assert.Empty(finalDocument.Pages[1].Annotations
                .OfType<CircleAnnotation>()
                .Where(item => item.Name == "FutureEditorEllipse-Test"));
        }
        finally
        {
            if (File.Exists(imagePath))
                File.Delete(imagePath);
            if (File.Exists(pdfPath))
                File.Delete(pdfPath);
            if (File.Exists(editedPath))
                File.Delete(editedPath);
            if (File.Exists(finalPath))
                File.Delete(finalPath);
        }
    }
}
