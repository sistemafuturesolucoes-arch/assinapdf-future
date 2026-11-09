using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Aspose.Pdf;
using Aspose.Pdf.Operators;
using Xunit;
using Xunit.Abstractions;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfCidSplitBoldPocTests
{
    private readonly ITestOutputHelper _output;

    public PdfCidSplitBoldPocTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SplitExactTargetFromTjOperatorAndPersistBoldFont()
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

        // CIDs extraídos do ToUnicode CMap do PDF de teste; não é um mapa genérico.
        var unicodeToCid = new Dictionary<char, ushort>
        {
            ['I'] = 0x002C, ['n'] = 0x0051, ['f'] = 0x0049, ['o'] = 0x0052,
            ['r'] = 0x0055, ['m'] = 0x0050, ['a'] = 0x0044, ['ç'] = 0x006F,
            ['õ'] = 0x007D, ['e'] = 0x0048, ['s'] = 0x0056, [' '] = 0x0003,
            ['d'] = 0x0047, ['C'] = 0x0026, ['h'] = 0x004B, ['q'] = 0x0054,
            ['u'] = 0x0058, ['S'] = 0x0036, ['F'] = 0x0029, ['B'] = 0x0025,
            ['c'] = 0x0046, ['t'] = 0x0057, ['l'] = 0x004F
        };

        var encodedTarget = new StringBuilder(targetText.Length * 2);
        foreach (var character in targetText)
        {
            Assert.True(unicodeToCid.TryGetValue(character, out var cid),
                $"CID desconhecido para U+{(int)character:X4}.");
            encodedTarget.Append((char)(cid >> 8));
            encodedTarget.Append((char)(cid & 0xFF));
        }

        var outputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_OPERATOR_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
            outputPath = inputPath + ".cid-bold-split-poc.pdf";
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        using (var document = new Document(inputPath))
        {
            var absorber = new Aspose.Pdf.Text.TextFragmentAbsorber(targetText);
            document.Pages.Accept(absorber);
            var fragment = absorber.TextFragments.Cast<Aspose.Pdf.Text.TextFragment>().Single();
            var pageNumber = checked((int)fragment.Page.Number);
            var page = document.Pages[pageNumber];
            var contents = page.Contents;

            string? activeFont = null;
            var activeSize = 0d;
            var matchFontSize = 0d;
            var matchIndex = -1;
            SetGlyphsPositionShowText? matchOperator = null;
            var matchOffset = -1;

            for (var index = 1; index <= contents.Count; index++)
            {
                var op = contents[index];
                if (op is SelectFont selectFont)
                {
                    activeFont = selectFont.FontName;
                    activeSize = selectFont.Size;
                    continue;
                }

                if (activeFont != regularResource || op is not SetGlyphsPositionShowText tj)
                    continue;

                var offset = (tj.Text ?? string.Empty).IndexOf(encodedTarget.ToString(), StringComparison.Ordinal);
                if (offset >= 0)
                {
                    Assert.Equal(-1, matchIndex);
                    matchIndex = index;
                    matchOperator = tj;
                    matchOffset = offset;
                    matchFontSize = activeSize;
                }
            }

            Assert.NotNull(matchOperator);
            Assert.Equal(0, matchOffset);
            Assert.True(matchFontSize > 0, "Não foi possível identificar o tamanho da fonte F2 no operador-alvo.");

            var glyphs = matchOperator!.GlyphPositions.ToArray();
            var joinedGlyphText = string.Concat(glyphs.Select(glyph => glyph.Text));
            Assert.Equal(matchOperator.Text, joinedGlyphText);
            Assert.StartsWith(encodedTarget.ToString(), joinedGlyphText, StringComparison.Ordinal);
            Assert.Equal(targetText.Length, encodedTarget.Length / 2);

            var targetGlyphCount = targetText.Length;
            Assert.True(glyphs.Length > targetGlyphCount,
                "O operador não contém texto posterior ao trecho-alvo; este teste esperava uma cauda a preservar.");

            var targetGlyphs = glyphs.Take(targetGlyphCount).Select(CloneGlyph).ToArray();
            var tailGlyphs = glyphs.Skip(targetGlyphCount).Select(CloneGlyph).ToArray();
            Assert.Equal(encodedTarget.ToString(), string.Concat(targetGlyphs.Select(glyph => glyph.Text)));

            _output.WriteLine($"MATCH page={pageNumber}; operator={matchIndex}; activeFont={regularResource}; fontSize={matchFontSize:F4}");
            _output.WriteLine($"GLYPHS total={glyphs.Length}; target={targetGlyphs.Length}; tail={tailGlyphs.Length}");
            _output.WriteLine($"TARGET_CID_UNITS={string.Concat(targetGlyphs.Select(glyph => glyph.Text)).Length}; " +
                $"TAIL_CID_UNITS={string.Concat(tailGlyphs.Select(glyph => glyph.Text)).Length}");
            _output.WriteLine($"OUTPUT={outputPath}");

            contents.Delete(matchIndex);
            contents.Insert(matchIndex, new Operator[]
            {
                new SelectFont(boldResource, matchFontSize),
                new SetGlyphsPositionShowText(targetGlyphs),
                new SelectFont(regularResource, matchFontSize),
                new SetGlyphsPositionShowText(tailGlyphs)
            });

            document.Save(outputPath);
        }

        using var reopened = new Document(outputPath);
        var verifyAbsorber = new Aspose.Pdf.Text.TextFragmentAbsorber(targetText);
        reopened.Pages.Accept(verifyAbsorber);
        var verifiedFragments = verifyAbsorber.TextFragments.Cast<Aspose.Pdf.Text.TextFragment>().ToArray();
        Assert.Single(verifiedFragments);

        var verified = verifiedFragments[0];
        var verifiedSegments = verified.Segments.Cast<Aspose.Pdf.Text.TextSegment>().ToArray();
        var verifiedText = string.Concat(verifiedSegments.Select(segment => segment.Text));
        var resources = verifiedSegments
            .Select(segment => segment.TextState.Font?.ResourceName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToArray();

        _output.WriteLine($"REOPEN_TEXT='{verifiedText}'");
        _output.WriteLine($"REOPEN_SEGMENTS={verifiedSegments.Length}; RESOURCES={string.Join(",", resources)}");

        Assert.Equal(targetText, verifiedText);
        Assert.Contains(boldResource, resources);
        Assert.DoesNotContain(regularResource, resources);
        _output.WriteLine("RESULTADO: texto preservado após salvar/reabrir e associado somente à fonte F1. Verificação visual e de avanço ainda necessária.");
    }

    private static GlyphPosition CloneGlyph(GlyphPosition glyph) =>
        glyph.HasPosition
            ? new GlyphPosition(glyph.Text, glyph.Position)
            : new GlyphPosition(glyph.Text);
}
