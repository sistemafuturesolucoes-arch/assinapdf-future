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
                        new Image
                        {
                            Source = bitmap,
                            Stretch = Avalonia.Media.Stretch.Uniform,
                            Tag = new PagePreviewInfo(i, rendered.PageWidthPoints, rendered.PageHeightPoints)
                        }
                    }
                }
            };
            var pageImage = ((StackPanel)page.Child!).Children.OfType<Image>().Single();
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
        if (e.ClickCount != 2 || sender is not Image image ||
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

            using (var document = Document.Open(_workingCopyPath))
            {
                var absorber = new TextFragmentAbsorber();
                document.Pages[pageIndex + 1].Accept(absorber);

                double bestDistance = double.MaxValue;
                foreach (TextFragment fragment in absorber.TextFragments)
                {
                    var bounds = fragment.Rectangle;
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
                        originalBounds = new Aspose.Pdf.Rectangle(
                            bounds.LLX, bounds.LLY, bounds.URX, bounds.URY);
                    }
                }
            }

            if (originalText == null || originalBounds == null)
            {
                EditorStatusText.Text =
                    "Não encontrei texto selecionável nesse ponto. Se a página for digitalizada, será necessário OCR.";
                return;
            }

            var editBox = new TextBox
            {
                Text = originalText,
                AcceptsReturn = false,
                TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                MinWidth = 380,
                Watermark = "Edite o texto selecionado"
            };
            var applyButton = new Button
            {
                Content = "Aplicar alteração",
                MinWidth = 140,
                Padding = new Thickness(12, 8)
            };
            var cancelButton = new Button
            {
                Content = "Cancelar",
                MinWidth = 100,
                Padding = new Thickness(12, 8)
            };
            var actions = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancelButton, applyButton }
            };
            var dialog = new Window
            {
                Title = "Editar texto do PDF",
                Width = 540,
                Height = 190,
                MinWidth = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Texto selecionado — altere o conteúdo e aplique:",
                            FontSize = 13
                        },
                        editBox,
                        actions
                    }
                }
            };

            applyButton.Click += (_, _) => dialog.Close(true);
            cancelButton.Click += (_, _) => dialog.Close(false);

            bool accepted = await dialog.ShowDialog<bool>(this);
            if (!accepted)
                return;

            string replacement = editBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(replacement))
            {
                EditorStatusText.Text =
                    "O texto não foi alterado. Para remover conteúdo, use uma ferramenta de remoção específica.";
                return;
            }

            ApplyPdfEdit(document =>
            {
                var pageAbsorber = new TextFragmentAbsorber();
                document.Pages[pageIndex + 1].Accept(pageAbsorber);

                TextFragment? target = null;
                double closest = double.MaxValue;
                foreach (TextFragment fragment in pageAbsorber.TextFragments)
                {
                    var bounds = fragment.Rectangle;
                    double cx = (bounds.LLX + bounds.URX) / 2;
                    double cy = (bounds.LLY + bounds.URY) / 2;
                    double ox = (originalBounds.LLX + originalBounds.URX) / 2;
                    double oy = (originalBounds.LLY + originalBounds.URY) / 2;
                    double distance = Math.Sqrt(
                        Math.Pow(cx - ox, 2) + Math.Pow(cy - oy, 2));

                    if (string.Equals(fragment.Text, originalText, StringComparison.Ordinal) &&
                        distance < closest)
                    {
                        closest = distance;
                        target = fragment;
                    }
                }

                if (target == null || closest > 8.0)
                    throw new InvalidOperationException(
                        "O texto selecionado não foi localizado novamente no PDF.");

                target.Text = replacement;
                ApplyOptionalStyle(target.TextState, preserveFontWhenBlank: true);
            }, "Texto editado diretamente na página.");
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = $"Não foi possível editar o texto selecionado: {ex.Message}";
        }
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
                    ApplyOptionalStyle(fragment.TextState, preserveFontWhenBlank: true);
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

    private void AddText_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string text = NewTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            EditorStatusText.Text = "Digite o texto que deseja inserir.";
            return;
        }

        if (!int.TryParse(PageNumberTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pageNumber) ||
            !double.TryParse(XPositionTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(YPositionTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
        {
            EditorStatusText.Text = "Página, X e Y precisam conter números válidos. X/Y são medidos em pontos a partir do canto inferior esquerdo.";
            return;
        }

        try
        {
            ApplyPdfEdit(document =>
            {
                if (pageNumber < 1 || pageNumber > document.Pages.Count)
                    throw new ArgumentOutOfRangeException(nameof(pageNumber), $"O PDF possui {document.Pages.Count} página(s).");

                var fragment = new TextFragment(text)
                {
                    Position = new Position(x, y)
                };

                fragment.TextState.FontName = string.IsNullOrWhiteSpace(FontNameTextBox.Text)
                    ? "Helvetica"
                    : FontNameTextBox.Text.Trim();
                fragment.TextState.FontSize = ParseFontSize(12f);
                fragment.TextState.IsBold = IsBoldCheckBox.IsChecked == true;

                if (TryParseHexColor(TextColorTextBox.Text, out var foreground))
                    fragment.TextState.ForegroundColor = foreground;
                if (TryParseHexColor(HighlightColorTextBox.Text, out var background))
                    fragment.TextState.BackgroundColor = background;

                new TextBuilder(document.Pages[pageNumber]).AppendText(fragment);
            }, $"Texto inserido na página {pageNumber}.");
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = $"Não foi possível inserir o texto: {ex.Message}";
        }
    }

    private void ApplyOptionalStyle(TextState state, bool preserveFontWhenBlank)
    {
        string fontName = FontNameTextBox.Text?.Trim() ?? string.Empty;
        if (!preserveFontWhenBlank || !string.IsNullOrWhiteSpace(fontName))
            state.FontName = string.IsNullOrWhiteSpace(fontName) ? "Helvetica" : fontName;

        if (float.TryParse(FontSizeTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float size) && size > 0)
            state.FontSize = size;

        if (IsBoldCheckBox.IsChecked == true)
            state.IsBold = true;

        if (TryParseHexColor(TextColorTextBox.Text, out var foreground))
            state.ForegroundColor = foreground;

        if (TryParseHexColor(HighlightColorTextBox.Text, out var background))
            state.BackgroundColor = background;
    }

    private float ParseFontSize(float fallback)
    {
        return float.TryParse(FontSizeTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float size) && size > 0
            ? size
            : fallback;
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