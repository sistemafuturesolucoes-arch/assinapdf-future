
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
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AssinaPDF.MacPrototype;

public partial class MainWindow : Window
{
    private readonly CertificateStoreService _certificateStore = new();
    private readonly List<CertificateInfo> _certificates = new();

    private Bitmap? _futureLogo;
    private PdfiumDocument? _pdfDocument;
    private readonly List<WriteableBitmap> _pageBitmaps = new();
    private readonly List<Image> _pageImages = new();
    private readonly List<Image> _thumbnailImages = new();
    private readonly List<Border> _pageCards = new();
    private readonly List<Canvas> _signatureOverlays = new();
    private IReadOnlyList<SignatureWidgetInfo> _signatureWidgets = Array.Empty<SignatureWidgetInfo>();
    private string? _currentPdfPath;

    private bool _loadingCertificates;
    private bool _operationInProgress;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panStartOffset;
    private double _zoom = 1.0;
    private float _renderScale = 1.5f;

    private readonly List<SignaturePlacement> _signaturePlacements = new();


    public MainWindow()
    {
        InitializeComponent();
        LoadFutureLogo();
        PdfScrollViewer.SizeChanged += (_, _) => ApplyZoom();
        PdfScrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            PdfScrollViewer_PointerWheelChanged,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += MainWindow_KeyDown;

        Closed += (_, _) =>
        {
            DisposeCertificates();
            _pdfDocument?.Dispose();
            _pdfDocument = null;

            foreach (var bitmap in _pageBitmaps)
                bitmap.Dispose();
            _pageBitmaps.Clear();

            _futureLogo?.Dispose();
            _futureLogo = null;
        };

        DashboardView.IsVisible = true;
        WorkspaceView.IsVisible = false;
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
            StatusText.Text = "Abrindo e renderizando todas as páginas do PDF...";

            var newDocument = new PdfiumDocument(path);
            var newBitmaps = new List<WriteableBitmap>();

            try
            {
                for (int i = 0; i < newDocument.PageCount; i++)
                {
                    var renderedPage = newDocument.RenderPage(i, 1.5f);
                    newBitmaps.Add(CreateBitmap(renderedPage));
                }

                var oldDocument = _pdfDocument;
                var oldBitmaps = _pageBitmaps.ToArray();
                try
                {
                    _signatureWidgets = PdfSignatureWidgetReader.Read(path);
                }
                catch (Exception signatureReadError)
                {
                    _signatureWidgets = Array.Empty<SignatureWidgetInfo>();
                    Debug.WriteLine($"Não foi possível ler os campos visuais de assinatura: {signatureReadError}");
                }

                _pdfDocument = newDocument;
                _renderScale = 1.5f;
                _currentPdfPath = path;
                _signaturePlacements.Clear();

                _pageBitmaps.Clear();
                _pageBitmaps.AddRange(newBitmaps);
                BuildHomePageViewer();

                DashboardView.IsVisible = false;
                WorkspaceView.IsVisible = true;
                PdfScrollViewer.IsVisible = true;
                ViewerPlaceholder.IsVisible = false;
                SelectedFileText.Text = files[0].Name;

                oldDocument?.Dispose();
                foreach (var bitmap in oldBitmaps)
                    bitmap.Dispose();

                _zoom = 1.0;
                ApplyZoom();
                UpdateActionButtons();

                StatusText.Text =
                    $"PDF aberto com sucesso. {newDocument.PageCount} página(s) disponíveis na visualização contínua.";
            }
            catch
            {
                foreach (var bitmap in newBitmaps)
                    bitmap.Dispose();
                newDocument.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível abrir o PDF: {ex.Message}";
        }
    }

    private void GoHome_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        DashboardView.IsVisible = true;
        WorkspaceView.IsVisible = false;
        DashboardStatusText.Text = string.IsNullOrWhiteSpace(_currentPdfPath)
            ? "Pronto para começar."
            : $"Último documento aberto: {System.IO.Path.GetFileName(_currentPdfPath)}";
    }

    private void DashboardOpen_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenPdf_Click(sender, e);
    }

    private void DashboardSign_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        // A navegação para assinatura apenas abre a área de trabalho.
        // O usuário escolhe o documento ao clicar em "Abrir PDF".
        DashboardView.IsVisible = false;
        WorkspaceView.IsVisible = true;
        DashboardStatusText.Text = "Área de assinatura aberta.";
        StatusText.Text = _pdfDocument == null
            ? "Clique em Abrir PDF para selecionar o documento que deseja assinar."
            : $"Documento carregado: {System.IO.Path.GetFileName(_currentPdfPath)}";
        UpdateActionButtons();
    }

    private void DashboardValidate_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        // A validação tem sua própria entrada na área de trabalho.
        // O usuário escolhe o PDF pelo botão Abrir PDF e inicia a análise
        // somente quando clicar em Validar assinatura.
        DashboardView.IsVisible = false;
        WorkspaceView.IsVisible = true;
        DashboardStatusText.Text = "Área de validação aberta.";
        StatusText.Text = _pdfDocument == null
            ? "Clique em Abrir PDF para selecionar o documento que deseja validar."
            : $"Documento carregado: {System.IO.Path.GetFileName(_currentPdfPath)}. Clique em Validar assinatura para analisar.";
        UpdateActionButtons();
    }

    private void DashboardEdit_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        DashboardStatusText.Text = "As ferramentas de edição serão disponibilizadas em uma próxima etapa.";
    }

    private void DashboardOrganize_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        DashboardStatusText.Text = "As ferramentas de organização serão disponibilizadas em uma próxima etapa.";
    }

    private void DashboardConvert_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        DashboardStatusText.Text = "O conversor de documentos será disponibilizado em uma próxima etapa.";
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

    private void BuildHomePageViewer()
    {
        PdfPagesPanel.Children.Clear();
        HomeThumbnailsPanel.Children.Clear();
        _pageImages.Clear();
        _thumbnailImages.Clear();
        _pageCards.Clear();

        for (int i = 0; i < _pageBitmaps.Count; i++)
        {
            int pageIndex = i;
            var bitmap = _pageBitmaps[i];

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false
            };

            var pageLabel = new TextBlock
            {
                Text = $"Página {pageIndex + 1}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(70, 70, 80)),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var pageContent = new StackPanel { Spacing = 4 };
            pageContent.Children.Add(pageLabel);
            pageContent.Children.Add(image);

            var pageCard = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(195, 198, 205)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Child = pageContent
            };

            PdfPagesPanel.Children.Add(pageCard);
            _pageImages.Add(image);
            _pageCards.Add(pageCard);

            var thumbnailImage = new Image
            {
                Source = bitmap,
                Width = 112,
                Height = 138,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false
            };

            _thumbnailImages.Add(thumbnailImage);

            var thumbnailContent = new StackPanel { Spacing = 4 };
            thumbnailContent.Children.Add(thumbnailImage);
            thumbnailContent.Children.Add(new TextBlock
            {
                Text = $"Página {pageIndex + 1}",
                FontSize = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(65, 65, 75))
            });

            var thumbnailButton = new Button
            {
                Content = thumbnailContent,
                Padding = new Thickness(5),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(205, 205, 215)),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Tag = pageIndex
            };

            thumbnailButton.Click += (_, _) => pageCard.BringIntoView();

            var contextMenu = new ContextMenu();
            var exportItem = new MenuItem { Header = "Exportar esta página..." };
            exportItem.Click += async (_, _) => await ExportPageAsync(pageIndex);
            contextMenu.Items.Add(exportItem);
            thumbnailButton.ContextMenu = contextMenu;

            HomeThumbnailsPanel.Children.Add(thumbnailButton);
        }
    }

    private void ApplyZoom()
    {
        if (_pageImages.Count == 0 || PdfScrollViewer.Bounds.Width <= 0 ||
            PdfScrollViewer.Bounds.Height <= 0)
            return;

        double maxWidth = _pageBitmaps.Max(bitmap => bitmap.PixelSize.Width);
        double maxHeight = _pageBitmaps.Max(bitmap => bitmap.PixelSize.Height);
        double availableWidth = Math.Max(120, PdfScrollViewer.Bounds.Width - 90);
        double availableHeight = Math.Max(120, PdfScrollViewer.Bounds.Height - 90);
        double fitScale = Math.Min(availableWidth / maxWidth, availableHeight / maxHeight);
        double scale = fitScale * _zoom;

        for (int i = 0; i < _pageImages.Count; i++)
        {
            var size = _pageBitmaps[i].PixelSize;
            _pageImages[i].Width = size.Width * scale;
            _pageImages[i].Height = size.Height * scale;
            _pageCards[i].Width = size.Width * scale + 30;
        }

        PdfScrollViewer.Cursor = new Cursor(
            _zoom > 1.001 ? StandardCursorType.Hand : StandardCursorType.Arrow);
    }

    private void ChangeZoom(double factor)
    {
        double nextZoom = Math.Clamp(_zoom * factor, 0.5, 5.0);
        if (Math.Abs(nextZoom - _zoom) < 0.0001)
            return;

        _zoom = nextZoom;
        RerenderPagesForZoom();
        ApplyZoom();
    }

    private void RerenderPagesForZoom()
    {
        if (_pdfDocument == null || _pageBitmaps.Count == 0)
            return;

        // A home usa o PDFium para rasterizar. Aumentamos a resolução real
        // conforme o zoom para não ampliar indefinidamente a imagem inicial.
        float requestedScale = 1.5f * (float)Math.Max(1.0, _zoom);
        if (requestedScale <= _renderScale * 1.20f)
            return;

        var replacements = new List<WriteableBitmap>(_pageBitmaps.Count);
        try
        {
            for (int i = 0; i < _pageBitmaps.Count; i++)
            {
                var rendered = _pdfDocument.RenderPage(i, requestedScale);
                replacements.Add(CreateBitmap(rendered));
            }

            for (int i = 0; i < replacements.Count; i++)
            {
                var previous = _pageBitmaps[i];
                _pageBitmaps[i] = replacements[i];
                _pageImages[i].Source = replacements[i];
                _thumbnailImages[i].Source = replacements[i];
                previous.Dispose();
            }

            _renderScale = requestedScale;
        }
        catch
        {
            foreach (var bitmap in replacements)
                bitmap.Dispose();
            throw;
        }
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
            return;

        if (e.Key == Key.Add || e.Key == Key.OemPlus)
        {
            ChangeZoom(1.15);
            e.Handled = true;
        }
        else if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
        {
            ChangeZoom(1.0 / 1.15);
            e.Handled = true;
        }
    }

    private void PdfScrollViewer_PointerWheelChanged(
        object? sender,
        PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
            return;

        if (e.Delta.Y == 0)
            return;

        ChangeZoom(e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15);
        e.Handled = true;
    }

    private void PdfScrollViewer_PointerPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (_zoom <= 1.001 ||
            !e.GetCurrentPoint(PdfScrollViewer).Properties.IsLeftButtonPressed)
            return;

        _isPanning = true;
        _panStart = e.GetPosition(PdfScrollViewer);
        _panStartOffset = PdfScrollViewer.Offset;
        PdfScrollViewer.Cursor = new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(PdfScrollViewer);
        e.Handled = true;
    }

    private void PdfScrollViewer_PointerMoved(
        object? sender,
        PointerEventArgs e)
    {
        if (!_isPanning)
            return;

        var current = e.GetPosition(PdfScrollViewer);
        var delta = current - _panStart;
        PdfScrollViewer.Offset = new Vector(
            Math.Max(0, _panStartOffset.X - delta.X),
            Math.Max(0, _panStartOffset.Y - delta.Y));
        e.Handled = true;
    }

    private void PdfScrollViewer_PointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        if (!_isPanning)
            return;

        _isPanning = false;
        e.Pointer.Capture(null);
        PdfScrollViewer.Cursor = new Cursor(StandardCursorType.Hand);
        e.Handled = true;
    }

    private async Task ExportPageAsync(int pageIndex)
    {
        if (string.IsNullOrWhiteSpace(_currentPdfPath) ||
            _pdfDocument == null ||
            pageIndex < 0 ||
            pageIndex >= _pdfDocument.PageCount)
            return;

        try
        {
            var saveFile = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Exportar página como PDF",
                    SuggestedFileName = $"pagina-{pageIndex + 1}.pdf",
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
                return;

            string destinationPath = System.IO.Path.GetFullPath(saveFile.Path.LocalPath);
            string sourcePath = System.IO.Path.GetFullPath(_currentPdfPath);
            if (string.Equals(
                sourcePath,
                destinationPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                StatusText.Text = "Escolha outro nome para preservar o PDF original.";
                return;
            }

            using var source = PdfReader.Open(
                sourcePath,
                PdfDocumentOpenMode.Import);
            using var output = new PdfSharp.Pdf.PdfDocument();
            output.AddPage(source.Pages[pageIndex]);
            output.Save(destinationPath);

            StatusText.Text = $"Página {pageIndex + 1} exportada: {destinationPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível exportar a página: {ex.Message}";
        }
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

            // Mantém a mesma configuração de validação criptográfica usada
            // pelo projeto Windows anterior. A consulta de revogação e a
            // confiança nas raízes do sistema ficam separadas desta etapa.
            var options = new ValidationOptions
            {
                CheckRevocation = false,
                TrustSystemRoots = false
            };

            var validator = new PdfSignatureValidator(options);

            await using var stream = new FileStream(
                _currentPdfPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 81920,
                useAsync: true);
            var results = await validator.ValidateAsync(stream);

            if (results.Count == 0)
            {
                StatusText.Text = "Nenhuma assinatura digital reconhecida neste PDF.";
                await ShowValidationReportAsync(
                    "Validação de assinatura",
                    "Nenhuma assinatura digital compatível foi encontrada neste documento.\n\nIsso não significa necessariamente que o PDF seja inválido; ele pode simplesmente não estar assinado.");
                return;
            }

            var report = new StringBuilder();
            report.AppendLine($"RESULTADO DA VALIDAÇÃO — {results.Count} assinatura(s) encontrada(s)");
            report.AppendLine(new string('=', 68));
            report.AppendLine();

            for (int i = 0; i < results.Count; i++)
            {
                var result = results[i];
                report.AppendLine($"ASSINATURA {i + 1}");
                report.AppendLine(new string('-', 68));
                report.AppendLine($"Campo: {result.FieldName}");
                report.AppendLine($"Titular: {result.SignerName}");
                report.AppendLine($"Data da assinatura: {result.SigningTime}");
                report.AppendLine($"Integridade do documento: {(result.IsIntegrityValid ? "VÁLIDA" : "INVÁLIDA / NÃO CONFIRMADA")}");
                report.AppendLine($"Assinatura criptográfica: {(result.IsSignatureValid ? "VÁLIDA" : "INVÁLIDA / NÃO CONFIRMADA")}");
                report.AppendLine($"Cadeia de confiança: {(result.IsCertificateChainValid ? "CONFIÁVEL" : "NÃO CONFIRMADA NESTA ETAPA")}");
                report.AppendLine($"Revogação: {(result.IsNotRevoked ? "Nenhuma revogação reportada" : "Não confirmada")}");
                report.AppendLine($"Fonte de revogação: {result.RevocationSource}");
                report.AppendLine($"Resultado informado pela biblioteca: {(result.IsValid ? "VÁLIDA" : "NÃO VALIDADA")}");

                if (result.Errors.Count > 0)
                {
                    report.AppendLine();
                    report.AppendLine("ERROS:");
                    foreach (var error in result.Errors)
                        report.AppendLine($" • {error}");
                }

                if (result.Warnings.Count > 0)
                {
                    report.AppendLine();
                    report.AppendLine("AVISOS:");
                    foreach (var warning in result.Warnings)
                        report.AppendLine($" • {warning}");
                }

                report.AppendLine();
            }

            report.AppendLine(new string('=', 68));
            report.AppendLine("Limites desta etapa: cadeia de confiança, revogação e carimbo do tempo não são atestados por esta verificação criptográfica. Para uma conclusão oficial ICP-Brasil, utilize também o serviço oficial de validação.");
            StatusText.Text = $"Validação concluída: {results.Count} assinatura(s) encontrada(s).";
            await ShowValidationReportAsync("Detalhes técnicos das assinaturas", report.ToString());
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

    private async Task ShowValidationReportAsync(string title, string report)
    {
        var reportText = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(16),
        };

        var closeButton = new Button
        {
            Content = "Fechar",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 100,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var dialog = new Window
        {
            Title = title,
            Width = 820,
            Height = 650,
            MinWidth = 620,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Content = new Grid
            {
                Margin = new Thickness(18),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 19,
                        FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(0, 0, 0, 12)
                    },
                    reportText,
                    closeButton
                }
            }
        };

        var contentGrid = (Grid)dialog.Content!;
        Grid.SetRow(reportText, 1);
        Grid.SetRow(closeButton, 2);

        closeButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
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
                    placement.NormalizedY > 1 ||
                    placement.NormalizedWidth <= 0 ||
                    placement.NormalizedHeight <= 0 ||
                    placement.NormalizedX + placement.NormalizedWidth > 1.000001 ||
                    placement.NormalizedY + placement.NormalizedHeight > 1.000001)
                {
                    throw new InvalidOperationException(
                        $"A posição {i + 1} contém coordenadas inválidas.");
                }

                float pdfX = (float)(
                    placement.NormalizedX * placement.PageWidthPoints);

                float pdfY = (float)(
                    (1.0 - placement.NormalizedY - placement.NormalizedHeight) *
                    placement.PageHeightPoints);

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
