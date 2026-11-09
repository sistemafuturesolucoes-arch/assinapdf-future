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

public sealed class PdfCidOperatorStructurePocTests
{
    private readonly ITestOutputHelper _output;

    public PdfCidOperatorStructurePocTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void InspectExactTargetOperatorStructure()
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

        // CIDs extraídos do ToUnicode CMap do PDF de teste; mapa restrito a este documento.
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

        using var document = new Document(inputPath);
        var absorber = new Aspose.Pdf.Text.TextFragmentAbsorber(targetText);
        document.Pages.Accept(absorber);
        var fragment = absorber.TextFragments.Cast<Aspose.Pdf.Text.TextFragment>().Single();
        var page = document.Pages[checked((int)fragment.Page.Number)];

        string? activeFont = null;
        var matches = new List<(int Index, TextShowOperator Operator, int Offset)>();
        for (var index = 1; index <= page.Contents.Count; index++)
        {
            var op = page.Contents[index];
            if (op is SelectFont selectFont)
            {
                activeFont = selectFont.FontName;
                continue;
            }

            if (activeFont == regularResource && op is TextShowOperator showText)
            {
                var offset = (showText.Text ?? string.Empty).IndexOf(
                    encodedTarget.ToString(), StringComparison.Ordinal);
                if (offset >= 0)
                    matches.Add((index, showText, offset));
            }
        }

        Assert.Single(matches);
        var match = matches[0];
        _output.WriteLine($"TARGET_OPERATOR index={match.Index}; type={match.Operator.GetType().Name}; offset={match.Offset}; encodedUnits={encodedTarget.Length}");

        if (match.Operator is SetGlyphsPositionShowText tj)
        {
            var items = tj.Items ?? Array.Empty<object>();
            _output.WriteLine($"TJ_ITEMS={items.Length}; GLYPH_POSITIONS={tj.GlyphPositions.Count()}");
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i] is string text)
                {
                    var offset = text.IndexOf(encodedTarget.ToString(), StringComparison.Ordinal);
                    _output.WriteLine($"ITEM[{i}] STRING units={text.Length}; exactOffset={offset}; startsTarget={text.StartsWith(encodedTarget.ToString(), StringComparison.Ordinal)}");
                }
                else
                {
                    _output.WriteLine($"ITEM[{i}] {items[i]?.GetType().Name ?? "null"}={items[i]}");
                }
            }

            var glyphIndex = 0;
            foreach (var glyphPosition in tj.GlyphPositions)
            {
                if (glyphIndex++ >= 12) break;
                _output.WriteLine($"GLYPH[{glyphIndex - 1}] textUnits={glyphPosition.Text.Length}; hasPosition={glyphPosition.HasPosition}; position={glyphPosition.Position}");
            }
        }
        else
        {
            _output.WriteLine("TARGET_OPERATOR não é TJ; a divisão terá de usar a classe concreta do operador.");
        }
    }
}
