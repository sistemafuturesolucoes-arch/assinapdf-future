using System.Text;
using System.Reflection;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

internal static class OriginalContentOperandInspector
{
    private sealed record StringToken(int Start, int End, byte[] Bytes, string Kind, string RawHex = "", string ContextHex = "");
    private sealed record LocatedOperand(int Page, string ObjectId, int Offset, int Length, string Kind, string Hex, string RawTokenHex, string Context);

    public static void Run(PdfDocument document)
    {
        Console.WriteLine("LOCALIZAÇÃO DE OFFSETS DOS OPERANDOS Tj (somente leitura)");
        int totalFound = 0, totalParsed = 0, totalUnsupported = 0;
        int totalExact = 0, totalEmpty = 0, totalDifferent = 0, totalUnavailable = 0;
        int scannerCompleteStreams = 0, scannerRejectedStreams = 0;
        int scannerMatchedOperands = 0, scannerMismatchedStreams = 0;
        var samples = new List<LocatedOperand>();

        for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            int expected = page.Operations.Count(x => x.Operator == "Tj");
            int found = 0;
            var locatedForPage = new List<(StringToken Token, string ObjectId)>();
            if (!page.Dictionary.Data.TryGetValue("Contents", out var contents))
            {
                Console.WriteLine($"  Página {pageNumber}: /Contents ausente; PdfPig Tj={expected}; offsets=0");
                continue;
            }

            var streams = new List<(StreamToken Stream, string Id)>();
            AddStream(contents, document, streams);
            foreach (var (stream, id) in streams)
            {
                var filters = document.Structure.FilterProvider.GetFilters(stream.StreamDictionary, document.Structure.TokenScanner);
                if (filters.Any(x => !x.IsSupported)) { totalUnsupported++; continue; }
                Memory<byte> decoded = stream.Data;
                for (int i = 0; i < filters.Count; i++)
                    decoded = filters[i].Decode(decoded, stream.StreamDictionary, document.Structure.FilterProvider, i);

                var bytes = decoded.ToArray();
                var legacyOperands = FindTjOperands(bytes).ToArray();
                var scannerResult = PdfContentStreamTokenScanner.Scan(bytes);
                if (scannerResult.IsComplete)
                {
                    scannerCompleteStreams++;
                    int streamMismatches = 0;
                    if (scannerResult.TjOperands.Count != legacyOperands.Length)
                        streamMismatches++;

                    int comparable = Math.Min(scannerResult.TjOperands.Count, legacyOperands.Length);
                    for (int operandIndex = 0; operandIndex < comparable; operandIndex++)
                    {
                        var scanned = scannerResult.TjOperands[operandIndex];
                        var legacy = legacyOperands[operandIndex];
                        if (scanned.TokenStart != legacy.Start ||
                            scanned.TokenEnd != legacy.End ||
                            !string.Equals(scanned.Kind, legacy.Kind, StringComparison.Ordinal) ||
                            !scanned.DecodedBytes.AsSpan().SequenceEqual(legacy.Bytes))
                            streamMismatches++;
                    }

                    if (streamMismatches == 0)
                        scannerMatchedOperands += scannerResult.TjOperands.Count;
                    else
                    {
                        scannerMismatchedStreams++;
                        Console.WriteLine($"    SCANNER DIVERGENTE stream {id}: scanner={scannerResult.TjOperands.Count}; legado={legacyOperands.Length}; divergências={streamMismatches}. Offsets do scanner NÃO serão aceitos.");
                    }
                }
                else
                {
                    scannerRejectedStreams++;
                    Console.WriteLine($"    SCANNER INTERROMPIDO stream {id}: {scannerResult.Reason} Offsets do scanner NÃO serão aceitos.");
                }

                foreach (var item in legacyOperands)
                {
                    found++;
                    totalFound++;
                    locatedForPage.Add((item with { RawHex = Convert.ToHexString(bytes.AsSpan(item.Start, item.End - item.Start)), ContextHex = GetContextHex(bytes, item.Start, item.End) }, id));
                    if (samples.Count < 40)
                         samples.Add(new LocatedOperand(pageNumber, id, item.Start, item.Bytes.Length, item.Kind, Convert.ToHexString(item.Bytes), Convert.ToHexString(bytes.AsSpan(item.Start, item.End - item.Start)), GetContextHex(bytes, item.Start, item.End)));
                }
                Console.WriteLine($"    SCANNER stream {id}: completo={(scannerResult.IsComplete ? "SIM" : "NÃO")}; Tj scanner={scannerResult.TjOperands.Count}; Tj referência={legacyOperands.Length}; status={(scannerResult.IsComplete && scannerResult.TjOperands.Count == legacyOperands.Length ? "contagem igual (bytes/offsets verificados no agregado)" : "revisar")}");
                totalParsed++;
            }

            var pdfPigOperations = page.Operations.Where(x => x.Operator == "Tj").ToArray();
            int compared = Math.Min(pdfPigOperations.Length, locatedForPage.Count);
            int exact = 0, empty = 0, different = 0, unavailable = 0;
            var details = new List<string>();
            for (int index = 0; index < compared; index++)
            {
                var operation = pdfPigOperations[index];
                var property = operation.GetType().GetProperty("Bytes", BindingFlags.Public | BindingFlags.Instance);
                var value = property?.GetValue(operation);
                if (value is ReadOnlyMemory<byte> memory)
                {
                    if (memory.Length == 0)
                    {
                        empty++;
                        if (details.Count < 8)
                            details.Add($"#{index + 1}: PdfPig Bytes VAZIO; decodificado={Convert.ToHexString(locatedForPage[index].Token.Bytes)}; tokenBruto={locatedForPage[index].Token.RawHex}; contexto={GetContextForDiagnostic(locatedForPage[index].Token)}; obj={locatedForPage[index].ObjectId}; offset={locatedForPage[index].Token.Start}; tipo={operation.GetType().Name}; detalhes={DescribeOperation(operation)}");
                    }
                    else if (memory.Span.SequenceEqual(locatedForPage[index].Token.Bytes))
                        exact++;
                    else
                    {
                        different++;
                        if (details.Count < 8)
                            details.Add($"#{index + 1}: PdfPig={Convert.ToHexString(memory.Span)}; stream={Convert.ToHexString(locatedForPage[index].Token.Bytes)}; obj={locatedForPage[index].ObjectId}; offset={locatedForPage[index].Token.Start}; tipo={operation.GetType().Name}");
                    }
                }
                else
                {
                    unavailable++;
                    if (details.Count < 8)
                        details.Add($"#{index + 1}: propriedade Bytes indisponível/tipo inesperado ({value?.GetType().FullName ?? "null"}); stream={Convert.ToHexString(locatedForPage[index].Token.Bytes)}; obj={locatedForPage[index].ObjectId}; offset={locatedForPage[index].Token.Start}; tipo={operation.GetType().Name}");
                }
            }

            totalExact += exact; totalEmpty += empty; totalDifferent += different; totalUnavailable += unavailable;
            Console.WriteLine($"  Página {pageNumber}: PdfPig Tj={expected}; operandos localizados={found}; comparados={compared}; exatos={exact}; Bytes vazios={empty}; bytes diferentes={different}; Bytes indisponíveis={unavailable}; diferença de contagem={found - expected}");
            foreach (string detail in details)
                Console.WriteLine($"    DIAGNÓSTICO {detail}");

            // Correlaciona o operando original com a sequência de ShowText e as Letters produzidas.
            int textSequence = 0, tjOperandIndex = 0, printedCorrelations = 0;
            var mappingStats = new Dictionary<string, (int Equal, int Different, int NoLetters, int OneByteOneLetter)>();
            var mappingSamples = new List<string>();
            foreach (var operationInOrder in page.Operations)
            {
                if (operationInOrder.Operator is not ("Tj" or "TJ")) continue;
                textSequence++;
                if (operationInOrder.Operator != "Tj") continue;
                if (tjOperandIndex >= locatedForPage.Count) break;

                var token = locatedForPage[tjOperandIndex].Token;
                var operationText = operationInOrder.GetType()
                    .GetProperty("Text", BindingFlags.Public | BindingFlags.Instance)?
                    .GetValue(operationInOrder)?.ToString() ?? "";
                var associatedLetters = page.Letters
                    .Where(letter => letter.TextSequence == textSequence)
                    .ToArray();
                bool isEmptyBytes = operationInOrder.GetType()
                    .GetProperty("Bytes", BindingFlags.Public | BindingFlags.Instance)?
                    .GetValue(operationInOrder) is ReadOnlyMemory<byte> opBytes && opBytes.Length == 0;
                string fontKey = associatedLetters.Length == 0 ? "(sem letra)" : string.Join(",", associatedLetters.Select(letter => letter.FontName).Distinct());
                var previous = mappingStats.TryGetValue(fontKey, out var stat) ? stat : (0, 0, 0, 0);
                bool equalCount = token.Bytes.Length == associatedLetters.Length;
                bool oneByteOneLetter = token.Bytes.Length == 1 && associatedLetters.Length == 1 && associatedLetters[0].Value.EnumerateRunes().Count() == 1;
                mappingStats[fontKey] = (previous.Item1 + (equalCount ? 1 : 0), previous.Item2 + (equalCount ? 0 : 1), previous.Item3 + (associatedLetters.Length == 0 ? 1 : 0), previous.Item4 + (oneByteOneLetter ? 1 : 0));
                if (oneByteOneLetter && mappingSamples.Count < 40)
                    mappingSamples.Add($"fonte={fontKey}; código={Convert.ToHexString(token.Bytes)}; letra={associatedLetters[0].Value}; offset={token.Start}");

                if (printedCorrelations < 20 || isEmptyBytes)
                {
                    string letterText = string.Concat(associatedLetters.Select(letter => letter.Value));
                    string fonts = string.Join(",", associatedLetters.Select(letter => letter.FontName).Distinct());
                    Console.WriteLine($"    GLIFO-MAP pág={pageNumber}; seq={textSequence}; op=Tj; obj={locatedForPage[tjOperandIndex].ObjectId}; offset={token.Start}; raw={token.RawHex}; opText='{EscapeForLog(operationText)}'; letters={associatedLetters.Length}; letterText='{EscapeForLog(letterText)}'; fontes={fonts}");
                    printedCorrelations++;
                }
                tjOperandIndex++;
            }
            Console.WriteLine("    ANÁLISE CANDIDATA POR FONTE (igualdade de contagem não prova codificação):");
            foreach (var entry in mappingStats.OrderBy(x => x.Key, StringComparer.Ordinal))
                Console.WriteLine($"      fonte={entry.Key}; bytes=letras:{entry.Value.Equal}; contagens diferentes:{entry.Value.Different}; sem letras:{entry.Value.NoLetters}; 1 byte/1 letra Unicode:{entry.Value.OneByteOneLetter}");
            foreach (string sample in mappingSamples)
                Console.WriteLine($"      CÓDIGO-1BYTE {sample}");
            Console.WriteLine($"    GLIFO-MAP resumo: operandos Tj correlacionados={tjOperandIndex}; sequências de texto={textSequence}; linhas exibidas={printedCorrelations} (primeiras 20 + Bytes vazios).");
        }

        Console.WriteLine($"  TOTAL: streams analisados={totalParsed}; streams ignorados por filtro={totalUnsupported}; operandos Tj localizados={totalFound}");
        Console.WriteLine($"  CORRELAÇÃO TOTAL: exatos={totalExact}; Bytes vazios no PdfPig={totalEmpty}; bytes diferentes={totalDifferent}; Bytes indisponíveis={totalUnavailable}");
        Console.WriteLine($"  VALIDAÇÃO DO NOVO SCANNER: streams completos={scannerCompleteStreams}; interrompidos={scannerRejectedStreams}; streams divergentes={scannerMismatchedStreams}; operandos com offset/tipo/bytes idênticos ao scanner legado={scannerMatchedOperands}");
        Console.WriteLine("  Regra: qualquer stream interrompido ou divergente é rejeitado; os offsets de referência continuam vindo do scanner legado nesta etapa.");
        Console.WriteLine("  AMOSTRAS (offsets em bytes do stream DECODIFICADO, não do arquivo PDF bruto):");
        foreach (var item in samples)
            Console.WriteLine($"    pág={item.Page}; obj={item.ObjectId}; offset={item.Offset}; bytes={item.Length}; tipo={item.Kind}; hex={item.Hex}");
        Console.WriteLine("  Limite: offsets apontam para o início do token string no stream decodificado. Ainda não prova o mapeamento glifo→byte nem modifica/salva o PDF.");
        Console.WriteLine();
    }

    private static IEnumerable<StringToken> FindTjOperands(byte[] data)
    {
        StringToken? lastString = null;
        int i = 0;
        while (i < data.Length)
        {
            byte b = data[i];
            if (IsWhite(b)) { i++; continue; }
            if (b == '%') { while (i < data.Length && data[i] != 10 && data[i] != 13) i++; continue; }

            if (b == '(')
            {
                int start = i++;
                int depth = 1;
                var value = new List<byte>();
                while (i < data.Length && depth > 0)
                {
                    byte c = data[i++];
                    if (c == '\\' && i < data.Length)
                    {
                        byte e = data[i++];
                        if (e == 10) continue;
                        if (e == 13) { if (i < data.Length && data[i] == 10) i++; continue; }
                        if (e >= '0' && e <= '7')
                        {
                            int oct = e - '0', n = 1;
                            while (n < 3 && i < data.Length && data[i] >= '0' && data[i] <= '7') { oct = oct * 8 + data[i++] - '0'; n++; }
                            value.Add((byte)(oct & 255)); continue;
                        }
                        value.Add(e switch { (byte)'n' => (byte)10, (byte)'r' => (byte)13, (byte)'t' => (byte)9, (byte)'b' => (byte)8, (byte)'f' => (byte)12, _ => e });
                        continue;
                    }
                    if (c == '(') { depth++; value.Add(c); continue; }
                    if (c == ')') { depth--; if (depth > 0) value.Add(c); continue; }
                    value.Add(c);
                }
                lastString = depth == 0 ? new StringToken(start, i, value.ToArray(), "literal") : null;
                continue;
            }

            if (b == '<' && i + 1 < data.Length && data[i + 1] != '<')
            {
                int start = i++;
                var hex = new StringBuilder();
                while (i < data.Length && data[i] != '>') { if (!IsWhite(data[i])) hex.Append((char)data[i]); i++; }
                if (i < data.Length) i++;
                if (hex.Length % 2 != 0) hex.Append('0');
                try { lastString = new StringToken(start, i, Convert.FromHexString(hex.ToString()), "hex"); }
                catch { lastString = null; }
                continue;
            }

            if (IsDelimiter(b)) { lastString = null; i++; continue; }

            int wordStart = i;
            while (i < data.Length && !IsWhite(data[i]) && !IsDelimiter(data[i])) i++;
            if (i == wordStart) { i++; continue; }
            string word = Encoding.ASCII.GetString(data, wordStart, i - wordStart);
            if (word == "Tj" && lastString is not null)
                yield return lastString;
            lastString = null;
        }
    }

    private static void AddStream(IToken token, PdfDocument document, List<(StreamToken Stream, string Id)> output)
    {
        string id = "direto";
        IToken resolved = token;
        if (token is IndirectReferenceToken reference)
        {
            var obj = document.Structure.GetObject(reference.Data);
            id = obj.Number.ToString();
            resolved = obj.Data;
        }
        if (resolved is ArrayToken array)
        {
            foreach (var child in array.Data) AddStream(child, document, output);
        }
        else if (resolved is StreamToken stream) output.Add((stream, id));
    }

    private static string EscapeForLog(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("'", "\\'");

    private static string DescribeOperation(object operation)
    {
        var parts = new List<string>();
        foreach (var property in operation.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            try
            {
                object? value = property.GetValue(operation);
                string rendered = value switch
                {
                    null => "null",
                    ReadOnlyMemory<byte> memory => $"ReadOnlyMemory[{memory.Length}]={Convert.ToHexString(memory.Span)}",
                    byte[] bytes => $"byte[{bytes.Length}]={Convert.ToHexString(bytes)}",
                    _ => value.ToString() ?? "null"
                };
                parts.Add($"{property.Name}={rendered}");
            }
            catch (Exception ex) { parts.Add($"{property.Name}=<erro:{ex.GetType().Name}>"); }
        }

        try
        {
            var write = operation.GetType().GetMethod("Write", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Stream) }, null);
            if (write is not null)
            {
                using var output = new MemoryStream();
                write.Invoke(operation, new object[] { output });
                parts.Add($"WriteHex={Convert.ToHexString(output.ToArray())}");
            }
        }
        catch (Exception ex) { parts.Add($"Write=<erro:{ex.GetType().Name}>"); }
        return string.Join("; ", parts);
    }

    private static string GetContextHex(byte[] data, int start, int end)
    {
        int from = Math.Max(0, start - 24);
        int to = Math.Min(data.Length, end + 24);
        return Convert.ToHexString(data.AsSpan(from, to - from));
    }

    private static string GetContextForDiagnostic(StringToken token)
        => $"raw={token.RawHex}; contextoAntesEDepois={token.ContextHex}; decoded={Convert.ToHexString(token.Bytes)}; kind={token.Kind}";

    private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}