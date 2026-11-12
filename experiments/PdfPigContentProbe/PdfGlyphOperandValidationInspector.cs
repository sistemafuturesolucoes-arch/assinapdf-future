using System.Reflection;
using UglyToad.PdfPig;

internal static class PdfGlyphOperandValidationInspector
{
    public static void Run(PdfDocument document)
    {
        Console.WriteLine("VALIDAÇÃO DE OPERANDOS Tj CONTRA ToUnicode (somente leitura)");

        int checkedOperands = 0;
        int exact = 0;
        int noLetters = 0;
        int emptyBytes = 0;
        int mixedFonts = 0;
        int noCMap = 0;
        int mismatchOrAmbiguous = 0;
        int mappedCodes = 0;
        int singleScalarCodes = 0;
        int multiScalarCodes = 0;
        int shown = 0;

        for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var lettersBySequence = page.Letters
                .GroupBy(letter => letter.TextSequence)
                .ToDictionary(group => group.Key, group => group.ToArray());

            int sequence = 0;
            foreach (var operation in page.Operations)
            {
                if (operation.Operator is not ("Tj" or "TJ")) continue;
                sequence++;

                if (operation.Operator != "Tj") continue;
                checkedOperands++;

                if (!lettersBySequence.TryGetValue(sequence, out var letters) || letters.Length == 0)
                {
                    noLetters++;
                    continue;
                }

                var byteProperty = operation.GetType().GetProperty(
                    "Bytes", BindingFlags.Public | BindingFlags.Instance);
                if (byteProperty?.GetValue(operation) is not ReadOnlyMemory<byte> memory)
                {
                    emptyBytes++;
                    continue;
                }

                if (memory.Length == 0)
                {
                    emptyBytes++;
                    continue;
                }

                string[] fonts = letters.Select(x => x.FontName ?? "(fonte desconhecida)")
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (fonts.Length != 1)
                {
                    mixedFonts++;
                    continue;
                }

                if (!PdfFontReflectionInspector.TryGetToUnicodeCMap(
                    document, letters[0], out CMapParseResult cmap))
                {
                    noCMap++;
                    continue;
                }

                string expectedUnicode = string.Concat(letters.Select(x => x.Value));
                if (!PdfGlyphByteMapper.TryMapOperand(
                    memory.Span, cmap, expectedUnicode,
                    out var spans, out string reason))
                {
                    mismatchOrAmbiguous++;
                    if (shown < 12)
                    {
                        Console.WriteLine(
                            $"  NÃO VALIDADO pág={pageNumber}; seq={sequence}; fonte={fonts[0]}; " +
                            $"bytes={Convert.ToHexString(memory.Span)}; texto='{expectedUnicode}'; motivo={reason}");
                        shown++;
                    }
                    continue;
                }

                exact++;
                mappedCodes += spans.Count;
                singleScalarCodes += spans.Count(x => x.IsSingleUnicodeScalar);
                multiScalarCodes += spans.Count(x => !x.IsSingleUnicodeScalar);

                if (shown < 12)
                {
                    string byteMap = string.Join(", ", spans.Select(x =>
                        $"{x.CodeHex}@{x.ByteOffset}+{x.ByteLength}=>'{x.Unicode}'"));
                    Console.WriteLine(
                        $"  EXATO pág={pageNumber}; seq={sequence}; fonte={fonts[0]}; " +
                        $"texto='{expectedUnicode}'; códigos={byteMap}");
                    shown++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  Operandos Tj examinados: {checkedOperands}");
        Console.WriteLine($"  Mapeamento exato: {exact}");
        Console.WriteLine($"  Sem letras associadas: {noLetters}");
        Console.WriteLine($"  Bytes ausentes/vazios: {emptyBytes}");
        Console.WriteLine($"  Mais de uma fonte na sequência: {mixedFonts}");
        Console.WriteLine($"  Fonte sem ToUnicode utilizável: {noCMap}");
        Console.WriteLine($"  Divergente/ambíguo: {mismatchOrAmbiguous}");
        Console.WriteLine($"  Códigos PDF mapeados: {mappedCodes}");
        Console.WriteLine($"  Códigos -> um escalar Unicode: {singleScalarCodes}");
        Console.WriteLine($"  Códigos -> múltiplos escalares Unicode: {multiScalarCodes}");
        Console.WriteLine("  Limite: usa Bytes da operação PdfPig e compara com ToUnicode/texto extraído; " +
                          "não prova offsets no stream original, geometria por glifo, avanço tipográfico ou edição.");
        Console.WriteLine();
    }
}
