using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace AssinaPDF.MacPrototype;

public partial class ConversionEditorWindow : Window
{
    private sealed class ConversionItem
    {
        public required string SourcePath { get; init; }
        public required string SourceName { get; init; }
        public required Bitmap Preview { get; init; }
        public bool IsPdfPage { get; init; }
        public int PageNumber { get; init; }
        public int Rotation { get; set; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Stride { get; init; }
        public byte[]? Pixels { get; init; }
    }

    private readonly bool _imagesToPdf;
    private readonly List<ConversionItem> _items = new();
    private ConversionItem? _selected;

    // Construtor público sem parâmetros exigido pelo carregador XAML do Avalonia.
    // O fluxo normal da aplicação usa o construtor que recebe o modo explicitamente.
    public ConversionEditorWindow() : this(imagesToPdf: true)
    {
    }

    public ConversionEditorWindow(bool imagesToPdf)
    {
        _imagesToPdf = imagesToPdf;
        InitializeComponent();

        Title = imagesToPdf ? "AssinaPDF | Imagens para PDF" : "AssinaPDF | PDF para imagens";
        ThumbnailHeading.Text = imagesToPdf ? "IMAGENS · ARQUIVOS" : "PÁGINAS · PDFs";
        ConvertButton.Content = imagesToPdf ? "Converter em um PDF" : "Exportar imagens";
        EmptyTitle.Text = imagesToPdf ? "Nenhuma imagem adicionada" : "Nenhum PDF adicionado";
        EmptyDescription.Text = imagesToPdf
            ? "Adicione várias imagens, organize a sequência e reúna tudo em um único PDF."
            : "Adicione vários PDFs. Todas as páginas serão exibidas para organizar e exportar como JPG, PNG ou TIFF.";
        StatusText.Text = imagesToPdf
            ? "Adicione uma ou mais imagens para montar seu PDF."
            : "Adicione um ou mais PDFs para exportar as páginas como JPG, PNG ou TIFF.";
        Closed += (_, _) => DisposeItems();
    }

    private async void AddFiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var filter = _imagesToPdf
                ? new FilePickerFileType("Imagens JPG, JPEG, PNG e BMP") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.JPG", "*.JPEG", "*.PNG", "*.BMP" } }
                : new FilePickerFileType("Documentos PDF") { Patterns = new[] { "*.pdf", "*.PDF" } };

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = _imagesToPdf ? "Adicionar imagens ao editor" : "Adicionar PDFs ao editor",
                AllowMultiple = true,
                FileTypeFilter = new[] { filter }
            });
            if (files.Count == 0) return;

            int added = 0;
            foreach (var file in files)
            {
                string path = Path.GetFullPath(file.Path.LocalPath);
                if (_imagesToPdf)
                {
                    var preview = new Bitmap(path);
                    _items.Add(new ConversionItem
                    {
                        SourcePath = path,
                        SourceName = file.Name,
                        Preview = preview,
                        Width = preview.PixelSize.Width,
                        Height = preview.PixelSize.Height
                    });
                    added++;
                }
                else
                {
                    using var document = new PdfiumDocument(path);
                    for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
                    {
                        var rendered = document.RenderPage(pageIndex, 1.5f);
                        int stride = checked(rendered.Width * 4);
                        var pixels = new byte[checked(stride * rendered.Height)];
                        for (int y = 0; y < rendered.Height; y++)
                            Array.Copy(rendered.Pixels, y * rendered.Stride, pixels, y * stride, stride);

                        var preview = CreateBitmap(rendered.Width, rendered.Height, pixels, stride);
                        _items.Add(new ConversionItem
                        {
                            SourcePath = path,
                            SourceName = file.Name,
                            Preview = preview,
                            IsPdfPage = true,
                            PageNumber = pageIndex + 1,
                            Width = rendered.Width,
                            Height = rendered.Height,
                            Stride = stride,
                            Pixels = pixels
                        });
                        added++;
                    }
                }
            }

            if (_selected == null) _selected = _items.FirstOrDefault();
            RebuildThumbnails();
            StatusText.Text = _imagesToPdf
                ? $"{added} imagem(ns) adicionada(s). Organize e clique em Converter em um PDF."
                : $"{added} página(s) carregada(s) de {files.Count} PDF(s). Organize e configure o formato de exportação.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível adicionar os arquivos: {ex.Message}";
        }
    }

    private void RebuildThumbnails()
    {
        ThumbnailsPanel.Children.Clear();
        foreach (var item in _items)
        {
            var image = new Image
            {
                Source = item.Preview,
                Width = 138,
                Height = 138,
                Stretch = Stretch.Uniform
            };
            var pageSurface = new Border
            {
                Width = 150,
                Height = 150,
                Background = Brushes.White,
                Padding = new Thickness(6),
                Child = image,
                RenderTransform = new RotateTransform(item.Rotation * 90),
                RenderTransformOrigin = RelativePoint.Center
            };
            var label = new TextBlock
            {
                Text = _imagesToPdf
                    ? item.SourceName
                    : $"{item.SourceName} · pág. {item.PageNumber}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(85, 92, 104)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 165,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            var content = new StackPanel { Spacing = 5 };
            content.Children.Add(pageSurface);
            content.Children.Add(label);
            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 231)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(7),
                Margin = new Thickness(5),
                Child = content
            };
            card.ContextMenu = BuildContextMenu(item);
            card.PointerPressed += (_, args) =>
            {
                if (args.GetCurrentPoint(card).Properties.IsRightButtonPressed)
                {
                    SelectItem(item);
                    return;
                }
                if (args.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                    SelectItem(item);
            };
            ThumbnailsPanel.Children.Add(card);
            if (ReferenceEquals(item, _selected))
            {
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(103, 65, 184));
                card.BorderThickness = new Thickness(3);
            }
        }

        ItemCountText.Text = _imagesToPdf
            ? $"{_items.Count} imagem(ns)"
            : $"{_items.Count} página(s)";
        EmptyState.IsVisible = _items.Count == 0;
        if (_selected != null && _items.Contains(_selected))
        {
            PreviewImage.Source = _selected.Preview;
            bool quarterTurn = _selected.Rotation % 2 != 0;
            double rotatedWidth = quarterTurn ? _selected.Height : _selected.Width;
            double rotatedHeight = quarterTurn ? _selected.Width : _selected.Height;
            double previewScale = Math.Min(1d, Math.Min(760d / Math.Max(1, rotatedWidth), 900d / Math.Max(1, rotatedHeight)));
            PreviewImage.Width = Math.Max(1, _selected.Width * previewScale);
            PreviewImage.Height = Math.Max(1, _selected.Height * previewScale);
            PreviewImage.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            PreviewImage.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            PreviewImage.RenderTransform = new RotateTransform(_selected.Rotation * 90);
            PreviewImage.RenderTransformOrigin = RelativePoint.Center;

            // A própria folha branca assume as dimensões da orientação final.
            // O conteúdo gira dentro dela, evitando que a área de fundo fique em retrato
            // quando a página passa para paisagem (e vice-versa).
            PreviewPageSurface.Width = rotatedWidth * previewScale + 36;
            PreviewPageSurface.Height = rotatedHeight * previewScale + 36;
            PreviewPageSurface.RenderTransform = null;
            PreviewPageSurface.IsVisible = true;
            PreviewImage.IsVisible = true;
        }
        else
        {
            PreviewImage.Source = null;
            PreviewImage.IsVisible = false;
        }
    }

    private ContextMenu BuildContextMenu(ConversionItem item)
    {
        var menu = new ContextMenu();
        void Add(string text, Action action)
        {
            var entry = new MenuItem { Header = text };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }

        Add("Selecionar", () => SelectItem(item));
        menu.Items.Add(new Separator());
        Add("Mover para cima", () => MoveItem(item, -1));
        Add("Mover para baixo", () => MoveItem(item, 1));
        Add("Girar para a esquerda", () => RotateItem(item, -1));
        Add("Girar para a direita", () => RotateItem(item, 1));
        menu.Items.Add(new Separator());
        Add(_imagesToPdf ? "Remover imagem" : "Remover página", () => RemoveItem(item));
        return menu;
    }

    private void SelectItem(ConversionItem item)
    {
        _selected = item;
        RebuildThumbnails();
        StatusText.Text = _imagesToPdf
            ? $"Imagem selecionada: {item.SourceName}"
            : $"{item.SourceName} · página {item.PageNumber}";
    }

    private void MoveItem(ConversionItem item, int direction)
    {
        int index = _items.IndexOf(item);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= _items.Count) return;
        (_items[index], _items[target]) = (_items[target], _items[index]);
        _selected = item;
        RebuildThumbnails();
    }

    private void RotateItem(ConversionItem item, int direction)
    {
        item.Rotation = (item.Rotation + (direction > 0 ? 1 : 3)) % 4;
        _selected = item;
        RebuildThumbnails();
        StatusText.Text = "Rotação aplicada à visualização e será considerada na conversão.";
    }

    private void RemoveItem(ConversionItem item)
    {
        int index = _items.IndexOf(item);
        if (index < 0) return;
        _items.RemoveAt(index);
        item.Preview.Dispose();
        _selected = _items.Count == 0 ? null : _items[Math.Min(index, _items.Count - 1)];
        RebuildThumbnails();
        StatusText.Text = _imagesToPdf ? "Imagem removida da sequência." : "Página removida da sequência.";
    }

    private void MoveUp_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected != null) MoveItem(_selected, -1);
    }

    private void MoveDown_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected != null) MoveItem(_selected, 1);
    }

    private void RotateLeft_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected != null) RotateItem(_selected, -1);
    }

    private void RotateRight_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selected != null) RotateItem(_selected, 1);
    }

    private async void Convert_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            StatusText.Text = _imagesToPdf ? "Adicione imagens antes de converter." : "Adicione PDFs antes de exportar.";
            return;
        }

        try
        {
            var optionsWindow = new ConversionExportOptionsWindow(_imagesToPdf);
            await optionsWindow.ShowDialog(this);
            if (optionsWindow.Settings is not ConversionExportSettings settings) return;

            if (_imagesToPdf)
                await SaveImagesAsPdfAsync(settings);
            else
                await ExportPdfPagesAsImageAsync(settings);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha na conversão: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task SaveImagesAsPdfAsync(ConversionExportSettings settings)
    {
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar imagens em um único PDF",
            SuggestedFileName = "imagens-combinadas.pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } }
        });
        if (destination == null) return;

        string outputPath = Path.GetFullPath(destination.Path.LocalPath);
        if (_items.Any(x => string.Equals(Path.GetFullPath(x.SourcePath), outputPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new InvalidOperationException("O PDF de destino não pode substituir uma imagem original.");

        using var output = new PdfDocument();
        foreach (var item in _items)
        {
            using XImage image = XImage.FromFile(item.SourcePath);
            bool quarterTurn = item.Rotation % 2 != 0;
            double contentWidth = quarterTurn ? image.PointHeight : image.PointWidth;
            double contentHeight = quarterTurn ? image.PointWidth : image.PointHeight;
            (double pageWidth, double pageHeight) = GetPageSizePoints(settings, contentWidth, contentHeight);
            double scale = settings.FillPage
                ? Math.Max(pageWidth / contentWidth, pageHeight / contentHeight)
                : Math.Min(pageWidth / contentWidth, pageHeight / contentHeight);

            var page = output.AddPage();
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);
            using XGraphics graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XBrushes.White, 0, 0, pageWidth, pageHeight);
            graphics.IntersectClip(new XRect(0, 0, pageWidth, pageHeight));
            graphics.TranslateTransform(pageWidth / 2, pageHeight / 2);
            graphics.RotateTransform(item.Rotation * 90);
            graphics.DrawImage(image,
                -image.PointWidth * scale / 2,
                -image.PointHeight * scale / 2,
                image.PointWidth * scale,
                image.PointHeight * scale);
        }

        output.Save(outputPath);
        StatusText.Text = "Arquivo salvo com sucesso.";
        await ShowSavedDialogAsync("PDF salvo com sucesso!", $"{Path.GetFileName(outputPath)}\n\nLocal: {Path.GetDirectoryName(outputPath)}");
    }

    private async System.Threading.Tasks.Task ExportPdfPagesAsImageAsync(ConversionExportSettings settings)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Escolher pasta para salvar as páginas em {settings.ImageFormat}",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;

        string folder = folders[0].Path.LocalPath;
        int exported = 0;
        string extension = settings.ImageFormat switch
        {
            "PNG" => "png",
            "TIFF" => "tiff",
            _ => "jpg"
        };
        SKEncodedImageFormat encodedFormat = settings.ImageFormat == "PNG"
            ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            using var document = new PdfiumDocument(item.SourcePath);
            var rendered = document.RenderPage(item.PageNumber - 1, settings.Dpi / 72f);
            using var source = new SKBitmap(rendered.Width, rendered.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            int copyLength = Math.Min(rendered.Width * 4, rendered.Stride);
            for (int y = 0; y < rendered.Height; y++)
                Marshal.Copy(rendered.Pixels, y * rendered.Stride,
                    IntPtr.Add(source.GetPixels(), y * source.RowBytes), copyLength);

            bool quarterTurn = item.Rotation % 2 != 0;
            double contentWidth = quarterTurn ? rendered.Height : rendered.Width;
            double contentHeight = quarterTurn ? rendered.Width : rendered.Height;
            (int outputWidth, int outputHeight) = GetOutputPixelSize(settings, contentWidth, contentHeight);
            using var outputBitmap = new SKBitmap(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(outputBitmap))
            {
                canvas.Clear(SKColors.White);
                double scale = settings.FillPage
                    ? Math.Max(outputWidth / contentWidth, outputHeight / contentHeight)
                    : Math.Min(outputWidth / contentWidth, outputHeight / contentHeight);
                canvas.Translate(outputWidth / 2f, outputHeight / 2f);
                canvas.RotateDegrees(item.Rotation * 90);
                canvas.Scale((float)scale);
                canvas.DrawBitmap(source, -rendered.Width / 2f, -rendered.Height / 2f);
                canvas.Flush();
            }

            string baseName = Path.GetFileNameWithoutExtension(item.SourceName);
            string outputName = $"{baseName}-arquivo-{i + 1:0000}-pagina-{item.PageNumber:000}.{extension}";
            string outputPath = Path.Combine(folder, outputName);

            if (settings.ImageFormat == "TIFF")
            {
                SaveBitmapAsTiff(outputBitmap, outputPath, settings.Dpi);
            }
            else
            {
                using SKImage image = SKImage.FromBitmap(outputBitmap);
                using SKData encoded = image.Encode(encodedFormat, settings.JpegQuality);
                using var stream = File.Create(outputPath);
                encoded.SaveTo(stream);
            }
            exported++;
        }

        StatusText.Text = $"{exported} arquivo(s) {settings.ImageFormat} exportado(s).";
        await ShowSavedDialogAsync("Exportação concluída!", $"{exported} imagem(ns) salva(s) em {settings.ImageFormat}.\n\nPasta: {folder}");
    }

    private static void SaveBitmapAsTiff(SKBitmap bitmap, string path, int dpi)
    {
        // TIFF baseline little-endian, RGB 8 bits/channel, uncompressed.
        // The export bitmap is opaque (white background), so alpha is intentionally flattened.
        const ushort entryCount = 13;
        int ifdStart = 8;
        int ifdLength = 2 + entryCount * 12 + 4;
        int bitsOffset = ifdStart + ifdLength;
        int xResolutionOffset = bitsOffset + 6;
        int yResolutionOffset = xResolutionOffset + 8;
        int pixelDataOffset = yResolutionOffset + 8;
        int byteCount = checked(bitmap.Width * bitmap.Height * 3);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write((uint)ifdStart);
        writer.Write(entryCount);

        WriteTiffEntry(writer, 256, 4, 1, (uint)bitmap.Width);
        WriteTiffEntry(writer, 257, 4, 1, (uint)bitmap.Height);
        WriteTiffEntry(writer, 258, 3, 3, (uint)bitsOffset);
        WriteTiffEntry(writer, 259, 3, 1, 1);
        WriteTiffEntry(writer, 262, 3, 1, 2);
        WriteTiffEntry(writer, 273, 4, 1, (uint)pixelDataOffset);
        WriteTiffEntry(writer, 277, 3, 1, 3);
        WriteTiffEntry(writer, 278, 4, 1, (uint)bitmap.Height);
        WriteTiffEntry(writer, 279, 4, 1, (uint)byteCount);
        WriteTiffEntry(writer, 282, 5, 1, (uint)xResolutionOffset);
        WriteTiffEntry(writer, 283, 5, 1, (uint)yResolutionOffset);
        WriteTiffEntry(writer, 284, 3, 1, 1);
        WriteTiffEntry(writer, 296, 3, 1, 2);
        writer.Write((uint)0);

        writer.Write((ushort)8);
        writer.Write((ushort)8);
        writer.Write((ushort)8);
        writer.Write((uint)dpi);
        writer.Write((uint)1);
        writer.Write((uint)dpi);
        writer.Write((uint)1);

        byte[] bgra = new byte[checked(bitmap.Width * 4)];
        byte[] rgb = new byte[checked(bitmap.Width * 3)];
        for (int y = 0; y < bitmap.Height; y++)
        {
            Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), y * bitmap.RowBytes), bgra, 0, bgra.Length);
            for (int x = 0; x < bitmap.Width; x++)
            {
                int source = x * 4;
                int target = x * 3;
                rgb[target] = bgra[source + 2];
                rgb[target + 1] = bgra[source + 1];
                rgb[target + 2] = bgra[source];
            }
            writer.Write(rgb);
        }
    }

    private static void WriteTiffEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(value);
    }

    private static (double Width, double Height) GetPageSizePoints(
        ConversionExportSettings settings, double contentWidth, double contentHeight)
    {
        if (settings.PageSize == "Original") return (contentWidth, contentHeight);
        (double widthMm, double heightMm) = GetPageSizeMm(settings);
        if (contentWidth > contentHeight) (widthMm, heightMm) = (Math.Max(widthMm, heightMm), Math.Min(widthMm, heightMm));
        else (widthMm, heightMm) = (Math.Min(widthMm, heightMm), Math.Max(widthMm, heightMm));
        return (widthMm * 72 / 25.4, heightMm * 72 / 25.4);
    }

    private static (int Width, int Height) GetOutputPixelSize(
        ConversionExportSettings settings, double contentWidth, double contentHeight)
    {
        if (settings.PageSize == "Original")
            return ((int)Math.Round(contentWidth), (int)Math.Round(contentHeight));
        (double widthMm, double heightMm) = GetPageSizeMm(settings);
        if (contentWidth > contentHeight) (widthMm, heightMm) = (Math.Max(widthMm, heightMm), Math.Min(widthMm, heightMm));
        else (widthMm, heightMm) = (Math.Min(widthMm, heightMm), Math.Max(widthMm, heightMm));
        return ((int)Math.Round(widthMm / 25.4 * settings.Dpi),
                (int)Math.Round(heightMm / 25.4 * settings.Dpi));
    }

    private static (double Width, double Height) GetPageSizeMm(ConversionExportSettings settings) =>
        settings.PageSize switch
        {
            "A3" => (297, 420),
            "A4" => (210, 297),
            "A5" => (148, 210),
            "Carta" => (215.9, 279.4),
            "Ofício" => (215.9, 355.6),
            "Personalizado" => (settings.CustomWidthMm, settings.CustomHeightMm),
            _ => (210, 297)
        };

    private async System.Threading.Tasks.Task ShowSavedDialogAsync(string heading, string details)
    {
        var dialog = new Window
        {
            Title = "AssinaPDF",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(247, 248, 250))
        };
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = "✓  " + heading,
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(22, 128, 74))
        });
        content.Children.Add(new TextBlock
        {
            Text = details,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(65, 72, 84)),
            TextWrapping = TextWrapping.Wrap
        });
        var ok = new Button
        {
            Content = "Concluir",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Padding = new Thickness(22, 9),
            Background = new SolidColorBrush(Color.FromRgb(103, 65, 184)),
            Foreground = Brushes.White
        };
        ok.Click += (_, _) => dialog.Close();
        content.Children.Add(ok);
        dialog.Content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            Child = content
        };
        await dialog.ShowDialog(this);
    }

    private static WriteableBitmap CreateBitmap(int width, int height, byte[] pixels, int stride)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using var framebuffer = bitmap.Lock();
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * stride,
                    IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes), stride);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void DisposeItems()
    {
        foreach (var item in _items) item.Preview.Dispose();
        _items.Clear();
    }
}
