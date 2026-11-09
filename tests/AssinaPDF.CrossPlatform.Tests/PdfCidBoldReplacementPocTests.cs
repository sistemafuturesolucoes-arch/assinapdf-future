using System;
using System.IO;
using System.Linq;
using System.Text;
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
    public void ReplaceRegularCidTextWithExplicitBoldFont_EmbedsBoldProgramAfterSaveAndReopen()
    {
        // Opt-in: define ASSINAPDF_CID_TEST_PDF to run against the real sample PDF.
        var inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("POC ignorada: defina ASSINAPDF_CID_TEST_PDF com o caminho do PDF de teste.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF de teste não encontrado: {inputPath}");

        // This phrase is regular Arial in the supplied sample PDF. "SóCheque"
        // is already Arial Bold there, so it is not a valid target for this test.
        const string targetText = "Informações de Cheques Sem Fundos do Banco Central";
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

            ushort? sourceWeight = ReadEmbeddedTrueTypeWeight(sourceFragment.TextState.Font);
            _output.WriteLine(
                $"ANTES: página={pageNumber}; texto='{sourceFragment.Text}'; " +
                $"fonte='{sourceFragment.TextState.Font?.FontName}'; " +
                $"recurso='{sourceFragment.TextState.Font?.ResourceName}'; " +
                $"IsBold={sourceFragment.TextState.IsBold}; peso TTF OS/2={sourceWeight?.ToString() ?? "indisponível"}");

            Assert.True(
                sourceWeight is >= 350 and <= 500,
                $"O trecho de teste não parece regular. Peso TTF encontrado: {sourceWeight?.ToString() ?? "indisponível"}.");

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
            ushort? savedWeight = ReadEmbeddedTrueTypeWeight(savedFont);

            _output.WriteLine(
                $"DEPOIS: arquivo='{outputPath}'; página={savedFragment.Page.Number}; " +
                $"texto='{savedFragment.Text}'; fonte='{savedFont?.FontName}'; " +
                $"recurso='{savedFont?.ResourceName}'; IsBold={savedFragment.TextState.IsBold}; " +
                $"Font.IsBold={savedFont?.IsBold}; embedded={savedFont?.IsEmbedded}; " +
                $"peso TTF OS/2={savedWeight?.ToString() ?? "indisponível"}; " +
                $"tamanho={savedFragment.TextState.FontSize}");

            Assert.Equal(targetText, savedFragment.Text);
            Assert.True(
                savedWeight >= 600,
                $"O PDF foi salvo/reaberto, mas a fonte incorporada não é negrito. Peso TTF OS/2: {savedWeight?.ToString() ?? "indisponível"}.");
        }

        _output.WriteLine("POC aprovada: o programa de fonte Bold permaneceu incorporado após salvar e reabrir o PDF.");
    }

    private static ushort? ReadEmbeddedTrueTypeWeight(Font? font)
    {
        if (font == null)
            return null;

        using var stream = new MemoryStream();
        font.Save(stream);
        byte[] data = stream.ToArray();

        // TrueType/OpenType sfnt header: 12 bytes, followed by 16-byte table records.
        if (data.Length < 12)
            return null;

        int tableCount = ReadUInt16BigEndian(data, 4);
        for (int index = 0; index < tableCount; index++)
        {
            int recordOffset = 12 + (index * 16);
            if (recordOffset + 16 > data.Length)
                return null;

            string tag = Encoding.ASCII.GetString(data, recordOffset, 4);
            if (!string.Equals(tag, "OS/2", StringComparison.Ordinal))
                continue;

            uint tableOffset = ReadUInt32BigEndian(data, recordOffset + 8);
            uint tableLength = ReadUInt32BigEndian(data, recordOffset + 12);
            if (tableLength < 6 || tableOffset + 6 > data.Length)
                return null;

            // OS/2: version (2), xAvgCharWidth (2), usWeightClass (2).
            return ReadUInt16BigEndian(data, checked((int)tableOffset + 4));
        }

        return null;
    }

    private static ushort ReadUInt16BigEndian(byte[] data, int offset) =>
        checked((ushort)((data[offset] << 8) | data[offset + 1]));

    private static uint ReadUInt32BigEndian(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];
}
