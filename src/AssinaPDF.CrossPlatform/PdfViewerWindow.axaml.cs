using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public partial class PdfViewerWindow : Window
{
    private readonly string _originalPath;
    private readonly Func<Task<string?>> _editAction;
    private PdfiumDocument? _document;
    private readonly List<WriteableBitmap> _thumbnails = new();
    private readonly List<WriteableBitmap> _pageBitmaps = new();
    private readonly List<Border> _pageSurfaces = new();
    private int _pageIndex;
    private double _zoom = 1.0;
    private bool _loading;

    public PdfViewerWindow(string pdfPath, Func<Task<string?>> editAction)
    {
        _originalPath = Path.GetFullPath(pdfPath);
        _editAction = editAction;
        InitializeComponent();
        DocumentNameText.Text = Path.GetFileName(_originalPath);
        Title = $"AssinaPDF | {Path.GetFileName(_originalPath)}";
        Opened += (_, _) => LoadDocument(_originalPath);
        PageScrollViewer.ScrollChanged += PageScrollViewer_ScrollChanged;
        Closed += (_, _) => DisposeResources();
        PageScrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            PageScrollViewer_PointerWheelChanged,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += Viewer_KeyDown;
    }

    private void LoadDocument(string path)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            ViewerStatusText.Text = "Carregando páginas e miniaturas...";
            var newDocument = new PdfiumDocument(path);
            var newThumbnails = new List<WriteableBitmap>();
            try
            {
                for (int i = 0; i < newDocument.PageCount; i++)
                {
                    var rendered = newDocument.RenderPage(i, 0.22f);
                    newThumbnails.Add(CreateBitmap(rendered));
                }

                _document?.Dispose();
                foreach (var bitmap in _thumbnails) bitmap.Dispose();
                _thumbnails.Clear();
                _thumbnails.AddRange(newThumbnails);
                _document = newDocument;
                _pageIndex = 0;
                BuildThumbnails();
                BuildContinuousPages();
                ViewerStatusText.Text = $"Documento carregado. {newDocument.PageCount} página(s). Role o mouse para navegar entre as páginas.";
            }
            catch
            {
                foreach (var bitmap in newThumbnails) bitmap.Dispose();
                newDocument.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            ViewerStatusText.Text = $"Não foi possível carregar o PDF: {ex.Message}";
        }
        finally { _loading = false; }
    }

    private void BuildThumbnails()
    {
        ThumbnailsPanel.Children.Clear();
        for (int i = 0; i < _thumbnails.Count; i++)
        {
            int index = i;
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(new Image
            {
                Source = _thumbnails[i], Width = 128, Height = 164,
                Stretch = Stretch.Uniform, IsHitTestVisible = false
            });
            content.Children.Add(new TextBlock
            {
                Text = $"Página {i + 1}", FontSize = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(65, 70, 78))
            });
            var button = new Button
            {
                Content = content, Padding = new Thickness(5),
                Background = i == _pageIndex ? new SolidColorBrush(Color.FromRgb(224, 232, 245)) : Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(190, 197, 207)),
                BorderThickness = new Thickness(i == _pageIndex ? 2 : 1),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            button.Click += (_, _) => SelectPage(index);
            ThumbnailsPanel.Children.Add(button);
        }
    }

    private void BuildContinuousPages()
    {
        PagesPanel.Children.Clear();
        foreach (var bitmap in _pageBitmaps) bitmap.Dispose();
        _pageBitmaps.Clear();
        _pageSurfaces.Clear();
        if (_document == null) return;

        for (int i = 0; i < _document.PageCount; i++)
        {
            // Renderização moderada para manter o consumo de memória mais previsível.
            var bitmap = CreateBitmap(_document.RenderPage(i, 0.85f));
            _pageBitmaps.Add(bitmap);
            var image = new Image
            {
                Source = bitmap, Stretch = Stretch.Uniform,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            var surface = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(197, 201, 208)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Child = image
            };
            _pageSurfaces.Add(surface);
            PagesPanel.Children.Add(surface);
        }
        ApplyZoomToPages();
        PageCounterText.Text = $"Página 1 de {_document.PageCount}";
    }

    private void SelectPage(int index)
    {
        if (_document == null || index < 0 || index >= _document.PageCount) return;
        _pageIndex = index;
        double y = 0;
        for (int i = 0; i < index; i++)
            y += _pageSurfaces[i].Bounds.Height + PagesPanel.Spacing;
        PageScrollViewer.Offset = new Vector(PageScrollViewer.Offset.X, y);
        UpdatePageSelection();
    }

    private void ApplyZoomToPages()
    {
        for (int i = 0; i < _pageBitmaps.Count; i++)
        {
            if (_pageSurfaces[i].Child is Image image)
            {
                image.Width = Math.Max(1, _pageBitmaps[i].PixelSize.Width * _zoom);
                image.Height = Math.Max(1, _pageBitmaps[i].PixelSize.Height * _zoom);
            }
        }
        ZoomText.Text = $"{_zoom:P0}";
    }

    private void UpdateCurrentPageFromScroll()
    {
        if (_document == null || _pageSurfaces.Count == 0) return;
        double viewportCenter = PageScrollViewer.Offset.Y + PageScrollViewer.Bounds.Height / 2;
        double accumulated = 0;
        int visiblePage = 0;
        for (int i = 0; i < _pageSurfaces.Count; i++)
        {
            double height = _pageSurfaces[i].Bounds.Height + PagesPanel.Spacing;
            if (viewportCenter < accumulated + height / 2)
            {
                visiblePage = i;
                break;
            }
            accumulated += height;
            visiblePage = i;
        }
        if (visiblePage != _pageIndex)
        {
            _pageIndex = visiblePage;
            UpdatePageSelection();
        }
        PageCounterText.Text = $"Página {_pageIndex + 1} de {_document.PageCount}";
    }

    private void UpdatePageSelection()
    {
        PageCounterText.Text = $"Página {_pageIndex + 1} de {_document?.PageCount ?? 0}";
        BuildThumbnails();
    }

    private static WriteableBitmap CreateBitmap(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(page.Width, page.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using var framebuffer = bitmap.Lock();
            int bytesPerRow = checked(page.Width * 4);
            for (int y = 0; y < page.Height; y++)
            {
                IntPtr destination = IntPtr.Add(framebuffer.Address, checked(y * framebuffer.RowBytes));
                Marshal.Copy(page.Pixels, checked(y * page.Stride), destination, bytesPerRow);
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private void ZoomIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetZoom(_zoom * 1.2);
    private void ZoomOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetZoom(_zoom / 1.2);
    private void FitWidth_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_pageBitmaps.Count == 0) return;
        double availableWidth = Math.Max(200, PageScrollViewer.Bounds.Width - 56);
        SetZoom(availableWidth / _pageBitmaps[Math.Clamp(_pageIndex, 0, _pageBitmaps.Count - 1)].PixelSize.Width);
    }

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, 0.25, 2.5);
        ApplyZoomToPages();
    }

    private void PageScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_document == null || _loading || PageScrollViewer.Extent.Height <= 0) return;
        UpdateCurrentPageFromScroll();
    }

    private void PageScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SetZoom(_zoom * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12));
        e.Handled = true;
    }

    private void Viewer_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left) SelectPage(_pageIndex - 1);
        else if (e.Key == Key.Right) SelectPage(_pageIndex + 1);
        else if (e.Key == Key.Add || (e.Key == Key.OemPlus && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            SetZoom(_zoom * 1.2);
        else if (e.Key == Key.Subtract || (e.Key == Key.OemMinus && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            SetZoom(_zoom / 1.2);
    }

    private async void EditPdf_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            ViewerStatusText.Text = "Abrindo o editor em uma cópia de trabalho...";
            string? editedPath = await _editAction();
            if (!string.IsNullOrWhiteSpace(editedPath) && File.Exists(editedPath))
            {
                DocumentNameText.Text = Path.GetFileName(editedPath);
                LoadDocument(editedPath);
            }
        }
        catch (Exception ex) { ViewerStatusText.Text = $"Não foi possível abrir o editor: {ex.Message}"; }
    }

    private void CloseViewer_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void DisposeResources()
    {
        _document?.Dispose();
        _document = null;
        foreach (var bitmap in _pageBitmaps) bitmap.Dispose();
        _pageBitmaps.Clear();
        foreach (var bitmap in _thumbnails) bitmap.Dispose();
        _thumbnails.Clear();
        _pageSurfaces.Clear();
    }
}