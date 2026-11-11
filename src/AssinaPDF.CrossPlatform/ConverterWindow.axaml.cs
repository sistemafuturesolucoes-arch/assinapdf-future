using Avalonia.Controls;

namespace AssinaPDF.MacPrototype;

public partial class ConverterWindow : Window
{
    public ConverterWindow()
    {
        InitializeComponent();
    }

    private async void NfeToDanfe_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new NfeImportWindow();
        await window.ShowDialog(this);
        NfeStatusText.Text = "Importador de XML de NF-e fechado.";
        StatusText.Text = "Você pode iniciar outra conversão.";
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private async void ImagesToPdf_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var editor = new ConversionEditorWindow(imagesToPdf: true);
        await editor.ShowDialog(this);
        ImagesStatusText.Text = "Editor de imagens fechado.";
        StatusText.Text = "Você pode iniciar outra conversão.";
    }

    private async void PdfToJpg_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var editor = new ConversionEditorWindow(imagesToPdf: false);
        await editor.ShowDialog(this);
        PdfToJpgStatusText.Text = "Editor de PDF fechado.";
        StatusText.Text = "Você pode iniciar outra conversão.";
    }
}
