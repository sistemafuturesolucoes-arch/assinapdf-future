using System.Reflection;
using System.Text.Json;
using UglyToad.PdfPig;

Console.WriteLine("AssinaPDF - PDF content operation / glyph sequence probe");
Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

string? pdfPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
if (pdfPath is null)
{
    pdfPath = FindLatestEditorPdf();
    if (pdfPath is not null)
        Console.WriteLine($"PDF selecionado do histórico do editor: {pdfPath}");
}
if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
{
    Console.Error.WriteLine("PDF não encontrado. Abra um PDF no AssinaPDF ou informe o caminho como argumento.");
    return 2;
}

try
{
    using var document = PdfDocument.Open(pdfPath);
    Console.WriteLine($"PDF: {Path.GetFileName(pdfPath)}");
    Console.WriteLine($"Páginas: {document.NumberOfPages}");
    Console.WriteLine("Atenção: análise somente leitura; nenhum arquivo será alterado.");
    Console.WriteLine();

    int totalLetters = 0, totalTextOps = 0, totalMappedLetters = 0, totalUnmappedLetters = 0;
    for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
    {
        var page = document.GetPage(pageNumber);
        var lettersBySequence = page.Letters
            .GroupBy(letter => letter.TextSequence)
            .ToDictionary(group => group.Key, group => group.ToArray());

        int sequence = 0;
        int pageTextOps = 0, pageMapped = 0, pageUnmapped = 0, pageInterestingOperands = 0;
        var seenSequences = new HashSet<int>();

        Console.WriteLine($"PÁGINA {pageNumber}: {page.Letters.Count} glifos/letras; {page.Operations.Count} operações totais");
        foreach (var operation in page.Operations)
        {
            if (operation.Operator is not ("Tj" or "TJ"))
                continue;

            sequence++;
            pageTextOps++;
            seenSequences.Add(sequence);
            lettersBySequence.TryGetValue(sequence, out var letters);
            int count = letters?.Length ?? 0;
            pageMapped += count;

            string preview = letters is null
                ? "(nenhuma letra associada)"
                : MakePreview(string.Concat(letters.Select(x => x.Value)), 72);

            Console.WriteLine($"  seq={sequence,4} op={operation.Operator} tipo={operation.GetType().Name,-28} letras={count,4} texto=\"{Escape(preview)}\"");
            if (HasInterestingOperands(operation))
            {
                pageInterestingOperands++;
                Console.WriteLine($"       OPERANDO INTERESSANTE: {DescribeOperands(operation)}");
            }
        }

        foreach (var group in lettersBySequence)
            if (!seenSequences.Contains(group.Key))
                pageUnmapped += group.Value.Length;

        totalLetters += page.Letters.Count;
        totalTextOps += pageTextOps;
        totalMappedLetters += pageMapped;
        totalUnmappedLetters += pageUnmapped;

        Console.WriteLine($"  RESUMO página {pageNumber}: operações Tj/TJ={pageTextOps}; letras associadas={pageMapped}; letras sem operação Tj/TJ visível nesta página={pageUnmapped}; operandos com múltiplos bytes/arrays TJ={pageInterestingOperands}");
        Console.WriteLine();
    }

    Console.WriteLine("RESUMO GERAL");
    Console.WriteLine($"  Letras: {totalLetters}");
    Console.WriteLine($"  Operações Tj/TJ visíveis: {totalTextOps}");
    Console.WriteLine($"  Letras ligadas por TextSequence: {totalMappedLetters}");
    Console.WriteLine($"  Letras sem operação Tj/TJ visível: {totalUnmappedLetters}");
    Console.WriteLine();
    Console.WriteLine("INTERPRETAÇÃO");
    Console.WriteLine("- TextSequence identifica a chamada de exibição de texto que produziu cada Letter no PdfPig.");
    Console.WriteLine("- A associação só é considerada direta quando a sequência encontra uma operação Tj/TJ na lista pública da página.");
    Console.WriteLine("- Letras sem operação visível podem vir de Form XObjects/conteúdo aninhado ou de diferenças do parser; não são atribuídas por aproximação.");
    Console.WriteLine("- Este teste ainda não fornece offset de bytes no stream original, nem reescreve ou salva PDFs.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Falha ao analisar PDF: {ex.GetType().Name}: {ex.Message}");
    return 10;
}

static bool HasInterestingOperands(object operation)
{
    var type = operation.GetType();
    var bytesValue = type.GetProperty("Bytes", BindingFlags.Public | BindingFlags.Instance)?.GetValue(operation);
    if (bytesValue is ReadOnlyMemory<byte> bytes && bytes.Length > 1)
        return true;

    var arrayValue = type.GetProperty("Array", BindingFlags.Public | BindingFlags.Instance)?.GetValue(operation);
    return arrayValue is System.Collections.IEnumerable;
}

static string DescribeOperands(object operation)
{
    var parts = new List<string>();
    var type = operation.GetType();
    var text = type.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance)?.GetValue(operation) as string;
    if (text is not null)
        parts.Add($"Text={MakePreview(text, 100)}");

    var bytesValue = type.GetProperty("Bytes", BindingFlags.Public | BindingFlags.Instance)?.GetValue(operation);
    if (bytesValue is ReadOnlyMemory<byte> bytes)
        parts.Add($"HexBytes={Convert.ToHexString(bytes.Span[..Math.Min(bytes.Length, 48)])}{(bytes.Length > 48 ? "..." : "")}");

    var arrayValue = type.GetProperty("Array", BindingFlags.Public | BindingFlags.Instance)?.GetValue(operation);
    if (arrayValue is System.Collections.IEnumerable items)
    {
        var values = new List<string>();
        foreach (var item in items)
        {
            if (item is null) continue;
            values.Add($"{item.GetType().Name}:{MakePreview(item.ToString() ?? "", 48)}");
            if (values.Count == 12) break;
        }
        parts.Add("Array=[" + string.Join(" | ", values) + "]");
    }

    return parts.Count == 0 ? "(operandos não expostos por propriedades públicas conhecidas)" : string.Join("; ", parts);
}

static string MakePreview(string value, int max) =>
    value.Length <= max ? value : value[..max] + "…";

static string Escape(string value) =>
    value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

static string? FindLatestEditorPdf()
{
    string appData = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(appData))
        return null;

    string root = Path.Combine(appData, "FutureSolucoes", "AssinaPDF", "DocumentosEditados");
    string historyPath = Path.Combine(root, "historico-edicao.json");
    if (!File.Exists(historyPath)) return null;

    try
    {
        using var history = JsonDocument.Parse(File.ReadAllText(historyPath));
        if (history.RootElement.ValueKind != JsonValueKind.Array) return null;
        var candidates = new List<(DateTime Modified, string Path)>();
        foreach (var entry in history.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("WorkingCopyPath", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String) continue;
            string? path = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            DateTime modified = File.GetLastWriteTimeUtc(path);
            if (entry.TryGetProperty("LastModifiedAtUtc", out var dateElement) &&
                dateElement.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(dateElement.GetString(), out var parsed))
                modified = parsed.ToUniversalTime();
            candidates.Add((modified, path));
        }
        return candidates.OrderByDescending(x => x.Modified).Select(x => x.Path).FirstOrDefault();
    }
    catch { return null; }
}
