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

public partial class BatchPdfPositioningControl : UserControl
{
    private PdfiumDocument? _document;
    private string? _currentPath;
    private readonly List<SignaturePlacement> _placements = new();
    private WriteableBitmap? _bitmap;
    private int _pageIndex;
    private float _pageWidthPoints;
    private float _pageHeightPoints;
    private Point _dragStart;
    private Point _dragCurrent;
    private bool _isDragging;
    private double _zoom = 1.0;

    public event Action<IReadOnlyList<SignaturePlacement>>? PlacementsChanged;

    public BatchPdfPositioningControl()
    {
        InitializeComponent();
        PdfPageSurface.Cursor = new Cursor(StandardCursorType.Cross);
        Unloaded += (_, _) => ReleaseDocument();
    }

    public void SetDocument(string? path, IReadOnlyList<SignaturePlacement>? placements)
    {
        if (string.Equals(_currentPath, path, StringComparison.Ordinal))
        {
            _placements.Clear();
            if (placements != null) _placements.AddRange(placements);
            RefreshMarkers();
            return;
        }

        ReleaseDocument();
        _currentPath = path;
        _placements.Clear();
        if (placements != null) _placements.AddRange(placements);
        _pageIndex = 0;
        _zoom = 1.0;

        if (string.IsNullOrWhiteSpace(path))
        {
            PreviewStatusText.Text = "Selecione um PDF para visualizar e marcar as posições.";
            PageIndicator.Text = "Página —";
            PdfPageImage.Source = null;
            MarkerCanvas.Children.Clear();
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            return;
        }

        try
        {
            _document = new PdfiumDocument(path);
            _zoom = CalculateFitWidthZoom();
            ShowPage(0);
            PreviewStatusText.Text = "Clique e arraste sobre o PDF para criar uma área de assinatura. As posições ficam salvas neste documento.";
        }
        catch (Exception ex)
        {
            PreviewStatusText.Text = "Não foi possível abrir a prévia: " + ex.Message;
        }
    }

    private void ReleaseDocument()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _document?.Dispose();
        _document = null;
    }

    private static WriteableBitmap CreateBitmap(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(new PixelSize(page.Width, page.Height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using var fb = bitmap.Lock();
            int rowBytes = checked(page.Width * 4);
            for (int y = 0; y < page.Height; y++)
                Marshal.Copy(page.Pixels, checked(y * page.Stride),
                    IntPtr.Add(fb.Address, checked(y * fb.RowBytes)), rowBytes);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private void ShowPage(int index)
    {
        if (_document == null || index < 0 || index >= _document.PageCount) return;
        try
        {
            var rendered = _document.RenderPage(index, (float)_zoom);
            var next = CreateBitmap(rendered);
            var previous = _bitmap;
            _bitmap = next;
            _pageIndex = index;
            _pageWidthPoints = rendered.PageWidthPoints;
            _pageHeightPoints = rendered.PageHeightPoints;
            PdfPageImage.Source = next;
            PdfPageImage.Width = rendered.Width;
            PdfPageImage.Height = rendered.Height;
            MarkerCanvas.Width = rendered.Width;
            MarkerCanvas.Height = rendered.Height;
            PdfPageSurface.Width = rendered.Width;
            PdfPageSurface.Height = rendered.Height;
            PdfScrollViewer.Offset = new Vector(0,0);
            previous?.Dispose();
            RefreshMarkers();
            PageIndicator.Text = $"{index + 1}/{_document.PageCount}";
            PreviousPageButton.IsEnabled = index > 0;
            NextPageButton.IsEnabled = index < _document.PageCount - 1;
            ZoomText.Text = $"{_zoom:P0}";
        }
        catch (Exception ex) { PreviewStatusText.Text = "Erro ao renderizar página: " + ex.Message; }
    }

    private void PdfPageSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_document == null || _bitmap == null ||
            !e.GetCurrentPoint(PdfPageSurface).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(PdfPageImage);
        var b = PdfPageImage.Bounds;
        if (b.Width <= 0 || b.Height <= 0 || p.X < 0 || p.Y < 0 || p.X > b.Width || p.Y > b.Height) return;
        _dragStart = _dragCurrent = p;
        _isDragging = true;
        e.Pointer.Capture(PdfPageSurface);
        DrawTemporarySelection();
        e.Handled = true;
    }

    private void PdfPageSurface_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        _dragCurrent = e.GetPosition(PdfPageImage);
        DrawTemporarySelection();
        e.Handled = true;
    }

    private void PdfPageSurface_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        _dragCurrent = e.GetPosition(PdfPageImage);
        _isDragging = false;
        e.Pointer.Capture(null);
        var b = PdfPageImage.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        double left = Math.Clamp(Math.Min(_dragStart.X,_dragCurrent.X),0,b.Width);
        double top = Math.Clamp(Math.Min(_dragStart.Y,_dragCurrent.Y),0,b.Height);
        double width = Math.Clamp(Math.Abs(_dragCurrent.X-_dragStart.X),0,b.Width-left);
        double height = Math.Clamp(Math.Abs(_dragCurrent.Y-_dragStart.Y),0,b.Height-top);
        if (width < 12 || height < 12) { RefreshMarkers(); return; }
        _placements.Add(new SignaturePlacement(_pageIndex, left/b.Width, top/b.Height,
            width/b.Width, height/b.Height, _pageWidthPoints, _pageHeightPoints));
        RefreshMarkers();
        PlacementsChanged?.Invoke(_placements.ToArray());
        PreviewStatusText.Text = $"{_placements.Count} posição(ões) definida(s) neste PDF.";
        e.Handled = true;
    }

    private void DrawTemporarySelection()
    {
        RefreshMarkers();
        double l=Math.Min(_dragStart.X,_dragCurrent.X), t=Math.Min(_dragStart.Y,_dragCurrent.Y);
        var shape = new Border { Width=Math.Abs(_dragCurrent.X-_dragStart.X),
            Height=Math.Abs(_dragCurrent.Y-_dragStart.Y),
            Background=new SolidColorBrush(Color.FromArgb(48,124,58,237)),
            BorderBrush=new SolidColorBrush(Color.FromRgb(124,58,237)),
            BorderThickness=new Thickness(2), IsHitTestVisible=false };
        Canvas.SetLeft(shape,l); Canvas.SetTop(shape,t); MarkerCanvas.Children.Add(shape);
    }

    private void RefreshMarkers()
    {
        MarkerCanvas.Children.Clear();
        for(int i=0;i<_placements.Count;i++)
        {
            var p=_placements[i];
            if(p.PageIndex!=_pageIndex) continue;
            var marker=new Border { Width=Math.Max(12,p.NormalizedWidth*MarkerCanvas.Width),
                Height=Math.Max(12,p.NormalizedHeight*MarkerCanvas.Height),
                CornerRadius=new CornerRadius(3),
                Background=new SolidColorBrush(Color.FromArgb(38,124,58,237)),
                BorderBrush=new SolidColorBrush(Color.FromRgb(124,58,237)),
                BorderThickness=new Thickness(2),
                Child=new TextBlock { Text=$"Assinatura {i+1}", FontSize=11,
                    FontWeight=FontWeight.SemiBold, Foreground=new SolidColorBrush(Color.FromRgb(91,33,182)),
                    HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center } };
            Canvas.SetLeft(marker,p.NormalizedX*MarkerCanvas.Width);
            Canvas.SetTop(marker,p.NormalizedY*MarkerCanvas.Height);
            MarkerCanvas.Children.Add(marker);
        }
    }

    private void ClearPositions_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _placements.Clear();
        RefreshMarkers();
        PlacementsChanged?.Invoke(Array.Empty<SignaturePlacement>());
        PreviewStatusText.Text = "Todas as posições deste PDF foram removidas.";
    }

    private double CalculateFitWidthZoom()
    {
        if (_document == null) return 0.7;
        double availableWidth = PdfScrollViewer.Bounds.Width;
        if (availableWidth <= 0) return 0.7;

        var sample = _document.RenderPage(_pageIndex, 0.1f);
        double pageWidthAt100Percent = sample.PageWidthPoints;
        if (pageWidthAt100Percent <= 0) return 0.7;

        return Math.Clamp((availableWidth - 32) / pageWidthAt100Percent, 0.35, 1.5);
    }

    private void FitWidth_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoom = CalculateFitWidthZoom();
        ShowPage(_pageIndex);
    }

    private void PreviousPage_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    { if (_pageIndex > 0) ShowPage(_pageIndex - 1); }

    private void NextPage_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    { if (_document != null && _pageIndex < _document.PageCount - 1) ShowPage(_pageIndex + 1); }

    private void ZoomIn_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    { _zoom=Math.Min(2.5,_zoom+0.15); ShowPage(_pageIndex); }
    private void ZoomOut_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    { _zoom=Math.Max(0.5,_zoom-0.15); ShowPage(_pageIndex); }
    private void FitPage_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    { _zoom=1.0; ShowPage(_pageIndex); }
}
