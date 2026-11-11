
using System;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace AssinaPDF.MacPrototype.Services;

public sealed class RenderedPdfPage
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }
    public float PageWidthPoints { get; }
    public float PageHeightPoints { get; }

    internal RenderedPdfPage(
        int width,
        int height,
        int stride,
        byte[] pixels,
        float pageWidthPoints,
        float pageHeightPoints)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
        PageWidthPoints = pageWidthPoints;
        PageHeightPoints = pageHeightPoints;
    }
}

public sealed record PdfiumPageObjectInfo(
    int PageIndex,
    int ObjectIndex,
    int ObjectType,
    string? Text,
    float Left,
    float Bottom,
    float Right,
    float Top,
    float? FontSize);

public sealed record PdfiumCharacterInfo(
    int PageIndex,
    int CharacterIndex,
    string Text,
    double Left,
    double Bottom,
    double Right,
    double Top,
    double FontSize,
    string? FontName,
    int FontFlags);

public sealed class PdfiumDocument : IDisposable
{
    private const int MaximumDimension = 5000;
    private const long MaximumPixels = 20_000_000;

    private static bool _libraryInitialized;

    private readonly byte[] _pdfBytes;
    private GCHandle _pinnedBytes;
    private IntPtr _document;
    private IntPtr _formHandle;
    private IntPtr _formFillInfo;
    private bool _disposed;

    public int PageCount { get; }

    public PdfiumDocument(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException(
                "O arquivo PDF não foi encontrado.", filePath);

        _pdfBytes = File.ReadAllBytes(filePath);

        if (_pdfBytes.Length == 0)
            throw new InvalidDataException("O arquivo PDF está vazio.");

        lock (PdfiumNative.SyncRoot)
        {
            try
            {
                if (!_libraryInitialized)
                {
                    PdfiumNative.InitLibrary();
                    _libraryInitialized = true;
                }

                _pinnedBytes = GCHandle.Alloc(
                    _pdfBytes, GCHandleType.Pinned);

                _document = PdfiumNative.LoadMemDocument64(
                    _pinnedBytes.AddrOfPinnedObject(),
                    (nuint)_pdfBytes.Length,
                    IntPtr.Zero);

                if (_document == IntPtr.Zero)
                {
                    uint error = PdfiumNative.GetLastError();

                    throw new InvalidDataException(
                        $"Não foi possível abrir o PDF. Código PDFium: {error}.");
                }

                // PDFium exige uma estrutura FPDF_FORMFILLINFO válida para
                // inicializar o renderizador de campos interativos (widgets).
                // Reservamos memória nativa zerada e definimos a versão 1;
                // os callbacks opcionais permanecem nulos nesta etapa.
                _formFillInfo = Marshal.AllocHGlobal(1024);
                for (int i = 0; i < 1024; i++)
                    Marshal.WriteByte(_formFillInfo, i, 0);
                Marshal.WriteInt32(_formFillInfo, 1);

                try
                {
                    _formHandle = PdfiumNative.InitFormFillEnvironment(
                        _document,
                        _formFillInfo);
                }
                catch (EntryPointNotFoundException)
                {
                    _formHandle = IntPtr.Zero;
                }

                PageCount = PdfiumNative.GetPageCount(_document);

                if (PageCount <= 0)
                    throw new InvalidDataException(
                        "O PDF não possui páginas válidas.");
            }
            catch
            {
                CloseDocument();
                throw;
            }
        }
    }

    public RenderedPdfPage RenderPage(
        int pageIndex,
        float scale = 1.5f)
    {
        ThrowIfDisposed();

        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        if (!float.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));

        lock (PdfiumNative.SyncRoot)
        {
            ThrowIfDisposed();

            IntPtr page = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;

            try
            {
                page = PdfiumNative.LoadPage(_document, pageIndex);

                if (page == IntPtr.Zero)
                    throw new InvalidDataException(
                        $"Não foi possível carregar a página {pageIndex + 1}.");

                float pageWidth = PdfiumNative.GetPageWidth(page);
                float pageHeight = PdfiumNative.GetPageHeight(page);

                if (!float.IsFinite(pageWidth) ||
                    !float.IsFinite(pageHeight) ||
                    pageWidth <= 0 ||
                    pageHeight <= 0)
                {
                    throw new InvalidDataException(
                        "O PDF possui dimensões de página inválidas.");
                }

                double requestedWidth = pageWidth * scale;
                double requestedHeight = pageHeight * scale;

                double dimensionScale = Math.Min(
                    1.0,
                    Math.Min(
                        MaximumDimension / requestedWidth,
                        MaximumDimension / requestedHeight));

                double pixelScale = Math.Min(
                    1.0,
                    Math.Sqrt(
                        MaximumPixels /
                        (requestedWidth * requestedHeight)));

                double finalScale = scale *
                    Math.Min(dimensionScale, pixelScale);

                int width = Math.Max(
                    1, (int)Math.Ceiling(pageWidth * finalScale));

                int height = Math.Max(
                    1, (int)Math.Ceiling(pageHeight * finalScale));

                bitmap = PdfiumNative.BitmapCreateEx(
                    width,
                    height,
                    PdfiumNative.BitmapBgra,
                    IntPtr.Zero,
                    0);

                if (bitmap == IntPtr.Zero)
                    throw new InvalidOperationException(
                        "O PDFium não conseguiu criar a imagem da página.");

                // Inicializa o bitmap com fundo branco totalmente opaco.
                PdfiumNative.BitmapFillRect(
                    bitmap,
                    0,
                    0,
                    width,
                    height,
                    0xFFFFFFFF);
                if (_formHandle != IntPtr.Zero)
                    PdfiumNative.FormOnAfterLoadPage(page, _formHandle);

                PdfiumNative.RenderPageBitmap(
                    bitmap,
                    page,
                    0,
                    0,
                    width,
                    height,
                    0,
                    1);

                // Widgets de formulários, incluindo campos de assinatura,
                // são desenhados pela API FPDF_FFLDraw após o conteúdo da página.
                if (_formHandle != IntPtr.Zero)
                {
                    PdfiumNative.FormFillDraw(
                        _formHandle,
                        bitmap,
                        page,
                        0,
                        0,
                        width,
                        height,
                        0,
                        1);
                }

                IntPtr buffer = PdfiumNative.BitmapGetBuffer(bitmap);
                int stride = PdfiumNative.BitmapGetStride(bitmap);

                if (buffer == IntPtr.Zero || stride < width * 4)
                    throw new InvalidOperationException(
                        "O PDFium retornou um buffer de imagem inválido.");

                byte[] pixels = new byte[checked(stride * height)];

                Marshal.Copy(
                    buffer,
                    pixels,
                    0,
                    pixels.Length);

                return new RenderedPdfPage(width, height, stride, pixels, pageWidth, pageHeight);
            }
            finally
            {
                if (page != IntPtr.Zero)
                {
                    if (_formHandle != IntPtr.Zero)
                        PdfiumNative.FormOnBeforeClosePage(page, _formHandle);

                    PdfiumNative.ClosePage(page);
                }

                if (bitmap != IntPtr.Zero)
                    PdfiumNative.BitmapDestroy(bitmap);
            }
        }
    }

    internal (float Width, float Height) GetPageDimensionsPoints(int pageIndex)
    {
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        lock (PdfiumNative.SyncRoot)
        {
            ThrowIfDisposed();
            IntPtr page = PdfiumNative.LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero)
                throw new InvalidDataException($"Não foi possível carregar a página {pageIndex + 1}.");

            try
            {
                return (PdfiumNative.GetPageWidth(page), PdfiumNative.GetPageHeight(page));
            }
            finally
            {
                PdfiumNative.ClosePage(page);
            }
        }
    }

    internal void RenderPageToPrinter(
        int pageIndex,
        IntPtr deviceContext,
        int x,
        int y,
        int width,
        int height)
    {
        ThrowIfDisposed();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A renderização direta para impressora está disponível somente no Windows.");
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (deviceContext == IntPtr.Zero || width <= 0 || height <= 0)
            throw new ArgumentException("A área de impressão é inválida.");

        lock (PdfiumNative.SyncRoot)
        {
            ThrowIfDisposed();
            IntPtr page = PdfiumNative.LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero)
                throw new InvalidDataException($"Não foi possível carregar a página {pageIndex + 1}.");

            try
            {
                // FPDF_RenderPage desenha os elementos PDF diretamente no DC da
                // impressora, preservando texto e vetores na saída de impressão.
                PdfiumNative.RenderPageToDevice(
                    deviceContext, page, x, y, width, height, 0, 0x01);
            }
            finally
            {
                PdfiumNative.ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Inspeciona os objetos nativos de uma página sem depender dos fragmentos
    /// de texto produzidos pelo Aspose. As coordenadas retornadas usam o sistema
    /// PDF (origem inferior esquerda), em pontos.
    /// </summary>
    public IReadOnlyList<PdfiumPageObjectInfo> InspectPageObjects(int pageIndex)
    {
        ThrowIfDisposed();

        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        lock (PdfiumNative.SyncRoot)
        {
            ThrowIfDisposed();

            IntPtr page = IntPtr.Zero;
            IntPtr textPage = IntPtr.Zero;
            try
            {
                page = PdfiumNative.LoadPage(_document, pageIndex);
                if (page == IntPtr.Zero)
                    throw new InvalidDataException($"Não foi possível carregar a página {pageIndex + 1}.");

                textPage = PdfiumNative.TextLoadPage(page);
                int objectCount = PdfiumNative.PageCountObjects(page);
                if (objectCount < 0)
                    throw new InvalidDataException($"Não foi possível enumerar os objetos da página {pageIndex + 1}.");

                var result = new List<PdfiumPageObjectInfo>(objectCount);
                for (int index = 0; index < objectCount; index++)
                {
                    IntPtr pageObject = PdfiumNative.PageGetObject(page, index);
                    if (pageObject == IntPtr.Zero)
                        continue;

                    int objectType = PdfiumNative.PageObjectGetType(pageObject);
                    if (PdfiumNative.PageObjectGetBounds(pageObject, out float left, out float bottom, out float right, out float top) == 0)
                        continue;

                    string? text = null;
                    float? fontSize = null;

                    // FPDF_PAGEOBJ_TEXT = 1. Os demais objetos também são
                    // devolvidos para que a futura camada visual possa selecionar
                    // imagens e elementos gráficos separadamente.
                    if (objectType == 1 && textPage != IntPtr.Zero)
                    {
                        uint requiredBytes = PdfiumNative.TextObjectGetText(pageObject, textPage, IntPtr.Zero, 0);
                        if (requiredBytes >= 2 && requiredBytes <= 4 * 1024 * 1024)
                        {
                            IntPtr buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
                            try
                            {
                                uint writtenBytes = PdfiumNative.TextObjectGetText(pageObject, textPage, buffer, requiredBytes);
                                if (writtenBytes > 0)
                                    text = Marshal.PtrToStringUni(buffer)?.TrimEnd('\0');
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(buffer);
                            }
                        }

                        if (PdfiumNative.TextObjectGetFontSize(pageObject, out float size) != 0 &&
                            float.IsFinite(size) && size > 0)
                            fontSize = size;
                    }

                    result.Add(new PdfiumPageObjectInfo(
                        pageIndex, index, objectType, text,
                        left, bottom, right, top, fontSize));
                }

                return result;
            }
            finally
            {
                if (textPage != IntPtr.Zero)
                    PdfiumNative.TextClosePage(textPage);
                if (page != IntPtr.Zero)
                    PdfiumNative.ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Retorna o fluxo de caracteres do PDFium com posição individual,
    /// tamanho e, quando disponível no build nativo, nome/flags da fonte.
    /// As coordenadas são pontos PDF, com origem no canto inferior esquerdo.
    /// </summary>
    public IReadOnlyList<PdfiumCharacterInfo> InspectPageCharacters(int pageIndex)
    {
        ThrowIfDisposed();

        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        lock (PdfiumNative.SyncRoot)
        {
            ThrowIfDisposed();

            IntPtr page = IntPtr.Zero;
            IntPtr textPage = IntPtr.Zero;
            try
            {
                page = PdfiumNative.LoadPage(_document, pageIndex);
                if (page == IntPtr.Zero)
                    throw new InvalidDataException($"Não foi possível carregar a página {pageIndex + 1}.");

                textPage = PdfiumNative.TextLoadPage(page);
                if (textPage == IntPtr.Zero)
                    throw new InvalidDataException($"Não foi possível analisar o texto da página {pageIndex + 1}.");

                int characterCount = PdfiumNative.TextCountChars(textPage);
                if (characterCount < 0)
                    throw new InvalidDataException($"Não foi possível contar os caracteres da página {pageIndex + 1}.");
                if (characterCount > 2_000_000)
                    throw new InvalidDataException("A página contém caracteres demais para inspeção segura.");

                var result = new List<PdfiumCharacterInfo>(characterCount);
                IntPtr fontBuffer = Marshal.AllocHGlobal(512);
                try
                {
                    for (int index = 0; index < characterCount; index++)
                    {
                        uint unicode = PdfiumNative.TextGetUnicode(textPage, index);
                        string character = unicode == 0 || !Rune.IsValid((int)unicode)
                            ? string.Empty
                            : char.ConvertFromUtf32((int)unicode);

                        PdfiumNative.TextGetCharBox(
                            textPage, index,
                            out double left, out double right,
                            out double bottom, out double top);

                        double fontSize = PdfiumNative.TextGetCharacterFontSize(textPage, index);
                        if (!double.IsFinite(left) || !double.IsFinite(right) ||
                            !double.IsFinite(bottom) || !double.IsFinite(top) ||
                            !double.IsFinite(fontSize))
                            continue;

                        string? fontName = null;
                        int fontFlags = 0;
                        try
                        {
                            nuint nameLength = PdfiumNative.TextGetFontInfo(
                                textPage, index, fontBuffer, (nuint)512, out fontFlags);
                            if (nameLength > 1 && nameLength <= 512)
                                fontName = Marshal.PtrToStringUTF8(fontBuffer);
                        }
                        catch (EntryPointNotFoundException)
                        {
                            // A API é experimental e pode não existir em algumas builds.
                            fontName = null;
                        }

                        result.Add(new PdfiumCharacterInfo(
                            pageIndex, index, character,
                            left, bottom, right, top,
                            fontSize, fontName, fontFlags));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(fontBuffer);
                }

                return result;
            }
            finally
            {
                if (textPage != IntPtr.Zero)
                    PdfiumNative.TextClosePage(textPage);
                if (page != IntPtr.Zero)
                    PdfiumNative.ClosePage(page);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void CloseDocument()
    {
        if (_formHandle != IntPtr.Zero)
        {
            PdfiumNative.ExitFormFillEnvironment(_formHandle);
            _formHandle = IntPtr.Zero;
        }

        if (_formFillInfo != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_formFillInfo);
            _formFillInfo = IntPtr.Zero;
        }

        if (_document != IntPtr.Zero)
        {
            PdfiumNative.CloseDocument(_document);
            _document = IntPtr.Zero;
        }

        if (_pinnedBytes.IsAllocated)
            _pinnedBytes.Free();
    }

    public void Dispose()
    {
        lock (PdfiumNative.SyncRoot)
        {
            if (_disposed)
                return;

            _disposed = true;
            CloseDocument();
        }
    }
}
