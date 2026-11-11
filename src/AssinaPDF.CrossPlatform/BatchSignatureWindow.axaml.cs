using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;
using SimpleSign.Core.Extensions;
using SimpleSign.Core.Signing;
using SimpleSign.PAdES;
using SimpleSign.PAdES.Signing;

namespace AssinaPDF.MacPrototype;

public partial class BatchSignatureWindow : Window
{
    private readonly CertificateStoreService _certificateStore = new();
    private readonly List<CertificateInfo> _certificates = new();
    private readonly ObservableCollection<BatchPdfItem> _documents = new();
    private IReadOnlyList<SignaturePlacement> _placementTemplate =
        Array.Empty<SignaturePlacement>();
    private bool _loadingCertificateSelection;
    private bool _operationInProgress;

    public BatchSignatureWindow()
    {
        InitializeComponent();
        DocumentsList.ItemsSource = _documents;

        try
        {
            _certificates.AddRange(_certificateStore.LoadCertificates());
            CertificatePicker.ItemsSource = _certificates;
            ApplyAllCertificatePicker.ItemsSource = _certificates;
            StatusText.Text = _certificates.Count == 0
                ? "Nenhum certificado encontrado no repositório do usuário."
                : $"{_certificates.Count} certificado(s) encontrado(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao carregar certificados: " + ex.Message;
        }

        Closed += (_, _) =>
        {
            foreach (var certificate in _certificates)
                certificate.Dispose();
            _certificates.Clear();
        };
    }

    private async void AddPdfs_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Adicionar PDFs para assinatura em lote",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Documentos PDF") { Patterns = new[] { "*.pdf", "*.PDF" } }
                }
            });

            int added = 0;
            int ignored = 0;
            foreach (var file in files)
            {
                string path = Path.GetFullPath(file.Path.LocalPath);
                if (!File.Exists(path) || _documents.Any(x => PathsEqual(x.SourcePath, path)))
                {
                    ignored++;
                    continue;
                }

                try
                {
                    using var document = new PdfiumDocument(path);
                    if (document.PageCount < 1)
                        throw new InvalidDataException("O PDF não contém páginas.");

                    _documents.Add(new BatchPdfItem(path, document.PageCount));
                    added++;
                }
                catch (Exception ex)
                {
                    ignored++;
                    StatusText.Text = $"{file.Name}: PDF não pôde ser aberto ({ex.Message}).";
                }
            }

            UpdateCount();
            if (_documents.Count > 0 && DocumentsList.SelectedIndex < 0)
                DocumentsList.SelectedIndex = 0;

            StatusText.Text = $"{added} PDF(s) adicionado(s); {ignored} ignorado(s). Os originais permanecem intactos.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível adicionar PDFs: " + ex.Message;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void DocumentsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not BatchPdfItem item)
        {
            SelectedDocumentText.Text = "Selecione um PDF na lista.";
            _loadingCertificateSelection = true;
            CertificatePicker.SelectedItem = null;
            _loadingCertificateSelection = false;
            CertificateDetailsText.Text = "O certificado selecionado será usado somente neste documento.";
            return;
        }

        SelectedDocumentText.Text = $"{Path.GetFileName(item.SourcePath)} · {item.PageCount} página(s)";
        _loadingCertificateSelection = true;
        CertificatePicker.SelectedItem = item.Certificate;
        _loadingCertificateSelection = false;
        UpdateCertificateDetails(item.Certificate);
    }

    private void CertificatePicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingCertificateSelection ||
            DocumentsList.SelectedItem is not BatchPdfItem item)
            return;

        item.Certificate = CertificatePicker.SelectedItem as CertificateInfo;
        UpdateCertificateDetails(item.Certificate);
        UpdateCount();
    }

    private void UpdateCertificateDetails(CertificateInfo? certificate)
    {
        if (certificate == null)
        {
            CertificateDetailsText.Text = "Nenhum certificado atribuído a este documento.";
            return;
        }

        CertificateDetailsText.Text = certificate.CanAttemptSigning
            ? certificate.Details
            : certificate.Details + "\nEste certificado não está habilitado para assinatura.";
    }

    private void ApplyCertificateToAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ApplyAllCertificatePicker.SelectedItem is not CertificateInfo certificate ||
            !certificate.CanAttemptSigning)
        {
            StatusText.Text = "Selecione um certificado habilitado para assinatura.";
            return;
        }

        foreach (var item in _documents)
            item.Certificate = certificate;

        UpdateCount();
        if (DocumentsList.SelectedItem is BatchPdfItem selected)
            UpdateCertificateDetails(selected.Certificate);

        StatusText.Text = $"Certificado aplicado a {_documents.Count} documento(s). Você ainda pode alterar cada documento individualmente.";
    }

    private void RemoveSelected_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not BatchPdfItem selected)
            return;

        int index = DocumentsList.SelectedIndex;
        _documents.Remove(selected);
        UpdateCount();

        if (_documents.Count > 0)
            DocumentsList.SelectedIndex = Math.Min(index, _documents.Count - 1);
        else
            DocumentsList.SelectedIndex = -1;

        StatusText.Text = "Documento removido da fila. O PDF original não foi excluído.";
    }

    private void ClearList_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _documents.Clear();
        DocumentsList.SelectedIndex = -1;
        _placementTemplate = Array.Empty<SignaturePlacement>();
        PlacementText.Text = "Nenhuma posição configurada.";
        UpdateCount();
        StatusText.Text = "Fila limpa. Nenhum PDF original foi excluído.";
    }

    private async void ConfigurePlacement_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DocumentsList.SelectedItem is not BatchPdfItem item)
        {
            StatusText.Text = "Selecione um PDF para configurar o posicionamento.";
            return;
        }

        try
        {
            using var document = new PdfiumDocument(item.SourcePath);
            var positioningWindow = new SignaturePositioningWindow(document);
            await positioningWindow.ShowDialog(this);

            if (positioningWindow.SelectedPlacements.Count == 0)
            {
                StatusText.Text = "Posicionamento cancelado ou nenhuma posição selecionada.";
                return;
            }

            _placementTemplate = positioningWindow.SelectedPlacements.ToArray();
            PlacementText.Text = $"{_placementTemplate.Count} posição(ões) configurada(s) com base em {Path.GetFileName(item.SourcePath)}. As coordenadas proporcionais serão reaproveitadas nos demais PDFs.";
            StatusText.Text = "Posicionamento visual configurado para a fila.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível configurar as posições: " + ex.Message;
        }
    }

    private async void SignAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_operationInProgress)
            return;

        if (_documents.Count == 0)
        {
            StatusText.Text = "Adicione pelo menos um PDF.";
            return;
        }

        var missingCertificate = _documents.FirstOrDefault(x =>
            x.Certificate == null || !x.Certificate.CanAttemptSigning);
        if (missingCertificate != null)
        {
            StatusText.Text = $"Atribua um certificado válido a “{Path.GetFileName(missingCertificate.SourcePath)}”.";
            return;
        }

        if (_placementTemplate.Count == 0)
        {
            StatusText.Text = "Configure ao menos uma posição visual antes de assinar.";
            return;
        }

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Escolher pasta para salvar os PDFs assinados",
                AllowMultiple = false
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível selecionar a pasta de destino: " + ex.Message;
            return;
        }

        if (folders.Count == 0)
            return;

        string outputDirectory;
        try
        {
            outputDirectory = Path.GetFullPath(folders[0].Path.LocalPath);
            if (!Directory.Exists(outputDirectory))
            {
                StatusText.Text = "A pasta de destino não existe ou não está acessível.";
                return;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Pasta de destino inválida: " + ex.Message;
            return;
        }

        _operationInProgress = true;
        SetControlsEnabled(false);
        int succeeded = 0;
        var failures = new List<string>();

        try
        {
            for (int index = 0; index < _documents.Count; index++)
            {
                var item = _documents[index];
                string fileName = Path.GetFileNameWithoutExtension(item.SourcePath);
                StatusText.Text = $"Assinando {index + 1} de {_documents.Count}: {Path.GetFileName(item.SourcePath)}";

                string? temporaryPath = null;
                try
                {
                    if (!File.Exists(item.SourcePath))
                        throw new FileNotFoundException("O PDF de origem não foi encontrado.", item.SourcePath);

                    using var pdf = new PdfiumDocument(item.SourcePath);
                    byte[] signedBytes = await File.ReadAllBytesAsync(item.SourcePath);

                    using var certificate = new X509Certificate2(item.Certificate!.Certificate);
                    string signerName = certificate.GetNameInfo(X509NameType.SimpleName, false);
                    string? reason = string.IsNullOrWhiteSpace(SigningReasonTextBox.Text)
                        ? null
                        : SigningReasonTextBox.Text.Trim();

                    foreach (var placement in _placementTemplate)
                    {
                        if (placement.PageIndex < 0 || placement.PageIndex >= pdf.PageCount)
                            throw new InvalidOperationException($"O PDF possui {pdf.PageCount} página(s), mas o posicionamento exige a página {placement.PageIndex + 1}.");

                        var page = pdf.RenderPage(placement.PageIndex, 0.10f);
                        if (placement.NormalizedX < 0 || placement.NormalizedY < 0 ||
                            placement.NormalizedWidth <= 0 || placement.NormalizedHeight <= 0 ||
                            placement.NormalizedX + placement.NormalizedWidth > 1.000001 ||
                            placement.NormalizedY + placement.NormalizedHeight > 1.000001)
                            throw new InvalidOperationException("Uma posição de assinatura possui coordenadas inválidas.");

                        float pdfX = (float)(placement.NormalizedX * page.PageWidthPoints);
                        float pdfY = (float)((1.0 - placement.NormalizedY - placement.NormalizedHeight) * page.PageHeightPoints);

                        signedBytes = await PadesSigner.Document(signedBytes)
                            .WithCertificate(certificate)
                            .WithMetadata(new SignatureMetadata
                            {
                                SignerName = signerName,
                                Reason = reason,
                                Location = "Future Soluções"
                            })
                            .WithAppearance(new SignatureAppearance
                            {
                                Page = placement.PageIndex + 1,
                                X = pdfX,
                                Y = pdfY,
                                ShowDate = true,
                                ShowReason = !string.IsNullOrWhiteSpace(reason),
                                ShowLocation = true
                            })
                            .WithLevel(AdesBaselineProfile.Basic())
                            .SignAsync();
                    }

                    string outputPath = GetAvailableOutputPath(outputDirectory, fileName);
                    if (PathsEqual(item.SourcePath, outputPath))
                        throw new IOException("O arquivo de saída não pode substituir o PDF original.");

                    temporaryPath = Path.Combine(outputDirectory, $".assinapdf-{Guid.NewGuid():N}.tmp");
                    await File.WriteAllBytesAsync(temporaryPath, signedBytes);
                    if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                        throw new IOException("O arquivo temporário de assinatura está vazio.");

                    File.Move(temporaryPath, outputPath, overwrite: false);
                    temporaryPath = null;
                    succeeded++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(item.SourcePath)}: {ex.Message}");
                }
                finally
                {
                    if (temporaryPath != null)
                    {
                        try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                        catch { /* Preserva o erro principal. */ }
                    }
                }
            }
        }
        finally
        {
            _operationInProgress = false;
            SetControlsEnabled(true);
        }

        string summary = $"{succeeded} de {_documents.Count} PDF(s) assinado(s) com sucesso.\nPasta de destino: {outputDirectory}";
        if (failures.Count > 0)
            summary += "\nFalhas:\n" + string.Join("\n", failures);
        StatusText.Text = summary;
    }

    private static string GetAvailableOutputPath(string directory, string baseName)
    {
        string candidate = Path.Combine(directory, baseName + "-assinado.pdf");
        int suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName}-assinado ({suffix}).pdf");
            suffix++;
        }
        return candidate;
    }

    private void SetControlsEnabled(bool enabled)
    {
        RootLayout.IsEnabled = enabled;
    }

    private void UpdateCount()
    {
        int assigned = _documents.Count(x => x.Certificate?.CanAttemptSigning == true);
        CountText.Text = $"{_documents.Count} PDF(s) · {assigned} com certificado atribuído";
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private sealed class BatchPdfItem : INotifyPropertyChanged
    {
        private CertificateInfo? _certificate;

        public string SourcePath { get; }
        public int PageCount { get; }
        public CertificateInfo? Certificate
        {
            get => _certificate;
            set
            {
                if (ReferenceEquals(_certificate, value))
                    return;

                _certificate = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }

        public string DisplayName => $"{Path.GetFileName(SourcePath)} · {PageCount} página(s)" +
            (Certificate == null ? " · sem certificado" : " · certificado atribuído");

        public event PropertyChangedEventHandler? PropertyChanged;

        public BatchPdfItem(string sourcePath, int pageCount)
        {
            SourcePath = sourcePath;
            PageCount = pageCount;
        }
    }
}
