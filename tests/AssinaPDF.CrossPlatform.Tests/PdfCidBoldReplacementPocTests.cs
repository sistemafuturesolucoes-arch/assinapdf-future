using System;
using System.IO;
using System.Linq;
using Aspose.Pdf;
using Aspose.Pdf.Facades;
using Aspose.Pdf.Text;
using Xunit;
using Xunit.Abstractions;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfCidBoldReplacementPocTests
{
    private readonly ITestOutputHelper _output;

    public PdfCidBoldReplacementPocTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ReplaceTextWithExplicitBoldFont_PersistsAfterSaveAndReopen()
    {
        // Opt-in: define ASSINAPDF_CID_TEST_PDF to run against the real sample PDF.
        var inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("POC ignorada: defina ASSINAPDF_CID_TEST_PDF com o caminho do PDF de teste.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF de teste não encontrado: {inputPath}");

        const string targetText = "SóCheque";
        var outputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(
                Path.GetDirectoryName(inputPath)!,
                Path.GetFileNameWithoutExtension(inputPath) + ".bold-poc.pdf");
        }

        Assert.NotEqual(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath));

        int pageNumber;
        float fontSize;
        Aspose.Pdf.Color? foregroundColor;

        using (var sourceDocument = new Document(inputPath))
        {
            var absorber = new TextFragmentAbsorber(targetText);
            sourceDocument.Pages.Accept(absorber);

            var sourceFragment = absorber.TextFragments.Cast<TextFragment>().FirstOrDefault();
            Assert.NotNull(sourceFragment);

            pageNumber = sourceFragment!.Page.Number;
            fontSize = sourceFragment.TextState.FontSize;
            foregroundColor = sourceFragment.TextState.ForegroundColor;

            _output.WriteLine(
                $"ANTES: página={pageNumber}; texto='{sourceFragment.Text}'; " +
                $"fonte='{sourceFragment.TextState.Font?.FontName}'; " +
                $"base='{sourceFragment.TextState.Font?.BaseFont}'; " +
                $"bold={sourceFragment.TextState.IsBold}; tamanho={fontSize}");

            var boldFont = FontRepository.FindFont("Arial", FontStyles.Bold);
            Assert.NotNull(boldFont);
            boldFont.IsEmbedded = true;

            var replacementState = new TextState
            {
                Font = boldFont,
                FontSize = fontSize,
                ForegroundColor = foregroundColor,
                FontStyle = FontStyles.Bold,
                IsBold = true
            };

            using var editor = new PdfContentEditor();
            editor.BindPdf(sourceDocument);

            var replaced = editor.ReplaceText(
                targetText,
                pageNumber,
                targetText,
                replacementState);

            Assert.True(replaced, "PdfContentEditor não encontrou/substituiu o texto-alvo.");
            sourceDocument.Save(outputPath);
        }

        Assert.True(File.Exists(outputPath), $"O PDF de saída não foi criado: {outputPath}");

        using (var savedDocument = new Document(outputPath))
        {
            var savedAbsorber = new TextFragmentAbsorber(targetText);
            savedDocument.Pages.Accept(savedAbsorber);

            var savedFragment = savedAbsorber.TextFragments.Cast<TextFragment>().FirstOrDefault();
            Assert.NotNull(savedFragment);

            var savedFont = savedFragment!.TextState.Font;
            _output.WriteLine(
                $"DEPOIS: arquivo='{outputPath}'; página={savedFragment.Page.Number}; " +
                $"texto='{savedFragment.Text}'; fonte='{savedFont?.FontName}'; " +
                $"base='{savedFont?.BaseFont}'; boldState={savedFragment.TextState.IsBold}; " +
                $"boldFont={savedFont?.IsBold}; embedded={savedFont?.IsEmbedded}; " +
                $"tamanho={savedFragment.TextState.FontSize}");

            Assert.Equal(targetText, savedFragment.Text);
            Assert.True(
                savedFragment.TextState.IsBold || savedFont?.IsBold == true,
                "O PDF foi salvo/reaberto, mas o fragmento não foi reconhecido como negrito.");
        }

        _output.WriteLine("POC aprovada: o negrito persistiu após salvar e reabrir o PDF.");
    }
}
