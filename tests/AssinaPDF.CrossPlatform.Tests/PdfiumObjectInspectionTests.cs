using Aspose.Pdf;
using Aspose.Pdf.Text;
using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfiumObjectInspectionTests
{
    [Fact]
    public void InspectPageCharacters_ReturnsIndividualTextPositionsAndFontMetrics()
    {
        string path = Path.Combine(Path.GetTempPath(), $"assinapdf-glyphs-{Guid.NewGuid():N}.pdf");

        try
        {
            using (var source = new Document())
            {
                var page = source.Pages.Add();
                var fragment = new TextFragment("Future PDF 123")
                {
                    Position = new Position(72, 700)
                };
                fragment.TextState.FontName = "Helvetica";
                fragment.TextState.FontSize = 16;
                new TextBuilder(page).AppendText(fragment);
                source.Save(path);
            }

            using var pdf = new PdfiumDocument(path);
            var characters = pdf.InspectPageCharacters(0);
            var letterF = Assert.Single(characters, item => item.Text == "F");

            Assert.True(characters.Count >= 10);
            Assert.True(letterF.Right > letterF.Left);
            Assert.True(letterF.Top > letterF.Bottom);
            Assert.InRange(letterF.FontSize, 15.5, 16.5);

            var following = Assert.Single(characters, item => item.Text == "u");
            Assert.True(following.Left >= letterF.Left);
            Assert.True(following.CharacterIndex > letterF.CharacterIndex);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void InspectPageObjects_ReturnsTextObjectBoundsAndFontSize()
    {
        string path = Path.Combine(Path.GetTempPath(), $"assinapdf-pdfium-{Guid.NewGuid():N}.pdf");

        try
        {
            using (var source = new Document())
            {
                var page = source.Pages.Add();
                var fragment = new TextFragment("AssinaPDF objeto de teste")
                {
                    Position = new Position(72, 700)
                };
                fragment.TextState.FontName = "Helvetica";
                fragment.TextState.FontSize = 14;
                new TextBuilder(page).AppendText(fragment);
                source.Save(path);
            }

            using var pdf = new PdfiumDocument(path);
            var objects = pdf.InspectPageObjects(0);
            var textObject = Assert.Single(objects, item =>
                item.ObjectType == 1 &&
                item.Text?.Contains("AssinaPDF objeto de teste", StringComparison.Ordinal) == true);

            Assert.True(textObject.Right > textObject.Left);
            Assert.True(textObject.Top > textObject.Bottom);
            Assert.NotNull(textObject.FontSize);
            Assert.InRange(textObject.FontSize!.Value, 13.9f, 14.1f);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
