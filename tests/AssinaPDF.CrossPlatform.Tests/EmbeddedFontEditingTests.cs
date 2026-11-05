using Aspose.Pdf;
using Aspose.Pdf.Text;
using AssinaPDF.MacPrototype;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class EmbeddedFontEditingTests
{
    [Fact]
    public void ApplyRichTextFormatting_PersistsBoldAndLargerFontAfterReopeningPdf()
    {
        using var document = new Document();
        var page = document.Pages.Add();
        var fragment = new TextFragment("Texto em negrito maior")
        {
            Position = new Position(50, 700)
        };
        fragment.TextState.Font = FontRepository.FindFont("Helvetica");
        fragment.TextState.FontSize = 11;
        new TextBuilder(page).AppendText(fragment);

        EditorWindow.ApplyRichTextFormatting(
            fragment,
            new[]
            {
                new EditorWindow.EditedTextRun(
                    0,
                    fragment.Text.Length,
                    "Helvetica",
                    18,
                    true,
                    false,
                    null,
                    null)
            },
            "Helvetica",
            originalFontEmbedded: false);

        using var output = new MemoryStream();
        document.Save(output);
        Assert.True(output.Length > 0);
        output.Position = 0;

        using var reopened = Document.Open(output);
        var absorber = new TextFragmentAbsorber("Texto em negrito maior");
        reopened.Pages[1].Accept(absorber);
        var saved = Assert.Single(absorber.TextFragments.Cast<TextFragment>());

        Assert.True(saved.TextState.IsBold,
            $"Helvetica bold não persistiu. IsBold={saved.TextState.IsBold}; " +
            $"IsItalic={saved.TextState.IsItalic}; FontStyle={saved.TextState.FontStyle}; " +
            $"FontName='{saved.TextState.FontName}'; Font.FontName='{saved.TextState.Font?.FontName}'; " +
            $"BaseFont='{saved.TextState.Font?.BaseFont}'; DecodedFontName='{saved.TextState.Font?.DecodedFontName}'; " +
            $"Font.IsBold={saved.TextState.Font?.IsBold}; Font.IsItalic={saved.TextState.Font?.IsItalic}; " +
            $"Subtype='{saved.TextState.Font?.Subtype}'; IsEmbedded={saved.TextState.Font?.IsEmbedded}");
        Assert.Equal(18, saved.TextState.FontSize, precision: 1);
    }

    [Fact]
    public void ApplyRichTextFormatting_UsesExplicitHelveticaBoldFaceWhenSourceFontIsNotEmbedded()
    {
        using var document = new Document();
        var page = document.Pages.Add();
        var fragment = new TextFragment("CID fallback bold")
        {
            Position = new Position(50, 700)
        };
        fragment.TextState.Font = FontRepository.FindFont("Helvetica");
        fragment.TextState.FontSize = 11;
        new TextBuilder(page).AppendText(fragment);

        EditorWindow.ApplyRichTextFormatting(
            fragment,
            new[]
            {
                new EditorWindow.EditedTextRun(
                    0,
                    fragment.Text.Length,
                    "Helvetica",
                    18,
                    true,
                    false,
                    null,
                    null)
            },
            "Helvetica",
            originalFontEmbedded: false);

        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;

        using var reopened = Document.Open(output);
        var absorber = new TextFragmentAbsorber("CID fallback bold");
        reopened.Pages[1].Accept(absorber);
        var saved = Assert.Single(absorber.TextFragments.Cast<TextFragment>());

        Assert.True(saved.TextState.IsBold);
        Assert.Equal(18, saved.TextState.FontSize, precision: 1);
        Assert.Contains(
            "Helvetica-Bold",
            saved.TextState.FontName ?? saved.TextState.Font?.FontName ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(saved.TextState.Font?.IsBold ?? false);
    }

    [Fact]
    public void ApplyRichTextFormatting_PreservesOriginalFaceWhenEmbeddedFontIsUnchanged()
    {
        using var document = new Document();
        var page = document.Pages.Add();
        var fragment = new TextFragment("Texto incorporado")
        {
            Position = new Position(50, 700)
        };
        fragment.TextState.FontName = "Helvetica";
        fragment.TextState.FontSize = 11;
        new TextBuilder(page).AppendText(fragment);

        // Simula o caso em que o PDF informa uma fonte incorporada por um
        // identificador/subconjunto que não existe no catálogo do sistema.
        // O editor deve manter a face já associada ao PDF se o usuário não
        // solicitar mudança de família ou estilo.
        const string embeddedFamilyIdentifier = "SubsetFontInternalName";

        EditorWindow.ApplyRichTextFormatting(
            fragment,
            new[]
            {
                new EditorWindow.EditedTextRun(
                    0,
                    fragment.Text.Length,
                    embeddedFamilyIdentifier,
                    13,
                    false,
                    false,
                    null,
                    null)
            },
            embeddedFamilyIdentifier,
            originalFontEmbedded: true);

        Assert.Equal("Helvetica", fragment.TextState.FontName);
        Assert.Equal(13, fragment.TextState.FontSize);

        using var output = new MemoryStream();
        document.Save(output);
        Assert.True(output.Length > 0);
        output.Position = 0;

        using var reopened = Document.Open(output);
        var absorber = new TextFragmentAbsorber("Texto incorporado");
        reopened.Pages[1].Accept(absorber);
        var saved = Assert.Single(absorber.TextFragments.Cast<TextFragment>());
        Assert.Equal(13, saved.TextState.FontSize);
    }
}
