using System.Collections;
using System.Reflection;
using UglyToad.PdfPig;

internal static class PdfFontReflectionInspector
{
    public static void Run(PdfDocument document)
    {
        Console.WriteLine("INSPEÇÃO DE ESTRUTURAS DE FONTE (reflection; somente leitura)");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pageNumber in Enumerable.Range(1, document.NumberOfPages))
        {
            var page = document.GetPage(pageNumber);
            foreach (var letter in page.Letters)
            {
                string key = letter.FontName + "|" + letter.Font.GetType().AssemblyQualifiedName;
                if (!seen.Add(key)) continue;
                object font = letter.Font;
                Console.WriteLine($"  FONTE: nome={letter.FontName}; letraExemplo='{Escape(letter.Value)}'; tipo={font.GetType().FullName}");
                DumpMembers(font, "    ", 0);
            }
        }
        Console.WriteLine($"  Tipos de fonte inspecionados: {seen.Count}");
        Console.WriteLine("  Limite: inspeção de metadados/API em memória; não altera nem salva PDF.");
        Console.WriteLine();
    }

    private static void DumpMembers(object instance, string indent, int depth)
    {
        Type type = instance.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name))
        {
            object? value;
            try { value = property.GetValue(instance); }
            catch (Exception ex) { Console.WriteLine($"{indent}{property.Name}: <getter {ex.GetType().Name}>"); continue; }
            PrintValue(property.Name, property.PropertyType, value, indent, depth);
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     .OrderBy(f => f.Name))
        {
            object? value;
            try { value = field.GetValue(instance); }
            catch { continue; }
            PrintValue("campo:" + field.Name, field.FieldType, value, indent, depth);
        }

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && (m.Name.Contains("Unicode", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Code", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Glyph", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Encoding", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Character", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Font", StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + "):" + m.ReturnType.Name)
            .Distinct().Take(40).ToArray();
        foreach (string method in methods) Console.WriteLine($"{indent}API: {method}");
    }

    private static void PrintValue(string name, Type declaredType, object? value, string indent, int depth)
    {
        if (value is null) { Console.WriteLine($"{indent}{name}: null ({declaredType.Name})"); return; }
        if (value is string s) { Console.WriteLine($"{indent}{name}: \"{Escape(s)}\""); return; }
        if (value.GetType().IsPrimitive || value is decimal || value is Enum)
        { Console.WriteLine($"{indent}{name}: {value} ({declaredType.Name})"); return; }
        if (value is IDictionary dictionary)
        { Console.WriteLine($"{indent}{name}: {value.GetType().Name}, itens={dictionary.Count}"); return; }
        if (value is IEnumerable enumerable && value is not string)
        {
            int count = 0;
            foreach (var _ in enumerable) { count++; if (count >= 1000) break; }
            Console.WriteLine($"{indent}{name}: {value.GetType().Name}, itens~={count}");
            return;
        }
        Console.WriteLine($"{indent}{name}: objeto {value.GetType().FullName}");
        if (depth == 0 && (name.Contains("Encoding", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("CMap", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Unicode", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Dictionary", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Font", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Program", StringComparison.OrdinalIgnoreCase)))
            DumpMembers(value, indent + "  ", depth + 1);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
}