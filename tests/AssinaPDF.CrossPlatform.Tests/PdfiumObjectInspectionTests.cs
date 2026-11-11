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
            var letterF = Assert.Single(characters, item => item.CharacterIndex == 0 && item.Text == "F");

            Assert.True(characters.Count >= 10);
            Assert.True(letterF.Right > letterF.Left);
            Assert.True(letterF.Top > letterF.Bottom);
            Assert.InRange(letterF.FontSize, 15.5, 16.5);

            var following = Assert.Single(characters, item => item.CharacterIndex == 1 && item.Text == "u");
            Assert.True(following.Left >= letterF.Left);
            Assert.True(following.CharacterIndex > letterF.CharacterIndex);

            var hit = PdfiumTextHitTest.FindCharacterAtPoint(
                characters,
                pageIndex: 0,
                x: (letterF.Left + letterF.Right) / 2,
                y: (letterF.Bottom + letterF.Top) / 2);

            Assert.NotNull(hit);
            Assert.Equal(0, hit.CharacterIndex);

            var miss = PdfiumTextHitTest.FindCharacterAtPoint(
                characters, pageIndex: 0, x: 10, y: 10);

            Assert.Null(miss);

            var secondLine = new TextFragment("Outra linha")
            {
                Position = new Position(72, 670)
            };
            secondLine.TextState.FontName = "Helvetica";
            secondLine.TextState.FontSize = 16;
            using (var source = new Document())
            {
                var page = source.Pages.Add();
                var firstLine = new TextFragment("Future PDF 123")
                {
                    Position = new Position(72, 700)
                };
                firstLine.TextState.FontName = "Helvetica";
                firstLine.TextState.FontSize = 16;
                new TextBuilder(page).AppendText(firstLine);
                new TextBuilder(page).AppendText(secondLine);
                source.Save(path);
            }

            using var multilinePdf = new PdfiumDocument(path);
            var multilineCharacters = multilinePdf.InspectPageCharacters(0);
            var lines = PdfiumTextGrouping.GroupIntoLines(multilineCharacters, pageIndex: 0);

            Assert.Equal(2, lines.Count);
            Assert.Contains(lines, line => line.Words.Select(word => word.Text)
                .SequenceEqual(new[] { "Future", "PDF", "123" }));
            Assert.Contains(lines, line => line.Words.Select(word => word.Text)
                .SequenceEqual(new[] { "Outra", "linha" }));

            var selectedWord = PdfiumTextHitTest.FindWordAtPoint(
                lines,
                pageIndex: 0,
                x: (letterF.Left + letterF.Right) / 2,
                y: (letterF.Bottom + letterF.Top) / 2);

            Assert.NotNull(selectedWord);
            Assert.Equal("Future", selectedWord.Text);

            var selectedLine = PdfiumTextHitTest.FindLineAtPoint(
                lines,
                pageIndex: 0,
                x: (letterF.Left + letterF.Right) / 2,
                y: (letterF.Bottom + letterF.Top) / 2);

            Assert.NotNull(selectedLine);
            Assert.Contains("Future", selectedLine.Text);
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
