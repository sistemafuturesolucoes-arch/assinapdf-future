using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using AvaloniaImage = Avalonia.Controls.Image;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Aspose.Pdf;
using Aspose.Pdf.Text;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

internal sealed record PagePreviewInfo(int PageIndex, float WidthPoints, float HeightPoints);

public partial class EditorWindow : Window
{
    private readonly string _workingCopyPath;
    private readonly string _originalPath;
    private readonly string _sessionDirectory;
    private PdfiumDocument? _document;
    private readonly List<WriteableBitmap> _bitmaps = new();
    private readonly Stack<byte[]> _undoStack = new();
    private readonly Stack<byte[]> _redoStack = new();

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
            LoadDocumentPreview();
            EditorStatusText.Text =
                "Documento aberto em uma cópia temporária. Edite o texto localizado ou insira novos textos; o original não será alterado.";
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

    private void LoadDocumentPreview()
    {
        _document?.Dispose();
        _document = null;

        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
        _bitmaps.Clear();
        EditorPagesPanel.Children.Clear();

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
                        new AvaloniaImage
                        {
                            Source = bitmap,
                            Stretch = Avalonia.Media.Stretch.Uniform,
                            Tag = new PagePreviewInfo(i, rendered.PageWidthPoints, rendered.PageHeightPoints)
                        }
                    }
                }
            };
            if (page.Child is not StackPanel pageContent)
                throw new InvalidOperationException("Não foi possível preparar a visualização da página.");
            var pageImage = pageContent.Children.OfType<AvaloniaImage>().Single();
            pageImage.PointerPressed += PageImage_PointerPressed;
            EditorPagesPanel.Children.Add(page);
        }
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

    private async void PageImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not AvaloniaImage image ||
            image.Tag is not PagePreviewInfo pageInfo)
            return;

        var point = e.GetPosition(image);
        if (image.Bounds.Width <= 0 || image.Bounds.Height <= 0 ||
            point.X < 0 || point.Y < 0 ||
            point.X > image.Bounds.Width || point.Y > image.Bounds.Height)
            return;

        double pdfX = point.X / image.Bounds.Width * pageInfo.WidthPoints;
        double pdfY = pageInfo.HeightPoints -
            point.Y / image.Bounds.Height * pageInfo.HeightPoints;

        e.Handled = true;
        await EditTextAtPointAsync(pageInfo.PageIndex, pdfX, pdfY);
    }

    private async Task EditTextAtPointAsync(int pageIndex, double x, double y)
    {
        try
        {
            string? originalText = null;
            Aspose.Pdf.Rectangle? originalBounds = null;
            bool originalBold = false;

            using (var document = Document.Open(_workingCopyPath))
            {
                var absorber = new TextFragmentAbsorber();
                document.Pages[pageIndex + 1].Accept(absorber);

                double bestDistance = double.MaxValue;
                foreach (TextFragment fragment in absorber.TextFragments)
                {
                    var bounds = fragment.Rectangle;
                    if (bounds == null)
                        continue;

                    double dx = x < bounds.LLX ? bounds.LLX - x :
                        x > bounds.URX ? x - bounds.URX : 0;
                    double dy = y < bounds.LLY ? bounds.LLY - y :
                        y > bounds.URY ? y - bounds.URY : 0;
                    double distance = Math.Sqrt(dx * dx + dy * dy);

                    if (distance <= 5.0 && distance < bestDistance &&
                        !string.IsNullOrWhiteSpace(fragment.Text))
                    {
                        bestDistance = distance;
                        originalText = fragment.Text;
                        originalBold = fragment.TextState.IsBold;
                        originalBounds = new Aspose.Pdf.Rectangle(
                            bounds.LLX, bounds.LLY, bounds.URX, bounds.URY);
                    }
                }
            }

            var textBox = new TextBox
            {
                Text = originalText ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MinHeight = 76,
                PlaceholderText = originalText == null
                    ? "Digite o texto que deseja inserir"
                    : "Edite o texto selecionado"
            };
            var installedFonts = Avalonia.Media.FontManager.Current.SystemFonts
                .Select(fontFamily => fontFamily.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var fontBox = new ComboBox
            {
                Width = 210,
                ItemsSource = installedFonts,
                PlaceholderText = "Fontes instaladas",
                SelectedItem = installedFonts.FirstOrDefault(name =>
                    string.Equals(name, "Arial", StringComparison.OrdinalIgnoreCase))
            };
            var sizeBox = new ComboBox
            {
                Width = 90,
                ItemsSource = new[] { "6", "7", "8", "9", "10", "11", "12", "14", "16", "18", "20", "22", "24", "26", "28", "36", "48", "72" },
                SelectedItem = originalText == null ? "12" : null,
                PlaceholderText = "Tamanho"
            };
            bool boldEnabled = false;
            int boldStart = -1;
            int boldLength = 0;
            var boldButton = new Button
            {
                Content = "B",
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize = 17,
                Width = 42,
                Height = 34,
            };
            boldButton.Click += (_, _) =>
            {
                boldStart = textBox.SelectionStart;
                boldLength = Math.Max(0, textBox.SelectionEnd - textBox.SelectionStart);
                if (boldLength <= 0)
                {
                    EditorStatusText.Text = "Selecione primeiro o trecho do texto que deseja colocar em negrito.";
                    boldEnabled = false;
                    return;
                }
                boldEnabled = !boldEnabled;
                boldButton.Background = boldEnabled
                    ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(210, 220, 240))
                    : Avalonia.Media.Brushes.Transparent;
            };

            string textColor = "";
            string highlightColor = "";
            var paletteColors = new[]
            {
                "#000000", "#404040", "#808080", "#C00000", "#FF0000", "#ED7D31",
                "#FFC000", "#FFFF00", "#70AD47", "#00B050", "#00B0F0", "#0070C0",
                "#002060", "#7030A0", "#FF00FF", "#FFFFFF"
            };
            var textPalette = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            var highlightPalette = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            foreach (string hex in paletteColors)
            {
                var textSwatch = new Button
                {
                    Width = 25, Height = 25, Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex)),
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(150, 150, 150)),
                    BorderThickness = new Thickness(1),
                };
                textSwatch.Click += (_, _) => textColor = hex;
                textPalette.Children.Add(textSwatch);

                var highlightSwatch = new Button
                {
                    Width = 25, Height = 25, Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex)),
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(150, 150, 150)),
                    BorderThickness = new Thickness(1),
                };
                highlightSwatch.Click += (_, _) => highlightColor = hex;
                highlightPalette.Children.Add(highlightSwatch);
            }
            var alignmentBox = new ComboBox
            {
                Width = 160,
                ItemsSource = new[] { "Esquerda", "Centralizado", "Direita" },
                SelectedIndex = 0
            };

            var dialog = new Window
            {
                Title = originalText == null ? "Inserir texto no PDF" : "Editar texto do PDF",
                Width = 650,
                Height = 500,
                MinWidth = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = true,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = originalText == null
                                ? $"Novo texto na página {pageIndex + 1}"
                                : "Texto selecionado",
                            FontSize = 14,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold
                        },
                        textBox,
                        new TextBlock
                        {
                            Text = "Formatação",
                            FontSize = 13,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold,
                            Margin = new Thickness(0, 4, 0, 0)
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 10,
                            Children = { fontBox, sizeBox, boldButton }
                        },
                        new TextBlock { Text = "Cor da fonte:" },
                        textPalette,
                        new TextBlock { Text = "Cor de marcação:" },
                        highlightPalette,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 10,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "Alinhamento:",
                                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                                },
                                alignmentBox
                            }
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 8,
                            Children =
                            {
                                new Button
                                {
                                    Content = "Cancelar",
                                    MinWidth = 100,
                                    Padding = new Thickness(12, 8),
                                    Command = null
                                },
                                new Button
                                {
                                    Content = originalText == null ? "Inserir texto" : "Aplicar alteração",
                                    MinWidth = 140,
                                    Padding = new Thickness(12, 8)
                                }
                            }
                        }
                    }
                }
            };

            var actionPanel = (StackPanel)((StackPanel)dialog.Content!).Children[^1];
            var cancelButton = (Button)actionPanel.Children[0];
            var applyButton = (Button)actionPanel.Children[1];
            applyButton.Click += (_, _) => dialog.Close(true);
            cancelButton.Click += (_, _) => dialog.Close(false);

            bool accepted = await dialog.ShowDialog<bool>(this);
            if (!accepted)
                return;

            string replacement = textBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(replacement))
            {
                EditorStatusText.Text = "Digite algum texto antes de aplicar.";
                return;
            }

            string fontName = fontBox.SelectedItem?.ToString() ?? string.Empty;
            string fontSizeText = sizeBox.SelectedItem?.ToString() ?? string.Empty;
            string alignment = alignmentBox.SelectedItem?.ToString() ?? "Esquerda";

            if (!string.IsNullOrWhiteSpace(fontSizeText) &&
                (!float.TryParse(fontSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedSize) ||
                 parsedSize <= 0))
            {
                EditorStatusText.Text = "Informe um tamanho de fonte válido, maior que zero.";
                return;
            }

            ApplyPdfEdit(document =>
            {
                if (originalBounds != null && originalText != null)
                {
                    var pageAbsorber = new TextFragmentAbsorber();
                    document.Pages[pageIndex + 1].Accept(pageAbsorber);

                    TextFragment? target = null;
                    double closest = double.MaxValue;
                    foreach (TextFragment fragment in pageAbsorber.TextFragments)
                    {
                        var bounds = fragment.Rectangle;
                        if (bounds == null)
                            continue;
                        double cx = (bounds.LLX + bounds.URX) / 2;
                        double cy = (bounds.LLY + bounds.URY) / 2;
                        double ox = (originalBounds.LLX + originalBounds.URX) / 2;
                        double oy = (originalBounds.LLY + originalBounds.URY) / 2;
                        double distance = Math.Sqrt(Math.Pow(cx - ox, 2) + Math.Pow(cy - oy, 2));

                        if (string.Equals(fragment.Text, originalText, StringComparison.Ordinal) &&
                            distance < closest)
                        {
                            closest = distance;
                            target = fragment;
                        }
                    }

                    if (target == null || closest > 8.0)
                        throw new InvalidOperationException("O texto selecionado não foi localizado novamente no PDF.");

                    target.Text = replacement;
                    ApplyDialogFormatting(target, fontName, fontSizeText,
                        textColor, highlightColor, alignment);

                    if (boldEnabled && boldStart >= 0 && boldLength > 0 &&
                        boldStart + boldLength <= replacement.Length)
                    {
                        string selectedText = replacement.Substring(boldStart, boldLength);
                        target.IsolateTextSegments(boldStart, boldLength);
                        foreach (TextSegment segment in target.Segments)
                        {
                            if (string.Equals(segment.Text, selectedText, StringComparison.Ordinal))
                                segment.TextState.IsBold = true;
                        }
                    }
                }
                else
                {
                    var fragment = new TextFragment(replacement)
                    {
                        Position = new Position(x, y)
                    };
                    ApplyDialogFormatting(fragment, fontName, fontSizeText,
                        textColor, highlightColor, alignment);
                    new TextBuilder(document.Pages[pageIndex + 1]).AppendText(fragment);
                }
            }, originalText == null
                ? $"Texto inserido na página {pageIndex + 1}."
                : "Texto e formatação atualizados.");
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = $"Não foi possível concluir a edição: {ex.Message}";
        }
    }

    private static void ApplyDialogFormatting(
        TextFragment fragment,
        string fontName,
        string fontSizeText,
        string textColor,
        string highlightColor,
        string alignment)
    {
        if (!string.IsNullOrWhiteSpace(fontName))
            fragment.TextState.FontName = fontName;

        if (float.TryParse(fontSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out float size) && size > 0)
            fragment.TextState.FontSize = size;

        if (TryParseHexColor(textColor, out var foreground))
            fragment.TextState.ForegroundColor = foreground;

        if (TryParseHexColor(highlightColor, out var background))
            fragment.TextState.BackgroundColor = background;

        fragment.HorizontalAlignment = alignment switch
        {
            "Centralizado" => Aspose.Pdf.HorizontalAlignment.Center,
            "Direita" => Aspose.Pdf.HorizontalAlignment.Right,
            _ => Aspose.Pdf.HorizontalAlignment.Left
        };
    }

    private void ReplaceText_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string search = SearchTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(search))
        {
            EditorStatusText.Text = "Informe o texto que deseja localizar no PDF.";
            return;
        }

        string replacement = ReplaceTextBox.Text ?? string.Empty;
        try
        {
            int changed = 0;
            ApplyPdfEdit(document =>
            {
                var absorber = new TextFragmentAbsorber(search);
                absorber.Visit(document);
                changed = absorber.TextFragments.Count;

                foreach (var fragment in absorber.TextFragments)
                {
                    fragment.Text = replacement;
                }
            }, $"Texto atualizado: {changed} ocorrência(s).");
            if (changed == 0)
                EditorStatusText.Text = "Nenhuma ocorrência encontrada. Verifique o texto digitado ou se o PDF contém texto selecionável.";
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = $"Não foi possível editar o texto: {ex.Message}";
        }
    }

    private static bool TryParseHexColor(string? value, out Aspose.Pdf.Color color)
    {
        color = Aspose.Pdf.Color.FromRgb(0, 0, 0);
        string hex = (value ?? string.Empty).Trim();
        if (hex.StartsWith("#", StringComparison.Ordinal))
            hex = hex[1..];

        if (hex.Length != 6 ||
            !byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return false;

        color = Aspose.Pdf.Color.FromRgb(r / 255d, g / 255d, b / 255d);
        return true;
    }

    private void ApplyPdfEdit(Action<Document> edit, string successMessage)
    {
        string outputPath = Path.Combine(_sessionDirectory, $"edit-{Guid.NewGuid():N}.pdf");
        byte[] before = File.ReadAllBytes(_workingCopyPath);

        try
        {
            using (var document = Document.Open(_workingCopyPath))
            {
                edit(document);
                document.Save(outputPath);
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new IOException("A biblioteca não gerou um PDF de saída válido.");

            _document?.Dispose();
            _document = null;
            File.Copy(outputPath, _workingCopyPath, true);
            PushHistory(_undoStack, before);
            _redoStack.Clear();
            LoadDocumentPreview();

            EditorStatusText.Text = successMessage +
                " O original permanece intacto. Atenção: qualquer alteração pode invalidar assinaturas digitais já existentes neste PDF.";
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); }
            catch { /* Limpeza temporária best-effort. */ }
        }
    }

    private static void PushHistory(Stack<byte[]> stack, byte[] snapshot)
    {
        stack.Push(snapshot);
        if (stack.Count <= 10)
            return;

        var keep = stack.Take(10).Reverse().ToArray();
        stack.Clear();
        foreach (var item in keep)
            stack.Push(item);
    }

    private void Undo_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_undoStack.Count == 0)
        {
            EditorStatusText.Text = "Não há alterações para desfazer.";
            return;
        }
        RestoreHistory(_undoStack, _redoStack, "Última alteração desfeita.");
    }

    private void Redo_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_redoStack.Count == 0)
        {
            EditorStatusText.Text = "Não há alterações para refazer.";
            return;
        }
        RestoreHistory(_redoStack, _undoStack, "Alteração refeita.");
    }

    private void RestoreHistory(Stack<byte[]> source, Stack<byte[]> destination, string message)
    {
        string restorePath = Path.Combine(_sessionDirectory, $"history-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] current = File.ReadAllBytes(_workingCopyPath);
            byte[] target = source.Pop();
            File.WriteAllBytes(restorePath, target);

            _document?.Dispose();
            _document = null;
            File.Copy(restorePath, _workingCopyPath, true);
            PushHistory(destination, current);
            LoadDocumentPreview();
            EditorStatusText.Text = message + " O PDF original permanece preservado.";
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = $"Não foi possível restaurar a versão: {ex.Message}";
        }
        finally
        {
            try { if (File.Exists(restorePath)) File.Delete(restorePath); }
            catch { /* Limpeza temporária best-effort. */ }
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
                    SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_originalPath)}-editado.pdf",
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

            bool destinationCreated = false;
            try
            {
                using var sourceStream = new FileStream(_workingCopyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var destinationStream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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