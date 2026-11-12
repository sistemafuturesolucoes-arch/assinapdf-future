using System.Reflection;
using System.Text;
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
    int candidateMappings = 0, ambiguousByteOperations = 0;
    var codeMap = new Dictionary<string, int>(StringComparer.Ordinal);
    var operandStats = new Dictionary<string, int>(StringComparer.Ordinal);
    var complexSamples = new List<string>();
    var glyphOperations = new List<(int Page, int Sequence, string Font, string Text, byte[] Bytes)>();
    var observedCodes = new Dictionary<(string Font, string Text), HashSet<string>>();
    for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
    {
        var page = document.GetPage(pageNumber);
        var lettersBySequence = page.Letters
            .GroupBy(letter => letter.TextSequence)
            .ToDictionary(group => group.Key, group => group.ToArray());

        int sequence = 0;
        int pageTextOps = 0, pageMapped = 0, pageUnmapped = 0, pageInterestingOperands = 0, pageTjOps = 0, pageTjArrayOps = 0;
        var seenSequences = new HashSet<int>();

        Console.WriteLine($"PÁGINA {pageNumber}: {page.Letters.Count} glifos/letras; {page.Operations.Count} operações totais");
        foreach (var operation in page.Operations)
        {
            if (operation.Operator is not ("Tj" or "TJ"))
                continue;

            sequence++;
            pageTextOps++;
            if (operation.Operator == "Tj") pageTjOps++;
            if (operation.Operator == "TJ") pageTjArrayOps++;
            seenSequences.Add(sequence);
            lettersBySequence.TryGetValue(sequence, out var letters);
            int count = letters?.Length ?? 0;
            pageMapped += count;

            string preview = letters is null
                ? "(nenhuma letra associada)"
                : MakePreview(string.Concat(letters.Select(x => x.Value)), 72);

            var bytesValue = operation.GetType().GetProperty("Bytes", BindingFlags.Public | BindingFlags.Instance)?.GetValue(operation);
            if (bytesValue is ReadOnlyMemory<byte> operandBytes && operandBytes.Length > 0)
            {
                string font = letters is null || letters.Length == 0
                    ? "(sem letra associada)"
                    : string.Join(",", letters.Select(x => x.FontName).Distinct());
                string bucket = $"{font}|bytes={operandBytes.Length}|letras={count}";
                operandStats[bucket] = operandStats.TryGetValue(bucket, out int bucketCount) ? bucketCount + 1 : 1;

                if (operation.Operator == "Tj" && letters is { Length: 1 })
                {
                    string letterTextForSimulation = letters[0].Value;
                    string fontForSimulation = letters[0].FontName ?? "(fonte desconhecida)";
                    if (letterTextForSimulation.EnumerateRunes().Count() == 1)
                    {
                        byte[] rawBytes = operandBytes.ToArray();
                        glyphOperations.Add((pageNumber, sequence, fontForSimulation, letterTextForSimulation, rawBytes));
                        var mapKey = (fontForSimulation, letterTextForSimulation);
                        if (!observedCodes.TryGetValue(mapKey, out var codes))
                        {
                            codes = new HashSet<string>(StringComparer.Ordinal);
                            observedCodes.Add(mapKey, codes);
                        }
                        codes.Add(Convert.ToHexString(rawBytes));
                    }
                }

                if (count > 0 && operandBytes.Length == 2 && count == 1)
                {
                    string letterText = letters![0].Value;
                    string code = Convert.ToHexString(operandBytes.Span);
                    string unicode = string.Join("+", letterText.EnumerateRunes().Select(rune => $"U+{rune.Value:X4}"));
                    string key = $"{font}|{code}|{unicode}|'{Escape(letterText)}'";
                    codeMap[key] = codeMap.TryGetValue(key, out int occurrences) ? occurrences + 1 : 1;
                    candidateMappings++;
                }
                else if (count > 0)
                {
                    ambiguousByteOperations++;
                    if (complexSamples.Count < 40 && (count > 1 || operandBytes.Length > 2))
                    {
                        string hex = Convert.ToHexString(operandBytes.Span[..Math.Min(operandBytes.Length, 32)]);
                        string textValue = string.Concat(letters!.Select(x => x.Value));
                        complexSamples.Add($"pág={pageNumber}; seq={sequence}; fonte={font}; bytes={operandBytes.Length}; letras={count}; hex={hex}; texto='{Escape(MakePreview(textValue, 32))}'");
                    }
                }
            }

            Console.WriteLine($"  seq={sequence,4} op={operation.Operator} tipo={operation.GetType().Name,-28} letras={count,4} texto=\"{Escape(preview)}\"");
            if (HasInterestingOperands(operation))
            {
                pageInterestingOperands++;
                if (pageInterestingOperands <= 30)
                {
                    string fonts = letters is null ? "(sem letra)" : string.Join(",", letters.Select(x => x.FontName).Distinct());
                    Console.WriteLine($"       OPERANDO: fonte={fonts}; {DescribeOperands(operation)}");
                }
            }
        }

        foreach (var group in lettersBySequence)
            if (!seenSequences.Contains(group.Key))
                pageUnmapped += group.Value.Length;

        totalLetters += page.Letters.Count;
        totalTextOps += pageTextOps;
        totalMappedLetters += pageMapped;
        totalUnmappedLetters += pageUnmapped;

        Console.WriteLine($"  RESUMO página {pageNumber}: Tj={pageTjOps}; TJ={pageTjArrayOps}; letras associadas={pageMapped}; letras sem operação visível={pageUnmapped}; operandos com múltiplos bytes/arrays={pageInterestingOperands}");
        if (pageInterestingOperands > 30)
            Console.WriteLine($"  (mostrados somente os primeiros 30 de {pageInterestingOperands} operandos interessantes)");
        Console.WriteLine();
    }

    Console.WriteLine("MAPEAMENTO CANDIDATO: operando de 2 bytes associado a exatamente 1 Letter");
    Console.WriteLine("Fonte | Código hexadecimal | Unicode | Caractere | Ocorrências");
    foreach (var entry in codeMap.OrderBy(x => x.Key, StringComparer.Ordinal).Take(100))
    {
        var parts = entry.Key.Split('|');
        Console.WriteLine($"  {parts[0]} | {parts[1]} | {parts[2]} | {parts[3]} | {entry.Value}");
    }
    if (codeMap.Count > 100)
        Console.WriteLine($"  (mostrados 100 de {codeMap.Count} combinações)");
    Console.WriteLine($"  Associações candidatas observadas: {candidateMappings}; operações com bytes fora da regra 2 bytes/1 letra: {ambiguousByteOperations}");
    Console.WriteLine();

    Console.WriteLine("DISTRIBUIÇÃO DE OPERANDOS POR FONTE / TAMANHO / LETRAS");
    foreach (var entry in operandStats.OrderBy(x => x.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {entry.Key}: {entry.Value}");
    Console.WriteLine();

    Console.WriteLine("AMOSTRAS DE OPERAÇÕES COM MÚLTIPLAS LETRAS OU MAIS DE 2 BYTES");
    foreach (string sample in complexSamples)
        Console.WriteLine($"  {sample}");
    if (complexSamples.Count == 0)
        Console.WriteLine("  Nenhuma encontrada nas operações com Bytes expostos.");
    Console.WriteLine();

    Console.WriteLine("SIMULAÇÃO DE SUBSTITUIÇÃO EM MEMÓRIA (sem alterar/salvar PDF)");
    const string targetText = "CONFIRME";
    const string replacementText = "BONFIRME";
    bool simulationCompleted = false;
    foreach (var pageGroup in glyphOperations.GroupBy(x => x.Page).OrderBy(x => x.Key))
    {
        var ops = pageGroup.OrderBy(x => x.Sequence).ToArray();
        for (int start = 0; start <= ops.Length - targetText.Length; start++)
        {
            var window = ops.Skip(start).Take(targetText.Length).ToArray();
            if (window.Length != targetText.Length ||
                window.Select((item, index) => item.Sequence == window[0].Sequence + index).Any(ok => !ok) ||
                window.Any(item => item.Font != window[0].Font || item.Text.EnumerateRunes().Count() != 1) ||
                string.Concat(window.Select(item => item.Text)) != targetText)
                continue;

            string font = window[0].Font;
            string replacementGlyph = replacementText[..1];
            if (!observedCodes.TryGetValue((font, replacementGlyph), out var replacementCodes) ||
                replacementCodes.Count != 1)
            {
                Console.WriteLine($"  NÃO EXECUTADA: fonte '{font}' não possui código único observado para '{replacementGlyph}'.");
                simulationCompleted = true;
                break;
            }

            byte[][] original = window.Select(item => item.Bytes.ToArray()).ToArray();
            byte[] replacementBytes = Convert.FromHexString(replacementCodes.Single());
            if (replacementBytes.Length != original[0].Length)
            {
                Console.WriteLine($"  NÃO EXECUTADA: código substituto tem {replacementBytes.Length} byte(s), mas o original tem {original[0].Length}.");
                simulationCompleted = true;
                break;
            }
            byte[][] simulated = original.Select(bytes => bytes.ToArray()).ToArray();
            simulated[0] = replacementBytes;

            bool neighborsUnchanged = Enumerable.Range(1, targetText.Length - 1)
                .All(index => original[index].SequenceEqual(simulated[index]));
            bool replacementChanged = !original[0].SequenceEqual(simulated[0]);
            Console.WriteLine($"  Página: {pageGroup.Key}; sequência inicial: {window[0].Sequence}; fonte: {font}");
            Console.WriteLine($"  Texto original: {targetText}; texto simulado: {replacementText}");
            Console.WriteLine($"  Glifo alterado: '{window[0].Text}' [{Convert.ToHexString(original[0])}] -> '{replacementGlyph}' [{Convert.ToHexString(simulated[0])}]");
            Console.WriteLine($"  Bytes dos outros {targetText.Length - 1} glifos preservados: {(neighborsUnchanged ? "SIM" : "NÃO")}");
            Console.WriteLine($"  Bytes do glifo selecionado alterados: {(replacementChanged ? "SIM" : "NÃO")}");
            Console.WriteLine("  Limite: simulação de operandos em memória; não reescreve content stream, não recalcula avanço tipográfico e não salva PDF.");
            simulationCompleted = true;
            break;
        }
        if (simulationCompleted) break;
    }
    if (!simulationCompleted)
        Console.WriteLine($"  NÃO EXECUTADA: não foi encontrada uma sequência contínua e unívoca para '{targetText}' na mesma fonte.");
    Console.WriteLine();

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
    Console.WriteLine("- O mapeamento candidato é apenas uma correlação operando/fonte/Unicode quando há exatamente 2 bytes e 1 Letter; não prova a codificação completa nem o offset no stream original.");
    Console.WriteLine("- A distribuição separa operandos de 1 byte, 2 bytes e tamanhos maiores, além da quantidade de Letters associadas.");
    Console.WriteLine("- Este teste não reescreve nem salva PDFs.");
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
        parts.Add($"BytesLength={bytes.Length}; HexBytes={Convert.ToHexString(bytes.Span[..Math.Min(bytes.Length, 48)])}{(bytes.Length > 48 ? "..." : "")}");

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
