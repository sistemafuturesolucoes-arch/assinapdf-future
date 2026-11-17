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

public partial class NfseImportWindow : Window
{
    private readonly ObservableCollection<NfseBatchItem> _documents = new();
    private readonly List<NfsePreviewPage> _pages = new();
    private readonly string _temporaryDirectory;
    private float _zoom = 1f;
    private const float MinZoom = 0.6f, MaxZoom = 2.5f, ZoomStep = 1.1f;

    public NfseImportWindow()
    {
        InitializeComponent();
        // Cada janela usa sua própria pasta temporária para não interferir
        // em outra sessão de importação aberta simultaneamente.
        _temporaryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FutureSolucoes", "AssinaPDF", "NfseBatchPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
        DocumentsList.ItemsSource = _documents;
        DocumentScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent,
            Preview_PointerWheelChanged, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        Closed += (_, _) => DisposeAll();
    }

    private async void AddXml_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecionar XMLs de NFS-e do Padrão Nacional",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("XML NFS-e Nacional") { Patterns = new[] { "*.xml", "*.XML" } }
                }
            });
            if (files.Count == 0) return;

            int generated = 0, errors = 0, duplicates = 0;
            var errorMessages = new List<string>();

            foreach (var file in files)
            {
                string path;
                try { path = file.Path.LocalPath; }
                catch
                {
                    errors++;
                    errorMessages.Add($"{file.Name}: caminho local indisponível.");
                    _documents.Add(new NfseBatchItem(file.Name, null, null, "Caminho local indisponível."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    errors++;
                    errorMessages.Add($"{file.Name}: arquivo não encontrado.");
                    _documents.Add(new NfseBatchItem(file.Name, null, null, "Arquivo não encontrado."));
                    continue;
                }

                if (_documents.Any(x => string.Equals(x.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    duplicates++;
                    _documents.Add(new NfseBatchItem(path, null, null, "O mesmo arquivo já foi importado nesta sessão.", true));
                    continue;
                }

                NfseDocumentSummary? summary = null;
                string? pdfPath = null;
                var newPages = new List<NfsePreviewPage>();
                try
                {
                    summary = NfseXmlReader.ReadFile(path);
                    if (_documents.Any(x => x.Summary != null &&
                        string.Equals(x.Summary.AccessKey, summary.AccessKey, StringComparison.Ordinal)))
                    {
                        duplicates++;
                        _documents.Add(new NfseBatchItem(path, summary, null, "Chave de acesso já importada.", true));
                        continue;
                    }

                    pdfPath = Path.Combine(_temporaryDirectory, $"danfse-{Guid.NewGuid():N}.pdf");
                    NfseDanfseGenerator.Generate(summary, pdfPath);

                    using (var pdf = new PdfiumDocument(pdfPath))
                    {
                        if (pdf.PageCount != 1)
                            throw new InvalidDataException("O DANFSe nacional deve ser gerado em uma única página A4.");
                        var rendered = pdf.RenderPage(0, _zoom);
                        newPages.Add(new NfsePreviewPage
                        {
                            PdfPath = pdfPath,
                            SourceName = Path.GetFileName(path),
                            PageIndex = 0,
                            Bitmap = CreateBitmap(rendered)
                        });
                    }

                    _documents.Add(new NfseBatchItem(path, summary, pdfPath, null));
                    _pages.AddRange(newPages);
                    generated++;
                }
                catch (Exception ex)
                {
                    foreach (var page in newPages) page.Bitmap?.Dispose();
                    if (!string.IsNullOrWhiteSpace(pdfPath)) TryDeleteFile(pdfPath);
                    errors++;
                    errorMessages.Add($"{file.Name}: {ex.Message}");
                    _documents.Add(new NfseBatchItem(path, summary, null, ex.Message));
                }
            }

            UpdateList();
            if (_documents.Count > 0) DocumentsList.SelectedIndex = _documents.Count - 1;
            string result = $"{generated} DANFSe(s) gerado(s); {errors} com erro; {duplicates} duplicado(s).";
            if (errorMessages.Count > 0) result += " " + string.Join(" | ", errorMessages.Take(3));
            StatusText.Text = result;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível selecionar os XMLs: " + ex.Message;
        }
    }

    private void UpdateList()
    {
        CountText.Text = $"{_documents.Count(x => x.Error == null && !x.IsDuplicate)} DANFSe(s) gerado(s) · " +
            $"{_documents.Count(x => x.Error != null && !x.IsDuplicate)} com erro · " +
            $"{_documents.Count(x => x.IsDuplicate)} duplicado(s)";
        RebuildPreview();
    }

    private void DocumentsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfseBatchItem item) return;
        if (item.Error != null)
        {
            StatusText.Text = item.IsDuplicate ? "Duplicado: " + item.Error :
                $"Falha em {Path.GetFileName(item.SourcePath)}: {item.Error}";
            return;
        }
        var page = _pages.FirstOrDefault(p => string.Equals(p.PdfPath, item.PdfPath, StringComparison.OrdinalIgnoreCase));
        if (page != null) NavigateToPage(page);
        StatusText.Text = item.DisplayName;
    }

    private void RebuildPreview()
    {
        PagesPanel.Children.Clear();
        foreach (var page in _pages)
        {
            var image = new Image
            {
                Source = page.Bitmap,
                Stretch = Avalonia.Media.Stretch.None,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            var card = new Border
            {
                Background = Avalonia.Media.Brushes.White,
                BorderBrush = Avalonia.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9),
                CornerRadius = new CornerRadius(3),
                Child = new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock { Text = page.SourceName, FontSize = 10, Foreground = Avalonia.Media.Brushes.Gray },
                        image
                    }
                }
            };
            page.Card = card;
            PagesPanel.Children.Add(card);
        }
        PageIndicator.Text = _pages.Count == 0
            ? "Nenhum DANFSe gerado"
            : $"{_pages.Count} DANFSe(s) · Zoom {_zoom * 100:0}%";
    }

    private void NavigateToPage(NfsePreviewPage page)
    {
        if (page.Card == null) return;
        DocumentScrollViewer.UpdateLayout();
        DocumentScrollViewer.Offset = new Vector(DocumentScrollViewer.Offset.X, Math.Max(0, page.Card.Bounds.Y - 8));
        PageIndicator.Text = $"{_pages.IndexOf(page) + 1} de {_pages.Count} DANFSe(s) · Zoom {_zoom * 100:0}%";
    }

    private async void ExportSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfseBatchItem item || item.Error != null ||
            item.IsDuplicate || string.IsNullOrWhiteSpace(item.PdfPath) || !File.Exists(item.PdfPath))
        {
            StatusText.Text = "Selecione uma NFS-e cujo DANFSe tenha sido gerado corretamente.";
            return;
        }
        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Salvar DANFSe",
                SuggestedFileName = $"DANFSe-NFSe-{item.Summary?.Number ?? "documento"}.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } }
            });
            if (destination == null) return;
            string output = Path.GetFullPath(destination.Path.LocalPath);
            EnsureNotSource(output, item.SourcePath);
            File.Copy(item.PdfPath, output, true);
            StatusText.Text = "DANFSe salvo em: " + output;
        }
        catch (Exception ex) { StatusText.Text = "Falha ao salvar DANFSe: " + ex.Message; }
    }

    private async void ExportAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var valid = _documents.Where(x => x.Error == null && !x.IsDuplicate &&
            !string.IsNullOrWhiteSpace(x.PdfPath) && File.Exists(x.PdfPath)).ToList();
        if (valid.Count == 0) { StatusText.Text = "Não há DANFSe válidos para exportar."; return; }
        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Salvar DANFSe consolidados",
                SuggestedFileName = "DANFSe-Nacional-Consolidados.pdf",
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
                foreach (var page in source.Pages) merged.AddPage(page);
            }
            if (merged.Pages.Count == 0) throw new InvalidDataException("Nenhuma página válida encontrada.");
            merged.Save(output);
            StatusText.Text = $"{valid.Count} DANFSe(s) exportado(s) em {output}";
        }
        catch (Exception ex) { StatusText.Text = "Falha ao consolidar DANFSe: " + ex.Message; }
    }

    private void RemoveSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfseBatchItem item) return;
        _documents.Remove(item);
        RemovePages(item);
        UpdateList();
        StatusText.Text = "Item removido da sessão. O XML original permanece intacto.";
    }

    private void ClearList_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        foreach (var item in _documents.ToList()) RemovePages(item);
        _documents.Clear();
        _pages.Clear();
        UpdateList();
        StatusText.Text = "Lista limpa. Nenhum XML original foi excluído.";
    }

    private void RemovePages(NfseBatchItem item)
    {
        var removed = _pages.Where(p => string.Equals(p.PdfPath, item.PdfPath, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var page in removed) { page.Bitmap?.Dispose(); _pages.Remove(page); }
        if (!string.IsNullOrWhiteSpace(item.PdfPath)) TryDeleteFile(item.PdfPath);
    }

    private void Preview_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_pages.Count == 0 || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        float next = Math.Clamp(e.Delta.Y > 0 ? _zoom * ZoomStep : _zoom / ZoomStep, MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 0.001f) return;
        _zoom = next;
        try
        {
            foreach (var page in _pages)
            {
                using var pdf = new PdfiumDocument(page.PdfPath);
                var rendered = pdf.RenderPage(page.PageIndex, _zoom);
                var bitmap = CreateBitmap(rendered);
                page.Bitmap?.Dispose();
                page.Bitmap = bitmap;
            }
            RebuildPreview();
            StatusText.Text = $"Zoom atualizado para {_zoom * 100:0}%.";
            e.Handled = true;
        }
        catch (Exception ex) { StatusText.Text = "Falha ao atualizar zoom: " + ex.Message; }
    }

    private static WriteableBitmap CreateBitmap(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(new PixelSize(page.Width, page.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using var fb = bitmap.Lock();
            int rowBytes = checked(page.Width * 4);
            for (int y = 0; y < page.Height; y++)
                Marshal.Copy(page.Pixels, checked(y * page.Stride), IntPtr.Add(fb.Address, checked(y * fb.RowBytes)), rowBytes);
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

    private void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private void DisposeAll()
    {
        foreach (var page in _pages) page.Bitmap?.Dispose();
        _pages.Clear();
        foreach (var item in _documents) if (!string.IsNullOrWhiteSpace(item.PdfPath)) TryDeleteFile(item.PdfPath);
        _documents.Clear();
        try { if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, true); } catch { }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private sealed class NfseBatchItem
    {
        public string SourcePath { get; }
        public NfseDocumentSummary? Summary { get; }
        public string? PdfPath { get; }
        public string? Error { get; }
        public bool IsDuplicate { get; }
        public string DisplayName => IsDuplicate
            ? $"↻ {Path.GetFileName(SourcePath)} · DUPLICADO"
            : Error != null
                ? $"⚠ {Path.GetFileName(SourcePath)} · ERRO: {Error}"
                : Summary?.DisplayName ?? Path.GetFileName(SourcePath);

        public NfseBatchItem(string sourcePath, NfseDocumentSummary? summary, string? pdfPath, string? error, bool duplicate = false)
        { SourcePath = sourcePath; Summary = summary; PdfPath = pdfPath; Error = error; IsDuplicate = duplicate; }
    }

    private sealed class NfsePreviewPage
    {
        public string PdfPath { get; set; } = "";
        public string SourceName { get; set; } = "";
        public int PageIndex { get; set; }
        public WriteableBitmap? Bitmap { get; set; }
        public Border? Card { get; set; }
    }
}