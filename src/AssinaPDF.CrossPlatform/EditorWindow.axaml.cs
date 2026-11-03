using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public partial class EditorWindow : Window
{
    private readonly string _workingCopyPath;
    private readonly string _originalPath;
    private readonly string _sessionDirectory;
    private PdfiumDocument? _document;
    private readonly List<WriteableBitmap> _bitmaps = new();

    public EditorWindow(string workingCopyPath, string originalPath, string sessionDirectory)
    {
        InitializeComponent();

        _workingCopyPath = workingCopyPath;
        _originalPath = originalPath;
        _sessionDirectory = sessionDirectory;

        DocumentNameText.Text = Path.GetFileName(originalPath);
        Title = $"AssinaPDF | Editor - {Path.GetFileName(originalPath)}";

        try
        {
            _document = new PdfiumDocument(_workingCopyPath);
            for (int i = 0; i < _document.PageCount; i++)
            {
                var rendered = _document.RenderPage(i, 1.25f);
                var bitmap = CreateBitmap(rendered);
                _bitmaps.Add(bitmap);

                var page = new Border
                {
                    Background = Avalonia.Media.Brushes.White,
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(190, 194, 201)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12),
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"Página {i + 1}",
                                FontSize = 12,
                                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(75, 80, 89)),
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                            },
                            new Image
                            {
                                Source = bitmap,
                                Stretch = Avalonia.Media.Stretch.Uniform,
                                IsHitTestVisible = false
                            }
                        }
                    }
                };
                EditorPagesPanel.Children.Add(page);
            }

            EditorStatusText.Text =
                $"{_document.PageCount} página(s) carregada(s) na cópia de trabalho. " +
                "As ferramentas de edição de conteúdo serão habilitadas nas próximas etapas.";
        }
        catch
        {
            DisposeResources();
            TryDeleteSessionDirectory();
            throw;
        }

        Closed += (_, _) =>
        {
            DisposeResources();
            TryDeleteSessionDirectory();
        };
    }

    private static WriteableBitmap CreateBitmap(RenderedPdfPage page)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(page.Width, page.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        try
        {
            using var framebuffer = bitmap.Lock();
            int bytesPerRow = checked(page.Width * 4);
            for (int y = 0; y < page.Height; y++)
            {
                IntPtr destination = IntPtr.Add(framebuffer.Address, checked(y * framebuffer.RowBytes));
                Marshal.Copy(page.Pixels, checked(y * page.Stride), destination, bytesPerRow);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private async void SaveAs_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Exportar cópia do PDF",
                    SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_originalPath)}-copia.pdf",
                    DefaultExtension = "pdf",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("Documento PDF") { Patterns = new[] { "*.pdf" } }
                    }
                });

            if (destination == null)
                return;

            string targetPath = Path.GetFullPath(destination.Path.LocalPath);
            if (SamePath(targetPath, _originalPath) || SamePath(targetPath, _workingCopyPath))
            {
                EditorStatusText.Text = "Escolha outro nome/local. O PDF original e a cópia temporária não podem ser sobrescritos.";
                return;
            }

            // Nunca sobrescreve um destino existente. Isso também protege o original
            // caso o usuário selecione um caminho alternativo para o mesmo arquivo.
            bool destinationCreated = false;
            try
            {
                using var sourceStream = new FileStream(
                    _workingCopyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var destinationStream = new FileStream(
                    targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                destinationCreated = true;
                sourceStream.CopyTo(destinationStream);
            }
            catch
            {
                if (destinationCreated)
                {
                    try { File.Delete(targetPath); }
                    catch { /* Limpeza best-effort do arquivo criado nesta operação. */ }
                }
                throw;
            }

            EditorStatusText.Text = $"Cópia exportada em: {targetPath}";
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = $"Não foi possível salvar a cópia: {ex.Message}";
        }
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void CloseEditor_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void DisposeResources()
    {
        _document?.Dispose();
        _document = null;
        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
        _bitmaps.Clear();
    }

    private void TryDeleteSessionDirectory()
    {
        try
        {
            if (Directory.Exists(_sessionDirectory))
                Directory.Delete(_sessionDirectory, true);
        }
        catch
        {
            // A limpeza temporária é best-effort; nunca altera o PDF original.
        }
    }
}