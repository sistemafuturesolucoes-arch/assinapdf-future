using System.Reflection;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

internal static class PdfRealStreamGlyphDryRunInspector
{
    private sealed record DecodedStream(StreamToken Token, string ObjectId, byte[] Bytes);
    private sealed record OperandRef(DecodedStream Stream, PdfContentStringOperand Operand);
    private sealed record TextOperation(int Sequence, int TjIndex, string Operator, Letter[] Letters, OperandRef? Source);

    public static void Run(PdfDocument document)
    {
        Console.WriteLine("SIMULAÇÃO INTEGRADA NO STREAM REAL (somente memória; sem salvar)");
        const string target = "CONFIRME";
        const string replacement = "BONFIRME";

        for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var streams = GetDecodedStreams(page, document);
            if (streams.Count == 0) continue;

            var operands = new List<OperandRef>();
            foreach (var stream in streams)
            {
                var scan = PdfContentStreamTokenScanner.Scan(stream.Bytes);
                if (!scan.IsComplete)
                {
                    Console.WriteLine($"  PÁGINA {pageNumber}: abortado; stream {stream.ObjectId}: {scan.Reason}");
                    return;
                }
                operands.AddRange(scan.TjOperands.Select(x => new OperandRef(stream, x)));
            }

            var lettersBySequence = page.Letters.GroupBy(x => x.TextSequence)
                .ToDictionary(x => x.Key, x => x.ToArray());
            var operations = new List<TextOperation>();
            int sequence = 0, tjIndex = 0;
            bool hasTjArray = false;
            foreach (var operation in page.Operations)
            {
                if (operation.Operator is not ("Tj" or "TJ")) continue;
                sequence++;
                if (operation.Operator == "TJ")
                {
                    hasTjArray = true;
                    operations.Add(new TextOperation(sequence, -1, "TJ",
                        lettersBySequence.TryGetValue(sequence, out var arrayLetters) ? arrayLetters : Array.Empty<Letter>(), null));
                    continue;
                }

                OperandRef? source = tjIndex < operands.Count ? operands[tjIndex] : null;
                operations.Add(new TextOperation(sequence, tjIndex, "Tj",
                    lettersBySequence.TryGetValue(sequence, out var opLetters) ? opLetters : Array.Empty<Letter>(), source));
                tjIndex++;
            }

            if (hasTjArray || tjIndex != operands.Count)
            {
                Console.WriteLine($"  PÁGINA {pageNumber}: abortado; TJ arrays={hasTjArray}; operações Tj={tjIndex}; operandos scanner={operands.Count}.");
                return;
            }

            for (int start = 0; start <= operations.Count - target.Length; start++)
            {
                var window = operations.Skip(start).Take(target.Length).ToArray();
                if (window.Any(x => x.Operator != "Tj" || x.Source is null || x.Letters.Length != 1) ||
                    window.Select((x, i) => x.Sequence == window[0].Sequence + i).Any(x => !x) ||
                    string.Concat(window.Select(x => x.Letters[0].Value)) != target)
                    continue;

                var first = window[0];
                string[] fonts = window.SelectMany(x => x.Letters).Select(x => x.FontName ?? "")
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (fonts.Length != 1)
                {
                    Console.WriteLine($"  PÁGINA {pageNumber}: alvo encontrado, mas possui fontes diferentes; abortado.");
                    return;
                }

                if (window.Any(x => !ReferenceEquals(x.Source!.Stream, first.Source!.Stream)))
                {
                    Console.WriteLine($"  PÁGINA {pageNumber}: alvo atravessa streams diferentes; abortado.");
                    return;
                }

                Letter selectedLetter = first.Letters[0];
                if (!PdfFontReflectionInspector.TryGetToUnicodeCMap(document, selectedLetter, out var cmap))
                {
                    Console.WriteLine($"  PÁGINA {pageNumber}: fonte sem ToUnicode utilizável; abortado.");
                    return;
                }

                var mapped = new List<(TextOperation Operation, IReadOnlyList<PdfGlyphByteSpan> Spans)>();
                foreach (var item in window)
                {
                    string expected = item.Letters[0].Value;
                    if (!PdfGlyphByteMapper.TryMapOperand(item.Source!.Operand.DecodedBytes, cmap,
                        expected, out var spans, out string mapReason))
                    {
                        Console.WriteLine($"  PÁGINA {pageNumber}: sequência {item.Sequence} não mapeada: {mapReason}");
                        return;
                    }
                    if (spans.Count != 1 || !spans[0].IsSingleUnicodeScalar)
                    {
                        Console.WriteLine($"  PÁGINA {pageNumber}: sequência {item.Sequence} não representa um único escalar/código; abortado.");
                        return;
                    }
                    mapped.Add((item, spans));
                }

                var selected = mapped[0].Spans[0];
                var replacementCodes = cmap.Mappings
                    .Where(x => string.Equals(x.Value, replacement[..1], StringComparison.Ordinal))
                    .Select(x => x.Key)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (replacementCodes.Length != 1)
                {
                    Console.WriteLine($"  PÁGINA {pageNumber}: código ToUnicode para '{replacement[..1]}' não é único (encontrados {replacementCodes.Length}); abortado.");
                    return;
                }

                byte[] replacementCode;
                try { replacementCode = Convert.FromHexString(replacementCodes[0]); }
                catch
                {
                    Console.WriteLine($"  PÁGINA {pageNumber}: código de substituição inválido no CMap; abortado.");
                    return;
                }

                var stream = first.Source!.Stream;
                byte[] rawToken = stream.Bytes.AsSpan(first.Source.Operand.TokenStart,
                    first.Source.Operand.TokenEnd - first.Source.Operand.TokenStart).ToArray();
                if (!PdfGlyphTokenRewritePlanner.TryPlanSingleScalarReplacement(
                    rawToken, cmap, selected, replacementCode, replacement[..1],
                    out var tokenPlan, out string tokenReason))
                {
                    Console.WriteLine($"  PÁGINA {pageNumber}: plano do token recusado: {tokenReason}");
                    return;
                }

                byte[] originalCode = Convert.FromHexString(selected.CodeHex);
                if (!PdfContentStreamRewritePlanner.TryPlanSingleTjReplacement(
                    stream.Bytes, first.Source.Operand.TokenStart, selected.ByteOffset,
                    originalCode, replacementCode, out var streamPlan, out string streamReason))
                {
                    Console.WriteLine($"  PÁGINA {pageNumber}: plano do stream recusado: {streamReason}");
                    return;
                }

                bool samePrefix = stream.Bytes.AsSpan(0, streamPlan.OriginalTokenOffset)
                    .SequenceEqual(streamPlan.RewrittenStream.AsSpan(0, streamPlan.OriginalTokenOffset));
                int oldSuffix = streamPlan.OriginalTokenOffset + streamPlan.OriginalTokenLength;
                int newSuffix = streamPlan.OriginalTokenOffset + streamPlan.RewrittenTokenLength;
                bool sameSuffix = stream.Bytes.AsSpan(oldSuffix)
                    .SequenceEqual(streamPlan.RewrittenStream.AsSpan(newSuffix));
                var afterScan = PdfContentStreamTokenScanner.Scan(streamPlan.RewrittenStream);
                bool scanValid = afterScan.IsComplete && afterScan.TjOperands.Count == operands.Count;

                Console.WriteLine($"  Página: {pageNumber}; sequência inicial: {first.Sequence}; fonte: {fonts[0]}");
                Console.WriteLine($"  Stream: objeto {stream.ObjectId}; bytes decodificados={stream.Bytes.Length}");
                Console.WriteLine($"  Texto simulado: {target} -> {replacement}");
                Console.WriteLine($"  Código selecionado: '{selected.Unicode}' <{selected.CodeHex}> @ byte {selected.ByteOffset}");
                Console.WriteLine($"  Código substituto: '{replacement[..1]}' <{replacementCodes[0]}>");
                Console.WriteLine($"  Token: {Convert.ToHexString(rawToken)} -> {Convert.ToHexString(tokenPlan.RewrittenToken)}");
                Console.WriteLine($"  Stream: reescrita em memória={(streamPlan.RewrittenStream.Length == stream.Bytes.Length ? "mesmo comprimento" : "comprimento diferente")}; prefixo preservado={samePrefix}; sufixo preservado={sameSuffix}; scanner após={scanValid}");
                Console.WriteLine($"  Validação: {tokenReason}; {streamReason}");
                Console.WriteLine("  IMPORTANTE: nenhum objeto PDF foi alterado; sem recompressão, /Length, xref ou gravação em arquivo.");
                return;
            }
        }

        Console.WriteLine($"  NÃO EXECUTADA: sequência '{target}' não encontrada como operações Tj contínuas, cada uma com uma Letter.");
    }

    private static List<DecodedStream> GetDecodedStreams(Page page, PdfDocument document)
    {
        var result = new List<DecodedStream>();
        if (!page.Dictionary.Data.TryGetValue("Contents", out var contents)) return result;
        AddStreams(contents, document, result);
        return result;
    }

    private static void AddStreams(IToken token, PdfDocument document, List<DecodedStream> output)
    {
        IToken resolved = token;
        string id = "direto";
        if (token is IndirectReferenceToken reference)
        {
            var obj = document.Structure.GetObject(reference.Data);
            id = obj.Number.ToString();
            resolved = obj.Data;
        }

        if (resolved is ArrayToken array)
        {
            foreach (var child in array.Data) AddStreams(child, document, output);
            return;
        }
        if (resolved is not StreamToken stream) return;

        var filters = document.Structure.FilterProvider.GetFilters(
            stream.StreamDictionary, document.Structure.TokenScanner);
        if (filters.Any(x => !x.IsSupported))
            throw new InvalidOperationException($"Filtro não suportado no stream {id}.");
        Memory<byte> decoded = stream.Data;
        for (int i = 0; i < filters.Count; i++)
            decoded = filters[i].Decode(decoded, stream.StreamDictionary,
                document.Structure.FilterProvider, i);
        output.Add(new DecodedStream(stream, id, decoded.ToArray()));
    }
}
