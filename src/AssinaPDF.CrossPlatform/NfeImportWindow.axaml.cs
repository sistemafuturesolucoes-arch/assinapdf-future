using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public partial class NfeImportWindow : Window
{
    private readonly ObservableCollection<NfeDocumentSummary> _documents = new();

    public NfeImportWindow()
    {
        InitializeComponent();
        DocumentsList.ItemsSource = _documents;
        DocumentsList.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(NfeDocumentSummary.DisplayName));
    }

    private async void AddXml_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecionar XMLs de NF-e",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Arquivos XML") { Patterns = new[] { "*.xml", "*.XML" } }
                }
            });

            if (files.Count == 0) return;

            int added = 0;
            int rejected = 0;
            var errors = new List<string>();

            foreach (var file in files)
            {
                string path = file.Path.LocalPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    rejected++;
                    errors.Add($"{file.Name}: caminho local indisponível.");
                    continue;
                }

                if (_documents.Any(d => string.Equals(d.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    var summary = NfeXmlReader.ReadFile(path);
                    _documents.Add(summary);
                    added++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
                {
                    rejected++;
                    errors.Add($"{file.Name}: {ex.Message}");
                }
            }

            CountText.Text = _documents.Count == 1 ? "1 NF-e importada" : $"{_documents.Count} NF-e(s) importadas";
            if (DocumentsList.SelectedItem == null && _documents.Count > 0)
                DocumentsList.SelectedIndex = 0;

            StatusText.Text = rejected == 0
                ? $"{added} XML(s) importado(s) e conferido(s). Nenhum arquivo original foi modificado."
                : $"{added} importado(s); {rejected} rejeitado(s). {string.Join(" | ", errors.Take(3))}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível selecionar os XMLs: {ex.Message}";
        }
    }

    private void DocumentsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfeDocumentSummary document)
        {
            DetailsText.Text = "Nenhum documento selecionado.";
            SelectionHint.Text = "Selecione um XML na lista para conferir os dados.";
            return;
        }

        SelectionHint.Text = $"Autorização: {document.AuthorizationReason} · Ambiente: {document.Environment}";
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var lines = new List<string>
        {
            $"NF-e: {document.InvoiceNumber}    Série: {document.Series}",
            $"Emissão: {document.IssueDate}",
            "",
            "EMITENTE",
            document.IssuerName,
            $"Documento: {FormatDocument(document.IssuerDocument)}",
            document.IssuerAddress,
            "",
            "DESTINATÁRIO",
            document.RecipientName,
            $"Documento: {FormatDocument(document.RecipientDocument)}",
            document.RecipientAddress,
            "",
            $"Produtos: {document.ProductTotal.ToString("C", culture)}",
            $"Total da NF-e: {document.InvoiceTotal.ToString("C", culture)}",
            $"Itens: {document.Items.Count}",
            "",
            "PROTOCOLO DE AUTORIZAÇÃO",
            $"Protocolo: {document.ProtocolNumber}",
            $"Recebimento: {document.AuthorizationDate}",
            $"Situação: {document.AuthorizationReason}",
            $"Ambiente: {document.Environment}",
            "",
            $"CHAVE DE ACESSO",
            document.AccessKey,
            "",
            "PRODUTOS"
        };

        foreach (var item in document.Items)
        {
            lines.Add($"{item.Number}. {item.Description}");
            lines.Add($"Código: {item.ProductCode} · NCM: {item.Ncm} · CFOP: {item.Cfop}");
            lines.Add($"Quantidade: {item.Quantity.ToString("N4", culture)} {item.Unit} · Unitário: {item.UnitPrice.ToString("C", culture)} · Total: {item.Total.ToString("C", culture)}");
            lines.Add("");
        }

        DetailsText.Text = string.Join(Environment.NewLine, lines);
    }

    private static string FormatDocument(string value) => value.Length == 14
        ? Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(@"00.000.000/0000-00")
        : value.Length == 11
            ? Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(@"000.000.000-00")
            : value;

    private void RemoveSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not NfeDocumentSummary selected) return;
        _documents.Remove(selected);
        RefreshList();
        StatusText.Text = "XML removido apenas da lista; o arquivo original permanece no computador.";
    }

    private void ClearList_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _documents.Clear();
        RefreshList();
        StatusText.Text = "Lista limpa. Nenhum arquivo do computador foi excluído.";
    }

    private void RefreshList()
    {
        CountText.Text = _documents.Count == 1 ? "1 NF-e importada" : $"{_documents.Count} NF-e(s) importadas";
        if (_documents.Count > 0)
            DocumentsList.SelectedIndex = 0;
        else
        {
            DocumentsList.SelectedIndex = -1;
            DetailsText.Text = "Nenhum documento selecionado.";
            SelectionHint.Text = "Selecione um XML na lista para conferir os dados.";
        }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
