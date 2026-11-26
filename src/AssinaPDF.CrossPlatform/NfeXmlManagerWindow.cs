using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly ObservableCollection<string> _receivedRows = new();
    private readonly ObservableCollection<string> _issuedRows = new();
    private readonly List<string> _receivedPaths = new();
    private readonly List<NfeDocumentSummary> _issuedDocuments = new();
    private Button? _queryReceivedButton;

    private readonly ComboBox _certificatePicker;
    private readonly ComboBox _environmentPicker;
    private readonly TextBox _cnpjInput;
    private readonly TextBlock _receivedStatus;
    private readonly TextBlock _issuedStatus;
    private readonly ListBox _receivedList;
    private readonly ListBox _issuedList;
    private string? _receivedBaseDirectory;
    private string? _lastIssuedFolder;

    public NfeXmlManagerWindow()
    {
        Title = "AssinaPDF | Buscar XMLs fiscais";
        Width = 1040;
        Height = 760;
        MinWidth = 820;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#F3F4F6");

        _certificates = _certificateStore.LoadCertificates().ToList();
        _certificatePicker = new ComboBox
        {
            ItemsSource = _certificates,
            SelectedIndex = _certificates.Count > 0 ? 0 : -1,
            MinWidth = 300
        };
        _certificatePicker.SelectionChanged += (_, _) => TryFillCnpjFromCertificate();
        _environmentPicker = new ComboBox
        {
            ItemsSource = new[] { "Produção", "Homologação" },
            SelectedIndex = 0,
            MinWidth = 150
        };
        _cnpjInput = new TextBox
        {
            Watermark = "CNPJ da empresa (14 dígitos)",
            MinWidth = 230
        };
        _receivedStatus = MakeStatus("Selecione o certificado e informe o CNPJ para consultar a distribuição da SEFAZ.");
        _issuedStatus = MakeStatus("Informe o CNPJ para localizar NF-e emitidas que já estejam salvas no computador.");
        _receivedList = new ListBox { ItemsSource = _receivedRows, MinHeight = 250 };
        _issuedList = new ListBox { ItemsSource = _issuedRows, MinHeight = 250 };

        var root = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = "Buscar XMLs fiscais",
            FontSize = 25,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#292D36")
        });
        root.Children.Add(new TextBlock
        {
            Text = "Consulte NF-e recebidas na SEFAZ e organize XMLs de notas emitidas que já estão no computador.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#68717D")
        });

        var companyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        companyRow.Children.Add(_cnpjInput);
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
        var issuedTab = MakeButton("Notas emitidas · arquivos locais", "#E7E9ED", "#34383E");
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
            Child = root
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

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var query = MakeButton("Consultar SEFAZ e baixar XMLs", "#6741B8", "#FFFFFF");
        _queryReceivedButton = query;
        var chooseFolder = MakeButton("Escolher pasta de destino", "#E7E9ED", "#34383E");
        query.Click += async (_, _) => await QueryReceivedAsync();
        chooseFolder.Click += async (_, _) => await ChooseReceivedFolderAsync();
        actions.Children.Add(query);
        actions.Children.Add(chooseFolder);
        panel.Children.Add(actions);
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

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var commonFolders = MakeButton("Buscar nas pastas comuns", "#6741B8", "#FFFFFF");
        var chooseFolder = MakeButton("Selecionar outra pasta", "#E7E9ED", "#34383E");
        var openFile = MakeButton("Abrir XML selecionado", "#E7E9ED", "#34383E");
        commonFolders.Click += async (_, _) => await SearchIssuedAsync(GetCommonFolders());
        chooseFolder.Click += async (_, _) => await ChooseIssuedFolderAsync();
        openFile.Click += (_, _) => OpenSelectedIssuedXml();
        actions.Children.Add(commonFolders);
        actions.Children.Add(chooseFolder);
        actions.Children.Add(openFile);
        panel.Children.Add(actions);
        panel.Children.Add(_issuedStatus);
        panel.Children.Add(_issuedList);
        return panel;
    }

    private async Task QueryReceivedAsync()
    {
        string cnpj = NfeDistributionService.NormalizeDigits(_cnpjInput.Text ?? "");
        if (cnpj.Length != 14)
        {
            _receivedStatus.Text = "Informe um CNPJ válido com 14 dígitos.";
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
        Directory.CreateDirectory(outputDirectory);

        var state = await LoadStateAsync(cnpj, production);
        if (state.NextAllowedUtc is DateTimeOffset allowed && allowed > DateTimeOffset.UtcNow)
        {
            _receivedStatus.Text =
                $"A SEFAZ solicitou aguardar até {allowed.ToLocalTime():dd/MM/yyyy HH:mm}. " +
                "O AssinaPDF bloqueou uma nova consulta para evitar consumo indevido.";
            return;
        }

        _receivedRows.Clear();
        _receivedPaths.Clear();
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
                        state.NextAllowedUtc = DateTimeOffset.UtcNow.AddHours(1);
                        await SaveStateAsync(cnpj, production, state);
                    }
                    throw new InvalidOperationException(
                        $"SEFAZ {batch.StatusCode}: {batch.StatusMessage}");
                }

                foreach (var document in batch.Documents)
                {
                    string key = NfeDistributionService.NormalizeDigits(document.AccessKey);
                    string namePart = key.Length == 44 ? key : SafeFilePart(document.Schema);
                    string fileName = $"{document.Nsu}-{namePart}.xml";
                    string path = Path.Combine(outputDirectory, fileName);
                    await File.WriteAllTextAsync(path, document.XmlContent, new UTF8Encoding(false));
                    _receivedPaths.Add(path);
                    _receivedRows.Add(
                        $"NSU {document.Nsu} · {document.DocumentType} · " +
                        $"{document.InvoiceNumber} · {document.IssuerName} · {path}");
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

            _receivedStatus.Text =
                $"{saved} documento(s) recebidos e salvos em:\n{outputDirectory}\n" +
                (reachedEnd
                    ? "Consulta concluída. A próxima consulta automática será liberada após o intervalo exigido pela SEFAZ."
                    : "Limite de 20 lotes por execução atingido. O NSU foi salvo; você poderá continuar depois.");
        }
        catch (Exception ex)
        {
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
            _lastIssuedFolder = folders[0].Path.LocalPath;
            await SearchIssuedAsync(new[] { _lastIssuedFolder });
        }
        catch (Exception ex)
        {
            _issuedStatus.Text = "Não foi possível selecionar a pasta: " + ex.Message;
        }
    }

    private async Task SearchIssuedAsync(IEnumerable<string> folders)
    {
        string cnpj = NfeDistributionService.NormalizeDigits(_cnpjInput.Text ?? "");
        if (cnpj.Length != 14)
        {
            _issuedStatus.Text = "Informe o CNPJ emitente com 14 dígitos antes de pesquisar.";
            return;
        }

        _issuedRows.Clear();
        _issuedDocuments.Clear();
        _issuedStatus.Text = "Procurando XMLs nas pastas selecionadas...";
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int examined = 0;
        int failures = 0;

        await Task.Run(() =>
        {
            foreach (string folder in folders.Where(Directory.Exists))
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, "*.xml", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        MaxRecursionDepth = 10
                    });
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    if (!seen.Add(file)) continue;
                    if (++examined > 3000) return;
                    try
                    {
                        var summary = NfeXmlReader.ReadFile(file);
                        if (NfeDistributionService.NormalizeDigits(summary.IssuerDocument) == cnpj)
                        {
                            lock (_issuedDocuments)
                                _issuedDocuments.Add(summary);
                        }
                    }
                    catch
                    {
                        failures++;
                    }
                }
            }
        });

        foreach (var document in _issuedDocuments
            .OrderByDescending(x => x.IssueDate, StringComparer.Ordinal))
        {
            _issuedRows.Add(
                $"NF-e {document.InvoiceNumber} · {document.IssueDate} · " +
                $"{document.IssuerName} · R$ {document.InvoiceTotal:N2} · {document.SourcePath}");
        }

        _issuedStatus.Text =
            $"{_issuedDocuments.Count} NF-e emitida(s) encontrada(s) em {examined} arquivo(s) analisado(s)." +
            (examined > 3000 ? " Limite de 3.000 arquivos por pesquisa atingido." : "") +
            (failures > 0 ? $" {failures} arquivo(s) não eram NF-e processadas compatíveis." : "");
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

    private void SetReceivedQueryEnabled(bool enabled)
    {
        if (_queryReceivedButton is not null)
            _queryReceivedButton.IsEnabled = enabled;
        _receivedList.IsEnabled = enabled;
    }

    private void TryFillCnpjFromCertificate()
    {
        if (_certificatePicker.SelectedItem is not CertificateInfo selected ||
            !string.IsNullOrWhiteSpace(_cnpjInput.Text))
            return;

        var match = System.Text.RegularExpressions.Regex.Match(
            selected.Certificate.Subject,
            @"(?:SERIALNUMBER|2\\.5\\.4\\.5)\\s*=\\s*(\\d{14})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
            _cnpjInput.Text = match.Groups[1].Value;
    }

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
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DistributionState>(stream)
                ?? new DistributionState();
        }
        catch
        {
            return new DistributionState();
        }
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
