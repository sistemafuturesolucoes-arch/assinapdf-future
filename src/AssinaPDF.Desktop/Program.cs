using System.Security.Cryptography.X509Certificates;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SimpleSign.Core.Signing;
using SimpleSign.PAdES;

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
    private readonly Label _certificateStatus;
    private readonly WebView2 _pdfViewer;
    private readonly ComboBox _certificatePicker;
    private readonly Button _signButton;
    private string? _currentPdfPath;
    private bool _viewerReady;
    private bool _signingInProgress;

    public MainForm()
    {
        Text = "AssinaPDF | Future Soluções";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 680);
        Size = new Size(1280, 850);
        BackColor = Color.FromArgb(18, 18, 24);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);
        AllowDrop = true;
        DragEnter += HandleDragEnter;
        DragDrop += HandleDragDrop;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 74,
            Padding = new Padding(22, 12, 22, 10),
            BackColor = Color.FromArgb(35, 25, 54)
        };
        header.Controls.Add(new Label
        {
            Text = "Future Soluções  •  Leitura e assinatura digital de PDFs",
            Dock = DockStyle.Bottom,
            Height = 24,
            ForeColor = Color.FromArgb(210, 200, 225)
        });
        header.Controls.Add(new Label
        {
            Text = "AssinaPDF",
            Dock = DockStyle.Top,
            Height = 32,
            Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold),
            ForeColor = Color.White
        });

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(16, 10, 16, 8),
            BackColor = Color.FromArgb(24, 24, 32),
            WrapContents = false,
            AutoScroll = true
        };

        var openButton = CreateButton("Abrir PDF");
        openButton.Click += OpenPdf;
        toolbar.Controls.Add(openButton);

        _signButton = CreateButton("Assinar PDF");
        _signButton.Enabled = false;
        _signButton.Click += async (_, _) => await SignPdfAsync();
        toolbar.Controls.Add(_signButton);

        toolbar.Controls.Add(new Label
        {
            Text = "Certificado do Windows:",
            AutoSize = true,
            Margin = new Padding(18, 9, 6, 0),
            ForeColor = Color.Gainsboro
        });

        _certificatePicker = new ComboBox
        {
            Width = 360,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 3, 6, 0),
            BackColor = Color.FromArgb(38, 38, 48),
            ForeColor = Color.White
        };
        _certificatePicker.SelectedIndexChanged += (_, _) =>
        {
            var item = _certificatePicker.SelectedItem as CertificateItem;
            _certificateStatus.Text = item?.Details ?? "Selecione um certificado.";
            UpdateSigningAvailability();
        };
        toolbar.Controls.Add(_certificatePicker);

        var refreshButton = CreateButton("Atualizar certificados");
        refreshButton.Click += (_, _) => LoadCertificates();
        toolbar.Controls.Add(refreshButton);

        _fileStatus = new Label
        {
            Text = "Abra um PDF ou arraste o arquivo para esta janela.",
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(18, 7, 18, 4),
            ForeColor = Color.FromArgb(215, 215, 225),
            AutoEllipsis = true
        };

        _pdfViewer = new WebView2
        {
            Dock = DockStyle.Fill,
            AllowExternalDrop = false,
            DefaultBackgroundColor = Color.FromArgb(28, 28, 36)
        };
        _pdfViewer.CoreWebView2InitializationCompleted += ViewerInitializationCompleted;

        _certificateStatus = new Label
        {
            Text = "Os certificados são consultados localmente no repositório do usuário atual.",
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(18, 8, 18, 8),
            BackColor = Color.FromArgb(24, 24, 32),
            ForeColor = Color.FromArgb(185, 185, 200),
            AutoEllipsis = true
        };

        Controls.Add(_pdfViewer);
        Controls.Add(_certificateStatus);
        Controls.Add(_fileStatus);
        Controls.Add(toolbar);
        Controls.Add(header);

        Shown += async (_, _) => await InitializeViewerAsync();
        LoadCertificates();
    }

    private static Button CreateButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 6),
            BackColor = Color.FromArgb(124, 58, 237),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private async Task InitializeViewerAsync()
    {
        try
        {
            await _pdfViewer.EnsureCoreWebView2Async();
            _viewerReady = true;
            if (!string.IsNullOrWhiteSpace(_currentPdfPath))
                NavigateToPdf(_currentPdfPath);
        }
        catch (Exception ex)
        {
            _fileStatus.Text = "Não foi possível iniciar o visualizador PDF. Verifique se o Microsoft Edge WebView2 Runtime está instalado.";
            MessageBox.Show(this,
                $"O visualizador não pôde ser inicializado.\r\n\r\nDetalhes: {ex.Message}",
                "AssinaPDF — visualizador indisponível",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ViewerInitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
            _fileStatus.Text = "Falha ao inicializar o visualizador integrado.";
    }

    private void OpenPdf(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecionar documento PDF",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            LoadPdf(dialog.FileName);
    }

    private void LoadPdf(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Selecione um arquivo PDF existente.", "Arquivo inválido",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var info = new FileInfo(fullPath);
            if (info.Length == 0)
            {
                MessageBox.Show(this, "O arquivo está vazio e não pode ser aberto.", "PDF inválido",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _currentPdfPath = fullPath;
            _fileStatus.Text = $"{info.Name}  •  {FormatFileSize(info.Length)}  •  {fullPath}";
            UpdateSigningAvailability();
            if (_viewerReady)
                NavigateToPdf(fullPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível abrir o arquivo.\r\n\r\n{ex.Message}",
                "Erro ao abrir PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void NavigateToPdf(string path)
    {
        try
        {
            if (!_viewerReady || !File.Exists(path))
                return;

            _pdfViewer.CoreWebView2.Navigate(new Uri(Path.GetFullPath(path)).AbsoluteUri);
            UpdateSigningAvailability();
        }
        catch (Exception ex)
        {
            _signButton.Enabled = false;
            MessageBox.Show(this, $"O visualizador não conseguiu carregar o PDF.\r\n\r\n{ex.Message}",
                "Erro de visualização", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadCertificates()
    {
        _certificatePicker.BeginUpdate();
        _certificatePicker.Items.Clear();
        var found = 0;
        var eligible = 0;

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

            foreach (var certificate in store.Certificates)
            {
                try
                {
                    var hasPrivateKey = certificate.HasPrivateKey;
                    var now = DateTime.Now;
                    var currentlyValid = now >= certificate.NotBefore && now <= certificate.NotAfter;
                    var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
                    var canAttemptSigning = hasPrivateKey && currentlyValid;
                    var item = new CertificateItem(
                        certificate,
                        $"{name} — válido até {certificate.NotAfter:dd/MM/yyyy}{(canAttemptSigning ? "" : " — não pronto para assinar")}",
                        $"Titular: {name} | Emissor: {certificate.GetNameInfo(X509NameType.SimpleName, true)} | Validade: {certificate.NotBefore:dd/MM/yyyy} a {certificate.NotAfter:dd/MM/yyyy} | Chave privada disponível: {(hasPrivateKey ? "sim" : "não")} | Vigência local: {(currentlyValid ? "dentro do prazo" : "fora do prazo")}");
                    _certificatePicker.Items.Add(item);
                    found++;
                    if (canAttemptSigning)
                        eligible++;
                }
                catch (CryptographicException)
                {
                    // Um certificado malformado não deve impedir a listagem dos demais.
                }
            }

            _certificateStatus.Text = found == 0
                ? "Nenhum certificado encontrado no repositório Pessoal do usuário atual."
                : $"{found} certificado(s) encontrado(s), {eligible} com chave privada e dentro da validade local. A confiança, revogação e compatibilidade serão avaliadas separadamente.";
        }
        catch (Exception ex)
        {
            _certificateStatus.Text = "Não foi possível consultar o repositório de certificados do Windows.";
            MessageBox.Show(this, $"Falha ao consultar certificados locais.\r\n\r\n{ex.Message}",
                "Certificados do Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _certificatePicker.EndUpdate();
            UpdateSigningAvailability();
        }
    }

    private async Task SignPdfAsync()
    {
        if (_signingInProgress || string.IsNullOrWhiteSpace(_currentPdfPath))
            return;

        if (_certificatePicker.SelectedItem is not CertificateItem item)
        {
            MessageBox.Show(this, "Selecione um certificado do Windows antes de assinar.", "Certificado necessário",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var certificate = item.Certificate;
        var now = DateTime.Now;
        if (!certificate.HasPrivateKey || now < certificate.NotBefore || now > certificate.NotAfter)
        {
            MessageBox.Show(this, "O certificado selecionado não tem chave privada acessível ou está fora da validade local. Escolha outro certificado.",
                "Certificado indisponível", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateSigningAvailability();
            return;
        }

        var inputPath = Path.GetFullPath(_currentPdfPath);
        var inputInfo = new FileInfo(inputPath);
        if (!inputInfo.Exists || inputInfo.Length == 0)
        {
            MessageBox.Show(this, "O PDF original não está mais disponível ou está vazio. Abra o documento novamente.",
                "Documento indisponível", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Salvar PDF assinado",
            Filter = "Documento PDF (*.pdf)|*.pdf",
            DefaultExt = "pdf",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"{Path.GetFileNameWithoutExtension(inputPath)}-assinado.pdf",
            InitialDirectory = Path.GetDirectoryName(inputPath),
            RestoreDirectory = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var outputPath = Path.GetFullPath(dialog.FileName);
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Para proteger o original, salve a assinatura em um novo arquivo com outro nome.",
                "Escolha outro nome", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string? temporaryPath = null;
        _signingInProgress = true;
        _signButton.Text = "Assinando...";
        _signButton.Enabled = false;
        _certificateStatus.Text = "Assinando localmente com o certificado selecionado. Não feche o aplicativo enquanto o processo estiver em andamento.";

        try
        {
            var pdfBytes = await File.ReadAllBytesAsync(inputPath);
            var signedBytes = await PadesSigner
                .Document(pdfBytes)
                .WithCertificate(certificate)
                .WithLevel(AdesBaselineProfile.Basic())
                .SignAsync();

            if (signedBytes.Length == 0)
                throw new InvalidDataException("O mecanismo de assinatura retornou um documento vazio.");

            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidOperationException("Não foi possível determinar a pasta de destino.");
            temporaryPath = Path.Combine(outputDirectory, $".assinapdf-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporaryPath, signedBytes);
            File.Move(temporaryPath, outputPath, overwrite: true);
            temporaryPath = null;

            _certificateStatus.Text = $"Assinatura PAdES básica concluída. Arquivo salvo em: {outputPath}";
            MessageBox.Show(this,
                $"O PDF assinado foi salvo em um arquivo separado.\r\n\r\nArquivo: {outputPath}\r\nCertificado: {certificate.GetNameInfo(X509NameType.SimpleName, false)}\r\n\r\nA assinatura foi gerada no perfil PAdES básico. A confiança da cadeia, a revogação e o carimbo do tempo não são certificados por esta mensagem.",
                "Assinatura concluída", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _certificateStatus.Text = "A assinatura não foi concluída. O arquivo original foi mantido sem alterações.";
            MessageBox.Show(this,
                $"Não foi possível assinar o PDF. O arquivo original não foi alterado.\r\n\r\nVerifique se o certificado permite uso da chave privada, se o token/cartão está conectado e se o documento é compatível.\r\n\r\nDetalhes: {ex.Message}",
                "Falha na assinatura", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Falha na limpeza temporária não deve ocultar o erro original.
                }
            }

            _signingInProgress = false;
            _signButton.Text = "Assinar PDF";
            UpdateSigningAvailability();
        }
    }

    private void UpdateSigningAvailability()
    {
        if (_signButton is null || _signingInProgress)
            return;

        var hasPdf = !string.IsNullOrWhiteSpace(_currentPdfPath) && File.Exists(_currentPdfPath);
        var item = _certificatePicker.SelectedItem as CertificateItem;
        var now = DateTime.Now;
        var certificateReady = item is not null
            && item.Certificate.HasPrivateKey
            && now >= item.Certificate.NotBefore
            && now <= item.Certificate.NotAfter;

        _signButton.Enabled = hasPdf && certificateReady;
    }

    private void HandleDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Effect = files?.Length == 1 && Path.GetExtension(files[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void HandleDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1)
            LoadPdf(files[0]);
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes / (1024d * 1024d):0.0} MB"
    };

    private sealed record CertificateItem(X509Certificate2 Certificate, string DisplayName, string Details)
    {
        public override string ToString() => DisplayName;
    }
}
