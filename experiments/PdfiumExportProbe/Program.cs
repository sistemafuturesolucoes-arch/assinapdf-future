using System.Reflection;
using System.Runtime.InteropServices;

Console.WriteLine("AssinaPDF - PDFium export probe");
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
    Console.WriteLine("Verifique se os assets nativos do pacote NuGet foram copiados para a saída.");
    return 2;
}

try
{
    Console.WriteLine($"Loaded: {loadedFrom}");
    string[] symbols =
    [
        "FPDF_InitLibrary",
        "FPDFText_LoadPage",
        "FPDFText_CountChars",
        "FPDFText_GetUnicode",
        "FPDFText_GetTextObject",
        "FPDFPage_CountObjects",
        "FPDFPage_GetObject",
        "FPDFTextObj_GetText",
        "FPDFText_GetFontInfo"
    ];

    bool allCore = true;
    foreach (string symbol in symbols)
    {
        bool found = NativeLibrary.TryGetExport(library, symbol, out _);
        Console.WriteLine($"{(found ? "FOUND " : "MISSING")} {symbol}");
        if (!found && symbol is "FPDF_InitLibrary" or "FPDFText_LoadPage" or "FPDFText_CountChars" or "FPDFText_GetUnicode")
            allCore = false;
    }

    bool hasCharacterObjectApi =
        NativeLibrary.TryGetExport(library, "FPDFText_GetTextObject", out _);
    Console.WriteLine();
    Console.WriteLine($"Core PDFium exports: {(allCore ? "OK" : "FAIL")}");
    Console.WriteLine($"FPDFText_GetTextObject: {(hasCharacterObjectApi ? "AVAILABLE" : "NOT EXPORTED")}");
    Console.WriteLine("This probe checks symbol availability only; it does not yet prove character-to-object mapping.");
    return allCore ? 0 : 3;
}
finally
{
    NativeLibrary.Free(library);
}
