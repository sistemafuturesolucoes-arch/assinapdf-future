using System;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AssinaPDF.MacPrototype;

public partial class EditorWebWindow : Window
{
    private EditorWebServer? _server;
    private readonly string? _workingCopyPath;
    private bool _initialPdfSent;

    public EditorWebWindow(string? workingCopyPath = null)
    {
        InitializeComponent();
        _workingCopyPath = workingCopyPath;
        Opened += async (_, _) => await StartEditorAsync();
        Closed += async (_, _) =>
        {
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

    private async void EditorWebView_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            LoadingTitle.Text = "Não foi possível carregar o editor";
            LoadingDetails.Text = "Verifique se os arquivos da pasta EditorWeb/dist foram incluídos na distribuição.";
            return;
        }

        LoadingOverlay.IsVisible = false;
        if (_initialPdfSent || string.IsNullOrWhiteSpace(_workingCopyPath) || !File.Exists(_workingCopyPath))
            return;

        _initialPdfSent = true;
        var bytes = await File.ReadAllBytesAsync(_workingCopyPath);
        var base64 = Convert.ToBase64String(bytes);
        var fileName = Path.GetFileName(_workingCopyPath);
        await EditorWebView.InvokeScript(
            $"window.receivePdfFromHost && window.receivePdfFromHost({JsonSerializer.Serialize(base64)}, {JsonSerializer.Serialize(fileName)})");
    }

    private async void EditorWebView_WebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.Body);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "savePdf")
                return;
            if (!root.TryGetProperty("base64", out var payload) || payload.ValueKind != JsonValueKind.String)
                return;

            var base64 = payload.GetString();
            if (string.IsNullOrWhiteSpace(base64))
                return;

            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0 || bytes.Length > 150 * 1024 * 1024)
                throw new InvalidDataException("O PDF exportado está vazio ou excede o limite de 150 MB.");

            if (!string.IsNullOrWhiteSpace(_workingCopyPath))
            {
                var directory = Path.GetDirectoryName(_workingCopyPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(_workingCopyPath, bytes);
                return;
            }

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

            await using var output = await target.OpenWriteAsync();
            await output.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            LoadingTitle.Text = "Falha ao salvar o PDF";
            LoadingDetails.Text = ex.Message;
            LoadingOverlay.IsVisible = true;
        }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
