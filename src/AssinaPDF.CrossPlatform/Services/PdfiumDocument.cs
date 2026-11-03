
using System;
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

public sealed class PdfiumDocument : IDisposable
{
    private const int MaximumDimension = 5000;
    private const long MaximumPixels = 20_000_000;

    private static bool _libraryInitialized;

    private readonly byte[] _pdfBytes;
    private GCHandle _pinnedBytes;
    private IntPtr _document;
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

                PdfiumNative.RenderPageBitmap(
                    bitmap,
                    page,
                    0,
                    0,
                    width,
                    height,
                    0,
                    1);

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
                if (bitmap != IntPtr.Zero)
                    PdfiumNative.BitmapDestroy(bitmap);

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
