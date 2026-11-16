using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AssinaPDF.MacPrototype;

public partial class MdfeImportWindow : Window
{
    private readonly List<MdfeBatchItem> _documents = new();
    private readonly List<MdfeBatchPage> _pages = new();
    private readonly string _temporaryDirectory;
    private float _previewZoom = 1.0f;
    private const float MinZoom = 0.60f, MaxZoom = 2.50f, ZoomStep = 1.10f;

    public MdfeImportWindow()
    {
        InitializeComponent();
        _temporaryDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FutureSolucoes", "AssinaPDF", "MdfeBatchPreview");
        Directory.CreateDirectory(_temporaryDirectory);
        DocumentsList.ItemsSource = _documents;
        DocumentScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, PreviewWheelChanged,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        Closed += (_, _) => DisposeAll();
    }

    private async void AddXml_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Selecionar XMLs autorizados de MDF-e", AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Arquivos XML") { Patterns = new[] { "*.xml", "*.XML" } } }
            });
            if (files.Count == 0) return;
            int generated = 0, rejected = 0, duplicates = 0;
            var errors = new List<string>();
            foreach (var file in files)
            {
                string path;
                try { path = Path.GetFullPath(file.Path.LocalPath); }
                catch { rejected++; errors.Add($"{file.Name}: caminho local indisponível."); continue; }
                if (!File.Exists(path)) { rejected++; errors.Add($"{file.Name}: arquivo não encontrado."); continue; }
                if (_documents.Any(d => PathEquals(d.SourcePath, path)))
                { duplicates++; _documents.Add(new MdfeBatchItem(path, null, null, "Arquivo já importado nesta sessão.", true)); continue; }

                string? pdfPath = null;
                var itemPages = new List<MdfeBatchPage>();
                MdfeDocumentSummary? summary = null;
                try
                {
                    summary = MdfeXmlReader.ReadFile(path);
                    if (_documents.Any(d => d.Summary?.AccessKey == summary.AccessKey))
                    { duplicates++; _documents.Add(new MdfeBatchItem(path, summary, null, "Chave de acesso já importada.", true)); continue; }
                    pdfPath = Path.Combine(_temporaryDirectory, $"damdfe-{Guid.NewGuid():N}.pdf");
                    MdfeDamDfeGenerator.Generate(summary, pdfPath);
                    using (var pdf = new PdfiumDocument(pdfPath))
                    {
                        if (pdf.PageCount < 1) throw new InvalidDataException("O DAMDFE gerado não contém páginas.");
                        for (int i = 0; i < pdf.PageCount; i++)
                        {
                            var rendered = pdf.RenderPage(i, _previewZoom);
                            itemPages.Add(new MdfeBatchPage { SourcePath = pdfPath, SourceName = Path.GetFileName(path),
                                PageIndex = i, Bitmap = CreateBitmap(rendered) });
                        }
                    }
                    _documents.Add(new MdfeBatchItem(path, summary, pdfPath, null));
                    _pages.AddRange(itemPages);
                    generated++;
                }
                catch (Exception ex)
                {
                    foreach (var page in itemPages) page.Bitmap?.Dispose();
                    if (pdfPath != null) TryDelete(pdfPath);
                    rejected++; errors.Add($"{file.Name}: {ex.Message}");
                    _documents.Add(new MdfeBatchItem(path, summary, null, ex.Message));
                }
            }
            RefreshList();
            if (_documents.Count > 0) DocumentsList.SelectedIndex = _documents.Count - 1;
            string result = $"{generated} DAMDFE(s) gerado(s); {rejected} com erro; {duplicates} duplicado(s).";
            if (errors.Count > 0) result += " " + string.Join(" | ", errors.Take(3));
            StatusText.Text = result;
        }
        catch (Exception ex) { StatusText.Text = "Não foi possível selecionar os XMLs: " + ex.Message; }
    }

    private void RefreshList()
    {
        DocumentsList.ItemsSource = null; DocumentsList.ItemsSource = _documents;
        int valid = _documents.Count(d => d.Error == null && !d.IsDuplicate && d.PdfPath != null);
        int errors = _documents.Count(d => d.Error != null && !d.IsDuplicate);
        int duplicates = _documents.Count(d => d.IsDuplicate);
        CountText.Text = $"{valid} DAMDFE(s) gerado(s) · {errors} com erro · {duplicates} duplicado(s)";
        RebuildPreview();
    }

    private void DocumentsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not MdfeBatchItem item) return;
        if (item.Error != null) { StatusText.Text = item.IsDuplicate ? "MDF-e duplicado." : "Falha ao gerar DAMDFE: " + item.Error; return; }
        var page = _pages.FirstOrDefault(p => PathEquals(p.SourcePath, item.PdfPath ?? ""));
        if (page != null) NavigateToPage(page);
        StatusText.Text = $"MDF-e {item.Summary?.DocumentNumber} · {item.Summary?.IssuerName} · {item.Summary?.OriginState} → {item.Summary?.DestinationState}";
    }

    private void RemoveSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not MdfeBatchItem item) return;
        _documents.Remove(item); RemovePages(item); RefreshList();
        StatusText.Text = "Documento removido da lista. O XML original permanece intacto.";
    }

    private void ClearList_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        foreach (var item in _documents.ToList()) RemovePages(item);
        _documents.Clear(); _pages.Clear(); RefreshList();
        StatusText.Text = "Lista limpa. Nenhum XML original foi excluído.";
    }

    private void RemovePages(MdfeBatchItem item)
    {
        var removed = _pages.Where(p => PathEquals(p.SourcePath, item.PdfPath ?? "")).ToList();
        foreach (var page in removed) { page.Bitmap?.Dispose(); _pages.Remove(page); }
        if (item.PdfPath != null) TryDelete(item.PdfPath);
    }

    private void RebuildPreview()
    {
        ThumbnailsPanel.Children.Clear(); PagesPanel.Children.Clear();
        int number = 0;
        foreach (var page in _pages)
        {
            number++;
            var thumb = new Image { Source = page.Bitmap, Width = 125, Height = 165, Stretch = Avalonia.Media.Stretch.Uniform };
            var thumbnail = new Border { Background = Avalonia.Media.Brushes.White, BorderBrush = Avalonia.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4),
                Child = new StackPanel { Spacing = 3, Children = { thumb, new TextBlock { Text = $"Página {number}",
                    FontSize = 10, Foreground = Avalonia.Media.Brushes.DimGray,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center } } } };
            thumbnail.PointerPressed += (_, args) => { NavigateToPage(page); args.Handled = true; };
            ThumbnailsPanel.Children.Add(thumbnail);
            var pageImage = new Image { Source = page.Bitmap, Stretch = Avalonia.Media.Stretch.None,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var card = new Border { Background = Avalonia.Media.Brushes.White, BorderBrush = Avalonia.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1), Padding = new Thickness(9), CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 0, 4), Child = new StackPanel { Spacing = 4, Children = {
                    new TextBlock { Text = $"Página {number} · {page.SourceName}", FontSize = 10,
                        Foreground = Avalonia.Media.Brushes.Gray }, pageImage } } };
            card.PointerPressed += (_, args) => { NavigateToPage(page); args.Handled = true; };
            page.Card = card; PagesPanel.Children.Add(card);
        }
        int valid = _documents.Count(d => d.PdfPath != null && d.Error == null && !d.IsDuplicate);
        PageIndicator.Text = _pages.Count == 0 ? "Nenhum DAMDFE gerado" :
            $"{valid} DAMDFE(s) · {_pages.Count} página(s) · Zoom {_previewZoom * 100:0}%";
    }

    private void NavigateToPage(MdfeBatchPage page)
    {
        if (page.Card == null) return;
        DocumentScrollViewer.UpdateLayout();
        DocumentScrollViewer.Offset = new Vector(DocumentScrollViewer.Offset.X, Math.Max(0, page.Card.Bounds.Y - 8));
        PageIndicator.Text = $"Página {_pages.IndexOf(page) + 1} de {_pages.Count} · {_previewZoom * 100:0}%";
    }

    private async void ExportDamDfe_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not MdfeBatchItem item || item.Error != null || item.IsDuplicate ||
            string.IsNullOrWhiteSpace(item.PdfPath) || !File.Exists(item.PdfPath))
        { StatusText.Text = "Selecione um MDF-e cujo DAMDFE tenha sido gerado corretamente."; return; }
        try
        {
            var dest = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Exportar DAMDFE individual", SuggestedFileName = $"DAMDFE-MDFe-{item.Summary?.DocumentNumber ?? "documento"}.pdf",
                DefaultExtension = "pdf", FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } } });
            if (dest == null) return;
            string output = Path.GetFullPath(dest.Path.LocalPath);
            EnsureNotSource(output, item.SourcePath); File.Copy(item.PdfPath, output, true);
            StatusText.Text = "DAMDFE exportado: " + output;
        }
        catch (Exception ex) { StatusText.Text = "Falha ao exportar DAMDFE: " + ex.Message; }
    }

    private async void ExportAllDamDfe_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var valid = _documents.Where(d => d.Error == null && !d.IsDuplicate && d.PdfPath != null && File.Exists(d.PdfPath)).ToList();
        if (valid.Count == 0) { StatusText.Text = "Não há DAMDFEs válidos para exportar."; return; }
        try
        {
            var dest = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Exportar DAMDFEs consolidados", SuggestedFileName = "DAMDFEs-MDFe-Consolidados.pdf",
                DefaultExtension = "pdf", FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } } });
            if (dest == null) return;
            string output = Path.GetFullPath(dest.Path.LocalPath);
            foreach (var item in valid) EnsureNotSource(output, item.SourcePath);
            using var merged = new PdfDocument();
            foreach (var item in valid)
            {
                using var src = PdfReader.Open(item.PdfPath!, PdfDocumentOpenMode.Import);
                for (int i = 0; i < src.Pages.Count; i++) merged.AddPage(src.Pages[i]);
            }
            if (merged.Pages.Count == 0) throw new InvalidDataException("Nenhuma página válida encontrada.");
            merged.Save(output); StatusText.Text = "DAMDFEs consolidados em: " + output;
        }
        catch (Exception ex) { StatusText.Text = "Falha ao consolidar DAMDFEs: " + ex.Message; }
    }

    private void PreviewWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_pages.Count == 0 || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        float next = Math.Clamp(e.Delta.Y > 0 ? _previewZoom * ZoomStep : _previewZoom / ZoomStep, MinZoom, MaxZoom);
        if (Math.Abs(next - _previewZoom) < 0.001f) return;
        _previewZoom = next;
        try { RenderAllPages(); RebuildPreview(); StatusText.Text = $"Zoom atualizado para {_previewZoom * 100:0}%."; e.Handled = true; }
        catch (Exception ex) { StatusText.Text = "Falha ao atualizar zoom: " + ex.Message; }
    }

    private void RenderAllPages()
    {
        var open = new Dictionary<string, PdfiumDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var page in _pages)
            {
                if (!open.TryGetValue(page.SourcePath, out var pdf))
                { pdf = new PdfiumDocument(page.SourcePath); open.Add(page.SourcePath, pdf); }
                var rendered = pdf.RenderPage(page.PageIndex, _previewZoom);
                var bitmap = CreateBitmap(rendered); page.Bitmap?.Dispose(); page.Bitmap = bitmap;
            }
        }
        finally { foreach (var pdf in open.Values) pdf.Dispose(); }
    }

    private static WriteableBitmap CreateBitmap(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(new PixelSize(page.Width, page.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using var fb = bitmap.Lock(); int row = checked(page.Width * 4);
            for (int y = 0; y < page.Height; y++)
                Marshal.Copy(page.Pixels, checked(y * page.Stride), IntPtr.Add(fb.Address, checked(y * fb.RowBytes)), row);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void EnsureNotSource(string output, string source)
    { if (PathEquals(output, source)) throw new InvalidOperationException("O PDF de saída não pode substituir o XML original."); }
    private static bool PathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private void DisposeAll()
    {
        foreach (var page in _pages) page.Bitmap?.Dispose();
        _pages.Clear();
        foreach (var item in _documents) if (item.PdfPath != null) TryDelete(item.PdfPath);
        _documents.Clear();
        try { if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, true); } catch { }
    }
    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private sealed class MdfeBatchItem
    {
        public string SourcePath { get; } public MdfeDocumentSummary? Summary { get; }
        public string? PdfPath { get; } public string? Error { get; } public bool IsDuplicate { get; }
        public string DisplayName => IsDuplicate ? $"↻ {Path.GetFileName(SourcePath)} · DUPLICADO" :
            Error != null ? $"⚠ {Path.GetFileName(SourcePath)} · ERRO: {Error}" :
            $"MDF-e {Summary?.DocumentNumber} · {Summary?.IssuerName} · {Summary?.OriginState} → {Summary?.DestinationState}";
        public MdfeBatchItem(string path, MdfeDocumentSummary? summary, string? pdfPath, string? error, bool duplicate = false)
        { SourcePath = path; Summary = summary; PdfPath = pdfPath; Error = error; IsDuplicate = duplicate; }
    }
    private sealed class MdfeBatchPage
    {
        public string SourcePath { get; set; } = ""; public string SourceName { get; set; } = "";
        public int PageIndex { get; set; } public WriteableBitmap? Bitmap { get; set; } public Border? Card { get; set; }
    }
}