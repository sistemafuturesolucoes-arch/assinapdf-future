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
    private string _currentPath;
    private int _pageIndex;
    private double _zoom = 1.0;
    private bool _loading;

    public PdfViewerWindow(string pdfPath, Func<Task<string?>> editAction)
    {
        _originalPath = Path.GetFullPath(pdfPath);
        _currentPath = _originalPath;
        _editAction = editAction;
        InitializeComponent();
        DocumentNameText.Text = Path.GetFileName(_originalPath);
        Title = $"AssinaPDF | {Path.GetFileName(_originalPath)}";
        Opened += (_, _) => LoadDocument(_originalPath);
        Closed += (_, _) => DisposeResources();
        KeyDown += Viewer_KeyDown;
    }

    private void LoadDocument(string path)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            _currentPath = Path.GetFullPath(path);
            ViewerStatusText.Text = "Carregando PDF no visualizador nativo...";
            var newDocument = new PdfiumDocument(_currentPath);
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
                _zoom = 1.0;
                BuildThumbnails();
                NavigateToPage(0);
                ViewerStatusText.Text = $"PDF aberto pelo mecanismo nativo do sistema. {newDocument.PageCount} página(s).";
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
            button.Click += (_, _) => NavigateToPage(index);
            ThumbnailsPanel.Children.Add(button);
        }
    }

    private void NavigateToPage(int index)
    {
        if (_document == null || index < 0 || index >= _document.PageCount) return;
        _pageIndex = index;
        var uri = new UriBuilder(new Uri(_currentPath))
        {
            Fragment = $"page={index + 1}&zoom={Math.Round(_zoom * 100)}"
        };
        PdfNativeView.Source = uri.Uri;
        PageCounterText.Text = $"Página {index + 1} de {_document.PageCount}";
        ZoomText.Text = $"{_zoom:P0}";
        BuildThumbnails();
    }

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, 0.25, 5.0);
        NavigateToPage(_pageIndex);
    }

    private void ZoomIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetZoom(_zoom * 1.2);
    private void ZoomOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetZoom(_zoom / 1.2);

    private void FitWidth_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // O visualizador nativo controla o ajuste de largura; o fragmento solicita modo page-width.
        if (_document == null) return;
        var uri = new UriBuilder(new Uri(_currentPath)) { Fragment = $"page={_pageIndex + 1}&zoom=page-width" };
        PdfNativeView.Source = uri.Uri;
        ViewerStatusText.Text = "Ajuste de largura solicitado ao visualizador nativo.";
    }

    private void PrintPdf_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            PdfNativeView.ShowPrintUI();
            ViewerStatusText.Text = "Diálogo de impressão aberto.";
        }
        catch (Exception ex)
        {
            ViewerStatusText.Text = $"Não foi possível abrir a impressão: {ex.Message}";
        }
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

    private void Viewer_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left) NavigateToPage(_pageIndex - 1);
        else if (e.Key == Key.Right) NavigateToPage(_pageIndex + 1);
        else if (e.Key == Key.Add || (e.Key == Key.OemPlus && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            SetZoom(_zoom * 1.2);
        else if (e.Key == Key.Subtract || (e.Key == Key.OemMinus && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            SetZoom(_zoom / 1.2);
        else if (e.Key == Key.P && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            PdfNativeView.ShowPrintUI();
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
                Title = $"AssinaPDF | {Path.GetFileName(editedPath)}";
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
        foreach (var bitmap in _thumbnails) bitmap.Dispose();
        _thumbnails.Clear();
    }
}
