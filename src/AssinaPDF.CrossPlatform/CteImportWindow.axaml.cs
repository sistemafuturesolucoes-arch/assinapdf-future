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

public partial class CteImportWindow : Window
{
    private readonly List<CteBatchItem> _documents = new();
    private readonly List<CteBatchPage> _pages = new();
    private readonly string _temporaryDirectory;
    private float _previewZoom = 1.0f;
    private const float MinimumPreviewZoom = 0.60f;
    private const float MaximumPreviewZoom = 2.50f;
    private const float PreviewZoomStep = 1.10f;

    public CteImportWindow()
    {
        InitializeComponent();
        _temporaryDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FutureSolucoes", "AssinaPDF", "CteBatchPreview");
        Directory.CreateDirectory(_temporaryDirectory);
        DocumentsList.ItemsSource = _documents;
        DocumentScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, DocumentPreview_PointerWheelChanged,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        Closed += (_, _) => DisposeAll();
    }

    private async void AddXml_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecionar XMLs de CT-e", AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Arquivos XML") { Patterns = new[] { "*.xml", "*.XML" } } }
            });
            if (files.Count == 0) return;
            int imported = 0, rejected = 0, duplicates = 0;
            var errors = new List<string>();

            foreach (var file in files)
            {
                string path;
                try { path = file.Path.LocalPath; }
                catch { rejected++; errors.Add($"{file.Name}: caminho local indisponível."); continue; }
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                { rejected++; errors.Add($"{file.Name}: arquivo não encontrado."); continue; }

                if (_documents.Any(d => string.Equals(d.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    duplicates++;
                    _documents.Add(new CteBatchItem(path, null, null, "O mesmo arquivo já foi importado nesta sessão.", true,
                        "O mesmo arquivo já foi importado nesta sessão."));
                    continue;
                }

                CteDocumentSummary? summary = null;
                string? pdfPath = null;
                var itemPages = new List<CteBatchPage>();
                try
                {
                    summary = CteDocumentReader.ReadFile(path);
                    if (_documents.Any(d => d.Summary != null && d.Summary.AccessKey == summary.AccessKey))
                    {
                        duplicates++;
                        _documents.Add(new CteBatchItem(path, summary, null, "Chave de acesso já importada.", true,
                            "Chave de acesso já importada: " + summary.AccessKey));
                        continue;
                    }

                    pdfPath = Path.Combine(_temporaryDirectory, $"dacte-{Guid.NewGuid():N}.pdf");
                    CteDacteGenerator.Generate(summary, pdfPath);
                    using (var pdf = new PdfiumDocument(pdfPath))
                    {
                        if (pdf.PageCount < 1) throw new InvalidDataException("O DACTE gerado não contém páginas.");
                        for (int i = 0; i < pdf.PageCount; i++)
                        {
                            var rendered = pdf.RenderPage(i, _previewZoom);
                            itemPages.Add(new CteBatchPage { SourcePath = pdfPath, SourceName = Path.GetFileName(path),
                                PageIndex = i, Bitmap = CreatePreviewBitmap(rendered) });
                        }
                    }
                    _documents.Add(new CteBatchItem(path, summary, pdfPath, null));
                    _pages.AddRange(itemPages);
                    imported++;
                }
                catch (Exception ex)
                {
                    foreach (var page in itemPages) page.Bitmap?.Dispose();
                    if (!string.IsNullOrWhiteSpace(pdfPath)) TryDeleteFile(pdfPath);
                    rejected++;
                    errors.Add($"{file.Name}: {ex.Message}");
                    _documents.Add(new CteBatchItem(path, summary, null, ex.Message));
                }
            }

            RefreshList();
            if (_documents.Count > 0) DocumentsList.SelectedIndex = _documents.Count - 1;
            string result = $"{imported} DACTE(s) gerado(s); {rejected} com erro; {duplicates} duplicado(s).";
            if (errors.Count > 0) result += " " + string.Join(" | ", errors.Take(3));
            StatusText.Text = result;
        }
        catch (Exception ex) { StatusText.Text = "Não foi possível selecionar os XMLs: " + ex.Message; }
    }

    private void RefreshList()
    {
        DocumentsList.ItemsSource = null;
        DocumentsList.ItemsSource = _documents;
        int valid = _documents.Count(d => d.Error == null && !d.IsDuplicate);
        int errors = _documents.Count(d => d.Error != null && !d.IsDuplicate);
        int duplicates = _documents.Count(d => d.IsDuplicate);
        CountText.Text = $"{valid} DACTE(s) gerado(s) · {errors} com erro · {duplicates} duplicado(s)";
        RebuildPreview();
    }

    private void DocumentsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not CteBatchItem item) return;
        if (item.Error != null) { StatusText.Text = item.IsDuplicate ? "CT-e duplicado: " + item.DuplicateReason : "Falha: " + item.Error; return; }
        var page = _pages.FirstOrDefault(p => string.Equals(p.SourcePath, item.PdfPath, StringComparison.OrdinalIgnoreCase));
        if (page != null) NavigateToPage(page);
        StatusText.Text = $"CT-e modelo {item.Summary?.Model} · {item.Summary?.DocumentNumber} · {item.Summary?.IssuerName}";
    }

    private void RemoveSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not CteBatchItem item) return;
        _documents.Remove(item);
        RemovePages(item);
        RefreshList();
        StatusText.Text = "Documento removido da lista. O XML original permanece intacto.";
    }

    private void ClearList_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        foreach (var item in _documents.ToList()) { RemovePages(item); if (item.PdfPath != null) TryDeleteFile(item.PdfPath); }
        _documents.Clear(); _pages.Clear(); RefreshList();
        StatusText.Text = "Lista limpa. Nenhum XML original foi excluído.";
    }

    private void RemovePages(CteBatchItem item)
    {
        var removed = _pages.Where(p => string.Equals(p.SourcePath, item.PdfPath, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var page in removed) { page.Bitmap?.Dispose(); _pages.Remove(page); }
        if (!string.IsNullOrWhiteSpace(item.PdfPath)) TryDeleteFile(item.PdfPath);
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
        int valid = _documents.Count(d => d.Error == null && !d.IsDuplicate);
        PageIndicator.Text = _pages.Count == 0 ? "Nenhum DACTE gerado" :
            $"{valid} DACTE(s) · {_pages.Count} página(s) · Zoom {_previewZoom * 100:0}%";
    }

    private void NavigateToPage(CteBatchPage page)
    {
        if (page.Card == null) return;
        DocumentScrollViewer.UpdateLayout();
        DocumentScrollViewer.Offset = new Vector(DocumentScrollViewer.Offset.X, Math.Max(0, page.Card.Bounds.Y - 8));
        PageIndicator.Text = $"Página {_pages.IndexOf(page) + 1} de {_pages.Count} · {_previewZoom * 100:0}%";
    }

    private async void ExportDacte_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not CteBatchItem item || item.Error != null || item.IsDuplicate ||
            string.IsNullOrWhiteSpace(item.PdfPath) || !File.Exists(item.PdfPath))
        { StatusText.Text = "Selecione um CT-e cujo DACTE tenha sido gerado corretamente."; return; }
        try
        {
            var dest = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Exportar DACTE individual", SuggestedFileName = $"DACTE-CTe{item.Summary?.Model ?? "57"}-{item.Summary?.DocumentNumber ?? "documento"}.pdf",
                DefaultExtension = "pdf", FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } } });
            if (dest == null) return;
            EnsureNotSource(Path.GetFullPath(dest.Path.LocalPath), item.SourcePath);
            File.Copy(item.PdfPath, dest.Path.LocalPath, true);
            StatusText.Text = "DACTE exportado: " + dest.Path.LocalPath;
        }
        catch (Exception ex) { StatusText.Text = "Falha ao exportar DACTE: " + ex.Message; }
    }

    private async void ExportAllDacte_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var valid = _documents.Where(d => d.Error == null && !d.IsDuplicate && d.PdfPath != null && File.Exists(d.PdfPath)).ToList();
        if (valid.Count == 0) { StatusText.Text = "Não há DACTEs válidos para exportar."; return; }
        try
        {
            var dest = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Exportar DACTEs consolidados", SuggestedFileName = "DACTEs-CTe-Consolidados.pdf", DefaultExtension = "pdf",
                FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } } });
            if (dest == null) return;
            string output = Path.GetFullPath(dest.Path.LocalPath);
            foreach (var item in valid) EnsureNotSource(output, item.SourcePath);
            using var merged = new PdfDocument();
            foreach (var item in valid) { using var src = PdfReader.Open(item.PdfPath!, PdfDocumentOpenMode.Import);
                for (int i = 0; i < src.Pages.Count; i++) merged.AddPage(src.Pages[i]); }
            if (merged.Pages.Count == 0) throw new InvalidDataException("Nenhuma página válida encontrada.");
            merged.Save(output); StatusText.Text = $"DACTEs consolidados em {output}";
        }
        catch (Exception ex) { StatusText.Text = "Falha ao consolidar DACTEs: " + ex.Message; }
    }

    private List<CteZipDocument> BuildZipDocuments() => _documents.Select(d =>
        new CteZipDocument(d.SourcePath, d.Summary, d.PdfPath, d.Error, d.IsDuplicate, d.DuplicateReason)).ToList();

    private async Task ExportZipAsync(IReadOnlyList<CteZipDocument> docs, string name, string title)
    {
        if (docs.Count == 0) { StatusText.Text = "Não existem registros para exportar."; return; }
        var dest = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = name,
            DefaultExtension = "zip", FileTypeChoices = new[] { new FilePickerFileType("Arquivo ZIP") { Patterns = new[] { "*.zip" } } } });
        if (dest == null) return;
        CteBatchZipExporter.Create(dest.Path.LocalPath, docs);
        StatusText.Text = "ZIP exportado: " + dest.Path.LocalPath;
    }

    private async void ExportZip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { try { await ExportZipAsync(BuildZipDocuments(), "Central-CTe-Empresas.zip", "Exportar todas as empresas em ZIP"); }
      catch (Exception ex) { StatusText.Text = "Falha ao exportar ZIP: " + ex.Message; } }

    private async void ExportSelectedCompanyZip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (DocumentsList.SelectedItem is not CteBatchItem selected || selected.Summary == null)
            { StatusText.Text = "Selecione um CT-e para identificar a empresa emitente."; return; }
            string cnpj = Digits(selected.Summary.IssuerDocument);
            var docs = BuildZipDocuments().Where(d => d.Summary != null && Digits(d.Summary.IssuerDocument) == cnpj).ToList();
            await ExportZipAsync(docs, $"CTe-{SafePart(selected.Summary.IssuerName)}.zip", "Exportar ZIP da empresa selecionada");
        }
        catch (Exception ex) { StatusText.Text = "Falha ao exportar ZIP da empresa: " + ex.Message; }
    }

    private void DocumentPreview_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_pages.Count == 0 || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        float next = Math.Clamp(e.Delta.Y > 0 ? _previewZoom * PreviewZoomStep : _previewZoom / PreviewZoomStep,
            MinimumPreviewZoom, MaximumPreviewZoom);
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
                if (!open.TryGetValue(page.SourcePath, out var pdf)) { pdf = new PdfiumDocument(page.SourcePath); open.Add(page.SourcePath, pdf); }
                var rendered = pdf.RenderPage(page.PageIndex, _previewZoom);
                var bitmap = CreatePreviewBitmap(rendered); page.Bitmap?.Dispose(); page.Bitmap = bitmap;
            }
        }
        finally { foreach (var pdf in open.Values) pdf.Dispose(); }
    }

    private static WriteableBitmap CreatePreviewBitmap(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(new PixelSize(page.Width, page.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try { using var fb = bitmap.Lock(); int row = checked(page.Width * 4);
            for (int y = 0; y < page.Height; y++) Marshal.Copy(page.Pixels, checked(y * page.Stride),
                IntPtr.Add(fb.Address, checked(y * fb.RowBytes)), row);
            return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }

    private static void EnsureNotSource(string output, string source)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(output, Path.GetFullPath(source), cmp))
            throw new InvalidOperationException("O PDF de saída não pode substituir o XML original.");
    }
    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
    private static string SafePart(string? s)
    {
        string value = (s ?? "Empresa").Trim();
        foreach (char c in Path.GetInvalidFileNameChars().Concat(new[] { '<','>',':','"','|','?','*' })) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "Empresa" : value.Length > 80 ? value[..80] : value;
    }
    private void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private void DisposeAll()
    {
        foreach (var page in _pages) page.Bitmap?.Dispose();
        _pages.Clear();
        foreach (var item in _documents) if (item.PdfPath != null) TryDeleteFile(item.PdfPath);
        _documents.Clear();
        try { if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, true); } catch { }
    }
    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private sealed class CteBatchItem
    {
        public string SourcePath { get; }
        public CteDocumentSummary? Summary { get; }
        public string? PdfPath { get; }
        public string? Error { get; }
        public bool IsDuplicate { get; }
        public string? DuplicateReason { get; }
        public string DisplayName => IsDuplicate ? $"↻ {Path.GetFileName(SourcePath)} · DUPLICADO" :
            Error != null ? $"⚠ {Path.GetFileName(SourcePath)} · ERRO: {Error}" :
            $"CT-e {Summary?.Model} · {Summary?.DocumentNumber} · {Summary?.IssuerName} · {Summary?.SenderName} → {Summary?.RecipientName}";
        public CteBatchItem(string path, CteDocumentSummary? summary, string? pdf, string? error, bool duplicate = false, string? reason = null)
        { SourcePath = path; Summary = summary; PdfPath = pdf; Error = error; IsDuplicate = duplicate; DuplicateReason = reason; }
    }
    private sealed class CteBatchPage
    {
        public string SourcePath { get; set; } = "";
        public string SourceName { get; set; } = "";
        public int PageIndex { get; set; }
        public WriteableBitmap? Bitmap { get; set; }
        public Border? Card { get; set; }
    }
}
