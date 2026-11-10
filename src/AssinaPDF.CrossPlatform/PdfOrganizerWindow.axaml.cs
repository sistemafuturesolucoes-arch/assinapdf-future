using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public partial class PdfOrganizerWindow : Window
{
    private sealed class OrganizerPage
    {
        public required WriteableBitmap Bitmap { get; set; }
        public required string SourcePath { get; init; }
        public required string SourceName { get; init; }
        public required int SourcePageNumber { get; init; }
        public required int SequenceNumber { get; set; }
        public int RotationQuarterTurns { get; set; }
        public Border? PageCard { get; set; }
        public Image? PageImage { get; set; }
        public Border? ThumbnailCard { get; set; }
    }

    private readonly List<OrganizerPage> _pages = new();
    private double _zoom = 1.0;
    private bool _loading;
    private bool _saving;
    private OrganizerPage? _selectedPage;
    private readonly HashSet<OrganizerPage> _selectedPages = new();
    private readonly Stack<List<OrganizerPage>> _undo = new();
    private readonly Stack<List<OrganizerPage>> _redo = new();
    private OrganizerPage? _rangeAnchor;
    private OrganizerPage? _dragPage;
    private bool _insertBeforeNextAdd;
    private bool _insertAfterNextAdd;
    private bool _isDirty;
    private bool _allowClose;

    public PdfOrganizerWindow()
    {
        InitializeComponent();
        Closed += (_, _) => DisposePages();
        Closing += OrganizerWindow_Closing;
        UpdatePageCount();
        UpdateZoomLabel();
    }

    private async void AddDocuments_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading) return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Adicionar documentos PDF",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Documentos PDF") { Patterns = new[] { "*.pdf", "*.PDF" } }
                }
            });

            if (files.Count == 0) return;

            _loading = true;
            AddDocumentsButton.IsEnabled = false;
            StatusText.Text = "Carregando e preparando as páginas...";

            int addedPages = 0;
            int insertionIndex = _pages.Count;
            if (_selectedPage != null && (_insertBeforeNextAdd || _insertAfterNextAdd))
            {
                insertionIndex = _pages.IndexOf(_selectedPage);
                if (_insertAfterNextAdd) insertionIndex++;
                if (insertionIndex < 0) insertionIndex = _pages.Count;
            }
            PushUndoState();
            foreach (var file in files)
            {
                string path = file.Path.LocalPath;
                using var document = new PdfiumDocument(path);
                var documentPages = new List<OrganizerPage>();

                try
                {
                    for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
                    {
                        var rendered = document.RenderPage(pageIndex, 1.25f);
                        documentPages.Add(new OrganizerPage
                        {
                            Bitmap = CreateBitmap(rendered),
                            SourcePath = path,
                            SourceName = Path.GetFileName(path),
                            SourcePageNumber = pageIndex + 1,
                            SequenceNumber = _pages.Count + documentPages.Count + 1
                        });
                    }
                }
                catch
                {
                    foreach (var page in documentPages) page.Bitmap.Dispose();
                    throw;
                }

                _pages.InsertRange(insertionIndex, documentPages);
                insertionIndex += documentPages.Count;
                addedPages += documentPages.Count;
            }

            _selectedPages.Clear();
            _selectedPage = _pages.LastOrDefault();
            if (_selectedPage != null) _selectedPages.Add(_selectedPage);
            _insertBeforeNextAdd = _insertAfterNextAdd = false;
            MarkDirty();
            RebuildPageViews();
            UpdatePageCount();
            StatusText.Text = $"{files.Count} documento(s) adicionado(s). {addedPages} página(s) inserida(s) na sequência.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível adicionar os documentos: {ex.Message}";
        }
        finally
        {
            _loading = false;
            AddDocumentsButton.IsEnabled = true;
        }
    }

    private async void SaveAs_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_saving || _loading) return;
        if (_pages.Count == 0)
        {
            StatusText.Text = "Adicione pelo menos um PDF antes de salvar.";
            return;
        }

        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Salvar PDF combinado como",
                SuggestedFileName = "documento-combinado.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } }
                }
            });

            if (destination == null) return;

            _saving = true;
            SaveAsButton.IsEnabled = false;
            AddDocumentsButton.IsEnabled = false;
            StatusText.Text = "Montando o PDF final...";

            string outputPath = destination.Path.LocalPath;
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new InvalidOperationException("Não foi possível identificar o caminho do arquivo de destino.");
            if (!outputPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                outputPath += ".pdf";

            string normalizedOutputPath = Path.GetFullPath(outputPath);
            if (_pages.Any(page => string.Equals(Path.GetFullPath(page.SourcePath), normalizedOutputPath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Escolha um nome ou local diferente dos PDFs originais. O AssinaPDF não sobrescreve os documentos de origem.");

            var sourceDocuments = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
            using var outputDocument = new PdfDocument();
            try
            {
                foreach (var page in _pages)
                {
                    if (!sourceDocuments.TryGetValue(page.SourcePath, out var sourceDocument))
                    {
                        sourceDocument = PdfReader.Open(page.SourcePath, PdfDocumentOpenMode.Import);
                        sourceDocuments.Add(page.SourcePath, sourceDocument);
                    }

                    int sourceIndex = page.SourcePageNumber - 1;
                    if (sourceIndex < 0 || sourceIndex >= sourceDocument.Pages.Count)
                        throw new InvalidDataException($"A página {page.SourcePageNumber} não foi encontrada em {page.SourceName}.");

                    var importedPage = outputDocument.AddPage(sourceDocument.Pages[sourceIndex]);
                    if (page.RotationQuarterTurns != 0)
                        importedPage.Rotate = (importedPage.Rotate + page.RotationQuarterTurns * 90) % 360;
                }

                outputDocument.Save(outputPath);
            }
            finally
            {
                foreach (var sourceDocument in sourceDocuments.Values)
                    sourceDocument.Dispose();
            }

            StatusText.Text = $"PDF salvo com sucesso: {Path.GetFileName(outputPath)}. Os arquivos originais foram preservados.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível salvar o PDF: {ex.Message}";
        }
        finally
        {
            _saving = false;
            SaveAsButton.IsEnabled = true;
            AddDocumentsButton.IsEnabled = true;
        }
    }


    private void SelectPage(OrganizerPage page, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Shift) && _rangeAnchor != null)
        {
            int a = _pages.IndexOf(_rangeAnchor);
            int b = _pages.IndexOf(page);
            if (a >= 0 && b >= 0)
            {
                _selectedPages.Clear();
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _selectedPages.Add(_pages[i]);
            }
        }
        else if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta))
        {
            if (!_selectedPages.Add(page)) _selectedPages.Remove(page);
            _rangeAnchor = page;
        }
        else
        {
            _selectedPages.Clear();
            _selectedPages.Add(page);
            _rangeAnchor = page;
        }

        _selectedPage = page;
        UpdateSelectionVisuals();
    }

    private void CompleteThumbnailDrag(PointerReleasedEventArgs args)
    {
        if (_dragPage == null) return;
        var dragged = _dragPage;
        _dragPage = null;
        var position = args.GetPosition(ThumbnailsPanel);
        var target = _pages.FirstOrDefault(p => p.ThumbnailCard != null &&
            position.Y >= p.ThumbnailCard.Bounds.Y &&
            position.Y <= p.ThumbnailCard.Bounds.Bottom);
        if (target == null || ReferenceEquals(target, dragged)) return;

        PushUndoState();
        int oldIndex = _pages.IndexOf(dragged);
        int newIndex = _pages.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0) return;
        _pages.RemoveAt(oldIndex);
        if (oldIndex < newIndex) newIndex--;
        _pages.Insert(newIndex, dragged);
        MarkDirty();
        RebuildPageViews();
        UpdatePageCount();
        StatusText.Text = "Página reposicionada por arrastar e soltar.";
    }

    private void InsertBefore_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedPage == null) { StatusText.Text = "Selecione a página de referência primeiro."; return; }
        _insertBeforeNextAdd = true;
        _insertAfterNextAdd = false;
        AddDocuments_Click(sender, e);
    }

    private void InsertAfter_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedPage == null) { StatusText.Text = "Selecione a página de referência primeiro."; return; }
        _insertBeforeNextAdd = false;
        _insertAfterNextAdd = true;
        AddDocuments_Click(sender, e);
    }

    private void DeleteSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedPages.Count == 0) { StatusText.Text = "Selecione uma ou mais páginas para excluir."; return; }
        PushUndoState();
        int removed = _selectedPages.Count;
        _pages.RemoveAll(p => _selectedPages.Contains(p));
        _selectedPages.Clear();
        _selectedPage = _pages.FirstOrDefault();
        if (_selectedPage != null) _selectedPages.Add(_selectedPage);
        MarkDirty();
        RebuildPageViews();
        UpdatePageCount();
        StatusText.Text = $"{removed} página(s) removida(s). Use Desfazer para recuperar.";
    }

    private void MoveUp_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => MoveSelectedPages(-1);
    private void MoveDown_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => MoveSelectedPages(1);

    private void MoveSelectedPages(int direction)
    {
        if (_selectedPages.Count == 0) { StatusText.Text = "Selecione as páginas que deseja mover."; return; }
        PushUndoState();
        if (direction < 0)
        {
            for (int i = 1; i < _pages.Count; i++)
                if (_selectedPages.Contains(_pages[i]) && !_selectedPages.Contains(_pages[i - 1]))
                    (_pages[i - 1], _pages[i]) = (_pages[i], _pages[i - 1]);
        }
        else
        {
            for (int i = _pages.Count - 2; i >= 0; i--)
                if (_selectedPages.Contains(_pages[i]) && !_selectedPages.Contains(_pages[i + 1]))
                    (_pages[i], _pages[i + 1]) = (_pages[i + 1], _pages[i]);
        }
        MarkDirty();
        RebuildPageViews();
        UpdatePageCount();
        StatusText.Text = direction < 0 ? "Páginas movidas para cima." : "Páginas movidas para baixo.";
    }

    private void PushUndoState()
    {
        _undo.Push(_pages.ToList());
        _redo.Clear();
    }

    private void Undo_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_undo.Count == 0) { StatusText.Text = "Não há alterações para desfazer."; return; }
        _redo.Push(_pages.ToList());
        _pages.Clear();
        _pages.AddRange(_undo.Pop());
        _selectedPages.Clear();
        _selectedPage = _pages.FirstOrDefault();
        if (_selectedPage != null) _selectedPages.Add(_selectedPage);
        MarkDirty();
        RebuildPageViews();
        UpdatePageCount();
        StatusText.Text = "Última alteração desfeita.";
    }

    private void Redo_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_redo.Count == 0) { StatusText.Text = "Não há alterações para refazer."; return; }
        _undo.Push(_pages.ToList());
        _pages.Clear();
        _pages.AddRange(_redo.Pop());
        _selectedPages.Clear();
        _selectedPage = _pages.FirstOrDefault();
        if (_selectedPage != null) _selectedPages.Add(_selectedPage);
        MarkDirty();
        RebuildPageViews();
        UpdatePageCount();
        StatusText.Text = "Alteração refeita.";
    }

    private async void ExportSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = _pages.Where(p => _selectedPages.Contains(p)).ToList();
        if (selected.Count == 0) { StatusText.Text = "Selecione as páginas que deseja exportar."; return; }
        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Exportar páginas selecionadas",
                SuggestedFileName = "paginas-selecionadas.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } }
            });
            if (destination == null) return;
            string path = destination.Path.LocalPath;
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) path += ".pdf";
            var sources = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
            using var output = new PdfDocument();
            try
            {
                foreach (var page in selected)
                {
                    if (!sources.TryGetValue(page.SourcePath, out var source))
                    {
                        source = PdfReader.Open(page.SourcePath, PdfDocumentOpenMode.Import);
                        sources.Add(page.SourcePath, source);
                    }
                    var imported = output.AddPage(source.Pages[page.SourcePageNumber - 1]);
                    if (page.RotationQuarterTurns != 0)
                        imported.Rotate = (imported.Rotate + page.RotationQuarterTurns * 90) % 360;
                }
                output.Save(path);
            }
            finally { foreach (var source in sources.Values) source.Dispose(); }
            StatusText.Text = $"{selected.Count} página(s) exportada(s) para {Path.GetFileName(path)}.";
        }
        catch (Exception ex) { StatusText.Text = $"Falha ao exportar seleção: {ex.Message}"; }
    }

    private void MarkDirty()
    {
        _isDirty = true;
        Title = "AssinaPDF | Combinar e organizar PDF *";
    }

    private async void OrganizerWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || !_isDirty) return;
        e.Cancel = true;
        var prompt = new Window
        {
            Title = "Alterações não salvas",
            Width = 390,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "Existem alterações que ainda não foram salvas. Deseja fechar sem salvar?", TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Continuar editando", MinWidth = 120 },
                            new Button { Content = "Fechar sem salvar", MinWidth = 120 }
                        }
                    }
                }
            }
        };
        var panel = (StackPanel)prompt.Content!;
        var actions = (StackPanel)panel.Children[1];
        ((Button)actions.Children[0]).Click += (_, _) => prompt.Close(false);
        ((Button)actions.Children[1]).Click += (_, _) => prompt.Close(true);
        bool? discard = await prompt.ShowDialog<bool?>(this);
        if (discard == true)
        {
            _allowClose = true;
            Close();
        }
    }

    private void RotateLeft_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RotateSelectedPage(-1);

    private void RotateRight_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RotateSelectedPage(1);

    private void RotateSelectedPage(int direction)
    {
        if (_selectedPage == null)
        {
            StatusText.Text = "Selecione uma página na coluna à esquerda para girá-la.";
            return;
        }

        var page = _selectedPage;
        var previousBitmap = page.Bitmap;
        try
        {
            page.Bitmap = RotateBitmap90(previousBitmap, direction > 0);
            PushUndoState();
            page.RotationQuarterTurns = (page.RotationQuarterTurns + (direction > 0 ? 1 : 3)) % 4;
            MarkDirty();
            RebuildPageViews();
            UpdateSelectionVisuals();
            StatusText.Text = $"Página {page.SequenceNumber} girada. A rotação será aplicada ao PDF quando salvarmos o documento final.";
            previousBitmap.Dispose();
        }
        catch (Exception ex)
        {
            page.Bitmap = previousBitmap;
            StatusText.Text = $"Não foi possível girar a página: {ex.Message}";
        }
    }

    private static WriteableBitmap RotateBitmap90(WriteableBitmap source, bool clockwise)
    {
        int sourceWidth = source.PixelSize.Width;
        int sourceHeight = source.PixelSize.Height;
        int sourceStride = checked(sourceWidth * 4);
        var sourcePixels = new byte[checked(sourceStride * sourceHeight)];

        using (var sourceLock = source.Lock())
        {
            for (int y = 0; y < sourceHeight; y++)
                Marshal.Copy(IntPtr.Add(sourceLock.Address, y * sourceLock.RowBytes), sourcePixels, y * sourceStride, sourceStride);
        }

        int targetWidth = sourceHeight;
        int targetHeight = sourceWidth;
        int targetStride = checked(targetWidth * 4);
        var targetPixels = new byte[checked(targetStride * targetHeight)];

        for (int y = 0; y < sourceHeight; y++)
        {
            for (int x = 0; x < sourceWidth; x++)
            {
                int targetX = clockwise ? sourceHeight - 1 - y : y;
                int targetY = clockwise ? x : sourceWidth - 1 - x;
                int sourceIndex = y * sourceStride + x * 4;
                int targetIndex = targetY * targetStride + targetX * 4;
                Buffer.BlockCopy(sourcePixels, sourceIndex, targetPixels, targetIndex, 4);
            }
        }

        var rotated = new WriteableBitmap(
            new PixelSize(targetWidth, targetHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        try
        {
            using var targetLock = rotated.Lock();
            for (int y = 0; y < targetHeight; y++)
                Marshal.Copy(targetPixels, y * targetStride, IntPtr.Add(targetLock.Address, y * targetLock.RowBytes), targetStride);
            return rotated;
        }
        catch
        {
            rotated.Dispose();
            throw;
        }
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var item in _pages)
        {
            if (item.ThumbnailCard != null)
            {
                bool selected = _selectedPages.Contains(item);
                item.ThumbnailCard.BorderBrush = new SolidColorBrush(selected
                    ? Color.FromRgb(103, 65, 184)
                    : Color.FromRgb(220, 224, 231));
                item.ThumbnailCard.BorderThickness = new Thickness(selected ? 3 : 1);
            }
        }
    }

    private void ZoomIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoom = Math.Min(2.5, _zoom + 0.1);
        ApplyZoom();
    }

    private void ZoomOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoom = Math.Max(0.4, _zoom - 0.1);
        ApplyZoom();
    }

    private void ZoomReset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoom = 1.0;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        foreach (var page in _pages)
        {
            if (page.PageImage == null) continue;
            double width = 720 * _zoom;
            double ratio = (double)page.Bitmap.PixelSize.Height / page.Bitmap.PixelSize.Width;
            page.PageImage.Width = width;
            page.PageImage.Height = width * ratio;
        }
        UpdateZoomLabel();
    }

    private void RebuildPageViews()
    {
        ThumbnailsPanel.Children.Clear();
        PagesPanel.Children.Clear();

        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            page.SequenceNumber = i + 1;

            var thumbnail = new Image
            {
                Source = page.Bitmap,
                Width = 150,
                Height = 190,
                Stretch = Stretch.Uniform
            };
            var thumbnailLabel = new TextBlock
            {
                Text = $"Página {i + 1}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 2)
            };
            var sourceLabel = new TextBlock
            {
                Text = page.SourceName,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(112, 119, 130)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 170,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            var thumbnailContent = new StackPanel { Spacing = 2 };
            thumbnailContent.Children.Add(thumbnail);
            thumbnailContent.Children.Add(thumbnailLabel);
            thumbnailContent.Children.Add(sourceLabel);

            var thumbnailBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 231)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8),
                Margin = new Thickness(5),
                Child = thumbnailContent
            };
            page.ThumbnailCard = thumbnailBorder;
            thumbnailBorder.PointerPressed += (_, args) =>
            {
                SelectPage(page, args.KeyModifiers);
                _dragPage = page;
                UpdateSelectionVisuals();
                if (page.PageCard != null)
                {
                    DocumentScrollViewer.Offset = new Vector(
                        DocumentScrollViewer.Offset.X,
                        Math.Max(0, page.PageCard.Bounds.Y - 8));
                }
            };
            thumbnailBorder.PointerReleased += (_, args) => CompleteThumbnailDrag(args);
            ThumbnailsPanel.Children.Add(thumbnailBorder);

            var pageImage = new Image
            {
                Source = page.Bitmap,
                Stretch = Stretch.Uniform,
                Width = 720 * _zoom,
                Height = 720 * _zoom * page.Bitmap.PixelSize.Height / page.Bitmap.PixelSize.Width
            };
            page.PageImage = pageImage;

            var pageLabel = new TextBlock
            {
                Text = $"Página {i + 1}  •  {page.SourceName}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(82, 88, 99)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var pageContent = new StackPanel { Spacing = 8 };
            pageContent.Children.Add(pageLabel);
            pageContent.Children.Add(pageImage);

            var pageCard = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(221, 225, 232)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(16),
                Margin = new Thickness(12, 10),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Child = pageContent
            };
            page.PageCard = pageCard;
            PagesPanel.Children.Add(pageCard);
        }

        EmptyState.IsVisible = _pages.Count == 0;
        ApplyZoom();
    }

    private void UpdatePageCount()
    {
        PageCountText.Text = $"{_pages.Count} página(s)";
        EmptyState.IsVisible = _pages.Count == 0;
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";

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
                IntPtr destination = IntPtr.Add(framebuffer.Address, checked(y * framebuffer.RowBytes));
                Marshal.Copy(page.Pixels, checked(y * page.Stride), destination, bytesPerRow);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void DisposePages()
    {
        foreach (var page in _pages.Concat(_undo.SelectMany(x => x)).Concat(_redo.SelectMany(x => x)).Distinct())
            page.Bitmap.Dispose();
        _pages.Clear();
    }
}
