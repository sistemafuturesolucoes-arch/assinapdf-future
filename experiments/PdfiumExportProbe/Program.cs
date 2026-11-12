using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

Console.WriteLine("AssinaPDF - PDFium character/object probe");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Base directory: {AppContext.BaseDirectory}");

string expectedFile = OperatingSystem.IsWindows()
    ? "pdfium.dll"
    : OperatingSystem.IsMacOS()
        ? "libpdfium.dylib"
        : throw new PlatformNotSupportedException("Probe disponível somente para Windows e macOS.");

var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (string file in Directory.EnumerateFiles(
    AppContext.BaseDirectory, expectedFile, SearchOption.AllDirectories))
    candidates.Add(file);
candidates.Add(expectedFile);

IntPtr library = IntPtr.Zero;
string? loadedFrom = null;
foreach (string candidate in candidates)
{
    if (NativeLibrary.TryLoad(candidate, out library))
    {
        loadedFrom = candidate;
        break;
    }
}

if (library == IntPtr.Zero)
{
    Console.WriteLine($"RESULT: PDFium não foi carregado ({expectedFile} não encontrado/carregável).");
    return 2;
}

try
{
    Console.WriteLine($"Loaded: {loadedFrom}");
    string[] symbols =
    [
        "FPDF_InitLibrary", "FPDF_LoadMemDocument64", "FPDF_GetPageCount",
        "FPDF_LoadPage", "FPDF_ClosePage", "FPDF_CloseDocument",
        "FPDFText_LoadPage", "FPDFText_ClosePage", "FPDFText_CountChars",
        "FPDFText_GetUnicode", "FPDFText_GetTextObject",
        "FPDFPage_CountObjects", "FPDFPage_GetObject", "FPDFTextObj_GetText"
    ];

    bool exportsOk = true;
    foreach (string symbol in symbols)
    {
        bool found = NativeLibrary.TryGetExport(library, symbol, out _);
        Console.WriteLine($"{(found ? "FOUND " : "MISSING")} {symbol}");
        exportsOk &= found;
    }

    if (!exportsOk)
    {
        Console.WriteLine("RESULT: faltam exports necessários; nenhuma chamada de análise foi executada.");
        return 3;
    }

    string? pdfPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
    if (pdfPath is null)
    {
        pdfPath = FindLatestEditorPdf();
        if (pdfPath is null)
        {
            Console.WriteLine("Nenhum PDF existente foi encontrado no histórico do editor AssinaPDF.");
            Console.WriteLine("Abra um PDF no editor e execute novamente, ou informe um caminho explicitamente.");
            return 12;
        }

        Console.WriteLine($"PDF selecionado automaticamente do histórico do editor: {pdfPath}");
    }

    if (!File.Exists(pdfPath))
    {
        Console.WriteLine($"PDF não encontrado: {pdfPath}");
        return 4;
    }

    var api = new PdfiumApi(library);
    api.InitLibrary();

    byte[] pdfBytes = File.ReadAllBytes(pdfPath);
    GCHandle pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
    IntPtr document = IntPtr.Zero;
    try
    {
        document = api.LoadMemDocument64(pinned.AddrOfPinnedObject(), (nuint)pdfBytes.Length, IntPtr.Zero);
        if (document == IntPtr.Zero)
        {
            Console.WriteLine("PDFium não conseguiu abrir o PDF (arquivo protegido, inválido ou incompatível).");
            return 6;
        }

        int pageCount = api.GetPageCount(document);
        if (pageCount <= 0 || pageCount > 100_000)
        {
            Console.WriteLine($"Contagem de páginas inválida/fora do limite: {pageCount}.");
            return 7;
        }

        Console.WriteLine();
        Console.WriteLine($"PDF: {Path.GetFileName(pdfPath)}");
        Console.WriteLine($"Total de páginas: {pageCount}");
        Console.WriteLine();
        Console.WriteLine($"{"Página",6} | {"Caracteres",10} | {"Objetos associados",18} | {"NULL",8} | {"Não localizados",15} | Status");
        Console.WriteLine(new string('-', 83));

        long totalCharacters = 0;
        long totalAssociatedObjects = 0;
        long totalNullMappings = 0;
        long totalUnmatched = 0;
        int failedPages = 0;

        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var result = AnalyzePage(api, document, pageIndex);
            if (result.Error)
            {
                failedPages++;
                Console.WriteLine($"{pageIndex + 1,6} | {"-",10} | {"-",18} | {"-",8} | {"-",15} | ERRO");
                continue;
            }

            totalCharacters += result.Characters;
            totalAssociatedObjects += result.Objects;
            totalNullMappings += result.NullMappings;
            totalUnmatched += result.Unmatched;

            string status = result.NullMappings == 0 && result.Unmatched == 0 ? "OK" : "REVISAR";
            Console.WriteLine($"{pageIndex + 1,6} | {result.Characters,10} | {result.Objects,18} | {result.NullMappings,8} | {result.Unmatched,15} | {status}");
        }

        Console.WriteLine(new string('-', 83));
        string finalStatus = failedPages > 0 ? $"{failedPages} ERRO(S)" : "CONCLUÍDO";
        Console.WriteLine($"{ "TOTAL",6} | {totalCharacters,10} | {totalAssociatedObjects,18} | {totalNullMappings,8} | {totalUnmatched,15} | {finalStatus}");
        Console.WriteLine();
        Console.WriteLine("Objetos associados são contados por página; o total soma essas contagens.");
        Console.WriteLine("NULL e ponteiros não localizados são métricas de diagnóstico, não prova isolada de corrupção.");
        Console.WriteLine("O teste não revela bytes nem operadores Tj/TJ e não modifica o PDF.");
        return failedPages > 0 ? 13 : totalNullMappings == 0 && totalUnmatched == 0 ? 0 : 11;
    }
    finally
    {
        if (document != IntPtr.Zero) api.CloseDocument(document);
        pinned.Free();
    }
}
finally
{
    NativeLibrary.Free(library);
}

static (int Characters, int Objects, int NullMappings, int Unmatched, bool Error)
    AnalyzePage(PdfiumApi api, IntPtr document, int pageIndex)
{
    IntPtr page = IntPtr.Zero;
    IntPtr textPage = IntPtr.Zero;

    try
    {
        page = api.LoadPage(document, pageIndex);
        if (page == IntPtr.Zero)
            return (0, 0, 0, 0, true);

        textPage = api.TextLoadPage(page);
        if (textPage == IntPtr.Zero)
            return (0, 0, 0, 0, true);

        int charCount = api.TextCountChars(textPage);
        int objectCount = api.PageCountObjects(page);
        if (charCount < 0 || objectCount < 0 ||
            charCount > 1_000_000 || objectCount > 1_000_000)
            return (0, 0, 0, 0, true);

        var pageObjects = new HashSet<IntPtr>();
        for (int i = 0; i < objectCount; i++)
        {
            IntPtr obj = api.PageGetObject(page, i);
            if (obj != IntPtr.Zero)
                pageObjects.Add(obj);
        }

        var associatedObjects = new HashSet<IntPtr>();
        int nullMappings = 0;
        int unmatched = 0;

        for (int i = 0; i < charCount; i++)
        {
            IntPtr textObject = api.GetTextObject(textPage, i);
            if (textObject == IntPtr.Zero)
            {
                nullMappings++;
                continue;
            }

            associatedObjects.Add(textObject);
            if (!pageObjects.Contains(textObject))
                unmatched++;
        }

        return (charCount, associatedObjects.Count, nullMappings, unmatched, false);
    }
    catch
    {
        return (0, 0, 0, 0, true);
    }
    finally
    {
        if (textPage != IntPtr.Zero)
            api.TextClosePage(textPage);
        if (page != IntPtr.Zero)
            api.ClosePage(page);
    }
}

static string Escape(string value) =>
    value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

static string? FindLatestEditorPdf()
{
    string appData = OperatingSystem.IsMacOS()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    if (string.IsNullOrWhiteSpace(appData))
        appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    string root = Path.Combine(appData, "FutureSolucoes", "AssinaPDF", "DocumentosEditados");
    string historyPath = Path.Combine(root, "historico-edicao.json");
    if (!File.Exists(historyPath))
        return null;

    try
    {
        using JsonDocument history = JsonDocument.Parse(File.ReadAllText(historyPath));
        if (history.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = new List<(DateTime LastModified, string Path)>();
        foreach (JsonElement entry in history.RootElement.EnumerateArray())
        {
            if (!TryGetString(entry, "WorkingCopyPath", out string? workingCopy) ||
                string.IsNullOrWhiteSpace(workingCopy) ||
                !File.Exists(workingCopy))
                continue;

            DateTime modified = File.GetLastWriteTimeUtc(workingCopy);
            if (TryGetString(entry, "LastModifiedAtUtc", out string? timestamp) &&
                DateTime.TryParse(timestamp, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                modified = parsed;

            candidates.Add((modified, workingCopy));
        }

        return candidates
            .OrderByDescending(candidate => candidate.LastModified)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }
    catch
    {
        return null;
    }

    static bool TryGetString(JsonElement element, string name, out string? value)
    {
        if (element.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }
}


sealed class PdfiumApi
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void InitLibraryDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr LoadMemDocument64Delegate(IntPtr data, nuint size, IntPtr password);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int GetPageCountDelegate(IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr LoadPageDelegate(IntPtr document, int index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ClosePageDelegate(IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void CloseDocumentDelegate(IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr TextLoadPageDelegate(IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void TextClosePageDelegate(IntPtr textPage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int TextCountCharsDelegate(IntPtr textPage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint GetUnicodeDelegate(IntPtr textPage, int index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr GetTextObjectDelegate(IntPtr textPage, int index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int PageCountObjectsDelegate(IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr PageGetObjectDelegate(IntPtr page, int index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint TextObjectGetTextDelegate(IntPtr textObject, IntPtr textPage, IntPtr buffer, uint length);

    internal readonly InitLibraryDelegate InitLibrary;
    internal readonly LoadMemDocument64Delegate LoadMemDocument64;
    internal readonly GetPageCountDelegate GetPageCount;
    internal readonly LoadPageDelegate LoadPage;
    internal readonly ClosePageDelegate ClosePage;
    internal readonly CloseDocumentDelegate CloseDocument;
    internal readonly TextLoadPageDelegate TextLoadPage;
    internal readonly TextClosePageDelegate TextClosePage;
    internal readonly TextCountCharsDelegate TextCountChars;
    internal readonly GetUnicodeDelegate GetUnicode;
    internal readonly GetTextObjectDelegate GetTextObject;
    internal readonly PageCountObjectsDelegate PageCountObjects;
    internal readonly PageGetObjectDelegate PageGetObject;
    private readonly TextObjectGetTextDelegate _textObjectGetText;

    internal PdfiumApi(IntPtr library)
    {
        T Get<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        InitLibrary = Get<InitLibraryDelegate>("FPDF_InitLibrary");
        LoadMemDocument64 = Get<LoadMemDocument64Delegate>("FPDF_LoadMemDocument64");
        GetPageCount = Get<GetPageCountDelegate>("FPDF_GetPageCount");
        LoadPage = Get<LoadPageDelegate>("FPDF_LoadPage");
        ClosePage = Get<ClosePageDelegate>("FPDF_ClosePage");
        CloseDocument = Get<CloseDocumentDelegate>("FPDF_CloseDocument");
        TextLoadPage = Get<TextLoadPageDelegate>("FPDFText_LoadPage");
        TextClosePage = Get<TextClosePageDelegate>("FPDFText_ClosePage");
        TextCountChars = Get<TextCountCharsDelegate>("FPDFText_CountChars");
        GetUnicode = Get<GetUnicodeDelegate>("FPDFText_GetUnicode");
        GetTextObject = Get<GetTextObjectDelegate>("FPDFText_GetTextObject");
        PageCountObjects = Get<PageCountObjectsDelegate>("FPDFPage_CountObjects");
        PageGetObject = Get<PageGetObjectDelegate>("FPDFPage_GetObject");
        _textObjectGetText = Get<TextObjectGetTextDelegate>("FPDFTextObj_GetText");
    }

    internal string? GetTextObjectText(IntPtr textObject, IntPtr textPage)
    {
        uint required = _textObjectGetText(textObject, textPage, IntPtr.Zero, 0);
        if (required < 2 || required > 4 * 1024 * 1024)
            return null;

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            uint written = _textObjectGetText(textObject, textPage, buffer, required);
            return written > 0 ? Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
