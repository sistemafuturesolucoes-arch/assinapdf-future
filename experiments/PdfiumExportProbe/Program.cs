using System.Runtime.InteropServices;
using System.Text;

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
        Console.WriteLine();
        Console.WriteLine("Export check concluído. Para testar o mapeamento, informe o caminho de um PDF:");
        Console.WriteLine("dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj -- ""C:\\caminho\\arquivo.pdf""");
        return 0;
    }

    if (!File.Exists(pdfPath))
    {
        Console.WriteLine($"PDF não encontrado: {pdfPath}");
        return 4;
    }

    int pageIndex = 0;
    int pageArg = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
    if (pageArg >= 0 && (pageArg + 1 >= args.Length ||
        !int.TryParse(args[pageArg + 1], out pageIndex) || pageIndex < 0))
    {
        Console.WriteLine("Valor inválido para --page. Use índice começando em zero.");
        return 5;
    }

    var api = new PdfiumApi(library);
    api.InitLibrary();

    byte[] pdfBytes = File.ReadAllBytes(pdfPath);
    GCHandle pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
    IntPtr document = IntPtr.Zero;
    IntPtr page = IntPtr.Zero;
    IntPtr textPage = IntPtr.Zero;
    try
    {
        document = api.LoadMemDocument64(pinned.AddrOfPinnedObject(), (nuint)pdfBytes.Length, IntPtr.Zero);
        if (document == IntPtr.Zero)
        {
            Console.WriteLine("PDFium não conseguiu abrir o PDF (arquivo protegido, inválido ou incompatível).");
            return 6;
        }

        int pageCount = api.GetPageCount(document);
        if (pageIndex >= pageCount)
        {
            Console.WriteLine($"Página solicitada {pageIndex} fora do intervalo. O PDF tem {pageCount} página(s); use índice 0 a {pageCount - 1}.");
            return 7;
        }

        page = api.LoadPage(document, pageIndex);
        if (page == IntPtr.Zero)
        {
            Console.WriteLine($"PDFium não conseguiu carregar a página de índice {pageIndex}.");
            return 8;
        }

        textPage = api.TextLoadPage(page);
        if (textPage == IntPtr.Zero)
        {
            Console.WriteLine("PDFium não conseguiu criar a página de texto.");
            return 9;
        }

        int charCount = api.TextCountChars(textPage);
        int objectCount = api.PageCountObjects(page);
        if (charCount < 0 || objectCount < 0 || charCount > 1_000_000 || objectCount > 1_000_000)
        {
            Console.WriteLine($"Contagens inválidas/fora do limite: chars={charCount}, objects={objectCount}.");
            return 10;
        }

        var objectIndices = new Dictionary<IntPtr, int>();
        for (int i = 0; i < objectCount; i++)
        {
            IntPtr obj = api.PageGetObject(page, i);
            if (obj != IntPtr.Zero)
                objectIndices[obj] = i;
        }

        var characterGroups = new Dictionary<IntPtr, StringBuilder>();
        int nullMappings = 0;
        int unmatchedObjects = 0;
        Console.WriteLine();
        Console.WriteLine($"PDF: {Path.GetFileName(pdfPath)} | Página: {pageIndex + 1}/{pageCount}");
        Console.WriteLine($"Caracteres PDFium: {charCount} | Objetos da página: {objectCount}");
        Console.WriteLine("Índice | Caractere | Objeto da página | Endereço do objeto");

        for (int i = 0; i < charCount; i++)
        {
            uint unicode = api.GetUnicode(textPage, i);
            string character = unicode == 0 || !Rune.IsValid((int)unicode)
                ? "�"
                : char.ConvertFromUtf32((int)unicode);

            IntPtr textObject = api.GetTextObject(textPage, i);
            if (textObject == IntPtr.Zero)
            {
                nullMappings++;
                Console.WriteLine($"{i,6} | {Escape(character),-10} | NULL");
                continue;
            }

            if (!characterGroups.TryGetValue(textObject, out StringBuilder? group))
                characterGroups[textObject] = group = new StringBuilder();
            group.Append(character);

            if (objectIndices.TryGetValue(textObject, out int objectIndex))
                Console.WriteLine($"{i,6} | {Escape(character),-10} | {objectIndex,16} | 0x{textObject.ToInt64():X}");
            else
            {
                unmatchedObjects++;
                Console.WriteLine($"{i,6} | {Escape(character),-10} | NÃO LOCALIZADO | 0x{textObject.ToInt64():X}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Resumo por objeto (texto agrupado pela identidade do ponteiro):");
        foreach (var pair in characterGroups.OrderBy(p =>
                     objectIndices.TryGetValue(p.Key, out int idx) ? idx : int.MaxValue))
        {
            string objectLabel = objectIndices.TryGetValue(pair.Key, out int index)
                ? index.ToString()
                : "não localizado";
            string? objectText = api.GetTextObjectText(pair.Key, textPage);
            Console.WriteLine($"Objeto {objectLabel}: caracteres='{Escape(pair.Value.ToString())}' | texto-do-objeto='{Escape(objectText ?? "(indisponível)")}'");
        }

        Console.WriteLine();
        Console.WriteLine($"Objetos de texto distintos associados a caracteres: {characterGroups.Count}");
        Console.WriteLine($"Caracteres sem objeto (NULL): {nullMappings}");
        Console.WriteLine($"Ponteiros não encontrados na enumeração da página: {unmatchedObjects}");
        Console.WriteLine("Este resultado comprova associação caractere→objeto somente para este PDF/página; não revela bytes nem operadores Tj/TJ.");
        return nullMappings == 0 && unmatchedObjects == 0 ? 0 : 11;
    }
    finally
    {
        if (textPage != IntPtr.Zero) api.TextClosePage(textPage);
        if (page != IntPtr.Zero) api.ClosePage(page);
        if (document != IntPtr.Zero) api.CloseDocument(document);
        pinned.Free();
    }
}
finally
{
    NativeLibrary.Free(library);
}

static string Escape(string value) =>
    value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

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
