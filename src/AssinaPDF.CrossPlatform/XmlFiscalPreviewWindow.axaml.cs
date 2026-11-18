using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public partial class XmlFiscalPreviewWindow : Window
{
    private readonly bool _isNfce;
    private readonly ObservableCollection<string> _fields = new();

    // Required by Avalonia's runtime XAML loader; the regular flow supplies the document type.
    public XmlFiscalPreviewWindow() : this(false)
    {
    }

    public XmlFiscalPreviewWindow(bool isNfce)
    {
        _isNfce = isNfce;
        InitializeComponent();
        FieldsList.ItemsSource = _fields;
        HeaderText.Text = isNfce ? "NFC-e · conferência do XML" : "CT-e · conferência do XML";
        DescriptionText.Text = isNfce
            ? "Leitura de NFC-e modelo 65 autorizada. Confira os dados antes da etapa de geração do DANFCE."
            : "Leitura de CT-e processado modelo 57 versão 4.00. Confira os dados antes da etapa de geração do DACTE.";
        Title = isNfce ? "AssinaPDF | NFC-e" : "AssinaPDF | CT-e";
    }

    private async void SelectXml_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = _isNfce ? "Selecionar XML de NFC-e" : "Selecionar XML processado de CT-e",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Arquivos XML") { Patterns = new[] { "*.xml", "*.XML" } }
                }
            });

            if (files.Count == 0) return;
            var path = files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Não foi possível acessar o arquivo XML selecionado.");

            object summary = _isNfce
                ? NfceXmlReader.ReadFile(path)
                : CteXmlReader.ReadFile(path);

            _fields.Clear();
            AddRecordFields(summary);
            FileNameText.Text = Path.GetFileName(path);
            StatusText.Text = _isNfce
                ? "XML de NFC-e validado e dados extraídos. Nenhum PDF oficial foi gerado nesta etapa."
                : "XML de CT-e validado e dados extraídos. Nenhum PDF oficial foi gerado nesta etapa.";
        }
        catch (Exception ex)
        {
            _fields.Clear();
            StatusText.Text = "Falha ao importar XML: " + ex.Message;
        }
    }

    private void AddRecordFields(object record)
    {
        foreach (var property in record.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var value = property.GetValue(record);
            if (value is IEnumerable sequence && value is not string)
            {
                foreach (var item in sequence)
                {
                    if (item is null) continue;
                    foreach (var child in item.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                        _fields.Add($"Item · {child.Name}: {Format(child.GetValue(item))}");
                }
                continue;
            }

            _fields.Add($"{FriendlyName(property.Name)}: {Format(value)}");
        }
    }

    private static string FriendlyName(string name)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");
        return result;
    }

    private static string Format(object? value) => value switch
    {
        null => "—",
        decimal amount => amount.ToString("N2", CultureInfo.GetCultureInfo("pt-BR")),
        DateTime date => date.ToString("dd/MM/yyyy HH:mm:ss"),
        _ => value.ToString() ?? "—"
    };

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}