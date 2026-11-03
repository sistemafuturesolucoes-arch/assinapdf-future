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
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;
using RichEditorControl = AvaloniaRichEditor.Controls.RichEditor;
using RichEditorToolbar = AvaloniaRichEditor.Controls.RichEditorToolbar;
using RichToolbarLevel = AvaloniaRichEditor.Controls.ToolbarLevel;
using RichFlowDocument = AvaloniaRichEditor.Documents.FlowDocument;
using RichParagraph = AvaloniaRichEditor.Documents.Paragraph;
using RichRun = AvaloniaRichEditor.Documents.Run;

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
            string? originalFontName = null;
            double originalFontSize = 12;
            bool originalBold = false;
            bool originalItalic = false;
            Avalonia.Media.Color? originalForeground = null;
            Avalonia.Media.Color? originalBackground = null;

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
                        originalFontName = fragment.TextState.FontName;
                        originalFontSize = fragment.TextState.FontSize > 0
                            ? fragment.TextState.FontSize
                            : 12;
                        originalBold = fragment.TextState.IsBold;
                        originalItalic = fragment.TextState.IsItalic;

                        if (fragment.TextState.ForegroundColor is { } foreground)
                            originalForeground = Avalonia.Media.Color.FromRgb(
                                foreground.R, foreground.G, foreground.B);
                        if (fragment.TextState.BackgroundColor is { } background)
                            originalBackground = Avalonia.Media.Color.FromRgb(
                                background.R, background.G, background.B);

                        originalBounds = new Aspose.Pdf.Rectangle(
                            bounds.LLX, bounds.LLY, bounds.URX, bounds.URY);
                    }
                }
            }

            var installedFonts = Avalonia.Media.FontManager.Current.SystemFonts
                .Select(fontFamily => fontFamily.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            var richDocument = new RichFlowDocument();
            var initialParagraph = new RichParagraph();
            initialParagraph.Inlines.Add(new RichRun
            {
                Text = originalText ?? string.Empty,
                FontFamily = originalFontName,
                FontSize = originalFontSize,
                FontWeight = originalBold
                    ? Avalonia.Media.FontWeight.Bold
                    : Avalonia.Media.FontWeight.Normal,
                FontStyle = originalItalic
                    ? Avalonia.Media.FontStyle.Italic
                    : Avalonia.Media.FontStyle.Normal,
                Foreground = new Avalonia.Media.SolidColorBrush(
                    originalForeground ?? Avalonia.Media.Colors.Black),
                Background = originalBackground.HasValue
                    ? new Avalonia.Media.SolidColorBrush(originalBackground.Value)
                    : null
            });
            richDocument.Blocks.Add(initialParagraph);

            var richEditor = new RichEditorControl
            {
                Document = richDocument,
                FontFamilyChoices = installedFonts,
                DefaultFontSize = originalFontSize,
                DefaultForeground = Avalonia.Media.Brushes.Black,
                AllowImages = false,
                AllowTables = false,
                AllowRichPaste = false,
                AllowFindReplace = false,
                MinHeight = 250,
                ShowPageBoundaries = false
            };

            RichEditorToolbar.FontSizes = new[]
            {
                6d, 7d, 8d, 9d, 10d, 11d, 12d, 14d, 16d,
                18d, 20d, 22d, 24d, 26d, 28d, 36d, 48d, 72d
            };

            var toolbar = new RichEditorToolbar
            {
                Target = richEditor,
                ToolbarLevel = RichToolbarLevel.Normal
            };

            var editorSurface = new Border
            {
                Background = Avalonia.Media.Brushes.White,
                BorderBrush = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(205, 209, 216)),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer
                {
                    Content = richEditor,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                }
            };

            var actionPanel = new StackPanel
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
                        Padding = new Thickness(12, 8)
                    },
                    new Button
                    {
                        Content = originalText == null ? "Inserir texto" : "Aplicar alteração",
                        MinWidth = 140,
                        Padding = new Thickness(12, 8)
                    }
                }
            };

            var dialog = new Window
            {
                Title = originalText == null ? "Inserir texto no PDF" : "Editar texto do PDF",
                Width = 760,
                Height = 640,
                MinWidth = 620,
                MinHeight = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = true,
                Content = new StackPanel
                {
                    Margin = new Thickness(18),
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = originalText == null
                                ? $"Novo texto na página {pageIndex + 1}"
                                : "Edite o texto e aplique a formatação diretamente neste campo",
                            FontSize = 14,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold
                        },
                        toolbar,
                        editorSurface,
                        new TextBlock
                        {
                            Text = "Selecione uma palavra ou trecho e use a barra de formatação. As alterações ficam nesta janela até você confirmar.",
                            FontSize = 11,
                            Foreground = new Avalonia.Media.SolidColorBrush(
                                Avalonia.Media.Color.FromRgb(95, 101, 111)),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        },
                        actionPanel
                    }
                }
            };

            var cancelButton = (Button)actionPanel.Children[0];
            var applyButton = (Button)actionPanel.Children[1];
            applyButton.Click += (_, _) => dialog.Close(true);
            cancelButton.Click += (_, _) => dialog.Close(false);
            dialog.Opened += (_, _) => richEditor.Focus();

            bool accepted = await dialog.ShowDialog<bool>(this);
            if (!accepted)
                return;

            var editedRuns = new List<EditedTextRun>();
            var replacementBuilder = new System.Text.StringBuilder();
            var editedParagraphs = richEditor.Document?.Blocks
                .OfType<RichParagraph>()
                .ToList() ?? new List<RichParagraph>();

            for (int paragraphIndex = 0; paragraphIndex < editedParagraphs.Count; paragraphIndex++)
            {
                if (paragraphIndex > 0)
                    replacementBuilder.Append('\n');

                foreach (var inline in editedParagraphs[paragraphIndex].Inlines)
                {
                    if (inline is not RichRun run || string.IsNullOrEmpty(run.Text))
                        continue;

                    int runStart = replacementBuilder.Length;
                    replacementBuilder.Append(run.Text);

                    editedRuns.Add(new EditedTextRun(
                        runStart,
                        run.Text.Length,
                        run.FontFamily,
                        run.FontSize,
                        run.FontWeight == Avalonia.Media.FontWeight.Bold,
                        run.FontStyle == Avalonia.Media.FontStyle.Italic,
                        GetAvaloniaSolidColor(run.Foreground),
                        GetAvaloniaSolidColor(run.Background)));
                }
            }

            string replacementText = replacementBuilder.ToString();
            if (string.IsNullOrWhiteSpace(replacementText))
            {
                EditorStatusText.Text = "Digite algum texto antes de aplicar.";
                return;
            }

            var alignment = editedParagraphs.FirstOrDefault()?.TextAlignment
                ?? Avalonia.Media.TextAlignment.Left;

            ApplyPdfEdit(document =>
            {
                TextFragment target;
                if (originalBounds != null && originalText != null)
                {
                    var pageAbsorber = new TextFragmentAbsorber();
                    document.Pages[pageIndex + 1].Accept(pageAbsorber);

                    target = null!;
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

                    target.Text = replacementText;
                }
                else
                {
                    target = new TextFragment(replacementText)
                    {
                        Position = new Position(x, y)
                    };
                }

                ApplyRichTextFormatting(target, editedRuns);
                target.HorizontalAlignment = alignment switch
                {
                    Avalonia.Media.TextAlignment.Center => Aspose.Pdf.HorizontalAlignment.Center,
                    Avalonia.Media.TextAlignment.Right => Aspose.Pdf.HorizontalAlignment.Right,
                    _ => Aspose.Pdf.HorizontalAlignment.Left
                };

                if (originalText == null)
                    new TextBuilder(document.Pages[pageIndex + 1]).AppendText(target);
            }, originalText == null
                ? $"Texto inserido na página {pageIndex + 1}."
                : "Texto e formatação atualizados.");
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = $"Não foi possível concluir a edição: {ex.Message}";
        }
    }

    private sealed record EditedTextRun(
        int Start,
        int Length,
        string? FontFamily,
        double FontSize,
        bool IsBold,
        bool IsItalic,
        Avalonia.Media.Color? Foreground,
        Avalonia.Media.Color? Background);

    private static Avalonia.Media.Color? GetAvaloniaSolidColor(Avalonia.Media.IBrush? brush)
    {
        if (brush is Avalonia.Media.ISolidColorBrush solid)
            return solid.Color;
        return null;
    }

    private static void ApplyRichTextFormatting(
        TextFragment fragment,
        IReadOnlyList<EditedTextRun> runs)
    {
        foreach (var run in runs.OrderBy(item => item.Start))
        {
            if (run.Length <= 0)
                continue;

            // Aspose retorna exatamente os segmentos isolados para este intervalo.
            // Aplicar o estilo diretamente neles evita depender de offsets recalculados
            // depois que a coleção de segmentos é dividida.
            var isolatedSegments = fragment.IsolateTextSegments(run.Start, run.Length);
            bool foundSegment = false;

            foreach (TextSegment segment in isolatedSegments)
            {
                foundSegment = true;

                if (!string.IsNullOrWhiteSpace(run.FontFamily))
                    segment.TextState.FontName = run.FontFamily;

                if (run.FontSize > 0)
                    segment.TextState.FontSize = (float)run.FontSize;

                segment.TextState.IsBold = run.IsBold;
                segment.TextState.IsItalic = run.IsItalic;

                if (run.Foreground is { } foreground)
                {
                    segment.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(
                        foreground.R / 255d,
                        foreground.G / 255d,
                        foreground.B / 255d);
                }

                // BackgroundColor não deve receber null: em documentos sem realce
                // isso pode causar falha no setter do Aspose durante a gravação.
                if (run.Background is { } background)
                {
                    segment.TextState.BackgroundColor = Aspose.Pdf.Color.FromRgb(
                        background.R / 255d,
                        background.G / 255d,
                        background.B / 255d);
                }
            }

            if (!foundSegment)
            {
                throw new InvalidOperationException(
                    $"Não foi possível localizar o trecho formatado no PDF (posição {run.Start}, tamanho {run.Length}).");
            }
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