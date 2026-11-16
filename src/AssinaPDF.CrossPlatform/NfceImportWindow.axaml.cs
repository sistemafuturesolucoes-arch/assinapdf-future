using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AssinaPDF.MacPrototype;

public partial class NfceImportWindow : Window
{
    private readonly ObservableCollection<NfceBatchItem> _documents = new();
    private readonly List<NfceBatchPage> _pages = new();
    private readonly string _temporaryDirectory;
    private float _previewZoom = 1.0f;

    private const float MinimumPreviewZoom = 0.60f;
    private const float MaximumPreviewZoom = 2.50f;
    private const float PreviewZoomStep = 1.10f;

    public NfceImportWindow()
    {
        InitializeComponent();
        _temporaryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FutureSolucoes", "AssinaPDF", "NfceBatchPreview");
        Directory.CreateDirectory(_temporaryDirectory);
        DocumentsList.ItemsSource = _documents;
        DocumentScrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            DocumentPreview_PointerWheelChanged,
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            true);
        Closed += (_, _) => DisposeAll();
    }

    private async void AddXml_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecionar XMLs de NFC-e",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Arquivos XML") { Patterns = new[] { "*.xml", "*.XML" } }
                }
            });
            if (files.Count == 0) return;

            int imported = 0, rejected = 0, duplicates = 0;
            var errors = new List<string>();

            foreach (var file in files)
            {
                string path;
                try { path = file.Path.LocalPath; }
                catch
                {
                    rejected++;
                    errors.Add($"{file.Name}: caminho local indisponível.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    rejected++;
                    errors.Add($"{file.Name}: arquivo não encontrado.");
                    continue;
                }

                var sameFile = _documents.FirstOrDefault(d =>
                    string.Equals(d.SourcePath, path, StringComparison.OrdinalIgnoreCase));
                if (sameFile != null)
                {
                    duplicates++;
                    _documents.Add(new NfceBatchItem(path, sameFile.Summary, null,
                        "O mesmo arquivo já foi importado nesta sessão.", true,
                        "O mesmo arquivo já foi importado nesta sessão."));
                    continue;
                }

                NfceDocumentSummary? summary = null;
                string? pdfPath = null;
                var itemPages = new List<NfceBatchPage>();
                try
                {
                    summary = NfceXmlReader.ReadFile(path);
                    var existingKey = _documents.FirstOrDefault(d =>
                        d.Summary != null &&
                        string.Equals(d.Summary.AccessKey, summary.AccessKey, StringComparison.Ordinal));
                    if (existingKey != null)
                    {
                        duplicates++;
                        _documents.Add(new NfceBatchItem(path, summary, null,
                            "Chave de acesso já importada.", true,
                            "Chave de acesso já importada: " + summary.AccessKey));
                        continue;
                    }

                    pdfPath = Path.Combine(_temporaryDirectory, $"danfce-{Guid.NewGuid():N}.pdf");
                    NfceDanfeGenerator.Generate(summary, pdfPath);

                    using (var pdf = new PdfiumDocument(pdfPath))
                    {
                        if (pdf.PageCount < 1)
                            throw new InvalidDataException("O DANFCE gerado não contém páginas.");

                        for (int pageIndex = 0; pageIndex < pdf.PageCount; pageIndex++)
                        {
                            var rendered = pdf.RenderPage(pageIndex, _previewZoom);
                            itemPages.Add(new NfceBatchPage
                            {
                                SourcePath = pdfPath,
                                SourceName = Path.GetFileName(path),
                                PageIndex = pageIndex,
                                Bitmap = CreatePreviewBitmap(rendered)
                            });
                        }
                    }

                    _documents.Add(new NfceBatchItem(path, summary, pdfPath, null));
                    _pages.AddRange(itemPages);
                    imported++;
                }
                catch (Exception ex)
                {
                    foreach (var page in itemPages) page.Bitmap?.Dispose();
                    if (!string.IsNullOrWhiteSpace(pdfPath)) TryDeleteFile(pdfPath);
                    rejected++;
                    errors.Add($"{file.Name}: {ex.Message}");
                    _documents.Add(new NfceBatchItem(path, summary, null, ex.Message));
                }
            }

            UpdateCount();
            RebuildPreview();
            if (_documents.Count > 0) DocumentsList.SelectedIndex = _documents.Count - 1;

            string result = $"{imported} DANFCE(s) gerado(s); {rejected} com erro; {duplicates} duplicado(s) ignorado(s).";
            if (errors.Count > 0) result += " " + string.Join(" | ", errors.Take(3));
            StatusText.Text = result;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível selecionar os XMLs: {ex.Message}";
        }
    }

    private void DocumentsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfceBatchItem item) return;
        if (item.Error != null)
        {
            StatusText.Text = item.IsDuplicate
                ? $"Duplicado: {item.DuplicateReason}"
                : $"Falha em {Path.GetFileName(item.SourcePath)}: {item.Error}";
            return;
        }

        var firstPage = _pages.FirstOrDefault(p =>
            string.Equals(p.SourcePath, item.PdfPath, StringComparison.OrdinalIgnoreCase));
        if (firstPage != null) NavigateToPage(firstPage);
        StatusText.Text = $"NFC-e {item.Summary?.InvoiceNumber} · {item.DisplayName}";
    }

    private void RemoveSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfceBatchItem selected) return;
        RemoveItem(selected);
        UpdateCount();
        RebuildPreview();
        StatusText.Text = "Documento removido da lista. O XML original permanece intacto.";
    }

    private void ClearList_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        foreach (var item in _documents.ToList()) RemoveItem(item);
        _documents.Clear();
        _pages.Clear();
        UpdateCount();
        RebuildPreview();
        StatusText.Text = "Lista limpa. Nenhum XML original foi excluído.";
    }

    private void RemoveItem(NfceBatchItem item)
    {
        _documents.Remove(item);
        var removed = _pages.Where(p => string.Equals(
            p.SourcePath, item.PdfPath, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var page in removed)
        {
            page.Bitmap?.Dispose();
            _pages.Remove(page);
        }
        if (!string.IsNullOrWhiteSpace(item.PdfPath)) TryDeleteFile(item.PdfPath);
    }

    private void UpdateCount()
    {
        int valid = _documents.Count(d => d.Error == null && !d.IsDuplicate);
        int errors = _documents.Count(d => d.Error != null && !d.IsDuplicate);
        int duplicates = _documents.Count(d => d.IsDuplicate);
        CountText.Text = $"{valid} DANFCE(s) gerado(s) · {errors} com erro · {duplicates} duplicado(s)";
    }

    private void RebuildPreview()
    {
        ThumbnailsPanel.Children.Clear();
        PagesPanel.Children.Clear();
        int number = 0;
        foreach (var page in _pages)
        {
            number++;
            var thumbnailImage = new Image
            {
                Source = page.Bitmap, Width = 125, Height = 165,
                Stretch = Avalonia.Media.Stretch.Uniform
            };
            var thumbnailLabel = new TextBlock
            {
                Text = $"Página {number}", FontSize = 10,
                Foreground = Avalonia.Media.Brushes.DimGray,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            var thumbnail = new Border
            {
                Background = Avalonia.Media.Brushes.White,
                BorderBrush = Avalonia.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4),
                Child = new StackPanel { Spacing = 3, Children = { thumbnailImage, thumbnailLabel } }
            };
            thumbnail.PointerPressed += (_, args) => { NavigateToPage(page); args.Handled = true; };
            ThumbnailsPanel.Children.Add(thumbnail);

            var pageImage = new Image
            {
                Source = page.Bitmap, Stretch = Avalonia.Media.Stretch.None,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            var pageTitle = new TextBlock
            {
                Text = $"Página {number} · {page.SourceName}", FontSize = 10,
                Foreground = Avalonia.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 5)
            };
            var pageCard = new Border
            {
                Background = Avalonia.Media.Brushes.White,
                BorderBrush = Avalonia.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9), CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 0, 4),
                Child = new StackPanel { Spacing = 4, Children = { pageTitle, pageImage } }
            };
            pageCard.PointerPressed += (_, args) => { NavigateToPage(page); args.Handled = true; };
            page.Thumbnail = thumbnail;
            page.Card = pageCard;
            page.MainImage = pageImage;
            PagesPanel.Children.Add(pageCard);
        }

        int validCount = _documents.Count(d => d.Error == null && !d.IsDuplicate);
        PageIndicator.Text = _pages.Count == 0
            ? "Nenhum DANFCE gerado"
            : $"{validCount} DANFCE(s) · {_pages.Count} página(s) · Zoom {_previewZoom * 100:0}%";
    }

    private void NavigateToPage(NfceBatchPage page)
    {
        if (page.Card == null) return;
        DocumentScrollViewer.UpdateLayout();
        double y = Math.Max(0, page.Card.Bounds.Y - 8);
        DocumentScrollViewer.Offset = new Vector(DocumentScrollViewer.Offset.X, y);
        PageIndicator.Text = $"Página {_pages.IndexOf(page) + 1} de {_pages.Count} · {_previewZoom * 100:0}%";
    }

    private async void ExportDanfe_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfceBatchItem item ||
            item.Error != null || item.IsDuplicate ||
            string.IsNullOrWhiteSpace(item.PdfPath) || !File.Exists(item.PdfPath))
        {
            StatusText.Text = "Selecione uma NFC-e cujo DANFCE tenha sido gerado corretamente.";
            return;
        }

        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Exportar DANFCE individual",
                SuggestedFileName = $"DANFCE-NFCe-{item.Summary?.InvoiceNumber ?? "documento"}.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } }
            });
            if (destination == null) return;
            string output = Path.GetFullPath(destination.Path.LocalPath);
            EnsureNotSource(output, item.SourcePath);
            File.Copy(item.PdfPath, output, true);
            StatusText.Text = $"DANFCE exportado: {output}";
        }
        catch (Exception ex) { StatusText.Text = "Falha ao exportar DANFCE: " + ex.Message; }
    }

    private async void ExportAllDanfe_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var valid = _documents.Where(d => d.Error == null && !d.IsDuplicate &&
            !string.IsNullOrWhiteSpace(d.PdfPath) && File.Exists(d.PdfPath)).ToList();
        if (valid.Count == 0) { StatusText.Text = "Não há DANFCEs válidos para exportar."; return; }

        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Exportar todos os DANFCEs em um único PDF",
                SuggestedFileName = "DANFCEs-NFCe-Consolidados.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } }
            });
            if (destination == null) return;
            string output = Path.GetFullPath(destination.Path.LocalPath);
            foreach (var item in valid) EnsureNotSource(output, item.SourcePath);

            using var merged = new PdfDocument();
            foreach (var item in valid)
            {
                using var source = PdfReader.Open(item.PdfPath!, PdfDocumentOpenMode.Import);
                for (int i = 0; i < source.Pages.Count; i++) merged.AddPage(source.Pages[i]);
            }
            if (merged.Pages.Count == 0) throw new InvalidDataException("Nenhuma página válida foi encontrada.");
            merged.Save(output);
            StatusText.Text = $"{valid.Count} DANFCE(s) consolidados em {output}";
        }
        catch (Exception ex) { StatusText.Text = "Falha ao consolidar DANFCEs: " + ex.Message; }
    }

    private List<NfceZipDocument> BuildZipDocuments() => _documents.Select(item =>
        new NfceZipDocument(item.SourcePath, item.Summary, item.PdfPath, item.Error,
            item.IsDuplicate, item.DuplicateReason)).ToList();

    private async Task ExportZipAsync(IReadOnlyList<NfceZipDocument> documents, string suggestedName, string title)
    {
        if (documents.Count == 0) { StatusText.Text = "Não existem registros para exportar."; return; }
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggestedName, DefaultExtension = "zip",
            FileTypeChoices = new[] { new FilePickerFileType("Arquivo ZIP") { Patterns = new[] { "*.zip" } } }
        });
        if (destination == null) return;
        string output = Path.GetFullPath(destination.Path.LocalPath);
        NfceBatchZipExporter.Create(output, documents);
        StatusText.Text = $"ZIP exportado com sucesso: {output}";
    }

    private async void ExportZip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { await ExportZipAsync(BuildZipDocuments(), "Central-NFCe-Empresas.zip", "Exportar todas as empresas em ZIP"); }
        catch (Exception ex) { StatusText.Text = "Falha ao exportar ZIP: " + ex.Message; }
    }

    private async void ExportSelectedCompanyZip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (DocumentsList.SelectedItem is not NfceBatchItem selected || selected.Summary == null)
            {
                StatusText.Text = "Selecione uma NFC-e para identificar a empresa emitente.";
                return;
            }
            string document = DigitsOnly(selected.Summary.IssuerDocument);
            if (string.IsNullOrWhiteSpace(document))
            {
                StatusText.Text = "Não foi possível identificar o CNPJ/CPF do emitente.";
                return;
            }
            var docs = BuildZipDocuments().Where(d => d.Summary != null &&
                DigitsOnly(d.Summary.IssuerDocument) == document).ToList();
            string name = SafeFilePart(selected.Summary.IssuerName);
            await ExportZipAsync(docs, $"NFCe-{name}.zip", "Exportar ZIP da empresa selecionada");
        }
        catch (Exception ex) { StatusText.Text = "Falha ao exportar ZIP da empresa: " + ex.Message; }
    }

    private void DocumentPreview_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_pages.Count == 0 || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        float next = e.Delta.Y > 0 ? _previewZoom * PreviewZoomStep : _previewZoom / PreviewZoomStep;
        next = Math.Clamp(next, MinimumPreviewZoom, MaximumPreviewZoom);
        if (Math.Abs(next - _previewZoom) < 0.001f) return;
        _previewZoom = next;
        try
        {
            RenderAllPages();
            RebuildPreview();
            StatusText.Text = $"Zoom atualizado para {_previewZoom * 100:0}%.";
            e.Handled = true;
        }
        catch (Exception ex) { StatusText.Text = "Não foi possível atualizar o zoom: " + ex.Message; }
    }

    private void RenderAllPages()
    {
        var open = new Dictionary<string, PdfiumDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var page in _pages)
            {
                if (!open.TryGetValue(page.SourcePath, out var pdf))
                {
                    pdf = new PdfiumDocument(page.SourcePath);
                    open.Add(page.SourcePath, pdf);
                }
                var rendered = pdf.RenderPage(page.PageIndex, _previewZoom);
                var bitmap = CreatePreviewBitmap(rendered);
                page.Bitmap?.Dispose();
                page.Bitmap = bitmap;
            }
        }
        finally { foreach (var pdf in open.Values) pdf.Dispose(); }
    }

    private static WriteableBitmap CreatePreviewBitmap(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(new PixelSize(page.Width, page.Height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
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

    private static void EnsureNotSource(string output, string source)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(output, Path.GetFullPath(source), comparison))
            throw new InvalidOperationException("O PDF de saída não pode substituir o XML original.");
    }

    private static string DigitsOnly(string? value) => new((value ?? "").Where(char.IsDigit).ToArray());

    private static string SafeFilePart(string? value)
    {
        string result = (value ?? "Empresa").Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(result) ? "Empresa" : result.Length > 80 ? result[..80] : result;
    }

    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* A limpeza não deve interromper a interface. */ }
    }

    private void DisposeAll()
    {
        foreach (var page in _pages) page.Bitmap?.Dispose();
        _pages.Clear();
        foreach (var item in _documents)
            if (!string.IsNullOrWhiteSpace(item.PdfPath)) TryDeleteFile(item.PdfPath);
        _documents.Clear();
        try { if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, true); }
        catch { /* Arquivos em uso não impedem o fechamento. */ }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private sealed class NfceBatchItem
    {
        public string SourcePath { get; }
        public NfceDocumentSummary? Summary { get; }
        public string? PdfPath { get; }
        public string? Error { get; }
        public bool IsDuplicate { get; }
        public string? DuplicateReason { get; }
        public string DisplayName => IsDuplicate
            ? $"↻ {Path.GetFileName(SourcePath)} · DUPLICADO"
            : Error != null
                ? $"⚠ {Path.GetFileName(SourcePath)} · ERRO: {Error}"
                : Summary?.DisplayName ?? Path.GetFileName(SourcePath);

        public NfceBatchItem(string sourcePath, NfceDocumentSummary? summary, string? pdfPath,
            string? error, bool isDuplicate = false, string? duplicateReason = null)
        {
            SourcePath = sourcePath; Summary = summary; PdfPath = pdfPath;
            Error = error; IsDuplicate = isDuplicate; DuplicateReason = duplicateReason;
        }
    }

    private sealed class NfceBatchPage
    {
        public string SourcePath { get; set; } = "";
        public string SourceName { get; set; } = "";
        public int PageIndex { get; set; }
        public WriteableBitmap? Bitmap { get; set; }
        public Border? Thumbnail { get; set; }
        public Border? Card { get; set; }
        public Image? MainImage { get; set; }
    }
}
