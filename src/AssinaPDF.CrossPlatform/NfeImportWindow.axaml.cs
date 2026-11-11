using System.Collections.ObjectModel;
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

public partial class NfeImportWindow : Window
{
    private readonly ObservableCollection<NfeBatchItem> _documents = new();
    private readonly List<NfeBatchPage> _pages = new();
    private readonly List<NfeZipDocument> _duplicateRecords = new();

    private readonly string _temporaryDirectory;
    private float _previewZoom = 1.0f;

    private const float MinimumPreviewZoom = 0.60f;
    private const float MaximumPreviewZoom = 2.50f;
    private const float PreviewZoomStep = 1.10f;

    public NfeImportWindow()
    {
        InitializeComponent();

        _temporaryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FutureSolucoes",
            "AssinaPDF",
            "NfeBatchPreview");

        Directory.CreateDirectory(_temporaryDirectory);

        DocumentsList.ItemsSource = _documents;

        DocumentScrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            DocumentPreview_PointerWheelChanged,
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            true);

        Closed += (_, _) => DisposeAll();
    }

    private async void AddXml_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Selecionar XMLs de NF-e",
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Arquivos XML")
                        {
                            Patterns = new[] { "*.xml", "*.XML" }
                        }
                    }
                });

            if (files.Count == 0)
                return;

            int imported = 0;
            int rejected = 0;
            int duplicates = 0;

            var errors = new List<string>();

            foreach (var file in files)
            {
                string path;

                try
                {
                    path = file.Path.LocalPath;
                }
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

                var existingPath = _documents.FirstOrDefault(d =>
                    string.Equals(
                        d.SourcePath,
                        path,
                        StringComparison.OrdinalIgnoreCase));

                if (existingPath != null)
                {
                    duplicates++;
                    _duplicateRecords.Add(new NfeZipDocument(
                        path,
                        existingPath.Summary,
                        null,
                        null,
                        true,
                        "O mesmo arquivo já foi importado nesta sessão."));
                    continue;
                }

                NfeDocumentSummary? summary = null;
                string? pdfPath = null;
                var itemPages = new List<NfeBatchPage>();

                try
                {
                    // Valida o XML antes de gerar o DANFE.
                    summary = NfeXmlReader.ReadFile(path);

                    string accessKey = (summary.AccessKey ?? "").Trim();

                    var existingKey = _documents.FirstOrDefault(d =>
                        d.Summary != null &&
                        !string.IsNullOrWhiteSpace(accessKey) &&
                        string.Equals(
                            d.Summary.AccessKey?.Trim(),
                            accessKey,
                            StringComparison.Ordinal));

                    bool keyAlreadyDuplicated = _duplicateRecords.Any(d =>
                        d.Summary != null &&
                        !string.IsNullOrWhiteSpace(accessKey) &&
                        string.Equals(
                            d.Summary.AccessKey?.Trim(),
                            accessKey,
                            StringComparison.Ordinal));

                    if (existingKey != null || keyAlreadyDuplicated)
                    {
                        duplicates++;
                        _duplicateRecords.Add(new NfeZipDocument(
                            path,
                            summary,
                            null,
                            null,
                            true,
                            "Chave de acesso já importada: " + accessKey));
                        continue;
                    }

                    pdfPath = Path.Combine(
                        _temporaryDirectory,
                        $"danfe-{Guid.NewGuid():N}.pdf");

                    // Geração automática do DANFE.
                    NfeDanfeGenerator.Generate(summary, pdfPath);

                    // Abre o PDF e renderiza todas as páginas.
                    using (var pdf = new PdfiumDocument(pdfPath))
                    {
                        if (pdf.PageCount < 1)
                            throw new InvalidDataException(
                                "O DANFE gerado não contém páginas.");

                        for (int pageIndex = 0;
                             pageIndex < pdf.PageCount;
                             pageIndex++)
                        {
                            var rendered = pdf.RenderPage(
                                pageIndex,
                                _previewZoom);

                            var bitmap = CreatePreviewBitmap(rendered);

                            itemPages.Add(new NfeBatchPage
                            {
                                SourcePath = pdfPath,
                                SourceName = Path.GetFileName(path),
                                PageIndex = pageIndex,
                                Bitmap = bitmap
                            });
                        }
                    }

                    var item = new NfeBatchItem(
                        path,
                        summary,
                        pdfPath,
                        null);

                    _documents.Add(item);
                    _pages.AddRange(itemPages);

                    imported++;
                }
                catch (Exception ex)
                {
                    foreach (var page in itemPages)
                        page.Bitmap?.Dispose();

                    if (!string.IsNullOrWhiteSpace(pdfPath))
                    {
                        TryDeleteFile(pdfPath);
                    }

                    rejected++;

                    string message = $"{file.Name}: {ex.Message}";
                    errors.Add(message);

                    // Mantém o registro do arquivo com erro na lista.
                    _documents.Add(new NfeBatchItem(
                        path,
                        summary,
                        null,
                        ex.Message));
                }
            }

            UpdateCount();
            RebuildPreview();

            if (_documents.Count > 0)
                DocumentsList.SelectedIndex = _documents.Count - 1;

            string result =
                $"{imported} DANFE(s) gerado(s); " +
                $"{rejected} com erro; {duplicates} duplicado(s) ignorado(s).";

            if (errors.Count > 0)
                result += " " + string.Join(" | ", errors.Take(3));

            StatusText.Text = result;
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Não foi possível selecionar os XMLs: {ex.Message}";
        }
    }

    private void DocumentsList_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfeBatchItem item)
            return;

        if (item.Error != null)
        {
            StatusText.Text =
                $"Falha em {Path.GetFileName(item.SourcePath)}: {item.Error}";
            return;
        }

        var firstPage = _pages.FirstOrDefault(p =>
            string.Equals(
                p.SourcePath,
                item.PdfPath,
                StringComparison.OrdinalIgnoreCase));

        if (firstPage != null)
            NavigateToPage(firstPage);

        StatusText.Text =
            $"NF-e {item.Summary?.InvoiceNumber} · {item.DisplayName}";
    }

    private void RemoveSelected_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfeBatchItem selected)
            return;

        RemoveItem(selected);

        UpdateCount();
        RebuildPreview();

        StatusText.Text =
            "Documento removido da lista. O XML original permanece intacto.";
    }

    private void ClearList_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        foreach (var item in _documents.ToList())
            RemoveItem(item);

        _documents.Clear();
        _pages.Clear();
        _duplicateRecords.Clear();

        UpdateCount();
        RebuildPreview();

        StatusText.Text =
            "Lista limpa. Nenhum XML original foi excluído.";
    }

    private void RemoveItem(NfeBatchItem item)
    {
        _documents.Remove(item);

        var removedPages = _pages
            .Where(p => string.Equals(
                p.SourcePath,
                item.PdfPath,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var page in removedPages)
        {
            page.Bitmap?.Dispose();
            _pages.Remove(page);
        }

        if (!string.IsNullOrWhiteSpace(item.PdfPath))
            TryDeleteFile(item.PdfPath);
    }

    private void UpdateCount()
    {
        int valid = _documents.Count(d => d.Error == null);
        int errors = _documents.Count(d => d.Error != null);

        CountText.Text =
            $"{valid} DANFE(s) gerado(s) · {errors} com erro · {_duplicateRecords.Count} duplicado(s)";
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
                Source = page.Bitmap,
                Width = 125,
                Height = 165,
                Stretch = Avalonia.Media.Stretch.Uniform
            };

            var thumbnailLabel = new TextBlock
            {
                Text = $"Página {number}",
                FontSize = 10,
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
                Child = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        thumbnailImage,
                        thumbnailLabel
                    }
                }
            };

            thumbnail.PointerPressed += (_, args) =>
            {
                NavigateToPage(page);
                args.Handled = true;
            };

            ThumbnailsPanel.Children.Add(thumbnail);

            var pageImage = new Image
            {
                Source = page.Bitmap,
                Stretch = Avalonia.Media.Stretch.None,
                HorizontalAlignment =
                    Avalonia.Layout.HorizontalAlignment.Center
            };

            var pageTitle = new TextBlock
            {
                Text =
                    $"Página {number} · {page.SourceName}",
                FontSize = 10,
                Foreground = Avalonia.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 5)
            };

            var pageCard = new Border
            {
                Background = Avalonia.Media.Brushes.White,
                BorderBrush = Avalonia.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 0, 4),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        pageTitle,
                        pageImage
                    }
                }
            };

            pageCard.PointerPressed += (_, args) =>
            {
                NavigateToPage(page);
                args.Handled = true;
            };

            page.Thumbnail = thumbnail;
            page.Card = pageCard;
            page.MainImage = pageImage;

            PagesPanel.Children.Add(pageCard);
        }

        PageIndicator.Text = _pages.Count == 0
            ? "Nenhuma página gerada"
            : $"{_documents.Count(d => d.Error == null)} DANFE(s) · {_pages.Count} página(s) · Zoom {_previewZoom * 100:0}%";
    }

    private void NavigateToPage(NfeBatchPage page)
    {
        if (page.Card == null)
            return;

        DocumentScrollViewer.UpdateLayout();

        double y = Math.Max(0, page.Card.Bounds.Y - 8);

        DocumentScrollViewer.Offset =
            new Vector(DocumentScrollViewer.Offset.X, y);

        PageIndicator.Text =
            $"Página {_pages.IndexOf(page) + 1} de {_pages.Count} · {_previewZoom * 100:0}%";
    }

    private async void ExportDanfe_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfeBatchItem item ||
            item.Error != null ||
            string.IsNullOrWhiteSpace(item.PdfPath) ||
            !File.Exists(item.PdfPath))
        {
            StatusText.Text =
                "Selecione uma NF-e cujo DANFE tenha sido gerado corretamente.";
            return;
        }

        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Exportar DANFE individual",
                    SuggestedFileName =
                        $"DANFE-NFe-{item.Summary?.InvoiceNumber ?? "documento"}.pdf",
                    DefaultExtension = "pdf",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("Documento PDF")
                        {
                            Patterns = new[] { "*.pdf" }
                        }
                    }
                });

            if (destination == null)
                return;

            string outputPath =
                Path.GetFullPath(destination.Path.LocalPath);

            if (string.Equals(
                outputPath,
                Path.GetFullPath(item.SourcePath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "O PDF não pode substituir o XML original.");
            }

            File.Copy(item.PdfPath, outputPath, true);

            StatusText.Text = $"DANFE exportado: {outputPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Falha ao exportar o DANFE: {ex.Message}";
        }
    }

    private async void ExportAllDanfe_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        var validItems = _documents
            .Where(d =>
                d.Error == null &&
                !string.IsNullOrWhiteSpace(d.PdfPath) &&
                File.Exists(d.PdfPath))
            .ToList();

        if (validItems.Count == 0)
        {
            StatusText.Text =
                "Não há DANFEs válidos para exportar.";
            return;
        }

        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Exportar todos os DANFEs em um único PDF",
                    SuggestedFileName = "DANFEs-NFe-Consolidados.pdf",
                    DefaultExtension = "pdf",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("Documento PDF")
                        {
                            Patterns = new[] { "*.pdf" }
                        }
                    }
                });

            if (destination == null)
                return;

            string outputPath =
                Path.GetFullPath(destination.Path.LocalPath);

            foreach (var item in validItems)
            {
                if (string.Equals(
                    outputPath,
                    Path.GetFullPath(item.SourcePath),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "O PDF consolidado não pode substituir um XML original.");
                }
            }

            var sourceDocuments =
                new Dictionary<string, PdfDocument>(
                    StringComparer.OrdinalIgnoreCase);

            using var outputDocument = new PdfDocument();

            try
            {
                // A ordem é a mesma em que os XMLs foram importados.
                foreach (var item in validItems)
                {
                    if (!sourceDocuments.TryGetValue(
                        item.PdfPath!,
                        out var sourceDocument))
                    {
                        sourceDocument = PdfReader.Open(
                            item.PdfPath!,
                            PdfDocumentOpenMode.Import);

                        sourceDocuments.Add(
                            item.PdfPath!,
                            sourceDocument);
                    }

                    for (int i = 0; i < sourceDocument.Pages.Count; i++)
                        outputDocument.AddPage(sourceDocument.Pages[i]);
                }

                if (outputDocument.Pages.Count == 0)
                    throw new InvalidDataException(
                        "Nenhuma página válida foi encontrada.");

                outputDocument.Save(outputPath);
            }
            finally
            {
                foreach (var sourceDocument in sourceDocuments.Values)
                    sourceDocument.Dispose();
            }

            StatusText.Text =
                $"{validItems.Count} DANFE(s) consolidados em {outputPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Falha ao consolidar os DANFEs: {ex.Message}";
        }
    }


    private NfeZipGroupingMode GetGroupingMode()
    {
        return GroupingModeCombo.SelectedIndex == 1
            ? NfeZipGroupingMode.Issuer
            : NfeZipGroupingMode.Recipient;
    }

    private static string GetCompanyDocument(
        NfeDocumentSummary summary,
        NfeZipGroupingMode mode)
    {
        string value = mode == NfeZipGroupingMode.Recipient
            ? summary.RecipientDocument
            : summary.IssuerDocument;

        return new string((value ?? "").Where(char.IsDigit).ToArray());
    }

    private List<NfeZipDocument> BuildAllZipDocuments()
    {
        var result = _documents.Select(item =>
            new NfeZipDocument(
                item.SourcePath,
                item.Summary,
                item.PdfPath,
                item.Error)).ToList();

        result.AddRange(_duplicateRecords);
        return result;
    }

    private async Task ExportZipAsync(
        IReadOnlyList<NfeZipDocument> documents,
        string suggestedName,
        string title)
    {
        if (documents.Count == 0)
        {
            StatusText.Text = "Não existem registros para exportar.";
            return;
        }

        var destination = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = "zip",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Arquivo ZIP")
                    {
                        Patterns = new[] { "*.zip" }
                    }
                }
            });

        if (destination == null)
            return;

        string outputPath = Path.GetFullPath(destination.Path.LocalPath);

        NfeBatchZipExporter.Create(
            outputPath,
            documents,
            GetGroupingMode());

        StatusText.Text =
            $"ZIP exportado com sucesso: {outputPath}";
    }

    private async void ExportZip_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var documents = BuildAllZipDocuments();

            await ExportZipAsync(
                documents,
                "Central-XML-Fiscal-Empresas.zip",
                "Exportar todas as empresas em ZIP");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao exportar ZIP: " + ex.Message;
        }
    }

    private async void ExportSelectedCompanyZip_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (DocumentsList.SelectedItem is not NfeBatchItem selected ||
                selected.Summary == null)
            {
                StatusText.Text =
                    "Selecione uma NF-e válida para identificar a empresa.";
                return;
            }

            var mode = GetGroupingMode();
            string selectedDocument =
                GetCompanyDocument(selected.Summary, mode);

            if (string.IsNullOrWhiteSpace(selectedDocument))
            {
                StatusText.Text =
                    "Não foi possível identificar o CNPJ/CPF da empresa.";
                return;
            }

            var documents = _documents
                .Where(item =>
                    item.Summary != null &&
                    GetCompanyDocument(item.Summary, mode) == selectedDocument)
                .Select(item => new NfeZipDocument(
                    item.SourcePath,
                    item.Summary,
                    item.PdfPath,
                    item.Error))
                .ToList();

            documents.AddRange(_duplicateRecords.Where(item =>
                item.Summary != null &&
                GetCompanyDocument(item.Summary, mode) == selectedDocument));

            string companyName = mode == NfeZipGroupingMode.Recipient
                ? selected.Summary.RecipientName
                : selected.Summary.IssuerName;

            string fileName = new string(
                (companyName ?? "Empresa")
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                .ToArray());

            await ExportZipAsync(
                documents,
                "XML-" + fileName + ".zip",
                "Exportar ZIP da empresa selecionada");
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "Falha ao exportar ZIP da empresa: " + ex.Message;
        }
    }
    private void DocumentPreview_PointerWheelChanged(
        object? sender,
        PointerWheelEventArgs e)
    {
        if (_pages.Count == 0 ||
            !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        float newZoom = e.Delta.Y > 0
            ? _previewZoom * PreviewZoomStep
            : _previewZoom / PreviewZoomStep;

        newZoom = Math.Clamp(
            newZoom,
            MinimumPreviewZoom,
            MaximumPreviewZoom);

        if (Math.Abs(newZoom - _previewZoom) < 0.001f)
            return;

        _previewZoom = newZoom;

        try
        {
            RenderAllPages();
            RebuildPreview();
            StatusText.Text =
                $"Zoom atualizado para {_previewZoom * 100:0}%.";
            e.Handled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Não foi possível atualizar o zoom: {ex.Message}";
        }
    }

    private void RenderAllPages()
    {
        var openDocuments =
            new Dictionary<string, PdfiumDocument>(
                StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var page in _pages)
            {
                if (!openDocuments.TryGetValue(
                    page.SourcePath,
                    out var document))
                {
                    document = new PdfiumDocument(page.SourcePath);
                    openDocuments.Add(page.SourcePath, document);
                }

                var rendered = document.RenderPage(
                    page.PageIndex,
                    _previewZoom);

                var newBitmap = CreatePreviewBitmap(rendered);

                page.Bitmap?.Dispose();
                page.Bitmap = newBitmap;
            }
        }
        finally
        {
            foreach (var document in openDocuments.Values)
                document.Dispose();
        }
    }

    private static WriteableBitmap CreatePreviewBitmap(
        RenderedPdfPage page)
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

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A limpeza não deve interromper a interface.
        }
    }

    private void DisposeAll()
    {
        foreach (var page in _pages)
            page.Bitmap?.Dispose();

        _pages.Clear();

        foreach (var item in _documents)
        {
            if (!string.IsNullOrWhiteSpace(item.PdfPath))
                TryDeleteFile(item.PdfPath);
        }

        _documents.Clear();

        try
        {
            if (Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, true);
        }
        catch
        {
            // Arquivos em uso não impedem o fechamento.
        }
    }

    private void Close_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private sealed class NfeBatchItem
    {
        public string SourcePath { get; }
        public NfeDocumentSummary? Summary { get; }
        public string? PdfPath { get; }
        public string? Error { get; }

        public string DisplayName
        {
            get
            {
                if (Error != null)
                    return $"⚠ {Path.GetFileName(SourcePath)} · ERRO: {Error}";

                return Summary?.DisplayName ?? Path.GetFileName(SourcePath);
            }
        }

        public NfeBatchItem(
            string sourcePath,
            NfeDocumentSummary? summary,
            string? pdfPath,
            string? error)
        {
            SourcePath = sourcePath;
            Summary = summary;
            PdfPath = pdfPath;
            Error = error;
        }
    }

    private sealed class NfeBatchPage
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