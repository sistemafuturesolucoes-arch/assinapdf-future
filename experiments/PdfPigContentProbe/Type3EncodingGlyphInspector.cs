using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

internal static class Type3EncodingGlyphInspector
{
    private sealed record GlyphEntry(int Code, string Name);
    private sealed record WidthEntry(int Code, double Width);

    public static void Run(PdfDocument document)
    {
        Console.WriteLine("INSPEÇÃO TYPE3: ENCODING, CHARPROCS E LARGURAS (somente leitura)");
        const string target = "CONFIRME";
        for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var bySequence = page.Letters.GroupBy(x => x.TextSequence)
                .ToDictionary(x => x.Key, x => x.ToArray());
            int sequence = 0;
            var textOps = new List<(int Sequence, object Operation)>();
            foreach (var op in page.Operations)
                if (op.Operator is "Tj" or "TJ")
                    textOps.Add((++sequence, op));

            for (int start = 0; start <= textOps.Count - target.Length; start++)
            {
                var window = textOps.Skip(start).Take(target.Length).ToArray();
                if (window.Select((x, i) => x.Sequence == window[0].Sequence + i).Any(x => !x))
                    continue;
                var letters = window.Select(x => bySequence.TryGetValue(x.Sequence, out var ls) && ls.Length == 1 ? ls[0] : null).ToArray();
                if (letters.Any(x => x is null) || string.Concat(letters.Select(x => x!.Value)) != target)
                    continue;

                var selected = letters[0]!;
                if (!(selected.FontName ?? "").Contains("Type3", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  Alvo encontrado na página {pageNumber}, mas a fonte não parece Type3: {selected.FontName}; ignorado.");
                    return;
                }

                InspectTarget(document, selected, window[0].Operation, pageNumber, window[0].Sequence);
                return;
            }
        }
        Console.WriteLine($"  Alvo '{target}' não encontrado em operações contínuas de uma Letter por Tj.");
        Console.WriteLine();
    }

    private static void InspectTarget(PdfDocument document, Letter selected, object operation, int page, int sequence)
    {
        Console.WriteLine($"  Alvo: página={page}; sequência={sequence}; fonte={selected.FontName}; caractere selecionado='{selected.Value}'");
        object? details = Get(selected, "FontDetails");
        object? nullableReference = Get(details, "FontDictionaryReference");
        if (nullableReference is null)
        {
            Console.WriteLine("  ABORTADO: referência do dicionário da fonte indisponível.");
            return;
        }

        object? fontToken;
        try { fontToken = Resolve(document, nullableReference); }
        catch (Exception ex)
        {
            Console.WriteLine($"  ABORTADO: não foi possível resolver fonte: {ex.GetType().Name}.");
            return;
        }
        object? fontDictionary = Get(fontToken, "Data");
        if (fontDictionary is null)
        {
            Console.WriteLine("  ABORTADO: dicionário Type3 não encontrado.");
            return;
        }

        string subtype = Value(fontDictionary, "Subtype");
        Console.WriteLine($"  /Subtype: {subtype}");
        if (!subtype.Contains("Type3", StringComparison.Ordinal))
        {
            Console.WriteLine("  ABORTADO: o dicionário referenciado não é /Type3.");
            return;
        }

        object? encoding = ValueObject(fontDictionary, "Encoding");
        Console.WriteLine("  /Encoding bruto: " + (encoding?.ToString() ?? "(não declarado)"));
        object? encodingDictionary = ResolveIfReference(document, encoding);
        string baseEncoding = "(não declarado)";
        var differences = new List<GlyphEntry>();
        if (encodingDictionary is not null && encodingDictionary.GetType().Name.Contains("Dictionary", StringComparison.Ordinal))
        {
            baseEncoding = Value(encodingDictionary, "BaseEncoding");
            object? differencesToken = ValueObject(encodingDictionary, "Differences");
            object? data = Get(differencesToken, "Data");
            if (data is IEnumerable items)
            {
                int? currentCode = null;
                foreach (var item in items)
                {
                    if (item is null) continue;
                    string itemText = item.ToString() ?? "";
                    if (int.TryParse(itemText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                    {
                        currentCode = code;
                        continue;
                    }
                    string name = itemText.Trim().TrimStart('/');
                    if (currentCode.HasValue && name.Length > 0)
                    {
                        differences.Add(new GlyphEntry(currentCode.Value, name));
                        currentCode++;
                    }
                }
            }
        }
        Console.WriteLine($"  /BaseEncoding: {baseEncoding}");
        Console.WriteLine($"  /Differences: {differences.Count} nomes; C={FormatCodes(differences, "C")}; B={FormatCodes(differences, "B")}");

        object? charProcs = ValueObject(fontDictionary, "CharProcs");
        var charProcEntries = Entries(charProcs).ToDictionary(x => x.Key.TrimStart('/'), x => x.Value, StringComparer.Ordinal);
        var charProcNames = charProcEntries.Keys.ToHashSet(StringComparer.Ordinal);
        Console.WriteLine($"  /CharProcs: {charProcNames.Count} programas; C={(charProcNames.Contains("C") ? "PRESENTE" : "AUSENTE")}; B={(charProcNames.Contains("B") ? "PRESENTE" : "AUSENTE")}");
        var widths = ReadWidths(fontDictionary);
        var selectedBytes = Get(operation, "Bytes") is ReadOnlyMemory<byte> selectedMemory
            ? selectedMemory.ToArray()
            : Array.Empty<byte>();
        if (selectedBytes.Length > 0)
        {
            Console.WriteLine($"  Código(s) bruto(s) da seleção: {Convert.ToHexString(selectedBytes)}");
            if (selectedBytes.Length == 1)
                DescribeCode(selectedBytes[0], differences, charProcNames, widths, charProcEntries);
            else
                Console.WriteLine("  Mapeamento Differences direto não aplicado: operando com mais de um byte.");
        }
        DumpObservedCodesForExactFont(document, selected, differences, charProcNames, charProcEntries, widths);

        var cCodes = differences.Where(x => x.Name == "C").Select(x => x.Code).Distinct().ToArray();
        var bCodes = differences.Where(x => x.Name == "B").Select(x => x.Code).Distinct().ToArray();
        foreach (int code in cCodes)
            Console.WriteLine($"  Largura código C {code}: {FindWidth(widths, code)}");
        foreach (int code in bCodes)
            Console.WriteLine($"  Largura código B {code}: {FindWidth(widths, code)}");
        if (cCodes.Length == 1 && bCodes.Length == 1)
        {
            double? cWidth = FindWidthValue(widths, cCodes[0]);
            double? bWidth = FindWidthValue(widths, bCodes[0]);
            Console.WriteLine($"  Larguras C/B iguais: {(cWidth.HasValue && bWidth.HasValue ? (Math.Abs(cWidth.Value - bWidth.Value) < 0.000001 ? "SIM" : "NÃO") : "NÃO DETERMINÁVEL")}");
        }
        else
            Console.WriteLine("  Larguras C/B iguais: NÃO DETERMINÁVEL (mapeamento Differences ausente ou ambíguo).");

        var operandBytes = Get(operation, "Bytes");
        if (operandBytes is ReadOnlyMemory<byte> memory)
            Console.WriteLine($"  Operando original associado à seleção: {Convert.ToHexString(memory.Span)}");
        Console.WriteLine("  FontMatrix: " + Value(fontDictionary, "FontMatrix"));
        Console.WriteLine("  FontBBox: " + Value(fontDictionary, "FontBBox"));
        Console.WriteLine("  Geometria da letra selecionada: " + DescribeGeometry(selected));
        var sameFontB = Enumerable.Range(1, document.NumberOfPages).SelectMany(n => document.GetPage(n).Letters)
            .Where(x => string.Equals(x.FontName, selected.FontName, StringComparison.Ordinal) && x.Value == "B")
            .Take(5).ToArray();
        Console.WriteLine($"  Amostras de 'B' extraídas na mesma fonte: {sameFontB.Length}");
        foreach (var sample in sameFontB)
            Console.WriteLine("    " + DescribeGeometry(sample));
        Console.WriteLine("  Conclusão: presença de nome/código e CharProc é evidência estrutural; não prova equivalência visual nem autoriza reescrita.");
        Console.WriteLine();
    }

    private static void DescribeCode(int code, List<GlyphEntry> differences, HashSet<string> charProcNames,
        List<WidthEntry>? widths, Dictionary<string, object>? charProcEntries)
    {
        var matches = differences.Where(x => x.Code == code).ToArray();
        if (matches.Length != 1)
        {
            Console.WriteLine($"  Código 0x{code:X2}: {(matches.Length == 0 ? "sem nome em Differences" : "mapeamento ambíguo")}");
            return;
        }
        string glyphName = matches[0].Name;
        bool hasProgram = charProcNames.Contains(glyphName);
        Console.WriteLine($"  Código 0x{code:X2} -> /{glyphName}; CharProc={(hasProgram ? "PRESENTE" : "AUSENTE")}");
        if (widths is not null)
            Console.WriteLine($"    Width={FindWidth(widths, code)}");
        if (hasProgram && charProcEntries is not null &&
            charProcEntries.TryGetValue(glyphName, out object? token))
            Console.WriteLine($"    CharProc token={token.GetType().Name}; referência={Get(token, "Data") ?? token}");
    }

    private static void DumpObservedCodesForExactFont(PdfDocument document, Letter selected,
        List<GlyphEntry> differences, HashSet<string> charProcNames, Dictionary<string, object> charProcEntries,
        List<WidthEntry> widths)
    {
        object? selectedDetails = Get(selected, "FontDetails");
        object? selectedReference = Get(selectedDetails, "FontDictionaryReference");
        string selectedObject = selectedReference?.ToString() ?? "(sem referência)";
        Console.WriteLine($"  Fonte exata da seleção: {selectedObject}");
        int printed = 0;
        foreach (int pageNumber in Enumerable.Range(1, document.NumberOfPages))
        {
            var page = document.GetPage(pageNumber);
            var lettersBySequence = page.Letters.GroupBy(x => x.TextSequence)
                .ToDictionary(x => x.Key, x => x.ToArray());
            int sequence = 0;
            foreach (var op in page.Operations)
            {
                if (op.Operator is not ("Tj" or "TJ")) continue;
                sequence++;
                if (!lettersBySequence.TryGetValue(sequence, out var letters) || letters.Length != 1)
                    continue;
                var letter = letters[0];
                if (letter.Value != "B" ||
                    !Equals(Get(Get(letter, "FontDetails"), "FontDictionaryReference"), selectedReference))
                    continue;
                object? raw = Get(op, "Bytes");
                if (raw is not ReadOnlyMemory<byte> memory || memory.Length == 0) continue;
                Console.WriteLine($"  Amostra B na fonte exata: página={pageNumber}; seq={sequence}; bytes={Convert.ToHexString(memory.Span)}");
                if (memory.Length == 1)
                    DescribeCode(memory.Span[0], differences, charProcNames, widths, charProcEntries);
                printed++;
                if (printed >= 20) break;
            }
            if (printed >= 20) break;
        }
        if (printed == 0)
            Console.WriteLine("  Nenhuma letra B foi encontrada com a mesma referência exata de dicionário de fonte.");
    }

    private static string FormatCodes(IEnumerable<GlyphEntry> entries, string name)
    {
        var codes = entries.Where(x => x.Name == name).Select(x => x.Code).Distinct().OrderBy(x => x).ToArray();
        return codes.Length == 0 ? "(nenhum)" : string.Join(", ", codes);
    }

    private static List<WidthEntry> ReadWidths(object dictionary)
    {
        int first = TryNumber(ValueObject(dictionary, "FirstChar"), out double f) ? (int)f : 0;
        object? token = ValueObject(dictionary, "Widths");
        object? data = Get(token, "Data");
        var result = new List<WidthEntry>();
        if (data is not IEnumerable items) return result;
        int code = first;
        foreach (var item in items)
        {
            if (item is not null && TryNumber(item, out double width))
                result.Add(new WidthEntry(code++, width));
        }
        return result;
    }

    private static string FindWidth(List<WidthEntry> widths, int code) =>
        FindWidthValue(widths, code)?.ToString("0.######", CultureInfo.InvariantCulture) ?? "(não encontrada)";

    private static double? FindWidthValue(List<WidthEntry> widths, int code) =>
        widths.FirstOrDefault(x => x.Code == code)?.Width;

    private static bool TryNumber(object? token, out double value)
    {
        value = 0;
        if (token is null) return false;
        object? data = Get(token, "Data");
        string raw = (data?.ToString() ?? token.ToString() ?? "").Trim();
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Value(object? dictionary, string key) =>
        ValueObject(dictionary, key)?.ToString() ?? "(não declarado)";

    private static object? ValueObject(object? dictionary, string key) =>
        Entries(dictionary).FirstOrDefault(x => x.Key.TrimStart('/') == key).Value;

    private static IEnumerable<(string Key, object Value)> Entries(object? dictionary)
    {
        object? data = Get(dictionary, "Data") ?? dictionary;
        if (data is not IEnumerable entries) yield break;
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            object? key = Get(entry, "Key");
            object? value = Get(entry, "Value");
            if (key is not null && value is not null)
                yield return (key.ToString() ?? "", value);
        }
    }

    private static object? ResolveIfReference(PdfDocument document, object? token)
    {
        if (token is null) return null;
        return Resolve(document, token);
    }

    private static object? Resolve(PdfDocument document, object referenceOrToken)
    {
        object current = referenceOrToken;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int depth = 0; depth < 16; depth++)
        {
            if (!visited.Add(current)) return null;
            string type = current.GetType().Name;
            if (type == "IndirectReferenceToken" || type == "IndirectReference")
            {
                object? reference = type == "IndirectReferenceToken" ? Get(current, "Data") : current;
                if (reference is null) return null;
                var method = document.Structure.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .First(m => m.Name == "GetObject" && m.GetParameters().Length == 1 &&
                                m.GetParameters()[0].ParameterType.IsInstanceOfType(reference));
                object? resolved = method.Invoke(document.Structure, new[] { reference });
                if (resolved is null) return null;
                current = resolved;
                continue;
            }
            if (type == "ObjectToken")
            {
                object? inner = Get(current, "Data");
                if (inner is null || ReferenceEquals(inner, current)) return null;
                current = inner;
                continue;
            }
            return current;
        }
        return null;
    }

    private static string DescribeGeometry(Letter letter)
    {
        string rectangle = Get(letter, "GlyphRectangle")?.ToString() ?? "(GlyphRectangle indisponível)";
        string width = Get(letter, "Width")?.ToString() ?? "(Width indisponível)";
        string height = Get(letter, "Height")?.ToString() ?? "(Height indisponível)";
        string start = Get(letter, "StartBaseLine")?.ToString() ?? "(StartBaseLine indisponível)";
        string end = Get(letter, "EndBaseLine")?.ToString() ?? "(EndBaseLine indisponível)";
        return $"texto='{letter.Value}'; rectangle={rectangle}; width={width}; height={height}; baseline={start} -> {end}";
    }

    private static object? Get(object? instance, string property) =>
        instance?.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
}
