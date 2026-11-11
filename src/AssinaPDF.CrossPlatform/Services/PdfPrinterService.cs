using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype.Services;

internal static class PdfPrinterService
{
    private const uint PdReturnDc = 0x00000100;
    private const uint PdUseDevModeCopiesAndCollate = 0x00040000;

    private const int HorzRes = 8;
    private const int VertRes = 10;
    private const int LogPixelsX = 88;
    private const int LogPixelsY = 90;

    [StructLayout(LayoutKind.Sequential)]
    private struct PrintDialogData
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hDevMode;
        public IntPtr hDevNames;
        public IntPtr hDC;
        public uint Flags;
        public ushort nFromPage;
        public ushort nToPage;
        public ushort nMinPage;
        public ushort nMaxPage;
        public ushort nCopies;
        public IntPtr hInstance;
        public IntPtr lCustData;
        public IntPtr lpfnPrintHook;
        public IntPtr lpfnSetupHook;
        public IntPtr lpPrintTemplateName;
        public IntPtr lpSetupTemplateName;
        public IntPtr hPrintTemplate;
        public IntPtr hSetupTemplate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszOutput;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszDatatype;
        public uint fwType;
    }

    [DllImport("comdlg32.dll", EntryPoint = "PrintDlgW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintDlg(ref PrintDialogData data);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", EntryPoint = "GetDeviceCaps", SetLastError = true)]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDoc(IntPtr hdc, ref DocInfo info);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StartPage(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndPage(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndDoc(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int AbortDoc(IntPtr hdc);

    public static bool ShowPrinterDialogAndPrint(
        PdfiumDocument document,
        string documentName,
        IntPtr ownerHandle)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A caixa de impressão nativa está disponível somente no Windows.");

        var dialog = new PrintDialogData
        {
            lStructSize = Marshal.SizeOf<PrintDialogData>(),
            hwndOwner = ownerHandle,
            Flags = PdReturnDc | PdUseDevModeCopiesAndCollate,
            nCopies = 1
        };

        bool documentStarted = false;
        try
        {
            if (!PrintDlg(ref dialog))
            {
                uint error = CommDlgExtendedError();
                if (error != 0)
                    throw new InvalidOperationException($"A caixa de impressão do Windows retornou o erro 0x{error:X8}.");
                return false;
            }

            if (dialog.hDC == IntPtr.Zero)
                throw new InvalidOperationException("O Windows não forneceu um contexto de impressão.");

            int printableWidth = GetDeviceCaps(dialog.hDC, HorzRes);
            int printableHeight = GetDeviceCaps(dialog.hDC, VertRes);
            int dpiX = GetDeviceCaps(dialog.hDC, LogPixelsX);
            int dpiY = GetDeviceCaps(dialog.hDC, LogPixelsY);

            if (printableWidth <= 0 || printableHeight <= 0 || dpiX <= 0 || dpiY <= 0)
                throw new InvalidOperationException("Não foi possível obter a área imprimível da impressora.");

            var info = new DocInfo
            {
                cbSize = Marshal.SizeOf<DocInfo>(),
                lpszDocName = documentName,
                lpszOutput = null,
                lpszDatatype = null,
                fwType = 0
            };

            if (StartDoc(dialog.hDC, ref info) <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "O Windows não conseguiu iniciar o trabalho de impressão.");
            documentStarted = true;

            for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                if (StartPage(dialog.hDC) <= 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Não foi possível iniciar a página {pageIndex + 1} da impressão.");

                var page = document.GetPageDimensionsPoints(pageIndex);
                double pageWidthPixels = page.Width * dpiX / 72.0;
                double pageHeightPixels = page.Height * dpiY / 72.0;
                double scale = Math.Min(
                    printableWidth / pageWidthPixels,
                    printableHeight / pageHeightPixels);

                int targetWidth = Math.Max(1, (int)Math.Round(pageWidthPixels * scale));
                int targetHeight = Math.Max(1, (int)Math.Round(pageHeightPixels * scale));
                int x = Math.Max(0, (printableWidth - targetWidth) / 2);
                int y = Math.Max(0, (printableHeight - targetHeight) / 2);

                document.RenderPageToPrinter(
                    pageIndex, dialog.hDC, x, y, targetWidth, targetHeight);

                if (EndPage(dialog.hDC) <= 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Não foi possível concluir a página {pageIndex + 1} da impressão.");
            }

            if (EndDoc(dialog.hDC) <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "O Windows não conseguiu finalizar o trabalho de impressão.");

            documentStarted = false;
            return true;
        }
        catch
        {
            if (documentStarted && dialog.hDC != IntPtr.Zero)
                AbortDoc(dialog.hDC);
            throw;
        }
        finally
        {
            if (dialog.hDC != IntPtr.Zero)
                DeleteDC(dialog.hDC);
            if (dialog.hDevMode != IntPtr.Zero)
                GlobalFree(dialog.hDevMode);
            if (dialog.hDevNames != IntPtr.Zero)
                GlobalFree(dialog.hDevNames);
        }
    }
}
