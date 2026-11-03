
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;
using SimpleSign.PAdES.Validation;
using SimpleSign.Core.Validation;
using System.Text;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using SimpleSign.PAdES;
using SimpleSign.PAdES.Signing;
using SimpleSign.Core.Extensions;
using SimpleSign.Core.Signing;

namespace AssinaPDF.MacPrototype;

public partial class MainWindow : Window
{
    private readonly CertificateStoreService _certificateStore = new();
    private readonly List<CertificateInfo> _certificates = new();

    private Bitmap? _futureLogo;
    private PdfiumDocument? _pdfDocument;
    private WriteableBitmap? _currentPageBitmap;
    private string? _currentPdfPath;

    private bool _loadingCertificates;
    private bool _operationInProgress;
    private int _currentPageIndex = -1;
    private float _currentPageWidthPoints;
    private float _currentPageHeightPoints;

    private readonly List<SignaturePlacement> _signaturePlacements = new();


    public MainWindow()
    {
        InitializeComponent();
        LoadFutureLogo();

        Closed += (_, _) =>
        {
            DisposeCertificates();
            _pdfDocument?.Dispose();
            _pdfDocument = null;

            _currentPageBitmap?.Dispose();
            _currentPageBitmap = null;

            _futureLogo?.Dispose();
            _futureLogo = null;
        };

        RefreshCertificates();
    }

    private void LoadFutureLogo()
    {
        try
        {
            var logoPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "logo-future.png");

            if (!File.Exists(logoPath))
            {
                StatusText.Text = "Logo Future não encontrada.";
                return;
            }

            _futureLogo = new Bitmap(logoPath);
            FutureLogo.Source = _futureLogo;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao carregar logo: {ex.Message}";
        }
    }

    private void OpenPromoLink_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://futuresolucoes.com.br/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível abrir o site: {ex.Message}";
        }
    }

    private async void OpenPdf_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Selecionar documento PDF",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new("Documentos PDF")
                        {
                            Patterns = new[] { "*.pdf", "*.PDF" }
                        }
                    }
                });

            if (files.Count == 0)
                return;

            string path = files[0].Path.LocalPath;

            StatusText.Text = "Abrindo e renderizando PDF...";

            var newDocument = new PdfiumDocument(path);
            WriteableBitmap? newBitmap = null;

            try
            {
                var renderedPage = newDocument.RenderPage(0);
                newBitmap = CreateBitmap(renderedPage);

                var oldDocument = _pdfDocument;
                var oldBitmap = _currentPageBitmap;

                _pdfDocument = newDocument;
                _currentPdfPath = path;
                _currentPageBitmap = newBitmap;
                _currentPageIndex = 0;
                _currentPageWidthPoints = renderedPage.PageWidthPoints;
                _currentPageHeightPoints = renderedPage.PageHeightPoints;

                PdfPageImage.Source = newBitmap;
                PdfPageImage.Width = renderedPage.Width;
                PdfPageImage.Height = renderedPage.Height;
                _signaturePlacements.Clear();
                PdfScrollViewer.IsVisible = true;
                ViewerPlaceholder.IsVisible = false;

                SelectedFileText.Text = files[0].Name;

                UpdatePageControls();
                UpdateActionButtons();

                oldDocument?.Dispose();
                oldBitmap?.Dispose();

                StatusText.Text =
                    $"PDF aberto com sucesso. {newDocument.PageCount} página(s).";
            }
            catch
            {
                newBitmap?.Dispose();
                newDocument.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível abrir o PDF: {ex.Message}";
        }
    }

    private static WriteableBitmap CreateBitmap(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(page.Width, page.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        try
        {
            using var framebuffer = bitmap.Lock();

            int bytesPerPixel = 4;
            int bytesPerRow = checked(page.Width * bytesPerPixel);

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

    private void ShowPage(int pageIndex)
    {
        if (_pdfDocument == null)
            return;

        if (pageIndex < 0 || pageIndex >= _pdfDocument.PageCount)
            return;

        try
        {
            StatusText.Text = "Renderizando página...";

            var renderedPage = _pdfDocument.RenderPage(pageIndex);
            var newBitmap = CreateBitmap(renderedPage);

            var oldBitmap = _currentPageBitmap;

            _currentPageBitmap = newBitmap;
            _currentPageIndex = pageIndex;
            _currentPageWidthPoints = renderedPage.PageWidthPoints;
            _currentPageHeightPoints = renderedPage.PageHeightPoints;

            PdfPageImage.Source = newBitmap;
            PdfPageImage.Width = renderedPage.Width;
            PdfPageImage.Height = renderedPage.Height;
            PdfScrollViewer.Offset = new Vector(0, 0);
            oldBitmap?.Dispose();

            UpdatePageControls();

            StatusText.Text =
                $"Página {pageIndex + 1} de {_pdfDocument.PageCount}.";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Erro ao renderizar página: {ex.Message}";
        }
    }

    private void PreviousPage_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowPage(_currentPageIndex - 1);
    }

    private void NextPage_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowPage(_currentPageIndex + 1);
    }

    private void UpdatePageControls()
    {
        if (_pdfDocument == null || _currentPageIndex < 0)
        {
            PageIndicator.Text = "Página 0 de 0";
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            return;
        }

        PageIndicator.Text =
            $"Página {_currentPageIndex + 1} de {_pdfDocument.PageCount}";

        PreviousPageButton.IsEnabled = _currentPageIndex > 0;

        NextPageButton.IsEnabled =
            _currentPageIndex < _pdfDocument.PageCount - 1;
    }

    private void RefreshCertificates_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        RefreshCertificates();
    }

    private void RefreshCertificates()
    {
        if (_loadingCertificates)
            return;

        _loadingCertificates = true;

        try
        {
            CertificatePicker.SelectedItem = null;
            CertificatePicker.ItemsSource = null;

            DisposeCertificates();

            var loaded = _certificateStore.LoadCertificates();
            _certificates.AddRange(loaded);

            CertificatePicker.ItemsSource = _certificates;

            int ready = 0;

            foreach (var item in _certificates)
            {
                if (item.CanAttemptSigning)
                    ready++;
            }

            StatusText.Text = _certificates.Count == 0
                ? "Nenhum certificado encontrado no repositório pessoal."
                : $"{_certificates.Count} certificado(s); " +
                  $"{ready} com chave privada acessível. " +
                  "Assinatura ainda pendente de integração.";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "Não foi possível consultar os certificados. " +
                $"Detalhe: {ex.Message}";
        }
        finally
        {
            _loadingCertificates = false;
            UpdateActionButtons();
        }
    }

    private void CertificatePicker_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingCertificates)
            return;

        UpdateActionButtons();

        if (CertificatePicker.SelectedItem is not CertificateInfo selected)
        {
            StatusText.Text = "Selecione um certificado digital.";
            return;
        }

        StatusText.Text = selected.Details;
    }


    private void UpdateActionButtons()
    {
        bool hasPdf = _pdfDocument != null && !_operationInProgress;
        bool hasCertificate =
            CertificatePicker.SelectedItem is CertificateInfo selected &&
            selected.CanAttemptSigning;

        SignButton.IsEnabled = hasPdf && hasCertificate;
        ValidateButton.IsEnabled = hasPdf;
    }

    private async void ValidatePdf_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_operationInProgress || string.IsNullOrWhiteSpace(_currentPdfPath))
            return;

        _operationInProgress = true;
        UpdateActionButtons();

        try
        {
            StatusText.Text = "Validando assinaturas digitais...";

            var options = new ValidationOptions
            {
                CheckRevocation = true,
                TrustSystemRoots = true
            };

            var validator = new PdfSignatureValidator(options);

            using var stream = File.OpenRead(_currentPdfPath);
            var results = await validator.ValidateAsync(stream);

            if (results.Count == 0)
            {
                StatusText.Text = "Nenhuma assinatura digital foi encontrada neste PDF.";
                return;
            }

            var report = new StringBuilder();
            report.AppendLine($"Assinaturas encontradas: {results.Count}");
            report.AppendLine();

            foreach (var result in results)
            {
                report.AppendLine($"Assinatura: {result.FieldName}");
                report.AppendLine($"Titular: {result.SignerName}");
                report.AppendLine($"Data: {result.SigningTime}");
                report.AppendLine($"Integridade: {(result.IsIntegrityValid ? "OK" : "FALHOU")}");
                report.AppendLine($"Assinatura criptográfica: {(result.IsSignatureValid ? "VÁLIDA" : "INVÁLIDA")}");
                report.AppendLine($"Cadeia de confiança: {(result.IsCertificateChainValid ? "CONFIÁVEL" : "NÃO CONFIRMADA")}");
                report.AppendLine($"Revogação: {(result.IsNotRevoked ? "Não revogado/não identificada revogação" : "Não confirmada")}");
                report.AppendLine($"Origem da consulta: {result.RevocationSource}");
                report.AppendLine($"Resultado geral: {(result.IsValid ? "VÁLIDA" : "NÃO VALIDADA")}");

                foreach (var error in result.Errors)
                    report.AppendLine($"Erro: {error}");

                foreach (var warning in result.Warnings)
                    report.AppendLine($"Aviso: {warning}");

                report.AppendLine();
            }

            StatusText.Text = report.ToString();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao validar o PDF: {ex.Message}";
        }
        finally
        {
            _operationInProgress = false;
            UpdateActionButtons();
        }
    }

    private async void SignPdf_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_operationInProgress || string.IsNullOrWhiteSpace(_currentPdfPath))
            return;

        if (CertificatePicker.SelectedItem is not CertificateInfo selected ||
            !selected.CanAttemptSigning)
        {
            StatusText.Text = "Selecione um certificado válido com chave privada acessível.";
            return;
        }

        if (_pdfDocument == null)
            return;

        var positioningWindow = new SignaturePositioningWindow(_pdfDocument);
        await positioningWindow.ShowDialog(this);

        _signaturePlacements.Clear();
        _signaturePlacements.AddRange(positioningWindow.SelectedPlacements);

        if (_signaturePlacements.Count == 0)
        {
            StatusText.Text = "Posicionamento cancelado ou nenhuma posição selecionada.";
            return;
        }

        var placements = _signaturePlacements.ToArray();
        var sourcePath = System.IO.Path.GetFullPath(_currentPdfPath);

        _operationInProgress = true;
        UpdateActionButtons();

        string? temporaryPath = null;

        try
        {
            StatusText.Text = "Escolha onde salvar o PDF assinado...";

            var saveFile = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Salvar PDF assinado",
                    SuggestedFileName =
                        $"{System.IO.Path.GetFileNameWithoutExtension(sourcePath)}-assinado.pdf",
                    DefaultExtension = "pdf",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("Documento PDF")
                        {
                            Patterns = new[] { "*.pdf" }
                        }
                    }
                });

            if (saveFile == null)
            {
                StatusText.Text = "Assinatura cancelada pelo usuário.";
                return;
            }

            var outputPath = System.IO.Path.GetFullPath(saveFile.Path.LocalPath);

            if (string.Equals(
                sourcePath,
                outputPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                StatusText.Text = "Escolha outro arquivo. O PDF original será preservado.";
                return;
            }

            using var signingCertificate =
                new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    selected.Certificate);

            var signerName = signingCertificate.GetNameInfo(
                System.Security.Cryptography.X509Certificates.X509NameType.SimpleName,
                false);

            var reason = SigningReasonTextBox.Text?.Trim();

            byte[] signedBytes = await File.ReadAllBytesAsync(sourcePath);

            for (int i = 0; i < placements.Length; i++)
            {
                var placement = placements[i];

                if (placement.PageWidthPoints <= 0 ||
                    placement.PageHeightPoints <= 0 ||
                    placement.NormalizedX < 0 ||
                    placement.NormalizedX > 1 ||
                    placement.NormalizedY < 0 ||
                    placement.NormalizedY > 1)
                {
                    throw new InvalidOperationException(
                        $"A posição {i + 1} contém coordenadas inválidas.");
                }

                float pdfX = (float)(
                    placement.NormalizedX * placement.PageWidthPoints);

                float pdfY = (float)(
                    (1.0 - placement.NormalizedY) * placement.PageHeightPoints);

                StatusText.Text =
                    $"Aplicando assinatura {i + 1} de {placements.Length}...";

                signedBytes = await PadesSigner
                    .Document(signedBytes)
                    .WithCertificate(signingCertificate)
                    .WithMetadata(new SignatureMetadata
                    {
                        SignerName = signerName,
                        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
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

            var outputDirectory = System.IO.Path.GetDirectoryName(outputPath)
                ?? throw new InvalidOperationException(
                    "Não foi possível identificar a pasta de destino.");

            temporaryPath = System.IO.Path.Combine(
                outputDirectory,
                $".assinapdf-{Guid.NewGuid():N}.tmp");

            await File.WriteAllBytesAsync(temporaryPath, signedBytes);

            if (!File.Exists(temporaryPath) ||
                new FileInfo(temporaryPath).Length == 0)
            {
                throw new IOException("O arquivo temporário de assinatura está vazio.");
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
            temporaryPath = null;

            StatusText.Text =
                $"PDF assinado com sucesso! {placements.Length} assinatura(s) aplicada(s).\n" +
                $"Arquivo: {outputPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Não foi possível concluir a assinatura.\nDetalhe: {ex.Message}";
        }
        finally
        {
            if (temporaryPath != null)
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // Não substituir o erro original por falha de limpeza.
                }
            }

            _operationInProgress = false;
            UpdateActionButtons();
        }
    }
    private void DisposeCertificates()
    {
        foreach (var item in _certificates)
            item.Dispose();

        _certificates.Clear();
    }
}
