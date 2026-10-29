namespace FutureSolucoes.AssinaPDF;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly Label _fileStatus;

    public MainForm()
    {
        Text = "AssinaPDF | Future Soluções";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1100, 760);
        BackColor = Color.FromArgb(18, 18, 24);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            Padding = new Padding(24, 14, 24, 12),
            BackColor = Color.FromArgb(35, 25, 54)
        };

        var title = new Label
        {
            Text = "AssinaPDF",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold),
            ForeColor = Color.White
        };
        var subtitle = new Label
        {
            Text = "Future Soluções  •  Leitura e assinatura digital de PDFs",
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = Color.FromArgb(210, 200, 225)
        };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        var openButton = new Button
        {
            Text = "Abrir PDF",
            AutoSize = true,
            Padding = new Padding(18, 8, 18, 8),
            BackColor = Color.FromArgb(124, 58, 237),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 0, 18)
        };
        openButton.FlatAppearance.BorderSize = 0;
        openButton.Click += OpenPdf;

        _fileStatus = new Label
        {
            Text = "Nenhum documento selecionado",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 48,
            ForeColor = Color.FromArgb(210, 210, 220),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var notice = new Label
        {
            Text = "Base inicial em desenvolvimento. A visualização do PDF e a assinatura criptográfica serão adicionadas e testadas nas próximas etapas.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(180, 180, 195),
            Padding = new Padding(48)
        };

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28),
            BackColor = Color.FromArgb(24, 24, 32)
        };
        content.Controls.Add(notice);
        content.Controls.Add(_fileStatus);
        content.Controls.Add(openButton);

        Controls.Add(content);
        Controls.Add(header);
    }

    private void OpenPdf(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecionar documento PDF",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _fileStatus.Text = $"Arquivo selecionado: {Path.GetFileName(dialog.FileName)}\r\n{dialog.FileName}";
        MessageBox.Show(
            this,
            "O arquivo foi selecionado. A visualização e a assinatura ainda não estão implementadas nesta versão inicial.",
            "AssinaPDF — etapa inicial",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
