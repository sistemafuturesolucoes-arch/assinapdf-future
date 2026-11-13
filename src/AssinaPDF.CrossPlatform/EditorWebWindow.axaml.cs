using System;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AssinaPDF.MacPrototype;

public partial class EditorWebWindow : Window
{
    private EditorWebServer? _server;

    public EditorWebWindow()
    {
        InitializeComponent();
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

    private void EditorWebView_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
            LoadingOverlay.IsVisible = false;
        else
        {
            LoadingTitle.Text = "Não foi possível carregar o editor";
            LoadingDetails.Text = "Verifique se os arquivos da pasta EditorWeb/dist foram incluídos na distribuição.";
        }
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

            var suggestedName = root.TryGetProperty("fileName", out var fileName)
                ? Path.GetFileName(fileName.GetString() ?? "documento-editado.pdf")
                : "documento-editado.pdf";

            var bytes = Convert.FromBase64String(base64);\n            if (!string.IsNullOrWhiteSpace(_workingCopyPath))\n            {\n                var directory = Path.GetDirectoryName(_workingCopyPath);\n                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);\n                await File.WriteAllBytesAsync(_workingCopyPath, bytes);\n                return;\n            }\n\n            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Salvar PDF editado",
                SuggestedFileName = suggestedName,
                DefaultExtension = "pdf",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } }
                }
            });
            if (target is null) return;

            var bytes = Convert.FromBase64String(base64);
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
