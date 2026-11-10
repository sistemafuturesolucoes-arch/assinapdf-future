using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using AssinaPDF.MacPrototype.Services;
using SkiaSharp;

namespace AssinaPDF.MacPrototype;

public partial class ConverterWindow : Window
{
    public ConverterWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private async void ImagesToPdf_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecione as imagens na ordem desejada",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Imagens JPG, PNG e BMP")
                    {
                        Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.JPG", "*.JPEG", "*.PNG", "*.BMP" }
                    }
                }
            });
            if (files.Count == 0) return;

            var save = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Salvar imagens em PDF",
                SuggestedFileName = "imagens-convertidas.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } }
                }
            });
            if (save == null) return;

            string destination = Path.GetFullPath(save.Path.LocalPath);
            var sources = files.Select(x => Path.GetFullPath(x.Path.LocalPath)).ToArray();
            if (sources.Contains(destination, OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                throw new InvalidOperationException("O arquivo de destino não pode substituir uma imagem de origem.");

            using var output = new PdfDocument();
            foreach (string sourcePath in sources)
            {
                using XImage image = XImage.FromFile(sourcePath);
                var page = output.AddPage();
                page.Width = XUnit.FromPoint(image.PointWidth);
                page.Height = XUnit.FromPoint(image.PointHeight);
                using XGraphics graphics = XGraphics.FromPdfPage(page);
                graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            }
            output.Save(destination);
            ImagesStatusText.Text = $"{files.Count} imagem(ns) convertida(s).";
            StatusText.Text = $"PDF criado: {destination}";
        }
        catch (Exception ex)
        {
            ImagesStatusText.Text = "Não foi possível converter as imagens.";
            StatusText.Text = ex.Message;
        }
    }

    private async void PdfToJpg_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecione o PDF para converter em JPG",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf", "*.PDF" } }
                }
            });
            if (files.Count == 0) return;

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Escolha a pasta para salvar as imagens JPG",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;

            string pdfPath = files[0].Path.LocalPath;
            string folder = folders[0].Path.LocalPath;
            string baseName = Path.GetFileNameWithoutExtension(pdfPath);
            using var document = new PdfiumDocument(pdfPath);

            for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                var rendered = document.RenderPage(pageIndex, 2.0f);
                using var bitmap = new SKBitmap(rendered.Width, rendered.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                IntPtr target = bitmap.GetPixels();
                int rowBytes = rendered.Width * 4;
                for (int y = 0; y < rendered.Height; y++)
                    Marshal.Copy(rendered.Pixels, y * rendered.Stride, IntPtr.Add(target, y * bitmap.RowBytes), rowBytes);

                using SKImage image = SKImage.FromBitmap(bitmap);
                using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, 92);
                string outputPath = Path.Combine(folder, $"{baseName}-pagina-{pageIndex + 1:000}.jpg");
                using var output = File.Create(outputPath);
                encoded.SaveTo(output);
            }

            PdfToJpgStatusText.Text = $"{document.PageCount} página(s) exportada(s).";
            StatusText.Text = $"Imagens JPG salvas em: {folder}";
        }
        catch (Exception ex)
        {
            PdfToJpgStatusText.Text = "Não foi possível converter o PDF.";
            StatusText.Text = ex.Message;
        }
    }
}