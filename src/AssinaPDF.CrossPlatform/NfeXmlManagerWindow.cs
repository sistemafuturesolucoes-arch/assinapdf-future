using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public sealed class NfeXmlManagerWindow : Window
{
    private readonly CertificateStoreService _certificateStore = new();
    private readonly List<CertificateInfo> _certificates;
    private string? _certificateLoadError;
    private readonly ObservableCollection<string> _receivedRows = new();
    private readonly ObservableCollection<string> _issuedRows = new();
    private readonly List<NfeDocumentSummary> _issuedDocuments = new();
    private readonly List<NfeDocumentSummary> _allIssuedDocuments = new();
    private readonly List<ReceivedDocumentExport> _receivedDocuments = new();
    private readonly List<ReceivedDocumentExport> _visibleReceivedDocuments = new();
    private Button? _queryReceivedButton;

    private readonly ComboBox _certificatePicker;
    private readonly ComboBox _environmentPicker;
    private readonly TextBox _cnpjInput;
    private readonly TextBlock _certificateCnpjStatus;
    private readonly CalendarDatePicker _monthPicker;
    private readonly CalendarDatePicker _receivedMonthPicker;
    private readonly TextBlock _receivedStatus;
    private readonly TextBlock _issuedStatus;
    private readonly ListBox _receivedList;
    private readonly ListBox _issuedList;
    private string? _receivedBaseDirectory;

    public NfeXmlManagerWindow()
    {
        Title = "AssinaPDF | Buscar XMLs fiscais";
        Width = 820;
        Height = 620;
        MinWidth = 600;
        MinHeight = 440;
        MaxWidth = 1100;
        MaxHeight = 850;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#F3F4F6");

        try
        {
            _certificates = _certificateStore.LoadCertificates().ToList();
        }
        catch (Exception ex)
        {
            _certificates = new List<CertificateInfo>();
            _certificateLoadError = ex.Message;
        }
        _certificatePicker = new ComboBox
        {
            ItemsSource = _certificates,
            SelectedIndex = _certificates.Count > 0 ? 0 : -1,
            MinWidth = 300
        };
        _environmentPicker = new ComboBox
        {
            ItemsSource = new[] { "Produção", "Homologação" },
            SelectedIndex = 0,
            MinWidth = 150
        };
        _cnpjInput = new TextBox
        {
            IsVisible = false,
            PlaceholderText = "CNPJ identificado no certificado",
            MinWidth = 210
        };
        _certificateCnpjStatus = new TextBlock
        {
            Text = "CNPJ será identificado pelo certificado selecionado.",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush.Parse("#4B535E")
        };
        _monthPicker = new CalendarDatePicker
        {
            SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
            SelectedDateFormat = CalendarDatePickerFormat.Custom,
            CustomDateFormatString = "MMMM yyyy",
            MinWidth = 180,
            PlaceholderText = "Escolher mês/ano"
        };
        _receivedMonthPicker = new CalendarDatePicker
        {
            SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
            SelectedDateFormat = CalendarDatePickerFormat.Custom,
            CustomDateFormatString = "MMMM yyyy",
            MinWidth = 180,
            PlaceholderText = "Escolher mês/ano"
        };
        _certificatePicker.SelectionChanged += (_, _) => TryFillCnpjFromCertificate();
        TryFillCnpjFromCertificate();
        _receivedStatus = MakeStatus(_certificateLoadError is null
            ? "Selecione um certificado e o AssinaPDF identificará automaticamente o CNPJ para consultar a SEFAZ."
            : "Não foi possível carregar o repositório de certificados. A busca local de notas emitidas continua disponível. " + _certificateLoadError);
        _issuedStatus = MakeStatus("A SEFAZ não disponibiliza à própria empresa o XML das NF-e que ela emitiu por esta consulta. Aqui você pode importar e organizar XMLs emitidos que já foram salvos pelo emissor fiscal.");
        _receivedList = new ListBox { ItemsSource = _receivedRows, Height = 170, SelectionMode = SelectionMode.Multiple };
        _issuedList = new ListBox
        {
            ItemsSource = _issuedRows,
            Height = 210,
            SelectionMode = SelectionMode.Multiple
        };
        _monthPicker.SelectedDateChanged += (_, _) => ApplyIssuedMonthFilter();
        _receivedMonthPicker.SelectedDateChanged += (_, _) => ApplyReceivedMonthFilter();

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = "Buscar XMLs fiscais",
            FontSize = 25,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#292D36")
        });
        root.Children.Add(new TextBlock
        {
            Text = "O certificado identifica automaticamente a empresa. Consulte documentos recebidos na SEFAZ e importe XMLs emitidos pelo seu emissor fiscal.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#68717D")
        });

        var companyRow = new WrapPanel { Orientation = Orientation.Horizontal };
        companyRow.Children.Add(new TextBlock
        {
            Text = "Empresa:",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush.Parse("#4B535E")
        });
        companyRow.Children.Add(_certificateCnpjStatus);
        companyRow.Children.Add(new TextBlock
        {
            Text = "Certificado:",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush.Parse("#4B535E")
        });
        companyRow.Children.Add(_certificatePicker);
        companyRow.Children.Add(_environmentPicker);
        root.Children.Add(companyRow);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var receivedTab = MakeButton("Notas recebidas · SEFAZ", "#6741B8", "White");
        var issuedTab = MakeButton("Notas emitidas · XMLs importados", "#E7E9ED", "#34383E");
        tabs.Children.Add(receivedTab);
        tabs.Children.Add(issuedTab);
        root.Children.Add(tabs);

        var content = new Grid();
        var receivedPanel = BuildReceivedPanel();
        var issuedPanel = BuildIssuedPanel();
        content.Children.Add(receivedPanel);
        content.Children.Add(issuedPanel);
        issuedPanel.IsVisible = false;
        root.Children.Add(content);

        receivedTab.Click += (_, _) =>
        {
            receivedPanel.IsVisible = true;
            issuedPanel.IsVisible = false;
            receivedTab.Background = Brush.Parse("#6741B8");
            receivedTab.Foreground = Brush.Parse("#FFFFFF");
            issuedTab.Background = Brush.Parse("#E7E9ED");
            issuedTab.Foreground = Brush.Parse("#34383E");
        };
        issuedTab.Click += (_, _) =>
        {
            receivedPanel.IsVisible = false;
            issuedPanel.IsVisible = true;
            issuedTab.Background = Brush.Parse("#6741B8");
            issuedTab.Foreground = Brush.Parse("#FFFFFF");
            receivedTab.Background = Brush.Parse("#E7E9ED");
            receivedTab.Foreground = Brush.Parse("#34383E");
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var close = MakeButton("Fechar", "#E7E9ED", "#34383E");
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        root.Children.Add(footer);

        Content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(2),
            Child = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            }
        };

        Closed += (_, _) =>
        {
            foreach (var certificate in _certificates)
                certificate.Dispose();
        };
    }

    private Control BuildReceivedPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Notas recebidas",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#292D36")
        });
        panel.Children.Add(new TextBlock
        {
            Text = "A consulta usa o serviço oficial NFeDistribuicaoDFe com o certificado A1/A3 selecionado. A SEFAZ pode disponibilizar primeiro apenas o resumo; o XML completo depende das regras de manifestação do destinatário.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#68717D")
        });

        var actions = new WrapPanel();
        var query = MakeButton("Consultar SEFAZ e baixar XMLs", "#6741B8", "#FFFFFF");
        _queryReceivedButton = query;
        var chooseFolder = MakeButton("Escolher pasta de destino", "#E7E9ED", "#34383E");
        query.Click += async (_, _) => await QueryReceivedAsync();
        chooseFolder.Click += async (_, _) => await ChooseReceivedFolderAsync();
        actions.Children.Add(query);
        actions.Children.Add(chooseFolder);
        panel.Children.Add(actions);

        var monthRow = new WrapPanel();
        monthRow.Children.Add(new TextBlock
        {
            Text = "Filtrar mês de emissão:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = Brush.Parse("#4B535E")
        });
        monthRow.Children.Add(_receivedMonthPicker);
        panel.Children.Add(monthRow);

        var exportActions = new WrapPanel();
        var exportSelectedXml = MakeButton("Exportar XML selecionados", "#6741B8", "#FFFFFF");
        var exportSelectedPdf = MakeButton("Exportar PDF selecionados", "#6741B8", "#FFFFFF");
        var exportAllXml = MakeButton("Exportar XML do mês", "#E7E9ED", "#34383E");
        var exportAllPdf = MakeButton("Exportar PDF do mês", "#E7E9ED", "#34383E");
        exportSelectedXml.Click += async (_, _) => await ExportReceivedDocumentsAsync(pdf: false, exportAll: false);
        exportSelectedPdf.Click += async (_, _) => await ExportReceivedDocumentsAsync(pdf: true, exportAll: false);
        exportAllXml.Click += async (_, _) => await ExportReceivedDocumentsAsync(pdf: false, exportAll: true);
        exportAllPdf.Click += async (_, _) => await ExportReceivedDocumentsAsync(pdf: true, exportAll: true);
        exportActions.Children.Add(exportSelectedXml);
        exportActions.Children.Add(exportSelectedPdf);
        exportActions.Children.Add(exportAllXml);
        exportActions.Children.Add(exportAllPdf);
        panel.Children.Add(exportActions);
        panel.Children.Add(_receivedStatus);
        panel.Children.Add(_receivedList);
        return panel;
    }

    private Control BuildIssuedPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Notas emitidas",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#292D36")
        });
        panel.Children.Add(new TextBlock
        {
            Text = "A distribuição da SEFAZ não entrega ao emitente o XML da própria NF-e por este serviço. Aqui o AssinaPDF procura os XMLs autorizados já salvos no computador, filtrando pelo CNPJ emitente.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#68717D")
        });

        var searchActions = new WrapPanel();
        var commonFolders = MakeButton("Buscar nas pastas comuns", "#6741B8", "#FFFFFF");
        var chooseFolder = MakeButton("Selecionar outra pasta", "#E7E9ED", "#34383E");
        var openFile = MakeButton("Abrir XML selecionado", "#E7E9ED", "#34383E");
        commonFolders.Click += async (_, _) => await SearchIssuedAsync(GetCommonFolders());
        chooseFolder.Click += async (_, _) => await ChooseIssuedFolderAsync();
        openFile.Click += (_, _) => OpenSelectedIssuedXml();
        searchActions.Children.Add(commonFolders);
        searchActions.Children.Add(chooseFolder);
        searchActions.Children.Add(openFile);
        panel.Children.Add(searchActions);

        var monthRow = new WrapPanel();
        monthRow.Children.Add(new TextBlock
        {
            Text = "Filtrar mês de emissão:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = Brush.Parse("#4B535E")
        });
        monthRow.Children.Add(_monthPicker);
        panel.Children.Add(monthRow);

        var exportActions = new WrapPanel();
        var exportSelectedXml = MakeButton("Exportar XML selecionados", "#6741B8", "#FFFFFF");
        var exportSelectedPdf = MakeButton("Exportar PDF selecionados", "#6741B8", "#FFFFFF");
        var exportMonthXml = MakeButton("Exportar XML do mês", "#E7E9ED", "#34383E");
        var exportMonthPdf = MakeButton("Exportar PDF do mês", "#E7E9ED", "#34383E");
        exportSelectedXml.Click += async (_, _) => await ExportIssuedDocumentsAsync(pdf: false, exportAll: false);
        exportSelectedPdf.Click += async (_, _) => await ExportIssuedDocumentsAsync(pdf: true, exportAll: false);
        exportMonthXml.Click += async (_, _) => await ExportIssuedDocumentsAsync(pdf: false, exportAll: true);
        exportMonthPdf.Click += async (_, _) => await ExportIssuedDocumentsAsync(pdf: true, exportAll: true);
        exportActions.Children.Add(exportSelectedXml);
        exportActions.Children.Add(exportSelectedPdf);
        exportActions.Children.Add(exportMonthXml);
        exportActions.Children.Add(exportMonthPdf);
        panel.Children.Add(exportActions);
        panel.Children.Add(_issuedStatus);
        panel.Children.Add(_issuedList);
        return panel;
    }

    private async Task QueryReceivedAsync()
    {
        string cnpj = GetSelectedCertificateCnpj();
        if (cnpj.Length != 14)
        {
            _receivedStatus.Text = "Não foi possível identificar um CNPJ válido no certificado selecionado. Selecione um certificado e-CNPJ válido.";
            return;
        }

        if (_certificatePicker.SelectedItem is not CertificateInfo selected ||
            !selected.Certificate.HasPrivateKey)
        {
            _receivedStatus.Text = "Selecione um certificado digital com chave privada acessível.";
            return;
        }

        bool production = _environmentPicker.SelectedIndex == 0;
        string baseDirectory = _receivedBaseDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AssinaPDF", "XML", "Recebidas");
        string outputDirectory = Path.Combine(baseDirectory, cnpj);

        DistributionState state;
        try
        {
            state = await LoadStateAsync(cnpj, production);
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex)
        {
            _receivedStatus.Text =
                "Não foi possível acessar o histórico de NSU ou a pasta de destino. " + ex.Message;
            return;
        }

        if (state.NextAllowedUtc is DateTimeOffset allowed && allowed > DateTimeOffset.UtcNow)
        {
            _receivedStatus.Text =
                $"A SEFAZ solicitou aguardar até {allowed.ToLocalTime():dd/MM/yyyy HH:mm}. " +
                "O AssinaPDF bloqueou uma nova consulta para evitar consumo indevido.";
            return;
        }

        _receivedRows.Clear();
        _receivedDocuments.Clear();
        _visibleReceivedDocuments.Clear();
        _receivedStatus.Text = "Conectando à SEFAZ com o certificado selecionado...";
        SetReceivedQueryEnabled(false);

        try
        {
            string currentNsu = string.IsNullOrWhiteSpace(state.LastNsu)
                ? "0"
                : state.LastNsu;
            int batches = 0;
            int saved = 0;
            bool reachedEnd = false;

            while (batches < 20)
            {
                batches++;
                _receivedStatus.Text =
                    $"Consultando SEFAZ... lote {batches}, a partir do NSU {currentNsu}.";
                var batch = await NfeDistributionService.QueryByNsuAsync(
                    selected.Certificate,
                    cnpj,
                    currentNsu,
                    production);

                if (batch.StatusCode is not ("137" or "138"))
                {
                    if (batch.StatusCode == "656")
                    {
                        if (!string.IsNullOrWhiteSpace(batch.LastNsu))
                            state.LastNsu = batch.LastNsu;
                        state.NextAllowedUtc = DateTimeOffset.UtcNow.AddHours(1);
                        await SaveStateAsync(cnpj, production, state);
                    }
                    throw new InvalidOperationException(
                        $"SEFAZ {batch.StatusCode}: {batch.StatusMessage}");
                }

                foreach (var document in batch.Documents)
                {
                    string key = document.AccessKey.Trim();
                    string namePart = key.Length == 44 ? key : SafeFilePart(document.Schema);
                    string fileName = $"{document.Nsu}-{namePart}.xml";
                    string path = Path.Combine(outputDirectory, fileName);
                    await File.WriteAllTextAsync(path, document.XmlContent, new UTF8Encoding(false));
                    _receivedDocuments.Add(new ReceivedDocumentExport(document, path));
                    saved++;
                }

                if (!string.IsNullOrWhiteSpace(batch.LastNsu))
                {
                    state.LastNsu = batch.LastNsu;
                    await SaveStateAsync(cnpj, production, state);
                }

                if (batch.StatusCode == "137")
                {
                    state.NextAllowedUtc = DateTimeOffset.UtcNow.AddHours(1);
                    await SaveStateAsync(cnpj, production, state);
                    reachedEnd = true;
                    break;
                }

                if (string.IsNullOrWhiteSpace(batch.LastNsu) ||
                    batch.LastNsu == batch.MaxNsu)
                {
                    state.NextAllowedUtc = DateTimeOffset.UtcNow.AddHours(1);
                    await SaveStateAsync(cnpj, production, state);
                    reachedEnd = true;
                    break;
                }

                currentNsu = batch.LastNsu;
            }

            ApplyReceivedMonthFilter(updateStatus: false);
            _receivedStatus.Text =
                $"{saved} documento(s) recebidos e salvos em:\n{outputDirectory}\n" +
                (reachedEnd
                    ? "Consulta concluída. A próxima consulta automática será liberada após o intervalo exigido pela SEFAZ."
                    : "Limite de 20 lotes por execução atingido. O NSU foi salvo; você poderá continuar depois.");
        }
        catch (Exception ex)
        {
            ApplyReceivedMonthFilter(updateStatus: false);
            _receivedStatus.Text =
                "Não foi possível concluir a consulta à SEFAZ.\n" + ex.Message;
        }
        finally
        {
            SetReceivedQueryEnabled(true);
        }
    }

    private async Task ChooseReceivedFolderAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Escolher pasta para salvar XMLs recebidos",
                    AllowMultiple = false
                });
            if (folders.Count == 0) return;
            _receivedBaseDirectory = folders[0].Path.LocalPath;
            _receivedStatus.Text = "Pasta de destino: " + _receivedBaseDirectory;
        }
        catch (Exception ex)
        {
            _receivedStatus.Text = "Não foi possível selecionar a pasta: " + ex.Message;
        }
    }

    private async Task ChooseIssuedFolderAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Selecionar pasta que contém XMLs emitidos",
                    AllowMultiple = false
                });
            if (folders.Count == 0) return;
            await SearchIssuedAsync(new[] { folders[0].Path.LocalPath });
        }
        catch (Exception ex)
        {
            _issuedStatus.Text = "Não foi possível selecionar a pasta: " + ex.Message;
        }
    }

    private async Task SearchIssuedAsync(IEnumerable<string> folders)
    {
        string cnpj = GetSelectedCertificateCnpj();
        if (cnpj.Length != 14)
        {
            _issuedStatus.Text = "Selecione um certificado e-CNPJ para identificar a empresa e filtrar os XMLs emitidos.";
            return;
        }

        _issuedRows.Clear();
        _issuedDocuments.Clear();
        _allIssuedDocuments.Clear();
        _issuedStatus.Text = "Procurando XMLs nas pastas selecionadas...";

        try
        {
            string[] selectedFolders = folders.ToArray();
            var result = await Task.Run(() =>
                NfeLocalXmlSearchService.FindIssued(cnpj, selectedFolders, 3000));

            _allIssuedDocuments.AddRange(result.Documents);
            ApplyIssuedMonthFilter();
            _issuedStatus.Text =
                $"{_issuedDocuments.Count} NF-e exibida(s) no mês selecionado, de {result.Documents.Count} encontrada(s) em {result.FilesExamined} arquivo(s) analisado(s)." +
                (result.LimitReached ? " Limite de 3.000 arquivos por pesquisa atingido." : "") +
                (result.InvalidFiles > 0 ? $" {result.InvalidFiles} arquivo(s) não eram NF-e processadas compatíveis ou não puderam ser lidos." : "");
        }
        catch (Exception ex)
        {
            _issuedStatus.Text = "Não foi possível pesquisar os XMLs locais: " + ex.Message;
        }
    }

    private IEnumerable<string> GetCommonFolders()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        return folders.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }

    private void OpenSelectedIssuedXml()
    {
        int index = _issuedList.SelectedIndex;
        if (index < 0 || index >= _issuedDocuments.Count)
        {
            _issuedStatus.Text = "Selecione uma NF-e na lista primeiro.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _issuedDocuments[index].SourcePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _issuedStatus.Text = "Não foi possível abrir o XML: " + ex.Message;
        }
    }

    private void ApplyReceivedMonthFilter(bool updateStatus = true)
    {
        _visibleReceivedDocuments.Clear();
        _receivedRows.Clear();

        IEnumerable<ReceivedDocumentExport> filtered = _receivedDocuments;
        if (_receivedMonthPicker.SelectedDate is DateTime selectedDate)
        {
            filtered = filtered.Where(item =>
                DateTimeOffset.TryParse(item.Document.IssueDate, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var issueDate) &&
                issueDate.Year == selectedDate.Year &&
                issueDate.Month == selectedDate.Month);
        }

        _visibleReceivedDocuments.AddRange(filtered);
        foreach (var item in _visibleReceivedDocuments)
        {
            var document = item.Document;
            _receivedRows.Add(
                $"NSU {document.Nsu} · {document.DocumentType} · " +
                $"{document.InvoiceNumber} · {document.IssuerName} · {FormatIssueDate(document.IssueDate)}");
        }

        if (updateStatus)
            _receivedStatus.Text =
                $"{_visibleReceivedDocuments.Count} documento(s) recebidos no mês selecionado " +
                $"({_receivedMonthPicker.SelectedDate?.ToString("MM/yyyy") ?? "todos os meses"}).";
    }

    private List<ReceivedDocumentExport> GetSelectedReceivedDocuments()
    {
        var selected = new List<ReceivedDocumentExport>();
        foreach (object item in _receivedList.SelectedItems)
        {
            if (item is string row)
            {
                int index = _receivedRows.IndexOf(row);
                if (index >= 0 && index < _visibleReceivedDocuments.Count)
                    selected.Add(_visibleReceivedDocuments[index]);
            }
        }
        return selected;
    }

    private async Task ExportReceivedDocumentsAsync(bool pdf, bool exportAll)
    {
        List<ReceivedDocumentExport> documents = exportAll
            ? _visibleReceivedDocuments.ToList()
            : GetSelectedReceivedDocuments();

        if (documents.Count == 0)
        {
            _receivedStatus.Text = exportAll
                ? "Não há documentos recebidos nesta consulta para exportar."
                : "Selecione um ou mais documentos recebidos antes de exportar.";
            return;
        }

        string extension = pdf ? "pdf" : "xml";
        string formatLabel = pdf ? "PDF" : "XML";
        try
        {
            if (documents.Count == 1)
            {
                var item = documents[0];
                string suggestedName = $"NSU-{SafeFilePart(item.Document.Nsu)}.{extension}";
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = $"Exportar documento recebido em {formatLabel}",
                    SuggestedFileName = suggestedName,
                    DefaultExtension = extension,
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType(pdf ? "Documento PDF" : "Arquivo XML")
                        {
                            Patterns = new[] { "*." + extension }
                        }
                    }
                });
                if (file is null) return;
                ExportReceivedOne(item, file.Path.LocalPath, pdf);
                _receivedStatus.Text = $"{formatLabel} exportado: {file.Path.LocalPath}";
                return;
            }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Escolher pasta para exportar {documents.Count} documentos em {formatLabel}",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;

            string directory = folders[0].Path.LocalPath;
            int exported = 0;
            foreach (var item in documents)
            {
                string key = SafeFilePart(item.Document.AccessKey);
                string name = $"NSU-{SafeFilePart(item.Document.Nsu)}-{key}.{extension}";
                ExportReceivedOne(item, Path.Combine(directory, name), pdf);
                exported++;
            }
            _receivedStatus.Text = $"{exported} arquivo(s) {formatLabel} exportado(s) para: {directory}";
        }
        catch (Exception ex)
        {
            _receivedStatus.Text = $"Não foi possível exportar os documentos em {formatLabel}: " + ex.Message;
        }
    }

    private static void ExportReceivedOne(ReceivedDocumentExport item, string destination, bool pdf)
    {
        if (!pdf)
        {
            File.Copy(item.SourcePath, destination, overwrite: true);
            return;
        }

        NfeDocumentSummary? completeInvoice = null;
        try
        {
            completeInvoice = NfeXmlReader.Parse(item.Document.XmlContent, item.SourcePath);
        }
        catch
        {
            // A SEFAZ pode entregar apenas o resumo; nesse caso, o PDF identifica claramente o tipo recebido.
        }

        if (completeInvoice is not null)
            NfePdfExportService.ExportInvoiceSummary(completeInvoice, destination);
        else
            NfePdfExportService.ExportReceivedDocumentSummary(item.Document, destination);
    }

    private void ApplyIssuedMonthFilter()
    {
        _issuedDocuments.Clear();
        _issuedRows.Clear();

        if (_monthPicker.SelectedDate is not DateTime selectedDate)
        {
            _issuedDocuments.AddRange(_allIssuedDocuments);
        }
        else
        {
            _issuedDocuments.AddRange(_allIssuedDocuments.Where(document =>
                DateTimeOffset.TryParse(document.IssueDate, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var issueDate) &&
                issueDate.Year == selectedDate.Year &&
                issueDate.Month == selectedDate.Month));
        }

        foreach (var document in _issuedDocuments)
        {
            _issuedRows.Add(
                $"NF-e {document.InvoiceNumber} · {FormatIssueDate(document.IssueDate)} · " +
                $"{document.IssuerName} · R$ {document.InvoiceTotal:N2}");
        }

        _issuedStatus.Text =
            $"{_issuedDocuments.Count} NF-e no mês selecionado ({_monthPicker.SelectedDate?.ToString("MM/yyyy") ?? "todos os meses"}). " +
            "Selecione uma ou mais notas para exportar.";
    }

    private static string FormatIssueDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date.ToLocalTime().ToString("dd/MM/yyyy")
            : value;

    private List<NfeDocumentSummary> GetSelectedIssuedDocuments()
    {
        var selected = new List<NfeDocumentSummary>();
        foreach (object item in _issuedList.SelectedItems)
        {
            if (item is string row)
            {
                int index = _issuedRows.IndexOf(row);
                if (index >= 0 && index < _issuedDocuments.Count)
                    selected.Add(_issuedDocuments[index]);
            }
        }
        return selected;
    }

    private async Task ExportIssuedDocumentsAsync(bool pdf, bool exportAll)
    {
        List<NfeDocumentSummary> documents = exportAll
            ? _issuedDocuments.ToList()
            : GetSelectedIssuedDocuments();

        if (documents.Count == 0)
        {
            _issuedStatus.Text = exportAll
                ? "Não há NF-e no mês selecionado para exportar."
                : "Selecione uma ou mais NF-e na lista antes de exportar.";
            return;
        }

        string extension = pdf ? "pdf" : "xml";
        string formatLabel = pdf ? "PDF" : "XML";

        try
        {
            if (documents.Count == 1)
            {
                var document = documents[0];
                string suggestedName = $"NFe-{SafeFilePart(document.InvoiceNumber)}.{extension}";
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = $"Exportar NF-e em {formatLabel}",
                    SuggestedFileName = suggestedName,
                    DefaultExtension = extension,
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType(pdf ? "Documento PDF" : "Arquivo XML")
                        {
                            Patterns = new[] { "*." + extension }
                        }
                    }
                });
                if (file is null) return;
                ExportOne(document, file.Path.LocalPath, pdf);
                _issuedStatus.Text = $"{formatLabel} exportado: {file.Path.LocalPath}";
                return;
            }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Escolher pasta para exportar {documents.Count} NF-e em {formatLabel}",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;

            string destinationDirectory = folders[0].Path.LocalPath;
            int exported = 0;
            foreach (var document in documents)
            {
                string fileName = $"NFe-{SafeFilePart(document.InvoiceNumber)}-{SafeFilePart(document.AccessKey)}.{extension}";
                string destination = Path.Combine(destinationDirectory, fileName);
                ExportOne(document, destination, pdf);
                exported++;
            }

            _issuedStatus.Text =
                $"{exported} arquivo(s) {formatLabel} exportado(s) para: {destinationDirectory}";
        }
        catch (Exception ex)
        {
            _issuedStatus.Text = $"Não foi possível exportar em {formatLabel}: " + ex.Message;
        }
    }

    private static void ExportOne(NfeDocumentSummary document, string destination, bool pdf)
    {
        if (pdf)
            NfePdfExportService.ExportInvoiceSummary(document, destination);
        else
            File.Copy(document.SourcePath, destination, overwrite: true);
    }

    private void SetReceivedQueryEnabled(bool enabled)
    {
        if (_queryReceivedButton is not null)
            _queryReceivedButton.IsEnabled = enabled;
        _receivedList.IsEnabled = enabled;
    }

    private string GetSelectedCertificateCnpj() =>
        _certificatePicker.SelectedItem is CertificateInfo selected
            ? ExtractCnpjFromCertificate(selected.Certificate)
            : string.Empty;

    private void TryFillCnpjFromCertificate()
    {
        _cnpjInput.Text = GetSelectedCertificateCnpj();
        _certificateCnpjStatus.Text = _cnpjInput.Text.Length == 14
            ? $"CNPJ identificado: {_cnpjInput.Text}"
            : "CNPJ não identificado. Selecione um certificado e-CNPJ.";
    }

    private static string ExtractCnpjFromCertificate(X509Certificate2 certificate)
    {
        string subject = certificate.Subject + "\n" + certificate.SubjectName.Name;
        var patterns = new[]
        {
            @"(?:SERIALNUMBER|2\.5\.4\.5)\s*=\s*(?:CNPJ[:\s]*)?([A-Z0-9]{12}[0-9]{2})",
            @"CNPJ[:\s]*([A-Z0-9]{12}[0-9]{2})"
        };

        foreach (string pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                subject,
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value.ToUpperInvariant();
        }

        return string.Empty;
    }

    private sealed record ReceivedDocumentExport(NfeDistributedDocument Document, string SourcePath);

    private sealed class DistributionState
    {
        public string LastNsu { get; set; } = "0";
        public DateTimeOffset? NextAllowedUtc { get; set; }
    }

    private static string StatePath(string cnpj, bool production)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FutureSolucoes", "AssinaPDF", "Sefaz");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{cnpj}-{(production ? "prod" : "hom")}.json");
    }

    private static async Task<DistributionState> LoadStateAsync(string cnpj, bool production)
    {
        string path = StatePath(cnpj, production);
        if (!File.Exists(path)) return new DistributionState();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<DistributionState>(stream)
            ?? new DistributionState();
    }

    private static async Task SaveStateAsync(
        string cnpj,
        bool production,
        DistributionState state)
    {
        string path = StatePath(cnpj, production);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, state);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string SafeFilePart(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "documento" : value;
    }

    private static TextBlock MakeStatus(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush.Parse("#58616D")
    };

    private static Button MakeButton(string label, string background, string foreground) => new()
    {
        Content = label,
        Background = Brush.Parse(background),
        Foreground = Brush.Parse(foreground),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(14, 9),
        CornerRadius = new CornerRadius(7)
    };
}
