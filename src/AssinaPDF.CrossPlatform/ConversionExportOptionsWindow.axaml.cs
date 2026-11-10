using System;
using System.Globalization;
using Avalonia.Controls;

namespace AssinaPDF.MacPrototype;

public sealed record ConversionExportSettings(
    string PageSize,
    double CustomWidthMm,
    double CustomHeightMm,
    bool FillPage,
    string ImageFormat,
    int Dpi,
    int JpegQuality);

public partial class ConversionExportOptionsWindow : Window
{
    private readonly bool _imagesToPdf;
    public ConversionExportSettings? Settings { get; private set; }

    public ConversionExportOptionsWindow(bool imagesToPdf)
    {
        _imagesToPdf = imagesToPdf;
        InitializeComponent();
        DialogTitle.Text = imagesToPdf ? "Configurar PDF" : "Configurar exportação de imagens";
        DialogDescription.Text = imagesToPdf
            ? "Escolha o tamanho das páginas e como as imagens serão posicionadas no PDF."
            : "Escolha o tamanho, formato e resolução das páginas que serão exportadas.";
        ImageOptionsPanel.IsVisible = !imagesToPdf;
        ExportButton.Content = imagesToPdf ? "Salvar PDF" : "Exportar imagens";
        QualityPanel.IsVisible = !imagesToPdf;
    }

    private void PageSize_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CustomSizePanel != null)
            CustomSizePanel.IsVisible = PageSizeCombo.SelectedIndex == 6;
    }

    private void Export_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string pageSize = (PageSizeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Original";
        if (!double.TryParse(CustomWidthBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out double width) ||
            !double.TryParse(CustomHeightBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out double height) ||
            width <= 0 || height <= 0 || width > 2000 || height > 2000)
        {
            if (pageSize == "Personalizado")
            {
                DialogDescription.Text = "Informe largura e altura válidas, entre 0 e 2000 mm.";
                return;
            }
            width = 210;
            height = 297;
        }

        string formatText = (FormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "JPG";
        string dpiText = (DpiCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "300 DPI";
        int dpi = dpiText.StartsWith("150", StringComparison.Ordinal) ? 150 :
            dpiText.StartsWith("600", StringComparison.Ordinal) ? 600 : 300;
        if (!int.TryParse(QualityBox.Text, out int quality)) quality = 92;
        quality = Math.Clamp(quality, 1, 100);

        Settings = new ConversionExportSettings(
            pageSize, width, height,
            FitModeCombo.SelectedIndex == 1,
            formatText.StartsWith("PNG", StringComparison.OrdinalIgnoreCase) ? "PNG" : "JPG",
            dpi, quality);
        Close();
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}