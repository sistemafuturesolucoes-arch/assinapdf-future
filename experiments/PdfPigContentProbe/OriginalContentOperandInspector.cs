using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

internal static class OriginalContentOperandInspector
{
    private sealed record StringToken(int Start, int End, byte[] Bytes, string Kind);
    private sealed record LocatedOperand(int Page, string ObjectId, int Offset, int Length, string Kind, string Hex);

    public static void Run(PdfDocument document)
    {
        Console.WriteLine("LOCALIZAÇÃO DE OFFSETS DOS OPERANDOS Tj (somente leitura)");
        int totalFound = 0, totalParsed = 0, totalUnsupported = 0;
        var samples = new List<LocatedOperand>();

        for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            int expected = page.Operations.Count(x => x.Operator == "Tj");
            int found = 0;
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
                foreach (var item in FindTjOperands(bytes))
                {
                    found++;
                    totalFound++;
                    if (samples.Count < 40)
                        samples.Add(new LocatedOperand(pageNumber, id, item.Start, item.Bytes.Length, item.Kind, Convert.ToHexString(item.Bytes)));
                }
                totalParsed++;
            }
            Console.WriteLine($"  Página {pageNumber}: PdfPig Tj={expected}; operandos Tj localizados={found}; diferença={found - expected}");
        }

        Console.WriteLine($"  TOTAL: streams analisados={totalParsed}; streams ignorados por filtro={totalUnsupported}; operandos Tj localizados={totalFound}");
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

    private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
