using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aspose.Pdf;
using Aspose.Pdf.Operators;
using Aspose.Pdf.Text;
using Xunit;
using Xunit.Abstractions;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfCidOperatorFontSwapPocTests
{
    private readonly ITestOutputHelper _output;

    public PdfCidOperatorFontSwapPocTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReplaceFontResourceOnlyForExactTargetTextOperators_PersistsAfterSaveAndReopen()
    {
        var inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("POC ignorada: defina ASSINAPDF_CID_TEST_PDF.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF não encontrado: {inputPath}");
        const string targetText = "Informações de Cheques Sem Fundos do Banco Central";
        const string regularResource = "F2";
        const string boldResource = "F1";

        var outputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_OPERATOR_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
            outputPath = Path.Combine(Path.GetDirectoryName(inputPath)!,
                Path.GetFileNameWithoutExtension(inputPath) + ".cid-operator-font-swap-poc.pdf");
        Assert.NotEqual(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath));

        List<string> expectedSegmentTexts;
        int targetPageNumber;
        using (var document = new Document(inputPath))
        {
            var absorber = new TextFragmentAbsorber(targetText);
            document.Pages.Accept(absorber);
            var fragment = absorber.TextFragments.Cast<TextFragment>().Single();
            targetPageNumber = checked((int)fragment.Page.Number);
            expectedSegmentTexts = fragment.Segments.Cast<TextSegment>()
                .Select(segment => segment.Text ?? string.Empty)
                .Where(text => text.Length > 0)
                .ToList();

            Assert.NotEmpty(expectedSegmentTexts);
            _output.WriteLine($"TARGET page={targetPageNumber}; text='{fragment.Text}'; segments={expectedSegmentTexts.Count}");
            foreach (var segment in fragment.Segments.Cast<TextSegment>())
                _output.WriteLine($"SEGMENT text='{segment.Text}'; resource={segment.TextState.Font?.ResourceName}; font={segment.TextState.Font?.FontName}");

            var page = document.Pages[targetPageNumber];
            var contents = page.Contents;
            string? activeFont = null;
            double activeSize = 0;
            var matches = new List<(int Index, string Text, string Resource, double Size)>();

            for (int index = 1; index <= contents.Count; index++)
            {
                var op = contents[index];
                if (op is SelectFont selectFont)
                {
                    activeFont = selectFont.FontName;
                    activeSize = selectFont.Size;
                    continue;
                }

                if (op is TextShowOperator showText)
                {
                    string shown = showText.Text ?? string.Empty;
                    _output.WriteLine($"OP[{index:D4}] FONT={activeFont}/{activeSize}; TYPE={op.GetType().Name}; TEXT='{shown}'");
                    if (activeFont == regularResource &&
                        expectedSegmentTexts.Contains(shown, StringComparer.Ordinal))
                        matches.Add((index, shown, activeFont, activeSize));
                }
            }

            var matchedTexts = matches.Select(match => match.Text).ToList();
            Assert.Equal(expectedSegmentTexts.OrderBy(text => text, StringComparer.Ordinal),
                matchedTexts.OrderBy(text => text, StringComparer.Ordinal));
            Assert.NotEmpty(matches);

            // Work backwards so inserted operators do not invalidate earlier 1-based indexes.
            foreach (var match in matches.OrderByDescending(match => match.Index))
            {
                contents.Insert(match.Index, new SelectFont(boldResource, match.Size));
                contents.Insert(match.Index + 2, new SelectFont(regularResource, match.Size));
            }

            document.Save(outputPath);
        }

        Assert.True(File.Exists(outputPath), "O PDF de saída não foi criado.");
        using (var saved = new Document(outputPath))
        {
            var verify = new TextFragmentAbsorber(targetText);
            saved.Pages.Accept(verify);
            var fragment = verify.TextFragments.Cast<TextFragment>().Single();
            Assert.Equal(targetText, fragment.Text);

            var savedSegments = fragment.Segments.Cast<TextSegment>().ToArray();
            Assert.NotEmpty(savedSegments);
            foreach (var segment in savedSegments)
            {
                var weight = ReadTrueTypeWeight(segment.TextState.Font);
                _output.WriteLine($"SAVED text='{segment.Text}'; resource={segment.TextState.Font?.ResourceName}; font={segment.TextState.Font?.FontName}; weight={weight?.ToString() ?? "n/a"}");
                Assert.Equal(boldResource, segment.TextState.Font?.ResourceName);
                Assert.True(weight >= 600, $"O segmento '{segment.Text}' não permaneceu em negrito. Peso={weight?.ToString() ?? "n/a"}");
            }
        }

        _output.WriteLine($"POC aprovada: somente os operadores do texto alvo foram trocados para {boldResource}. Arquivo: {outputPath}");
    }

    private static ushort? ReadTrueTypeWeight(Font? font)
    {
        if (font == null) return null;
        using var stream = new MemoryStream();
        font.Save(stream);
        var bytes = stream.ToArray();
        if (bytes.Length < 12) return null;
        int count = ReadU16(bytes, 4);
        for (int i = 0; i < count; i++)
        {
            int record = 12 + i * 16;
            if (record + 16 > bytes.Length) return null;
            if (System.Text.Encoding.ASCII.GetString(bytes, record, 4) != "OS/2") continue;
            uint offset = ReadU32(bytes, record + 8);
            uint length = ReadU32(bytes, record + 12);
            if (length < 6 || offset + 6 > bytes.Length) return null;
            return ReadU16(bytes, checked((int)offset + 4));
        }
        return null;
    }

    private static ushort ReadU16(byte[] bytes, int offset) =>
        checked((ushort)((bytes[offset] << 8) | bytes[offset + 1]));

    private static uint ReadU32(byte[] bytes, int offset) =>
        ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
        ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
}
