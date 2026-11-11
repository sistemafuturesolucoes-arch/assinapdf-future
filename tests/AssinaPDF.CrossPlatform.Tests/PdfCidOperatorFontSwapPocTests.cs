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

public sealed class PdfCidOperatorFontSwapPocTests
{
    private readonly ITestOutputHelper _output;

    public PdfCidOperatorFontSwapPocTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void LocateExactTargetCidSequenceInsideF2TextOperators()
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

        // CIDs extraídos do ToUnicode CMap do F2 deste PDF de teste.
        // Não usar este mapa como regra genérica para outros documentos/fontes.
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
                $"Não há CID conhecido para o caractere U+{(int)character:X4} '{character}'.");
            encodedTarget.Append((char)(cid >> 8));
            encodedTarget.Append((char)(cid & 0xFF));
        }

        using var document = new Document(inputPath);
        var absorber = new Aspose.Pdf.Text.TextFragmentAbsorber(targetText);
        document.Pages.Accept(absorber);
        var fragment = absorber.TextFragments.Cast<Aspose.Pdf.Text.TextFragment>().Single();
        var pageNumber = checked((int)fragment.Page.Number);
        var page = document.Pages[pageNumber];

        _output.WriteLine($"TARGET page={pageNumber}; chars={targetText.Length}; encodedUnits={encodedTarget.Length}");
        _output.WriteLine($"SEGMENT resource={fragment.Segments.Cast<Aspose.Pdf.Text.TextSegment>().First().TextState.Font?.ResourceName}; text='{fragment.Text}'");

        var candidates = new List<(int Index, int Offset, int Length, string Preview)>();
        string? activeFont = null;
        var operatorCount = 0;
        var exactMatches = 0;

        for (var index = 1; index <= page.Contents.Count; index++)
        {
            var op = page.Contents[index];
            if (op is SelectFont selectFont)
            {
                activeFont = selectFont.FontName;
                continue;
            }

            if (op is not TextShowOperator showText || activeFont != regularResource)
                continue;

            operatorCount++;
            var shown = showText.Text ?? string.Empty;
            var matchAt = shown.IndexOf(encodedTarget.ToString(), StringComparison.Ordinal);
            if (matchAt >= 0)
            {
                exactMatches++;
                candidates.Add((index, matchAt, encodedTarget.Length, "EXACT"));
                continue;
            }

            var bestLength = 0;
            var bestTargetOffset = 0;
            var bestOperatorOffset = 0;
            for (var targetOffset = 0; targetOffset < encodedTarget.Length; targetOffset += 2)
            {
                for (var operatorOffset = 0; operatorOffset < shown.Length; operatorOffset += 2)
                {
                    var length = 0;
                    while (targetOffset + length + 1 < encodedTarget.Length &&
                           operatorOffset + length + 1 < shown.Length &&
                           encodedTarget[targetOffset + length] == shown[operatorOffset + length] &&
                           encodedTarget[targetOffset + length + 1] == shown[operatorOffset + length + 1])
                        length += 2;

                    if (length > bestLength)
                    {
                        bestLength = length;
                        bestTargetOffset = targetOffset;
                        bestOperatorOffset = operatorOffset;
                    }
                }
            }

            if (bestLength >= 4)
            {
                var preview = targetText.Substring(bestTargetOffset / 2,
                    Math.Min(bestLength / 2, targetText.Length - bestTargetOffset / 2));
                candidates.Add((index, bestOperatorOffset, bestLength, preview));
            }
        }

        _output.WriteLine($"F2_TEXT_OPERATORS={operatorCount}; EXACT_OPERATOR_MATCHES={exactMatches}");
        foreach (var candidate in candidates
                     .OrderByDescending(item => item.Length)
                     .ThenBy(item => item.Index)
                     .Take(8))
        {
            _output.WriteLine($"CANDIDATE OP[{candidate.Index:D4}] offsetUnits={candidate.Offset}; " +
                $"matchedChars={candidate.Length / 2}; targetPart='{candidate.Preview}'");
        }

        if (exactMatches == 0)
            _output.WriteLine("RESULTADO: a sequência completa não está em um único operador; os candidatos acima mostram os maiores trechos CID encontrados.");
        else
            _output.WriteLine("RESULTADO: sequência CID completa localizada em operador(es); nenhuma alteração foi feita no PDF.");

        Assert.True(candidates.Count > 0,
            "Nenhum trecho CID do alvo foi localizado nos operadores F2. Verifique o ToUnicode/CID map usado nesta POC.");
    }
}
