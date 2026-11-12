using System.Collections;
using System.Reflection;
using UglyToad.PdfPig;

internal static class PdfFontReflectionInspector
{
    public static void Run(PdfDocument document)
    {
        Console.WriteLine("INSPEÇÃO DOS DICIONÁRIOS PDF DAS FONTES (somente leitura)");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pageNumber in Enumerable.Range(1, document.NumberOfPages))
        {
            var page = document.GetPage(pageNumber);
            foreach (var letter in page.Letters)
            {
                var reference = letter.Font.FontDictionaryReference;
                if (reference is null) continue;
                string id = reference.ObjectNumber.ToString();
                if (!seen.Add(id)) continue;
                Console.WriteLine($"  FONTE: {letter.FontName}; objeto={id} 0");
                try
                {
                    var getObject = document.Structure.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .First(m => m.Name == "GetObject" && m.GetParameters().Length == 1 &&
                                    m.GetParameters()[0].ParameterType.IsInstanceOfType(reference));
                    object? objectToken = getObject.Invoke(document.Structure, new object[] { reference });
                    if (objectToken is null) { Console.WriteLine("    objeto não resolvido"); continue; }
                    object? dictionary = objectToken.GetType().GetProperty("Data")?.GetValue(objectToken);
                    if (dictionary is null) { Console.WriteLine($"    token sem Data: {objectToken.GetType().Name}"); continue; }
                    DumpDictionary(dictionary, document, "    ", 0, new HashSet<string>());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    falha ao inspecionar objeto: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        Console.WriteLine($"  Dicionários de fonte inspecionados: {seen.Count}");
        Console.WriteLine("  Limite: leitura de objetos/dicionários; nenhum conteúdo é alterado ou salvo.");
        Console.WriteLine();
    }

    private static void DumpDictionary(object dictionary, PdfDocument document, string indent, int depth, HashSet<string> visited)
    {
        if (depth > 4) return;
        object? data = dictionary.GetType().GetProperty("Data")?.GetValue(dictionary) ?? dictionary;
        if (data is not IEnumerable entries) { Console.WriteLine($"{indent}dicionário não enumerável: {dictionary.GetType().Name}"); return; }
        foreach (object? entry in entries)
        {
            if (entry is null) continue;
            Type et = entry.GetType();
            object? key = et.GetProperty("Key")?.GetValue(entry);
            object? value = et.GetProperty("Value")?.GetValue(entry);
            if (key is null || value is null) continue;
            string keyText = key.ToString() ?? "?";
            string type = value.GetType().Name;
            Console.WriteLine($"{indent}{keyText}: {type}");
            if (type == "IndirectReferenceToken")
            {
                object? reference = value.GetType().GetProperty("Data")?.GetValue(value);
                if (reference is null) continue;
                string refId = reference.GetType().GetProperty("ObjectNumber")?.GetValue(reference)?.ToString() ?? reference.ToString() ?? "?";
                if (!visited.Add(refId)) { Console.WriteLine($"{indent}  referência já visitada: {refId}"); continue; }
                try
                {
                    var method = document.Structure.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .First(m => m.Name == "GetObject" && m.GetParameters().Length == 1 &&
                                    m.GetParameters()[0].ParameterType.IsInstanceOfType(reference));
                    object? resolved = method.Invoke(document.Structure, new[] { reference });
                    object? resolvedData = resolved?.GetType().GetProperty("Data")?.GetValue(resolved);
                    if (resolvedData is not null && resolvedData.GetType().Name.Contains("Dictionary"))
                        DumpDictionary(resolvedData, document, indent + "  ", depth + 1, visited);
                    else if (resolvedData is not null)
                        Console.WriteLine($"{indent}  resolvido como {resolvedData.GetType().Name}");
                }
                catch (Exception ex) { Console.WriteLine($"{indent}  falha ao resolver: {ex.GetType().Name}"); }
            }
            else if (type.Contains("Dictionary"))
                DumpDictionary(value, document, indent + "  ", depth + 1, visited);
            else if (type == "ArrayToken")
                DumpArray(value, document, indent + "  ", depth + 1, visited);
        }
    }

    private static void DumpArray(object token, PdfDocument document, string indent, int depth, HashSet<string> visited)
    {
        if (depth > 4) return;
        object? data = token.GetType().GetProperty("Data")?.GetValue(token);
        if (data is not IEnumerable values) return;
        int index = 0;
        foreach (object? value in values)
        {
            if (value is null) continue;
            Console.WriteLine($"{indent}[{index++}]: {value.GetType().Name} {Preview(value.ToString() ?? "", 100)}");
            if (value.GetType().Name.Contains("Dictionary"))
                DumpDictionary(value, document, indent + "  ", depth + 1, visited);
        }
    }

    private static string Preview(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}