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

    public ConversionEditorWindow(bool imagesToPdf)
    {
        _imagesToPdf = imagesToPdf;
        InitializeComponent();

        Title = imagesToPdf ? "AssinaPDF | Imagens para PDF" : "AssinaPDF | PDF para imagens";
        ThumbnailHeading.Text = imagesToPdf ? "IMAGENS · ARQUIVOS" : "PÁGINAS · PDFs";
        ConvertButton.Content = imagesToPdf ? "Converter em um PDF" : "Exportar páginas em JPG";
        EmptyTitle.Text = imagesToPdf ? "Nenhuma imagem adicionada" : "Nenhum PDF adicionado";
        EmptyDescription.Text = imagesToPdf
            ? "Adicione várias imagens, organize a sequência e reúna tudo em um único PDF."
            : "Adicione vários PDFs. Todas as páginas serão exibidas para organizar e exportar como JPG.";
        StatusText.Text = imagesToPdf
            ? "Adicione uma ou mais imagens para montar seu PDF."
            : "Adicione um ou mais PDFs para exportar as páginas como JPG.";
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
                : $"{added} página(s) carregada(s) de {files.Count} PDF(s). Organize e exporte em JPG.";
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
                Width = 150,
                Height = 150,
                Stretch = Stretch.Uniform,
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
            content.Children.Add(image);
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
            PreviewImage.RenderTransform = new RotateTransform(_selected.Rotation * 90);
            PreviewImage.RenderTransformOrigin = RelativePoint.Center;
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
            if (_imagesToPdf)
                await SaveImagesAsPdfAsync();
            else
                await ExportPdfPagesAsJpgAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha na conversão: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task SaveImagesAsPdfAsync()
    {
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar todas as imagens em um único PDF",
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
            double width = quarterTurn ? image.PointHeight : image.PointWidth;
            double height = quarterTurn ? image.PointWidth : image.PointHeight;
            var page = output.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);
            using XGraphics graphics = XGraphics.FromPdfPage(page);
            graphics.TranslateTransform(width / 2, height / 2);
            graphics.RotateTransform(item.Rotation * 90);
            graphics.DrawImage(image, -image.PointWidth / 2, -image.PointHeight / 2,
                image.PointWidth, image.PointHeight);
        }

        output.Save(outputPath);
        StatusText.Text = $"{_items.Count} imagem(ns) reunida(s) em {Path.GetFileName(outputPath)}. Originais preservados.";
    }

    private async System.Threading.Tasks.Task ExportPdfPagesAsJpgAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolher pasta para salvar todas as páginas em JPG",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;

        string folder = folders[0].Path.LocalPath;
        int exported = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item.Pixels == null) continue;

            using var source = new SKBitmap(item.Width, item.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            Marshal.Copy(item.Pixels, 0, source.GetPixels(), Math.Min(item.Pixels.Length, source.ByteCount));

            bool quarterTurn = item.Rotation % 2 != 0;
            int outputWidth = quarterTurn ? item.Height : item.Width;
            int outputHeight = quarterTurn ? item.Width : item.Height;
            using var rotated = new SKBitmap(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Clear(SKColors.White);
                canvas.Translate(outputWidth / 2f, outputHeight / 2f);
                canvas.RotateDegrees(item.Rotation * 90);
                canvas.DrawBitmap(source, -item.Width / 2f, -item.Height / 2f);
                canvas.Flush();
            }

            using SKImage image = SKImage.FromBitmap(rotated);
            using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            string baseName = Path.GetFileNameWithoutExtension(item.SourceName);
            string outputName = $"{baseName}-arquivo-{i + 1:0000}-pagina-{item.PageNumber:000}.jpg";
            string outputPath = Path.Combine(folder, outputName);
            using var stream = File.Create(outputPath);
            encoded.SaveTo(stream);
            exported++;
        }

        StatusText.Text = $"{exported} imagem(ns) JPG exportada(s) para a pasta escolhida. PDFs originais preservados.";
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
