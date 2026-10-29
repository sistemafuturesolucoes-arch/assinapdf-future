using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FutureSolucoes.AssinaPDF;

internal sealed record SignaturePlacementPoint(int Page, float X, float Y);

internal sealed class SignaturePlacementDialog : Form
{
    private readonly string _pdfPath;
    private string? _viewerTempFolder;
    private readonly WebView2 _viewer;
    private readonly Label _selectionStatus;
    private readonly Button _continueButton;
    private readonly TextBox _reason;
    private readonly List<SignaturePlacementPoint> _placements = new();

    public IReadOnlyList<SignaturePlacementPoint> Placements => _placements.AsReadOnly();
    public string SigningReason => _reason.Text.Trim();

    public SignaturePlacementDialog(string pdfPath)
    {
        _pdfPath = Path.GetFullPath(pdfPath);
        Text = "Selecione os locais da assinatura";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 560);
        Size = new Size(1000, 690);
        MaximizeBox = false;
        BackColor = Color.FromArgb(242, 243, 245);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var header = new Panel { Dock = DockStyle.Top, Height = 94, Padding = new Padding(20, 12, 20, 10), BackColor = Color.FromArgb(224, 227, 231) };
        header.Controls.Add(new Label { Text = "Selecione o local para assinar", Dock = DockStyle.Top, Height = 36, Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold), ForeColor = Color.FromArgb(45, 49, 56) });
        header.Controls.Add(new Label { Text = "Clique no PDF para adicionar uma marca de assinatura. Você pode selecionar mais de um local, inclusive em páginas diferentes.", Dock = DockStyle.Top, Height = 32, ForeColor = Color.FromArgb(65, 69, 76) });

        _viewer = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(245, 246, 248) };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 118, Padding = new Padding(18, 8, 18, 8), BackColor = Color.FromArgb(224, 227, 231) };
        _selectionStatus = new Label { Dock = DockStyle.Top, Height = 25, Text = "Carregando documento...", ForeColor = Color.FromArgb(65, 69, 76) };
        var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = bottom.BackColor };
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        _reason = new TextBox { Dock = DockStyle.Fill, Text = "Assinatura digital", MaxLength = 200, BackColor = Color.White, ForeColor = Color.FromArgb(45, 49, 56), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 7, 10, 4) };
        var clear = CreateButton("Limpar locais");
        clear.Click += async (_, _) => { _placements.Clear(); UpdateSelectionStatus(); if (_viewer.CoreWebView2 is not null) await _viewer.CoreWebView2.ExecuteScriptAsync("clearSelections()"); };
        _continueButton = CreateButton("Continuar");
        _continueButton.Enabled = false;
        _continueButton.DialogResult = DialogResult.OK;
        var cancel = CreateButton("Cancelar");
        cancel.DialogResult = DialogResult.Cancel;
        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttonPanel.Controls.Add(_continueButton);
        buttonPanel.Controls.Add(cancel);
        controls.Controls.Add(_reason, 0, 0);
        controls.Controls.Add(clear, 1, 0);
        controls.Controls.Add(buttonPanel, 2, 0);
        bottom.Controls.Add(controls);
        bottom.Controls.Add(_selectionStatus);

        Controls.Add(_viewer);
        Controls.Add(bottom);
        Controls.Add(header);
        CancelButton = cancel;
        AcceptButton = _continueButton;
        Shown += async (_, _) => await InitializeViewerAsync();
        FormClosed += (_, _) => { try { if (_viewerTempFolder is not null && Directory.Exists(_viewerTempFolder)) Directory.Delete(_viewerTempFolder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
    }

    private async Task InitializeViewerAsync()
    {
        try
        {
            var webViewData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FutureSolucoes", "AssinaPDF", "WebView2");
            Directory.CreateDirectory(webViewData);
            var environment = await CoreWebView2Environment.CreateAsync(null, webViewData);
            await _viewer.EnsureCoreWebView2Async(environment);
            _viewerTempFolder = Path.Combine(Path.GetTempPath(), "AssinaPDF-Placement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_viewerTempFolder);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "placement-viewer.html"), Path.Combine(_viewerTempFolder, "placement-viewer.html"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "placement-viewer.js"), Path.Combine(_viewerTempFolder, "placement-viewer.js"));
            CopyDirectory(Path.Combine(AppContext.BaseDirectory, "viewer"), Path.Combine(_viewerTempFolder, "viewer"));
            File.Copy(_pdfPath, Path.Combine(_viewerTempFolder, Path.GetFileName(_pdfPath)));
            _viewer.CoreWebView2.SetVirtualHostNameToFolderMapping("assinapdf-viewer.local", _viewerTempFolder, CoreWebView2HostResourceAccessKind.Allow);
            _viewer.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                try
                {
                    using var json = JsonDocument.Parse(args.WebMessageAsJson);
                    if (!json.RootElement.TryGetProperty("placements", out var list) || list.ValueKind != JsonValueKind.Array) return;
                    _placements.Clear();
                    foreach (var item in list.EnumerateArray())
                    {
                        var page = item.GetProperty("page").GetInt32();
                        var x = item.GetProperty("x").GetSingle();
                        var y = item.GetProperty("y").GetSingle();
                        if (page > 0 && float.IsFinite(x) && float.IsFinite(y) && x >= 0 && y >= 0)
                            _placements.Add(new SignaturePlacementPoint(page, x, y));
                    }
                    UpdateSelectionStatus();
                }
                catch (JsonException) { }
                catch (InvalidOperationException) { }
            };
            var viewerUrl = "https://assinapdf-viewer.local/placement-viewer.html?file=" + Uri.EscapeDataString(Path.GetFileName(_pdfPath));
            _viewer.CoreWebView2.Navigate(viewerUrl);
        }
        catch (Exception ex)
        {
            _selectionStatus.Text = "Não foi possível abrir a visualização para selecionar os locais.";
            MessageBox.Show(this, $"A visualização de seleção não pôde ser iniciada.\r\n\r\n{ex.Message}", "AssinaPDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private void UpdateSelectionStatus()
    {
        _selectionStatus.Text = _placements.Count switch
        {
            0 => "Nenhum local selecionado — clique em uma ou mais páginas do PDF.",
            1 => "1 local selecionado. Você pode adicionar outros locais antes de continuar.",
            _ => $"{_placements.Count} locais selecionados. Cada local receberá uma assinatura digital."
        };
        _continueButton.Enabled = _placements.Count > 0;
    }

    private static Button CreateButton(string text)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), BackColor = Color.FromArgb(92, 98, 108), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(5, 3, 0, 3), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}