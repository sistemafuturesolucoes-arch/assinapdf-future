using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Reflection;

namespace FutureSolucoes.AssinaPDF.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new LauncherForm());
    }

    internal static byte[] ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Recurso embutido não encontrado: {name}");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}

internal sealed class LauncherForm : Form
{
    private readonly Button _installButton;
    private readonly Button _portableButton;
    private readonly Label _status;
    private readonly Image? _logo;

    public LauncherForm()
    {
        Text = "AssinaPDF | Future Soluções";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(700, 430);
        BackColor = Color.FromArgb(18, 18, 27);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);
        try { using var ms = new MemoryStream(Program.ReadResource("logo-future.png")); _logo = Image.FromStream(ms); }
        catch { _logo = null; }

        var header = new Panel { Dock = DockStyle.Top, Height = 150, BackColor = Color.FromArgb(39, 25, 62), Padding = new Padding(28, 18, 28, 18) };
        var logo = new PictureBox { Dock = DockStyle.Left, Width = 150, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Image = _logo };
        header.Controls.Add(logo);
        var titleArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        titleArea.Controls.Add(new Label { Text = "AssinaPDF", Dock = DockStyle.Top, Height = 58, Font = new Font("Segoe UI", 27F, FontStyle.Bold), ForeColor = Color.White, TextAlign = ContentAlignment.BottomLeft });
        titleArea.Controls.Add(new Label { Text = "Assinatura digital de PDFs • Future Soluções", Dock = DockStyle.Top, Height = 32, Font = new Font("Segoe UI", 10F), ForeColor = Color.FromArgb(221, 210, 239), TextAlign = ContentAlignment.MiddleLeft });
        header.Controls.Add(titleArea);

        var intro = new Label { Text = "Como deseja usar o AssinaPDF?", Dock = DockStyle.Top, Height = 62, Padding = new Padding(28, 22, 28, 8), Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold), ForeColor = Color.FromArgb(238, 235, 246) };
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 2, 28, 12), ColumnCount = 2, RowCount = 1, BackColor = BackColor };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _installButton = CreateOption("Instalar no computador", "Instala o AssinaPDF e cria atalhos para abrir depois.");
        _portableButton = CreateOption("Assinar sem instalar", "Abre o aplicativo diretamente, sem instalar no Windows.");
        _installButton.Click += async (_, _) => await InstallAsync();
        _portableButton.Click += async (_, _) => await RunPortableAsync();
        options.Controls.Add(_installButton, 0, 0);
        options.Controls.Add(_portableButton, 1, 0);

        _status = new Label { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(28, 10, 28, 8), ForeColor = Color.FromArgb(183, 178, 198), Text = "Seus certificados permanecem no Windows. A chave privada não é exportada." };
        Controls.Add(options);
        Controls.Add(_status);
        Controls.Add(intro);
        Controls.Add(header);
        if (_logo is not null) Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    }

    private static Button CreateOption(string title, string description)
    {
        var button = new Button { Dock = DockStyle.Fill, Text = title + Environment.NewLine + Environment.NewLine + description, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold), BackColor = Color.FromArgb(124, 58, 237), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(8), Cursor = Cursors.Hand, Padding = new Padding(12) };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(142, 82, 246);
        return button;
    }

    private async Task InstallAsync()
    {
        SetBusy(true, "Preparando o instalador...");
        string? msiPath = null;
        try
        {
            msiPath = Path.Combine(Path.GetTempPath(), $"AssinaPDF-Setup-{Guid.NewGuid():N}.msi");
            await File.WriteAllBytesAsync(msiPath, Program.ReadResource("AssinaPDF-Setup-x64.msi"));
            var start = new ProcessStartInfo("msiexec.exe") { UseShellExecute = true, Verb = "runas", ArgumentList = { "/i", msiPath } };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("O Windows não iniciou o instalador.");
            _status.Text = "O assistente de instalação do Windows foi aberto.";
            await Task.Run(() => process.WaitForExit());
            if (process.ExitCode == 0) _status.Text = "Instalação concluída. Você já pode abrir o AssinaPDF pelo atalho.";
            else if (process.ExitCode == 1602) _status.Text = "Instalação cancelada pelo usuário.";
            else _status.Text = $"O instalador encerrou com código {process.ExitCode}.";
        }
        catch (Exception ex) { MessageBox.Show(this, $"Não foi possível iniciar a instalação.\r\n\r\n{ex.Message}", "Instalação do AssinaPDF", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { if (msiPath is not null) { try { File.Delete(msiPath); } catch { } } SetBusy(false, _status.Text); }
    }

    private async Task RunPortableAsync()
    {
        SetBusy(true, "Preparando a versão sem instalação...");
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FutureSolucoes", "AssinaPDF", "Portable");
            Directory.CreateDirectory(folder);
            var zipPath = Path.Combine(Path.GetTempPath(), $"AssinaPDF-{Guid.NewGuid():N}.zip");
            try
            {
                await File.WriteAllBytesAsync(zipPath, Program.ReadResource("AssinaPDF-portable.zip"));
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, folder, overwriteFiles: true));
            }
            finally { try { File.Delete(zipPath); } catch { } }
            var exe = Path.Combine(folder, "AssinaPDF.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("O aplicativo não foi encontrado no pacote portátil.", exe);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            _status.Text = "AssinaPDF aberto. Nenhuma instalação foi realizada.";
        }
        catch (Exception ex) { MessageBox.Show(this, $"Não foi possível abrir a versão portátil.\r\n\r\n{ex.Message}", "AssinaPDF", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false, _status.Text); }
    }

    private void SetBusy(bool busy, string message)
    {
        _installButton.Enabled = !busy;
        _portableButton.Enabled = !busy;
        _status.Text = message;
        UseWaitCursor = busy;
    }
}