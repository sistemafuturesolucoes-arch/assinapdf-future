using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public partial class OfficeToPdfWindow : Window
{
    private sealed class OfficeDocumentItem : INotifyPropertyChanged
    {
        public required string SourcePath { get; init; }
        public required string DisplayName { get; init; }
        private string _status = "Aguardando conversão";
        private string? _pdfPath;
        private Bitmap? _preview;
        public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
        public string? PdfPath { get => _pdfPath; set { _pdfPath = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PdfPath))); } }
        public string? WorkDirectory { get; set; }
        public Bitmap? Preview { get => _preview; set { _preview = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<OfficeDocumentItem> _documents = new();
    private readonly string _sessionDirectory = Path.Combine(
        Path.GetTempPath(), "AssinaPDF", "OfficeToPdf", Guid.NewGuid().ToString("N"));
    private readonly OfficeToPdfService _converter = new();
    private bool _busy;

    public OfficeToPdfWindow()
    {
        InitializeComponent();
        DocumentsList.ItemsSource = _documents;
        Closed += (_, _) => DisposeSession();
        var executable = OfficeToPdfService.FindLibreOffice();
        StatusText.Text = executable is null
            ? "LibreOffice não encontrado. Instale-o para habilitar as conversões Word/Excel."
            : "Motor de conversão encontrado. Adicione documentos Word ou Excel.";
    }

    private async void AddFiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecionar documentos Word e Excel",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Word e Excel")
                    {
                        Patterns = new[] { "*.doc", "*.docx", "*.odt", "*.rtf", "*.xls", "*.xlsx", "*.xlsm", "*.ods", "*.csv" }
                    }
                }
            });

            int added = 0;
            foreach (var file in files)
            {
                var path = Path.GetFullPath(file.Path.LocalPath);
                if (!File.Exists(path) || !OfficeToPdfService.IsSupported(path)) continue;
                if (_documents.Any(x => string.Equals(x.SourcePath, path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                    continue;

                _documents.Add(new OfficeDocumentItem
                {
                    SourcePath = path,
                    DisplayName = file.Name
                });
                added++;
            }
            UpdateCount();
            StatusText.Text = added == 0
                ? "Nenhum arquivo novo foi adicionado."
                : $"{added} documento(s) adicionado(s). Os originais permanecem intactos.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao adicionar documentos: {ex.Message}";
        }
    }

    private async void ConvertSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is OfficeDocumentItem item)
            await ConvertItemsAsync(new[] { item });
        else
            StatusText.Text = "Selecione um documento para converter.";
    }

    private async void ConvertAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ConvertItemsAsync(_documents.Where(x => x.PdfPath is null).ToArray());
    }

    private async Task ConvertItemsAsync(IReadOnlyCollection<OfficeDocumentItem> items)
    {
        if (_busy || items.Count == 0) return;
        _busy = true;
        int converted = 0, failed = 0;
        try
        {
            Directory.CreateDirectory(_sessionDirectory);
            foreach (var item in items)
            {
                item.Status = "Convertendo…";
                var work = Path.Combine(_sessionDirectory, Guid.NewGuid().ToString("N"));
                item.WorkDirectory = work;
                try
                {
                    var pdf = await _converter.ConvertToPdfAsync(item.SourcePath, work);
                    using var document = new PdfiumDocument(pdf);
                    var page = document.RenderPage(0, 1.25f);
                    item.Preview?.Dispose();
                    item.Preview = CreateBitmap(page.Width, page.Height, page.Pixels, page.Stride);
                    item.PdfPath = pdf;
                    item.Status = $"PDF pronto · {document.PageCount} página(s)";
                    converted++;
                }
                catch (Exception ex)
                {
                    item.Status = "Erro: " + ex.Message;
                    failed++;
                }
            }
            UpdateCount();
            if (DocumentsList.SelectedItem is OfficeDocumentItem selected)
                ShowPreview(selected);
            StatusText.Text = $"{converted} convertido(s); {failed} com erro.";
        }
        finally
        {
            _busy = false;
        }
    }

    private void DocumentsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentsList.SelectedItem is OfficeDocumentItem item)
            ShowPreview(item);
    }

    private void ShowPreview(OfficeDocumentItem item)
    {
        if (item.Preview is null)
        {
            PreviewImage.Source = null;
            PreviewSurface.IsVisible = false;
            EmptyState.IsVisible = true;
            return;
        }
        PreviewImage.Source = item.Preview;
        PreviewSurface.IsVisible = true;
        EmptyState.IsVisible = false;
    }

    private async void ExportSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not OfficeDocumentItem item || item.PdfPath is null)
        {
            StatusText.Text = "Converta e selecione um documento antes de exportar.";
            return;
        }
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar PDF convertido",
            SuggestedFileName = Path.GetFileNameWithoutExtension(item.SourcePath) + ".pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } }
        });
        if (destination is null) return;
        var output = Path.GetFullPath(destination.Path.LocalPath);
        if (string.Equals(output, item.SourcePath, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("O PDF de destino não pode substituir o arquivo original.");
        File.Copy(item.PdfPath, output, overwrite: true);
        StatusText.Text = $"PDF salvo: {output}";
    }

    private async void ExportAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var converted = _documents.Where(x => x.PdfPath is not null).ToArray();
        if (converted.Length == 0)
        {
            StatusText.Text = "Não há PDFs convertidos para exportar.";
            return;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolher pasta para exportar PDFs",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        var folder = folders[0].Path.LocalPath;
        int exported = 0;
        foreach (var item in converted)
        {
            var name = Path.GetFileNameWithoutExtension(item.SourcePath) + ".pdf";
            var output = Path.Combine(folder, name);
            if (File.Exists(output))
                output = Path.Combine(folder, Path.GetFileNameWithoutExtension(item.SourcePath) +
                    "-" + Guid.NewGuid().ToString("N")[..6] + ".pdf");
            File.Copy(item.PdfPath!, output);
            exported++;
        }
        StatusText.Text = $"{exported} PDF(s) exportado(s) para {folder}.";
    }

    private void RemoveSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not OfficeDocumentItem item) return;
        item.Preview?.Dispose();
        _documents.Remove(item);
        UpdateCount();
        PreviewImage.Source = null;
        PreviewSurface.IsVisible = false;
        EmptyState.IsVisible = true;
        StatusText.Text = "Documento removido da lista; o arquivo original não foi alterado.";
    }

    private void UpdateCount() =>
        CountText.Text = $"{_documents.Count} documento(s) · {_documents.Count(x => x.PdfPath is not null)} convertido(s)";

    private static WriteableBitmap CreateBitmap(int width, int height, byte[] pixels, int stride)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using var framebuffer = bitmap.Lock();
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * stride, IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes), stride);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private void DisposeSession()
    {
        foreach (var item in _documents) item.Preview?.Dispose();
        try { if (Directory.Exists(_sessionDirectory)) Directory.Delete(_sessionDirectory, recursive: true); } catch { }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
