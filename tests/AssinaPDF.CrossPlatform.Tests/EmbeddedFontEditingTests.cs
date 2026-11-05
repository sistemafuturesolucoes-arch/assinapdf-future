using Aspose.Pdf;
using Aspose.Pdf.Text;
using AssinaPDF.MacPrototype;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class EmbeddedFontEditingTests
{
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
