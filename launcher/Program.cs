using System.Diagnostics;
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
        ClientSize = new Size(620, 390);
        BackColor = Color.FromArgb(242, 243, 245);
        ForeColor = Color.FromArgb(45, 49, 56);
        Font = new Font("Segoe UI", 10F);

        try
        {
            using var ms = new MemoryStream(Program.ReadResource("logo-future.png"));
            using var loaded = Image.FromStream(ms);
            _logo = new Bitmap(loaded);
        }
        catch { _logo = null; }

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 190,
            BackColor = Color.FromArgb(232, 234, 237),
            Padding = new Padding(20, 14, 20, 14)
        };
        var logo = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Image = _logo
        };
        header.Controls.Add(logo);

        var prompt = new Label
        {
            Text = "Como deseja usar o AssinaPDF?",
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(16, 14, 16, 4),
            Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(45, 49, 56),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var options = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 4, 22, 10),
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _installButton = CreateOption("Instalar no computador", "Instala e cria atalhos no Windows.");
        _portableButton = CreateOption("Assinar sem instalar", "Executa sem instalar no Windows.");
        _installButton.Click += async (_, _) => await InstallAsync();
        _portableButton.Click += async (_, _) => await RunPortableAsync();
        options.Controls.Add(_installButton, 0, 0);
        options.Controls.Add(_portableButton, 1, 0);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(14, 7, 14, 6),
            ForeColor = Color.FromArgb(75, 80, 88),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Future Soluções"
        };

        Controls.Add(options);
        Controls.Add(_status);
        Controls.Add(prompt);
        Controls.Add(header);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
    }

    private static Button CreateOption(string title, string description)
    {
        var button = new Button
        {
            Dock = DockStyle.Fill,
            Text = title + Environment.NewLine + Environment.NewLine + description,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
            BackColor = Color.FromArgb(210, 214, 219),
            ForeColor = Color.FromArgb(42, 46, 52),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(8),
            Cursor = Cursors.Hand,
            Padding = new Padding(12)
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(190, 195, 202);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(222, 225, 229);
        return button;
    }

    private async Task InstallAsync()
    {
        SetBusy(true, "Verificando o visualizador PDF...");
        string? msiPath = null;
        string? webViewInstallerPath = null;
        try
        {
            // O bootstrapper oficial instala/atualiza o Evergreen WebView2 Runtime.
            webViewInstallerPath = Path.Combine(Path.GetTempPath(), $"MicrosoftEdgeWebView2Setup-{Guid.NewGuid():N}.exe");
            await File.WriteAllBytesAsync(webViewInstallerPath, Program.ReadResource("MicrosoftEdgeWebView2Setup.exe"));
            using (var webView = Process.Start(new ProcessStartInfo(webViewInstallerPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { "/silent", "/install" }
            }) ?? throw new InvalidOperationException("O Windows não iniciou a instalação do WebView2 Runtime."))
            {
                await Task.Run(() => webView.WaitForExit());
                if (webView.ExitCode != 0 && webView.ExitCode != 3010)
                    throw new InvalidOperationException($"A instalação do WebView2 Runtime terminou com o código {webView.ExitCode}.");
            }

            SetBusy(true, "Instalando o AssinaPDF...");
            msiPath = Path.Combine(Path.GetTempPath(), $"AssinaPDF-Setup-{Guid.NewGuid():N}.msi");
            await File.WriteAllBytesAsync(msiPath, Program.ReadResource("AssinaPDF-Setup-x64.msi"));
            using var process = Process.Start(new ProcessStartInfo("msiexec.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { "/i", msiPath }
            }) ?? throw new InvalidOperationException("O Windows não iniciou o instalador do AssinaPDF.");

            await Task.Run(() => process.WaitForExit());
            if (process.ExitCode == 0 || process.ExitCode == 3010)
            {
                MessageBox.Show(this,
                    "O AssinaPDF Future foi instalado com sucesso!\r\n\r\nVocê já pode abrir o aplicativo pelo atalho criado na área de trabalho ou no menu Iniciar.",
                    "Instalação bem-sucedida", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
                return;
            }
            if (process.ExitCode == 1602)
                _status.Text = "Instalação cancelada pelo usuário.";
            else
                throw new InvalidOperationException($"O instalador encerrou com o código {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível concluir a instalação.\r\n\r\n{ex.Message}",
                "Instalação do AssinaPDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "A instalação não foi concluída.";
        }
        finally
        {
            TryDelete(msiPath);
            TryDelete(webViewInstallerPath);
            if (!IsDisposed) SetBusy(false, _status.Text);
        }
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
            finally { TryDelete(zipPath); }

            var exe = Path.Combine(folder, "AssinaPDF.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("O aplicativo não foi encontrado no pacote portátil.", exe);

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            _status.Text = "AssinaPDF aberto sem instalação.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível abrir a versão portátil.\r\n\r\n{ex.Message}",
                "AssinaPDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { if (!IsDisposed) SetBusy(false, _status.Text); }
    }

    private void SetBusy(bool busy, string message)
    {
        _installButton.Enabled = !busy;
        _portableButton.Enabled = !busy;
        _status.Text = message;
        UseWaitCursor = busy;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); } catch { }
    }
}