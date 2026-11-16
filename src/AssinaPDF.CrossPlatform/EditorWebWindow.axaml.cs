using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public partial class EditorWebWindow : Window
{
    private readonly EditorHistoryService _history = new();
    private EditorWebServer? _server;
    private string? _workingCopyPath;
    private string? _historyId;
    private bool _initialPdfSent;
    private readonly bool _startBlankDocument;

    public EditorWebWindow() : this(null, null, true)
    {
    }

    public EditorWebWindow(string? workingCopyPath, string? historyId, bool startBlankDocument = false)
    {
        InitializeComponent();

        // O WebView2 precisa de uma pasta de dados gravável pelo usuário.
        // Nunca usar AppContext.BaseDirectory, pois o aplicativo pode estar em Program Files.
        if (OperatingSystem.IsWindows())
        {
            var webViewData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FutureSolucoes",
                "AssinaPDF",
                "WebView2");

            Directory.CreateDirectory(webViewData);

            EditorWebView.EnvironmentRequested += (_, args) =>
            {
                if (args is WindowsWebView2EnvironmentRequestedEventArgs webView2)
                    webView2.UserDataFolder = webViewData;
            };
        }

        _workingCopyPath = workingCopyPath;
        _historyId = historyId;
        _startBlankDocument = startBlankDocument;
        Opened += async (_, _) => await StartEditorAsync();
        Closed += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_historyId))
                _history.Touch(_historyId);
            if (_server is not null)
            {
                await _server.DisposeAsync();
                _server = null;
            }
        };
    }

    private async System.Threading.Tasks.Task StartEditorAsync()
    {
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "EditorWeb", "dist");
            _server = new EditorWebServer(root);
            _server.Start();
            EditorWebView.Navigate(new Uri(_server.BaseUri, "index.html"));
            LoadingOverlay.IsVisible = false;
            LoadingTitle.Text = "Carregando editor Future…";
            LoadingDetails.Text = "Os arquivos são servidos somente pelo endereço local deste aplicativo.";
        }
        catch (Exception ex)
        {
            LoadingTitle.Text = "Editor web ainda não compilado";
            LoadingDetails.Text = ex.Message + Environment.NewLine +
                "Abra a pasta src/AssinaPDF.CrossPlatform/EditorWeb, execute npm install e npm run build e inicie o AssinaPDF novamente.";
        }
    }

    private void EditorWebView_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingOverlay.IsVisible = false;
            return;
        }

        LoadingOverlay.IsVisible = true;
        LoadingTitle.Text = "Não foi possível carregar o editor";
        LoadingDetails.Text = "Verifique se os arquivos da pasta EditorWeb/dist foram incluídos na distribuição.";
    }

    private async void EditorWebView_WebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.Body))
                return;

            using var message = JsonDocument.Parse(e.Body);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return;

            var messageType = type.GetString();
            if (messageType == "editorReady")
            {
                if (!_initialPdfSent && !string.IsNullOrWhiteSpace(_workingCopyPath) && File.Exists(_workingCopyPath))
                {
                    _initialPdfSent = true;
                    await SendPdfToWebAsync(_workingCopyPath);
                }
                else if (_startBlankDocument)
                {
                    _initialPdfSent = true;
                    await EditorWebView.InvokeScript("window.startBlankDocument && window.startBlankDocument()");
                }
                return;
            }

            if (messageType == "openPdf")
            {
                await OpenPdfAsync();
                return;
            }


            if (messageType == "signPdf")
            {
                if (!root.TryGetProperty("base64", out var signPayload) ||
                    signPayload.ValueKind != JsonValueKind.String)
                    return;
                var signBase64 = signPayload.GetString();
                if (string.IsNullOrWhiteSpace(signBase64))
                    return;
                var signBytes = Convert.FromBase64String(signBase64);
                if (signBytes.Length == 0 || signBytes.Length > 150 * 1024 * 1024)
                    throw new InvalidDataException("O PDF para assinatura está vazio ou excede 150 MB.");
                var signName = root.TryGetProperty("fileName", out var signFileName)
                    ? Path.GetFileName(signFileName.GetString() ?? "documento-editado.pdf")
                    : "documento-editado.pdf";
                await SignPdfFromEditorAsync(signBytes, signName);
                return;
            }

            if (messageType != "savePdf" ||
                !root.TryGetProperty("base64", out var payload) ||
                payload.ValueKind != JsonValueKind.String)
                return;

            var base64 = payload.GetString();
            if (string.IsNullOrWhiteSpace(base64))
                return;

            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0 || bytes.Length > 150 * 1024 * 1024)
                throw new InvalidDataException("O PDF exportado está vazio ou excede o limite de 150 MB.");

            var suggestedName = root.TryGetProperty("fileName", out var fileName)
                ? Path.GetFileName(fileName.GetString() ?? "documento-editado.pdf")
                : "documento-editado.pdf";

            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Salvar PDF editado",
                SuggestedFileName = suggestedName,
                DefaultExtension = "pdf",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } }
                }
            });
            if (target is null)
                return;

            var targetPath = target.Path.LocalPath;
            await using (var output = await target.OpenWriteAsync())
                await output.WriteAsync(bytes);

            if (string.IsNullOrWhiteSpace(_workingCopyPath))
            {
                var entry = _history.CreateWorkingCopy(targetPath);
                _workingCopyPath = entry.WorkingCopyPath;
                _historyId = entry.Id;
            }
            else
            {
                await File.WriteAllBytesAsync(_workingCopyPath, bytes);
            }

            if (!string.IsNullOrWhiteSpace(_historyId))
                _history.Touch(_historyId);
        }
        catch (Exception ex)
        {
            LoadingTitle.Text = "Falha ao processar o documento";
            LoadingDetails.Text = ex.Message;
            LoadingOverlay.IsVisible = true;
        }
    }


    private async System.Threading.Tasks.Task SignPdfFromEditorAsync(byte[] pdfBytes, string suggestedName)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "assinapdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        var certificates = new List<CertificateInfo>();
        try
        {
            await File.WriteAllBytesAsync(tempPath, pdfBytes);
            IReadOnlyList<SignaturePlacement> placements;
            using (var pdfium = new PdfiumDocument(tempPath))
            {
                var positionWindow = new SignaturePositioningWindow(pdfium);
                await positionWindow.ShowDialog(this);
                placements = positionWindow.SelectedPlacements;
            }
            if (placements.Count == 0)
                return;

            certificates.AddRange(new CertificateStoreService().LoadCertificates());
            var available = certificates.Where(certificate => certificate.CanAttemptSigning).ToList();
            if (available.Count == 0)
                throw new InvalidOperationException("Nenhum certificado válido com chave privada acessível foi encontrado.");

            var picker = new Window
            {
                Title = "Escolher certificado digital",
                Width = 520,
                Height = 390,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(248, 249, 251))
            };
            var list = new ListBox { ItemsSource = available, SelectedIndex = 0, MinHeight = 120 };
            var reason = new TextBox { Text = "Assinatura digital do documento", MinHeight = 36 };
            var details = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 75 };
            void RefreshDetails()
            {
                details.Text = (list.SelectedItem as CertificateInfo)?.Details ?? "Selecione um certificado.";
            }
            list.SelectionChanged += (_, _) => RefreshDetails();
            RefreshDetails();

            var cancel = new Button { Content = "Cancelar", Padding = new Avalonia.Thickness(14, 8) };
            var sign = new Button
            {
                Content = "Assinar documento",
                Padding = new Avalonia.Thickness(14, 8),
                Background = new SolidColorBrush(Color.FromRgb(109, 75, 195)),
                Foreground = Brushes.White
            };
            cancel.Click += (_, _) => picker.Close();
            sign.Click += (_, _) =>
            {
                if (list.SelectedItem is CertificateInfo)
                {
                    picker.Tag = true;
                    picker.Close();
                }
            };
            picker.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Selecione o certificado", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = placements.Count + " campo(s) de assinatura selecionado(s). A chave privada permanece neste computador.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    list,
                    details,
                    new TextBlock { Text = "Motivo da assinatura" },
                    reason,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, sign }
                    }
                }
            };
            await picker.ShowDialog(this);
            if (picker.Tag is not true || list.SelectedItem is not CertificateInfo selected)
                return;

            var servicePlacements = new List<AssinaPDF.MacPrototype.Services.SignaturePlacement>();
            foreach (var placement in placements)
            {
                var point = placement.GetPdfOrigin();
                servicePlacements.Add(new AssinaPDF.MacPrototype.Services.SignaturePlacement(
                    placement.PageIndex + 1, (float)point.X, (float)point.Y));
            }

            string signatureReason = string.IsNullOrWhiteSpace(reason.Text)
                ? "Assinatura digital do documento"
                : reason.Text.Trim();
            var signer = new PdfSigningService();
            byte[] signed = await signer.SignAsync(pdfBytes, selected.Certificate, signatureReason, servicePlacements);
            if (signed.Length == 0)
                throw new InvalidDataException("O PDF assinado retornou vazio.");

            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Salvar PDF assinado",
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) + "-assinado.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = new[] { new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } } }
            });
            if (destination is null)
                return;
            await using (var output = await destination.OpenWriteAsync())
                await output.WriteAsync(signed);

            if (string.IsNullOrWhiteSpace(_workingCopyPath))
            {
                var entry = _history.CreateWorkingCopy(destination.Path.LocalPath);
                _workingCopyPath = entry.WorkingCopyPath;
                _historyId = entry.Id;
            }
            else
            {
                await File.WriteAllBytesAsync(_workingCopyPath, signed);
            }
            if (!string.IsNullOrWhiteSpace(_historyId))
                _history.Touch(_historyId);
            await SendPdfToWebAsync(_workingCopyPath);
            // O editor recarrega o PDF assinado; não mantenha a camada de carregamento sobre a tela.
        }
        finally
        {
            foreach (var certificate in certificates)
                certificate.Dispose();
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { }
        }
    }

    private async System.Threading.Tasks.Task SendPdfToWebAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var base64 = Convert.ToBase64String(bytes);
        var fileName = Path.GetFileName(path);
        await EditorWebView.InvokeScript(
            $"window.receivePdfFromHost && window.receivePdfFromHost({JsonSerializer.Serialize(base64)}, {JsonSerializer.Serialize(fileName)})");
    }

    private async System.Threading.Tasks.Task OpenPdfAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Selecionar documento PDF",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Documentos PDF") { Patterns = new[] { "*.pdf", "*.PDF" } }
            }
        });
        if (files.Count == 0)
            return;

        var entry = _history.CreateWorkingCopy(files[0].Path.LocalPath);
        _workingCopyPath = entry.WorkingCopyPath;
        _historyId = entry.Id;
        await SendPdfToWebAsync(_workingCopyPath);
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
