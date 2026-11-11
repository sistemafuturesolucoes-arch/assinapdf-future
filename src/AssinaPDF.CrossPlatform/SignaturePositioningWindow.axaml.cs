using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public partial class SignaturePositioningWindow : Window
{
    private PdfiumDocument? _document;
    private readonly List<SignaturePlacement> _placements = new();
    private readonly Dictionary<int, Bitmap> _thumbnailCache = new();

    private WriteableBitmap? _bitmap;
    private int _pageIndex;
    private float _pageWidthPoints;
    private float _pageHeightPoints;
    private Point _dragStart;
    private Point _dragCurrent;
    private bool _isDragging;

    public IReadOnlyList<SignaturePlacement> SelectedPlacements { get; private set; }
        = Array.Empty<SignaturePlacement>();

    public SignaturePositioningWindow()
    {
        InitializeComponent();

        PdfPageSurface.Cursor = new Cursor(StandardCursorType.Cross);

        Closed += (_, _) =>
        {
            _bitmap?.Dispose();
            _bitmap = null;

            foreach (var thumbnail in _thumbnailCache.Values)
                thumbnail.Dispose();

            _thumbnailCache.Clear();
        };
    }

    public SignaturePositioningWindow(PdfiumDocument document) : this()
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));

        BuildThumbnails();
        ShowPage(0);
    }

    private static WriteableBitmap CreateBitmap(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(page.Width, page.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        try
        {
            using var framebuffer = bitmap.Lock();
            int bytesPerRow = checked(page.Width * 4);

            for (int y = 0; y < page.Height; y++)
            {
                IntPtr destination = IntPtr.Add(
                    framebuffer.Address,
                    checked(y * framebuffer.RowBytes));

                Marshal.Copy(
                    page.Pixels,
                    checked(y * page.Stride),
                    destination,
                    bytesPerRow);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap CreateThumbnail(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(page.Width, page.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        try
        {
            using var framebuffer = bitmap.Lock();
            int bytesPerRow = checked(page.Width * 4);

            for (int y = 0; y < page.Height; y++)
            {
                IntPtr destination = IntPtr.Add(
                    framebuffer.Address,
                    checked(y * framebuffer.RowBytes));

                Marshal.Copy(
                    page.Pixels,
                    checked(y * page.Stride),
                    destination,
                    bytesPerRow);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private void BuildThumbnails()
    {
        var document = _document;
        if (document == null)
            return;

        ThumbnailsPanel.Children.Clear();

        for (int i = 0; i < document.PageCount; i++)
        {
            int pageIndex = i;

            var button = new Button
            {
                Padding = new Thickness(5),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(205, 205, 215)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Tag = pageIndex
            };

            var content = new StackPanel { Spacing = 4 };

            var image = new Image
            {
                Height = 112,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = $"Página {pageIndex + 1}",
                FontSize = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(55, 50, 65))
            };

            content.Children.Add(image);
            content.Children.Add(label);
            button.Content = content;

            button.Click += (_, _) => ShowPage(pageIndex);

            ThumbnailsPanel.Children.Add(button);

            try
            {
                if (!_thumbnailCache.TryGetValue(pageIndex, out var thumbnail))
                {
                    var rendered = document.RenderPage(pageIndex, 0.22f);
                    thumbnail = CreateThumbnail(rendered);
                    _thumbnailCache[pageIndex] = thumbnail;
                }

                image.Source = thumbnail;
            }
            catch
            {
                label.Text = $"Página {pageIndex + 1} (prévia indisponível)";
            }
        }

        UpdateThumbnailSelection();
    }

    private void UpdateThumbnailSelection()
    {
        for (int i = 0; i < ThumbnailsPanel.Children.Count; i++)
        {
            if (ThumbnailsPanel.Children[i] is not Button button)
                continue;

            bool selected = i == _pageIndex;

            button.BorderBrush = selected
                ? new SolidColorBrush(Color.FromRgb(95, 45, 180))
                : new SolidColorBrush(Color.FromRgb(205, 205, 215));

            button.BorderThickness = selected
                ? new Thickness(2)
                : new Thickness(1);

            button.Background = selected
                ? new SolidColorBrush(Color.FromRgb(239, 231, 250))
                : Brushes.White;
        }
    }

    private void ShowPage(int pageIndex)
    {
        var document = _document;

        if (document == null ||
            pageIndex < 0 ||
            pageIndex >= document.PageCount)
            return;

        try
        {
            var renderedPage = document.RenderPage(pageIndex, 1.0f);
            var newBitmap = CreateBitmap(renderedPage);
            var oldBitmap = _bitmap;

            _bitmap = newBitmap;
            _pageIndex = pageIndex;
            _pageWidthPoints = renderedPage.PageWidthPoints;
            _pageHeightPoints = renderedPage.PageHeightPoints;

            PdfPageImage.Source = newBitmap;
            PdfPageImage.Width = renderedPage.Width;
            PdfPageImage.Height = renderedPage.Height;

            MarkerCanvas.Width = renderedPage.Width;
            MarkerCanvas.Height = renderedPage.Height;

            PdfPageSurface.Width = renderedPage.Width;
            PdfPageSurface.Height = renderedPage.Height;

            PdfScrollViewer.Offset = new Vector(0, 0);

            oldBitmap?.Dispose();

            RefreshMarkers();
            UpdatePageControls();
            UpdateThumbnailSelection();
        }
        catch (Exception ex)
        {
            PositionStatusText.Text = $"Erro ao renderizar página: {ex.Message}";
        }
    }

    private void PdfPageSurface_PointerPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (_document == null ||
            _bitmap == null ||
            !e.GetCurrentPoint(PdfPageSurface).Properties.IsLeftButtonPressed)
            return;

        var point = e.GetPosition(PdfPageImage);
        var bounds = PdfPageImage.Bounds;

        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            point.X < 0 || point.Y < 0 ||
            point.X > bounds.Width || point.Y > bounds.Height)
            return;

        _dragStart = point;
        _dragCurrent = point;
        _isDragging = true;
        e.Pointer.Capture(PdfPageSurface);
        RefreshMarkers();
        DrawTemporarySelection();
        PositionStatusText.Text = "Arraste para definir a largura e a altura da assinatura.";
        e.Handled = true;
    }

    private void PdfPageSurface_PointerMoved(
        object? sender,
        PointerEventArgs e)
    {
        if (!_isDragging)
            return;

        _dragCurrent = e.GetPosition(PdfPageImage);
        DrawTemporarySelection();
        e.Handled = true;
    }

    private void PdfPageSurface_PointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        if (!_isDragging)
            return;

        _dragCurrent = e.GetPosition(PdfPageImage);
        _isDragging = false;
        e.Pointer.Capture(null);

        var bounds = PdfPageImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        double left = Math.Clamp(Math.Min(_dragStart.X, _dragCurrent.X), 0, bounds.Width);
        double top = Math.Clamp(Math.Min(_dragStart.Y, _dragCurrent.Y), 0, bounds.Height);
        double width = Math.Clamp(Math.Abs(_dragCurrent.X - _dragStart.X), 0, bounds.Width - left);
        double height = Math.Clamp(Math.Abs(_dragCurrent.Y - _dragStart.Y), 0, bounds.Height - top);

        if (width < 12 || height < 12)
        {
            RefreshMarkers();
            PositionStatusText.Text = "Seleção muito pequena. Clique e arraste uma área maior.";
            e.Handled = true;
            return;
        }

        _placements.Add(new SignaturePlacement(
            _pageIndex,
            left / bounds.Width,
            top / bounds.Height,
            width / bounds.Width,
            height / bounds.Height,
            _pageWidthPoints,
            _pageHeightPoints));

        RefreshMarkers();
        PositionStatusText.Text =
            $"Área da assinatura {_placements.Count} definida na página {_pageIndex + 1}.";
        e.Handled = true;
    }

    private void DrawTemporarySelection()
    {
        RefreshMarkers();

        double left = Math.Min(_dragStart.X, _dragCurrent.X);
        double top = Math.Min(_dragStart.Y, _dragCurrent.Y);
        double width = Math.Abs(_dragCurrent.X - _dragStart.X);
        double height = Math.Abs(_dragCurrent.Y - _dragStart.Y);

        var preview = new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromArgb(48, 124, 58, 237)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(124, 58, 237)),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false
        };

        Canvas.SetLeft(preview, left);
        Canvas.SetTop(preview, top);
        MarkerCanvas.Children.Add(preview);
    }

    private void RefreshMarkers()
    {
        MarkerCanvas.Children.Clear();

        for (int i = 0; i < _placements.Count; i++)
        {
            var placement = _placements[i];
            if (placement.PageIndex != _pageIndex)
                continue;

            double width = Math.Max(12, placement.NormalizedWidth * MarkerCanvas.Width);
            double height = Math.Max(12, placement.NormalizedHeight * MarkerCanvas.Height);
            double left = placement.NormalizedX * MarkerCanvas.Width;
            double top = placement.NormalizedY * MarkerCanvas.Height;

            var marker = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(38, 124, 58, 237)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(124, 58, 237)),
                BorderThickness = new Thickness(2),
                Child = new TextBlock
                {
                    Text = $"Assinatura {i + 1}",
                    Foreground = new SolidColorBrush(Color.FromRgb(91, 33, 182)),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };

            Canvas.SetLeft(marker, left);
            Canvas.SetTop(marker, top);
            MarkerCanvas.Children.Add(marker);
        }

        PositionCountText.Text = _placements.Count == 1
            ? "1 posição"
            : $"{_placements.Count} posições";
    }

    private void UpdatePageControls()
    {
        var document = _document;
        if (document == null)
            return;

        PageIndicator.Text = $"Página {_pageIndex + 1} de {document.PageCount}";
    }

    private void ClearPositions_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        _placements.Clear();
        RefreshMarkers();
        PositionStatusText.Text = "Todas as posições foram removidas.";
    }

    private void Cancel_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectedPlacements = Array.Empty<SignaturePlacement>();
        Close();
    }

    private void Complete_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_placements.Count == 0)
        {
            PositionStatusText.Text =
                "Marque pelo menos uma posição antes de concluir.";
            return;
        }

        SelectedPlacements = _placements.ToArray();
        Close();
    }
}

