using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;
using SimpleSign.PAdES.Validation;

namespace AssinaPDF.MacPrototype;

public partial class ValidationWindow : Window
{
    private string? _pdfPath;
    private PdfiumDocument? _document;
    private WriteableBitmap? _pageBitmap;
    private int _pageIndex;
    private double _zoom = 1.0;
    private bool _validating;

    public ValidationWindow()
    {
        InitializeComponent();
        Closed += (_, _) => DisposeDocument();
    }

    private async void OpenPdf_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecionar PDF para validar",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Documentos PDF") { Patterns = new[] { "*.pdf", "*.PDF" } }
                }
            });
            if (files.Count == 0) return;

            string path = files[0].Path.LocalPath;
            var newDocument = new PdfiumDocument(path);
            var oldDocument = _document;
            _document = newDocument;
            _pdfPath = path;
            _pageIndex = 0;
            _zoom = 1.0;
            oldDocument?.Dispose();

            FileNameText.Text = files[0].Name;
            SummaryText.Text = "Documento carregado. Clique em Validar assinaturas para iniciar a análise.";
            ReportText.Text = string.Empty;
            ValidateButton.IsEnabled = true;
            PdfScrollViewer.IsVisible = true;
            ViewerPlaceholder.IsVisible = false;
            RenderCurrentPage();
            StatusText.Text = $"PDF aberto: {newDocument.PageCount} página(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível abrir o PDF: {ex.Message}";
        }
    }

    private async void Validate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_validating || string.IsNullOrWhiteSpace(_pdfPath)) return;
        _validating = true;
        ValidateButton.IsEnabled = false;
        SummaryText.Text = "Analisando assinaturas digitais...";
        ReportText.Text = string.Empty;

        try
        {
            // Mantém explícitos os limites atuais: esta etapa não consulta revogação
            // nem confia automaticamente nas raízes do sistema.
            var validator = new PdfSignatureValidator(new ValidationOptions
            {
                CheckRevocation = false,
                TrustSystemRoots = false
            });
            await using var stream = new FileStream(_pdfPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 81920, useAsync: true);
            var results = await validator.ValidateAsync(stream);

            if (results.Count == 0)
            {
                SummaryText.Text = "Nenhuma assinatura digital reconhecida.";
                ReportText.Text = "Nenhuma assinatura digital compatível foi encontrada neste PDF. Isso não significa necessariamente que o arquivo seja inválido; ele pode não estar assinado.";
                StatusText.Text = "Análise concluída: nenhuma assinatura encontrada.";
                return;
            }

            int validCount = results.Count(x => x.IsIntegrityValid && x.IsSignatureValid);
            SummaryText.Text = $"{results.Count} assinatura(s) encontrada(s). {validCount} com integridade e assinatura criptográfica confirmadas nesta verificação.";
            var report = new StringBuilder();
            report.AppendLine($"RELATÓRIO DE VALIDAÇÃO — {results.Count} assinatura(s)");
            report.AppendLine(new string('=', 58));
            foreach (var result in results)
            {
                report.AppendLine();
                report.AppendLine($"Campo: {result.FieldName}");
                report.AppendLine($"Titular: {result.SignerName}");
                report.AppendLine($"Data: {(result.SigningTime.HasValue ? result.SigningTime.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz") : "Não informada")}");
                report.AppendLine($"Integridade: {(result.IsIntegrityValid ? "CONFIRMADA" : "NÃO CONFIRMADA")}");
                report.AppendLine($"Assinatura criptográfica: {(result.IsSignatureValid ? "CONFIRMADA" : "NÃO CONFIRMADA")}");
                report.AppendLine($"Cadeia de confiança: {(result.IsCertificateChainValid ? "CONFIRMADA" : "NÃO CONFIRMADA NESTA ETAPA")}");
                report.AppendLine($"Revogação: {(result.IsNotRevoked ? "Nenhuma revogação reportada" : "Não confirmada")}");
                report.AppendLine($"Fonte de revogação: {result.RevocationSource}");
                report.AppendLine($"Resultado da biblioteca: {(result.IsValid ? "VÁLIDA" : "NÃO VALIDADA")}");
                if (result.Errors.Count > 0)
                {
                    report.AppendLine("Erros:");
                    foreach (var error in result.Errors) report.AppendLine($" • {error}");
                }
                if (result.Warnings.Count > 0)
                {
                    report.AppendLine("Avisos:");
                    foreach (var warning in result.Warnings) report.AppendLine($" • {warning}");
                }
                report.AppendLine(new string('-', 58));
            }
            report.AppendLine();
            report.AppendLine("IMPORTANTE: nesta versão, a consulta de revogação e a confiança nas raízes do sistema estão desabilitadas. Portanto, o resultado não equivale a uma validação oficial completa ICP-Brasil nem atesta sozinho a situação atual do certificado.");
            ReportText.Text = report.ToString();
            StatusText.Text = $"Análise concluída: {results.Count} assinatura(s) examinada(s).";
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Não foi possível concluir a validação.";
            ReportText.Text = ex.ToString();
            StatusText.Text = $"Erro durante a validação: {ex.Message}";
        }
        finally
        {
            _validating = false;
            ValidateButton.IsEnabled = !string.IsNullOrWhiteSpace(_pdfPath);
        }
    }

    private void PreviousPage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_document == null || _pageIndex <= 0) return;
        _pageIndex--;
        RenderCurrentPage();
    }

    private void NextPage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_document == null || _pageIndex >= _document.PageCount - 1) return;
        _pageIndex++;
        RenderCurrentPage();
    }

    private void ZoomIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoom = Math.Min(2.5, _zoom + 0.15);
        RenderCurrentPage();
    }

    private void ZoomOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoom = Math.Max(0.5, _zoom - 0.15);
        RenderCurrentPage();
    }

    private void RenderCurrentPage()
    {
        if (_document == null) return;
        try
        {
            var rendered = _document.RenderPage(_pageIndex, (float)(1.25 * _zoom));
            var bitmap = new WriteableBitmap(new PixelSize(rendered.Width, rendered.Height),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var framebuffer = bitmap.Lock())
            {
                int rowBytes = checked(rendered.Width * 4);
                for (int y = 0; y < rendered.Height; y++)
                    Marshal.Copy(rendered.Pixels, checked(y * rendered.Stride),
                        IntPtr.Add(framebuffer.Address, checked(y * framebuffer.RowBytes)), rowBytes);
            }
            var old = _pageBitmap;
            _pageBitmap = bitmap;
            PdfPageImage.Source = bitmap;
            old?.Dispose();
            ZoomText.Text = $"{_zoom:P0}";
            PageText.Text = $"Página {_pageIndex + 1} / {_document.PageCount}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao renderizar página: {ex.Message}";
        }
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void DisposeDocument()
    {
        PdfPageImage.Source = null;
        _pageBitmap?.Dispose();
        _pageBitmap = null;
        _document?.Dispose();
        _document = null;
    }
}
