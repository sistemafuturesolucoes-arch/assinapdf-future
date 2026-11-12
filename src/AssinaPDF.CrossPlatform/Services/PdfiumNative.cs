
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AssinaPDF.MacPrototype.Services;

internal static class PdfiumNative
{
    private const string LibraryName = "pdfium";

    private const CallingConvention NativeCallingConvention =
        CallingConvention.Winapi;

    internal const int BitmapBgra = 4;

    internal static readonly object SyncRoot = new();

    static PdfiumNative()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(PdfiumNative).Assembly,
            ResolveLibrary);
    }

    private static IntPtr ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
            return IntPtr.Zero;

        if (OperatingSystem.IsWindows())
        {
            if (NativeLibrary.TryLoad(
                "pdfium.dll",
                assembly,
                searchPath,
                out IntPtr windowsLibrary))
            {
                return windowsLibrary;
            }

            throw new DllNotFoundException(
                "Não foi possível localizar pdfium.dll.");
        }

        if (OperatingSystem.IsMacOS())
        {
            string architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new PlatformNotSupportedException(
                    "Arquitetura do macOS não suportada pelo PDFium.")
            };

            string[] possiblePaths =
            {
                Path.Combine(
                    AppContext.BaseDirectory,
                    "libpdfium.dylib"),

                Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
                    architecture,
                    "native",
                    "libpdfium.dylib")
            };

            foreach (string libraryPath in possiblePaths)
            {
                if (File.Exists(libraryPath) &&
                    NativeLibrary.TryLoad(
                        libraryPath,
                        out IntPtr macLibrary))
                {
                    return macLibrary;
                }
            }

            if (NativeLibrary.TryLoad(
                "libpdfium.dylib",
                assembly,
                searchPath,
                out IntPtr fallbackLibrary))
            {
                return fallbackLibrary;
            }

            throw new DllNotFoundException(
                $"Não foi possível localizar o PDFium para {architecture}. " +
                $"Diretório da aplicação: {AppContext.BaseDirectory}");
        }

        throw new PlatformNotSupportedException(
            "Sistema operacional não suportado pelo PDFium.");
    }

    [DllImport(
        LibraryName,
        EntryPoint = "FPDFDOC_InitFormFillEnvironment",
        CallingConvention = NativeCallingConvention)]
    internal static extern IntPtr InitFormFillEnvironment(
        IntPtr document,
        IntPtr formFillInfo);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDFDOC_ExitFormFillEnvironment",
        CallingConvention = NativeCallingConvention)]
    internal static extern void ExitFormFillEnvironment(IntPtr formHandle);

    [DllImport(
        LibraryName,
        EntryPoint = "FORM_OnAfterLoadPage",
        CallingConvention = NativeCallingConvention)]
    internal static extern void FormOnAfterLoadPage(
        IntPtr page,
        IntPtr formHandle);

    [DllImport(
        LibraryName,
        EntryPoint = "FORM_OnBeforeClosePage",
        CallingConvention = NativeCallingConvention)]
    internal static extern void FormOnBeforeClosePage(
        IntPtr page,
        IntPtr formHandle);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_FFLDraw",
        CallingConvention = NativeCallingConvention)]
    internal static extern void FormFillDraw(
        IntPtr formHandle,
        IntPtr bitmap,
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_InitLibrary",
        CallingConvention = NativeCallingConvention)]
    internal static extern void InitLibrary();

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_LoadMemDocument64",
        CallingConvention = NativeCallingConvention)]
    internal static extern IntPtr LoadMemDocument64(
        IntPtr dataBuffer,
        nuint size,
        IntPtr password);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_GetLastError",
        CallingConvention = NativeCallingConvention)]
    internal static extern uint GetLastError();

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_GetPageCount",
        CallingConvention = NativeCallingConvention)]
    internal static extern int GetPageCount(IntPtr document);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_LoadPage",
        CallingConvention = NativeCallingConvention)]
    internal static extern IntPtr LoadPage(
        IntPtr document,
        int pageIndex);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_GetPageWidthF",
        CallingConvention = NativeCallingConvention)]
    internal static extern float GetPageWidth(IntPtr page);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_GetPageHeightF",
        CallingConvention = NativeCallingConvention)]
    internal static extern float GetPageHeight(IntPtr page);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDFBitmap_CreateEx",
        CallingConvention = NativeCallingConvention)]
    internal static extern IntPtr BitmapCreateEx(
        int width,
        int height,
        int format,
        IntPtr firstScan,
        int stride);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDFBitmap_FillRect",
        CallingConvention = NativeCallingConvention)]
    internal static extern void BitmapFillRect(
        IntPtr bitmap,
        int left,
        int top,
        int width,
        int height,
        uint color);
    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_RenderPageBitmap",
        CallingConvention = NativeCallingConvention)]
    internal static extern void RenderPageBitmap(
        IntPtr bitmap,
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDFBitmap_GetBuffer",
        CallingConvention = NativeCallingConvention)]
    internal static extern IntPtr BitmapGetBuffer(IntPtr bitmap);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDFBitmap_GetStride",
        CallingConvention = NativeCallingConvention)]
    internal static extern int BitmapGetStride(IntPtr bitmap);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDFBitmap_Destroy",
        CallingConvention = NativeCallingConvention)]
    internal static extern void BitmapDestroy(IntPtr bitmap);

    [DllImport(LibraryName, EntryPoint = "FPDFPage_CountObjects", CallingConvention = NativeCallingConvention)]
    internal static extern int PageCountObjects(IntPtr page);

    [DllImport(LibraryName, EntryPoint = "FPDFPage_GetObject", CallingConvention = NativeCallingConvention)]
    internal static extern IntPtr PageGetObject(IntPtr page, int index);

    [DllImport(LibraryName, EntryPoint = "FPDFPageObj_GetType", CallingConvention = NativeCallingConvention)]
    internal static extern int PageObjectGetType(IntPtr pageObject);

    [DllImport(LibraryName, EntryPoint = "FPDFPageObj_GetBounds", CallingConvention = NativeCallingConvention)]
    internal static extern int PageObjectGetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);

    [DllImport(LibraryName, EntryPoint = "FPDFText_LoadPage", CallingConvention = NativeCallingConvention)]
    internal static extern IntPtr TextLoadPage(IntPtr page);

    [DllImport(LibraryName, EntryPoint = "FPDFText_ClosePage", CallingConvention = NativeCallingConvention)]
    internal static extern void TextClosePage(IntPtr textPage);

    [DllImport(LibraryName, EntryPoint = "FPDFTextObj_GetText", CallingConvention = NativeCallingConvention)]
    internal static extern uint TextObjectGetText(IntPtr textObject, IntPtr textPage, IntPtr buffer, uint length);

    [DllImport(LibraryName, EntryPoint = "FPDFTextObj_GetFontSize", CallingConvention = NativeCallingConvention)]
    internal static extern int TextObjectGetFontSize(IntPtr textObject, out float fontSize);

    [DllImport(LibraryName, EntryPoint = "FPDFText_CountChars", CallingConvention = NativeCallingConvention)]
    internal static extern int TextCountChars(IntPtr textPage);

    [DllImport(LibraryName, EntryPoint = "FPDFText_GetUnicode", CallingConvention = NativeCallingConvention)]
    internal static extern uint TextGetUnicode(IntPtr textPage, int index);

    [DllImport(LibraryName, EntryPoint = "FPDFText_GetFontSize", CallingConvention = NativeCallingConvention)]
    internal static extern double TextGetCharacterFontSize(IntPtr textPage, int index);

    [DllImport(LibraryName, EntryPoint = "FPDFText_GetCharBox", CallingConvention = NativeCallingConvention)]
    internal static extern int TextGetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);

    // API experimental do PDFium: alguns builds podem não exportá-la.
    [DllImport(LibraryName, EntryPoint = "FPDFText_GetFontInfo", CallingConvention = NativeCallingConvention)]
    internal static extern nuint TextGetFontInfo(IntPtr textPage, int index, IntPtr buffer, nuint bufferLength, out int flags);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_RenderPage",
        CallingConvention = NativeCallingConvention)]
    internal static extern void RenderPageToDevice(
        IntPtr deviceContext,
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_ClosePage",
        CallingConvention = NativeCallingConvention)]
    internal static extern void ClosePage(IntPtr page);

    [DllImport(
        LibraryName,
        EntryPoint = "FPDF_CloseDocument",
        CallingConvention = NativeCallingConvention)]
    internal static extern void CloseDocument(IntPtr document);
}
