using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

internal static class PdfFontReflectionInspector
{
    private const int MaxFonts = 80;
    private const int MaxCMapChars = 12000;
    private const int MaxArrayItems = 96;

    public static void Run(PdfDocument document)
    {
        Console.WriteLine("MAPAS DE CODIFICAÇÃO DAS FONTES (somente leitura)");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int inspected = 0;

        foreach (var pageNumber in Enumerable.Range(1, document.NumberOfPages))
        {
            foreach (var letter in document.GetPage(pageNumber).Letters)
            {
                var nullableReference = letter.Font.FontDictionaryReference;
                if (!nullableReference.HasValue) continue;

                var reference = nullableReference.Value;
                string id = reference.ObjectNumber.ToString();
                if (!seen.Add(id)) continue;
                if (++inspected > MaxFonts)
                {
                    Console.WriteLine($"  Limite de {MaxFonts} fontes distintas atingido.");
                    break;
                }

                Console.WriteLine($"\n  FONTE: {letter.FontName}; objeto={id} 0");
                try
                {
                    object? fontToken = ResolveReference(document, reference);
                    object? dictionary = GetProperty(fontToken, "Data");
                    if (dictionary is null)
                    {
                        Console.WriteLine("    Dicionário da fonte não resolvido.");
                        continue;
                    }

                    InspectFontDictionary(dictionary, document, "    ", 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    ERRO controlado: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"\n  Fontes distintas examinadas: {seen.Count}");
        Console.WriteLine("  Limite: inspeção e decodificação em memória; nenhum PDF é alterado ou salvo.");
        Console.WriteLine();
    }

    private static void InspectFontDictionary(object dictionary, PdfDocument document, string indent, int depth)
    {
        if (depth > 2) return;

        foreach (var (key, value) in Entries(dictionary))
        {
            string type = value.GetType().Name;
            if (key is "Type" or "Subtype" or "BaseFont" or "BaseEncoding" or "FirstChar" or "LastChar" or "DW" or "W" or "FontBBox" or "FontMatrix")
                Console.WriteLine($"{indent}/{key}: {Preview(value.ToString() ?? type, 180)}");

            if (key == "ToUnicode")
            {
                object? stream = ResolveToken(document, value);
                if (stream is not null && stream.GetType().Name == "StreamToken")
                    DumpDecodedCMap(stream, document, indent);
                else
                    Console.WriteLine($"{indent}/ToUnicode: não resolvido como stream ({stream?.GetType().Name ?? "null"})");
            }
            else if (key == "Encoding")
            {
                object? encoding = ResolveToken(document, value);
                if (encoding is not null && encoding.GetType().Name.Contains("Dictionary"))
                    DumpEncodingDictionary(encoding, document, indent);
                else
                    Console.WriteLine($"{indent}/Encoding: {encoding?.GetType().Name ?? type} {Preview(encoding?.ToString() ?? value.ToString() ?? "", 160)}");
            }
            else if (key == "DescendantFonts")
            {
                DumpDescendantFonts(value, document, indent);
            }
            else if (key == "CharProcs")
            {
                Console.WriteLine($"{indent}/CharProcs: dicionário de programas Type3 ({Entries(value).Count()} entradas)");
            }
            else if (key == "Widths")
            {
                Console.WriteLine($"{indent}/Widths: {Preview(value.ToString() ?? type, 180)}");
            }
        }
    }

    private static void DumpEncodingDictionary(object token, PdfDocument document, string indent)
    {
        Console.WriteLine($"{indent}/Encoding: dicionário");
        foreach (var (key, value) in Entries(token))
        {
            if (key == "BaseEncoding")
                Console.WriteLine($"{indent}  /BaseEncoding: {value}");
            else if (key == "Differences")
            {
                object? data = GetProperty(value, "Data");
                if (data is IEnumerable items)
                {
                    Console.WriteLine($"{indent}  /Differences:");
                    int index = 0;
                    foreach (var item in items)
                    {
                        if (item is null) continue;
                        if (index >= MaxArrayItems)
                        {
                            Console.WriteLine($"{indent}    ... limite de {MaxArrayItems} itens");
                            break;
                        }
                        Console.WriteLine($"{indent}    [{index++}] {item.GetType().Name}: {Preview(item.ToString() ?? "", 100)}");
                    }
                }
                else Console.WriteLine($"{indent}  /Differences: {value}");
            }
            else
                Console.WriteLine($"{indent}  /{key}: {value.GetType().Name}");
        }
    }

    private static void DumpDescendantFonts(object arrayToken, PdfDocument document, string indent)
    {
        object? data = GetProperty(arrayToken, "Data");
        if (data is not IEnumerable items) return;

        int index = 0;
        foreach (var item in items)
        {
            if (item is null) continue;
            object? descendant = ResolveToken(document, item);
            object? dictionary = GetProperty(descendant, "Data");
            if (dictionary is null) continue;

            Console.WriteLine($"{indent}/DescendantFonts[{index++}]:");
            foreach (var (key, value) in Entries(dictionary))
            {
                if (key is "Type" or "Subtype" or "BaseFont" or "CIDSystemInfo" or "DW" or "W" or "CIDToGIDMap" or "FontDescriptor")
                    Console.WriteLine($"{indent}  /{key}: {value.GetType().Name} {Preview(value.ToString() ?? "", 180)}");
                if (key == "ToUnicode")
                {
                    object? cmap = ResolveToken(document, value);
                    if (cmap is not null && cmap.GetType().Name == "StreamToken")
                        DumpDecodedCMap(cmap, document, indent + "  ");
                }
            }
        }
    }

    private static void DumpDecodedCMap(object streamToken, PdfDocument document, string indent)
    {
        try
        {
            if (streamToken is not StreamToken stream)
            {
                Console.WriteLine($"{indent}/ToUnicode: token não é StreamToken.");
                return;
            }

            Memory<byte> decoded = stream.Data;
            var filters = document.Structure.FilterProvider.GetFilters(
                stream.StreamDictionary, document.Structure.TokenScanner);
            int filterIndex = 0;
            foreach (var filter in filters)
            {
                if (!filter.IsSupported)
                {
                    Console.WriteLine($"{indent}/ToUnicode: filtro não suportado ({filter.GetType().Name})");
                    return;
                }
                decoded = filter.Decode(
                    decoded, stream.StreamDictionary, document.Structure.FilterProvider, filterIndex++);
            }

            string cmapText = Encoding.Latin1.GetString(decoded.Span);
            Console.WriteLine($"{indent}/ToUnicode: stream decodificado; {decoded.Length} bytes");
            Console.WriteLine($"{indent}  CMap: {Preview(cmapText, MaxCMapChars).Replace("\r", "").Replace("\n", "\n" + indent + "  ")}");
            PrintCMapStatistics(cmapText, indent + "  ");
            PrintCMapMappings(cmapText, indent + "  ");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{indent}/ToUnicode: falha controlada ao decodificar: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void PrintCMapMappings(string text, string indent)
    {
        CMapParseResult result = ToUnicodeCMapParser.Parse(text);
        Console.WriteLine($"{indent}Mapeamentos Unicode interpretados: {result.Mappings.Count}; entradas inválidas/rejeitadas: {result.MalformedEntries}");
        Console.WriteLine($"{indent}  bfchar: {result.ParsedBfCharEntries}; bfrange: {result.ParsedBfRangeEntries}");
        foreach (var pair in result.Mappings.Take(24))
        {
            string scalars = string.Join(" ", pair.Value.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));
            Console.WriteLine($"{indent}  código PDF <{pair.Key}> -> \"{EscapeForCMap(pair.Value)}\" ({scalars})");
        }
        if (result.Mappings.Count > 24)
            Console.WriteLine($"{indent}  ... exibidos 24 de {result.Mappings.Count} mapeamentos");
    }

    private static string EscapeForCMap(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"")
             .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static bool TryDecodeUnicodeHex(string hex, out string value)
    {
        value = "";
        if (hex.Length == 0 || hex.Length % 4 != 0) return false;
        try
        {
            var bytes = Convert.FromHexString(hex);
            // Reject invalid UTF-16 (including unpaired surrogates) instead of
            // silently replacing malformed destination data with U+FFFD.
            value = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseHex(string hex, out long value) =>
        long.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    private static string NormalizeHex(string hex) =>
        hex.Length % 2 == 0 ? hex.ToUpperInvariant() : ("0" + hex).ToUpperInvariant();

    private static string EscapeForCMap(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"")
             .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static void PrintCMapStatistics(string text, string indent)
    {
        int codeSpaces = Count(text, "begincodespacerange");
        int chars = Count(text, "beginbfchar");
        int ranges = Count(text, "beginbfrange");
        int cidChars = Count(text, "begincidchar");
        int cidRanges = Count(text, "begincidrange");
        Console.WriteLine($"{indent}Estruturas CMap: codespacerange={codeSpaces}; bfchar={chars}; bfrange={ranges}; cidchar={cidChars}; cidrange={cidRanges}");
    }

    private static int Count(string text, string value)
    {
        int count = 0, offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static object? ResolveToken(PdfDocument document, object token)
    {
        // References and ObjectToken wrappers can be nested. Resolve/unwrap in a
        // bounded loop, stopping only when the underlying token is reached.
        // StreamToken and DictionaryToken are deliberately preserved.
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int depth = 0; depth < 16; depth++)
        {
            if (!visited.Add(token)) return null;

            string typeName = token.GetType().Name;
            if (typeName == "IndirectReferenceToken")
            {
                object? reference = GetProperty(token, "Data");
                if (reference is null) return null;
                object? resolved = ResolveReference(document, reference);
                if (resolved is null) return null;
                token = resolved;
                continue;
            }

            if (typeName == "ObjectToken")
            {
                object? inner = GetProperty(token, "Data");
                if (inner is null || ReferenceEquals(inner, token)) return null;
                token = inner;
                continue;
            }

            return token;
        }

        // A cyclic or unexpectedly deep token chain is not safe to inspect.
        return null;
    }

    private static object? ResolveReference(PdfDocument document, object reference)
    {
        var method = document.Structure.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .First(m => m.Name == "GetObject" && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsInstanceOfType(reference));
        return method.Invoke(document.Structure, new[] { reference });
    }

    private static IEnumerable<(string Key, object Value)> Entries(object dictionary)
    {
        object? data = GetProperty(dictionary, "Data") ?? dictionary;
        if (data is not IEnumerable entries) yield break;
        foreach (object? entry in entries)
        {
            if (entry is null) continue;
            object? key = GetProperty(entry, "Key");
            object? value = GetProperty(entry, "Value");
            if (key is not null && value is not null)
                yield return (key.ToString() ?? "?", value);
        }
    }

    private static object? GetProperty(object? instance, string name) =>
        instance?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);

    private static string Preview(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
