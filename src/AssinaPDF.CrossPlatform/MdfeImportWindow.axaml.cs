using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public partial class MdfeImportWindow : Window
{
    private readonly List<MdfeBatchItem> _documents = new();
    public MdfeImportWindow() { InitializeComponent(); DocumentsList.ItemsSource = _documents; }

    private async void AddXml_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Selecionar XMLs de MDF-e", AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Arquivos XML") { Patterns = new[] { "*.xml", "*.XML" } } }
            });
            if (files.Count == 0) return;
            int imported = 0, rejected = 0, duplicates = 0;
            var errors = new List<string>();
            foreach (var file in files)
            {
                string path;
                try { path = Path.GetFullPath(file.Path.LocalPath); }
                catch { rejected++; errors.Add($"{file.Name}: caminho local indisponível."); continue; }
                if (!File.Exists(path)) { rejected++; errors.Add($"{file.Name}: arquivo não encontrado."); continue; }
                if (_documents.Any(d => PathEquals(d.SourcePath, path))) { duplicates++; _documents.Add(new MdfeBatchItem(path, null, "Arquivo já importado nesta sessão.", true)); continue; }
                try
                {
                    var summary = MdfeXmlReader.ReadFile(path);
                    if (_documents.Any(d => d.Summary?.AccessKey == summary.AccessKey)) { duplicates++; _documents.Add(new MdfeBatchItem(path, summary, "Chave de acesso já importada.", true)); continue; }
                    _documents.Add(new MdfeBatchItem(path, summary, null)); imported++;
                }
                catch (Exception ex) { rejected++; errors.Add($"{file.Name}: {ex.Message}"); _documents.Add(new MdfeBatchItem(path, null, ex.Message)); }
            }
            RefreshList();
            if (_documents.Count > 0) DocumentsList.SelectedIndex = _documents.Count - 1;
            string message = $"{imported} MDF-e(s) autorizados e validados; {rejected} com erro; {duplicates} duplicado(s).";
            if (errors.Count > 0) message += " " + string.Join(" | ", errors.Take(3));
            StatusText.Text = message;
        }
        catch (Exception ex) { StatusText.Text = "Não foi possível selecionar os XMLs: " + ex.Message; }
    }

    private void RefreshList()
    {
        DocumentsList.ItemsSource = null; DocumentsList.ItemsSource = _documents;
        int valid = _documents.Count(d => d.Summary != null && d.Error == null && !d.IsDuplicate);
        int errors = _documents.Count(d => d.Error != null && !d.IsDuplicate);
        int duplicates = _documents.Count(d => d.IsDuplicate);
        CountText.Text = $"{valid} MDF-e(s) validados · {errors} com erro · {duplicates} duplicado(s)";
    }

    private void DocumentsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not MdfeBatchItem item) return;
        if (item.Summary is null) { DetailStatus.Text = item.Error ?? "Registro duplicado."; ClearDetails(); return; }
        var d = item.Summary;
        DetailStatus.Text = item.IsDuplicate ? "Documento duplicado — não será exportado no lote." : "XML processado e autorizado. Chave, emitente e protocolo conferidos.";
        NumberText.Text = $"{d.DocumentNumber} / {d.Series}"; KeyText.Text = d.AccessKey;
        IssuerText.Text = d.IssuerName; DocumentText.Text = d.IssuerDocument; IssueText.Text = d.IssueDate;
        RouteText.Text = $"{d.OriginState} → {d.DestinationState}"; ModalText.Text = d.Modal == "1" ? "Rodoviário" : d.Modal;
        VehicleText.Text = string.IsNullOrWhiteSpace(d.VehiclePlate) ? "Não informado" : $"{d.VehiclePlate} / {d.VehicleState}";
        DriverText.Text = string.IsNullOrWhiteSpace(d.DriverName) ? "Não informado" : $"{d.DriverName} / {d.DriverDocument}";
        CargoText.Text = $"{d.CargoValue:N2} · {d.CargoQuantity:N4} {d.CargoUnit}";
        LinkedDocsText.Text = $"{d.CteCount} CT-e(s) · {d.NfeCount} NF-e(s)";
        ProtocolText.Text = $"{d.ProtocolNumber} · {d.AuthorizationDate} · {d.Environment}";
    }

    private void ClearDetails()
    {
        NumberText.Text = KeyText.Text = IssuerText.Text = DocumentText.Text = IssueText.Text =
            RouteText.Text = ModalText.Text = VehicleText.Text = DriverText.Text = CargoText.Text =
            LinkedDocsText.Text = ProtocolText.Text = "—";
    }

    private async void ExportSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not MdfeBatchItem item || item.Summary is null || item.Error != null || item.IsDuplicate) { StatusText.Text = "Selecione um MDF-e válido."; return; }
        try
        {
            var dest = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Exportar XML original do MDF-e", SuggestedFileName = Path.GetFileName(item.SourcePath), DefaultExtension = "xml",
                FileTypeChoices = new[] { new FilePickerFileType("Arquivo XML") { Patterns = new[] { "*.xml" } } }
            });
            if (dest is null) return;
            string target = Path.GetFullPath(dest.Path.LocalPath);
            if (PathEquals(target, item.SourcePath)) throw new InvalidOperationException("O destino não pode ser o próprio XML original.");
            File.Copy(item.SourcePath, target, true); StatusText.Text = "XML original exportado sem alterações: " + target;
        }
        catch (Exception ex) { StatusText.Text = "Falha ao exportar XML: " + ex.Message; }
    }

    private async void ExportZip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var valid = _documents.Where(d => d.Summary != null && d.Error == null && !d.IsDuplicate && File.Exists(d.SourcePath)).ToList();
        if (valid.Count == 0) { StatusText.Text = "Não há XMLs válidos para exportar."; return; }
        try
        {
            var dest = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Exportar XMLs MDF-e em ZIP", SuggestedFileName = "MDFe-XMLs-Validados.zip", DefaultExtension = "zip",
                FileTypeChoices = new[] { new FilePickerFileType("Arquivo ZIP") { Patterns = new[] { "*.zip" } } }
            });
            if (dest is null) return;
            string target = Path.GetFullPath(dest.Path.LocalPath);
            if (valid.Any(d => PathEquals(target, d.SourcePath))) throw new InvalidOperationException("O ZIP não pode substituir um XML original.");
            using (var archive = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in valid)
                {
                    string name = Path.GetFileName(item.SourcePath), entry = name; int n = 2;
                    while (!names.Add(entry)) entry = $"{Path.GetFileNameWithoutExtension(name)}-{n++}{Path.GetExtension(name)}";
                    archive.CreateEntryFromFile(item.SourcePath, entry, CompressionLevel.Optimal);
                }
            }
            StatusText.Text = $"{valid.Count} XML(s) original(is) exportado(s) em: {target}";
        }
        catch (Exception ex) { StatusText.Text = "Falha ao gerar ZIP: " + ex.Message; }
    }

    private void RemoveSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (DocumentsList.SelectedItem is MdfeBatchItem item) { _documents.Remove(item); RefreshList(); ClearDetails(); StatusText.Text = "Registro removido. O XML original permanece intacto."; } }
    private void ClearList_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { _documents.Clear(); RefreshList(); ClearDetails(); StatusText.Text = "Lista limpa. Nenhum XML original foi excluído."; }
    private static bool PathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private sealed class MdfeBatchItem
    {
        public string SourcePath { get; } public MdfeDocumentSummary? Summary { get; } public string? Error { get; } public bool IsDuplicate { get; }
        public string DisplayName => IsDuplicate ? $"↻ {Path.GetFileName(SourcePath)} · DUPLICADO" : Error != null ? $"⚠ {Path.GetFileName(SourcePath)} · ERRO: {Error}" : $"MDF-e {Summary?.DocumentNumber} · {Summary?.IssuerName} · {Summary?.OriginState} → {Summary?.DestinationState}";
        public MdfeBatchItem(string path, MdfeDocumentSummary? summary, string? error, bool duplicate = false) { SourcePath = path; Summary = summary; Error = error; IsDuplicate = duplicate; }
    }
}