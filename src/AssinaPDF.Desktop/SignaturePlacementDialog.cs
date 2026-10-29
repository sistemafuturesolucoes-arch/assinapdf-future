using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FutureSolucoes.AssinaPDF;

internal sealed record SignaturePlacementPoint(int Page, float X, float Y);

internal sealed class SignaturePlacementDialog : Form
{
    private readonly string _pdfPath;
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
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 680);
        Size = new Size(1120, 820);
        BackColor = Color.FromArgb(18, 18, 27);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var header = new Panel { Dock = DockStyle.Top, Height = 94, Padding = new Padding(20, 12, 20, 10), BackColor = Color.FromArgb(39, 25, 62) };
        header.Controls.Add(new Label { Text = "Selecione o local para assinar", Dock = DockStyle.Top, Height = 36, Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold), ForeColor = Color.White });
        header.Controls.Add(new Label { Text = "Clique no PDF para adicionar uma marca de assinatura. Você pode selecionar mais de um local, inclusive em páginas diferentes.", Dock = DockStyle.Top, Height = 32, ForeColor = Color.FromArgb(222, 213, 236) });

        _viewer = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(30, 30, 38) };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 100, Padding = new Padding(18, 8, 18, 8), BackColor = Color.FromArgb(24, 24, 34) };
        _selectionStatus = new Label { Dock = DockStyle.Top, Height = 25, Text = "Carregando documento...", ForeColor = Color.FromArgb(225, 218, 239) };
        var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = bottom.BackColor };
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        _reason = new TextBox { Dock = DockStyle.Fill, Text = "Assinatura digital", MaxLength = 200, BackColor = Color.FromArgb(38, 38, 48), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 7, 10, 4) };
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
    }

    private async Task InitializeViewerAsync()
    {
        try
        {
            await _viewer.EnsureCoreWebView2Async();
            _viewer.CoreWebView2.SetVirtualHostNameToFolderMapping("assinapdf-viewer.local", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.Allow);
            var pdfDirectory = Path.GetDirectoryName(_pdfPath) ?? throw new InvalidOperationException("Pasta do PDF indisponível.");
            _viewer.CoreWebView2.SetVirtualHostNameToFolderMapping("assinapdf-pdf.local", pdfDirectory, CoreWebView2HostResourceAccessKind.Allow);
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
            var pdfUrl = "https://assinapdf-pdf.local/" + Uri.EscapeDataString(Path.GetFileName(_pdfPath));
            var viewerUrl = "https://assinapdf-viewer.local/placement-viewer.html?file=" + Uri.EscapeDataString(pdfUrl);
            _viewer.CoreWebView2.Navigate(viewerUrl);
        }
        catch (Exception ex)
        {
            _selectionStatus.Text = "Não foi possível abrir a visualização para selecionar os locais.";
            MessageBox.Show(this, $"A visualização de seleção não pôde ser iniciada.\r\n\r\n{ex.Message}", "AssinaPDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), BackColor = Color.FromArgb(124, 58, 237), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(5, 3, 0, 3), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}