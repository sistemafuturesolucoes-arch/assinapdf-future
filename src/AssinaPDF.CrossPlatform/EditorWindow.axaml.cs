using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private readonly Dictionary<int, (AvaloniaImage Image, Canvas Overlay)> _pageSurfaces = new();
    private int? _selectedPageIndex;
    private double _selectedPdfX;
    private double _selectedPdfY;

    // Construtor sem parâmetros usado pelo carregador XAML do Avalonia.
    // A janela funcional é criada pelo fluxo do editor com os caminhos explícitos.
    public EditorWindow()
    {
        _workingCopyPath = string.Empty;
        _originalPath = string.Empty;
        _sessionDirectory = string.Empty;
        InitializeComponent();
    }

    public EditorWindow(string workingCopyPath, string originalPath, string sessionDirectory)
    {
        InitializeComponent();

        _workingCopyPath = workingCopyPath;
        _originalPath = originalPath;
        _sessionDirectory = sessionDirectory;
        Directory.CreateDirectory(_sessionDirectory);

        DocumentNameText.Text = Path.GetFileName(originalPath);
        Title = $"AssinaPDF | Editor - {Path.GetFileName(originalPath)}";

        EditorStatusText.Text =
            "Preparando o PDF e verificando as fontes utilizadas...";

        Closed += (_, _) => DisposeResources();
        Opened += async (_, _) => await InitializeDocumentAsync();
    }

    private async Task InitializeDocumentAsync()
    {
        // A verificação e a obtenção das fontes acontecem antes da primeira
        // renderização, para que a prévia e o editor usem o mesmo conjunto de fontes.
        await VerifyDocumentFontsAsync();

        try
        {
            LoadDocumentPreview();
            // Preserve the result/warning produced by VerifyDocumentFontsAsync.
            // It is more useful than replacing it with a generic "opened" message.
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    private static void RegisterDocumentFont(
        Aspose.Pdf.Text.Font? font,
        string? stateFontName,
        IDictionary<string, bool> fonts)
    {
        string rawName = font?.DecodedFontName ??
                         font?.BaseFont ??
                         stateFontName ??
                         string.Empty;
        string family = NormalizePdfFontFamily(rawName);

        if (string.IsNullOrWhiteSpace(family) ||
            family.StartsWith("CIDFONT+", StringComparison.OrdinalIgnoreCase) ||
            family.Length <= 2)
            return;

        bool embedded = font?.IsEmbedded == true;
        if (fonts.TryGetValue(family, out bool wasEmbedded))
            fonts[family] = wasEmbedded || embedded;
        else
            fonts[family] = embedded;
    }

    private async Task VerifyDocumentFontsAsync()
    {
        if (!File.Exists(_workingCopyPath))
            return;

        EditorStatusText.Text = "Verificando as fontes utilizadas no PDF...";

        try
        {
            var fonts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            using (var document = Document.Open(_workingCopyPath))
            {
                foreach (Aspose.Pdf.Page page in document.Pages)
                {
                    var absorber = new TextFragmentAbsorber();
                    page.Accept(absorber);

                    foreach (TextFragment fragment in absorber.TextFragments)
                    {
                        // Um fragmento pode conter vários segmentos com fontes
                        // diferentes. A pré-verificação precisa inspecionar cada
                        // segmento, não apenas a fonte herdada pelo fragmento.
                        var segments = fragment.Segments.Cast<TextSegment>().ToArray();
                        if (segments.Length == 0)
                        {
                            RegisterDocumentFont(
                                fragment.TextState.Font,
                                fragment.TextState.FontName,
                                fonts);
                            continue;
                        }

                        foreach (TextSegment segment in segments)
                        {
                            RegisterDocumentFont(
                                segment.TextState.Font,
                                segment.TextState.FontName,
                                fonts);
                        }
                    }
                }
            }

            var missing = fonts
                .Where(item => !item.Value && !FontDownloadService.IsAvailable(item.Key))
                .Select(item => item.Key)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            if (missing.Length == 0)
            {
                EditorStatusText.Text = fonts.Count == 0
                    ? "PDF aberto. Não foram encontrados textos com fontes identificáveis."
                    : $"Verificação concluída: {fonts.Count} família(s) de fonte identificada(s); nenhuma fonte ausente que precise ser baixada.";
                return;
            }

            var unavailable = new List<string>();
            foreach (string family in missing)
            {
                try
                {
                    bool downloaded = await FontDownloadService.EnsureAvailableAsync(this, family);
                    if (!downloaded)
                        unavailable.Add(family);
                }
                catch (Exception ex)
                {
                    unavailable.Add(family);
                    System.Diagnostics.Debug.WriteLine(
                        $"AssinaPDF: não foi possível obter a fonte '{family}': {ex.Message}");
                }
            }

            if (unavailable.Count == 0)
            {
                EditorStatusText.Text = $"Verificação concluída: {fonts.Count} fonte(s) identificada(s). As fontes ausentes foram obtidas para uso interno do AssinaPDF; nenhuma foi instalada no sistema.";
                return;
            }

            string names = string.Join(", ", unavailable);
            EditorStatusText.Text = $"Atenção: {unavailable.Count} fonte(s) não estão disponíveis ({names}). O PDF continua aberto e será exibido com a substituição de fonte do mecanismo de visualização. Ao editar, selecione uma família instalada para substituir a original; espaçamento e quebras de linha podem mudar.";
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = "Não foi possível concluir a verificação de fontes. O PDF continua aberto; a aparência original da visualização foi mantida. " + ex.Message;
        }
    }

    private void LoadDocumentPreview()
    {
        _document?.Dispose();
        _document = null;

        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
        _bitmaps.Clear();
        EditorPagesPanel.Children.Clear();
        _pageSurfaces.Clear();
        ClearTextSelection();

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
                        new Grid
                        {
                            Children =
                            {
                                new AvaloniaImage
                                {
                                    Source = bitmap,
                                    Stretch = Avalonia.Media.Stretch.Uniform,
                                    Tag = new PagePreviewInfo(i, rendered.PageWidthPoints, rendered.PageHeightPoints)
                                },
                                new Canvas
                                {
                                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                                    IsHitTestVisible = false
                                }
                            }
                        }
                    }
                }
            };
            if (page.Child is not StackPanel pageContent ||
                pageContent.Children.OfType<Grid>().SingleOrDefault() is not Grid pageSurface)
                throw new InvalidOperationException("Não foi possível preparar a visualização da página.");

            var pageImage = pageSurface.Children.OfType<AvaloniaImage>().Single();
            var overlay = pageSurface.Children.OfType<Canvas>().Single();
            pageImage.PointerPressed += PageImage_PointerPressed;
            _pageSurfaces[i] = (pageImage, overlay);
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

    private void PageImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || sender is not AvaloniaImage image ||
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

        try
        {
            using var document = Document.Open(_workingCopyPath);
            var absorber = new TextFragmentAbsorber();
            document.Pages[pageInfo.PageIndex + 1].Accept(absorber);

            TextFragment? selected = null;
            double bestDistance = double.MaxValue;
            foreach (TextFragment fragment in absorber.TextFragments)
            {
                var bounds = fragment.Rectangle;
                if (bounds == null || string.IsNullOrWhiteSpace(fragment.Text))
                    continue;

                double dx = pdfX < bounds.LLX ? bounds.LLX - pdfX :
                    pdfX > bounds.URX ? pdfX - bounds.URX : 0;
                double dy = pdfY < bounds.LLY ? bounds.LLY - pdfY :
                    pdfY > bounds.URY ? pdfY - bounds.URY : 0;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance <= 5.0 && distance < bestDistance)
                {
                    selected = fragment;
                    bestDistance = distance;
                }
            }

            ClearTextSelection();
            if (selected?.Rectangle is not { } selectedBounds)
            {
                EditorStatusText.Text =
                    "Nenhum texto selecionável nesse ponto. PDFs digitalizados ou textos convertidos em imagem precisam de OCR.";
                return;
            }

            _selectedPageIndex = pageInfo.PageIndex;
            _selectedPdfX = (selectedBounds.LLX + selectedBounds.URX) / 2;
            _selectedPdfY = (selectedBounds.LLY + selectedBounds.URY) / 2;

            if (!_pageSurfaces.TryGetValue(pageInfo.PageIndex, out var surface))
                return;

            double left = selectedBounds.LLX / pageInfo.WidthPoints * image.Bounds.Width;
            double top = (pageInfo.HeightPoints - selectedBounds.URY) /
                         pageInfo.HeightPoints * image.Bounds.Height;
            double width = (selectedBounds.URX - selectedBounds.LLX) /
                           pageInfo.WidthPoints * image.Bounds.Width;
            double height = (selectedBounds.URY - selectedBounds.LLY) /
                            pageInfo.HeightPoints * image.Bounds.Height;

            var highlight = new Border
            {
                BorderBrush = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(35, 116, 220)),
                BorderThickness = new Thickness(2),
                Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(28, 35, 116, 220)),
                Width = Math.Max(4, width),
                Height = Math.Max(4, height),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(highlight, left);
            Canvas.SetTop(highlight, top);
            surface.Overlay.Children.Add(highlight);

            SelectedTextLabel.Text = selected.Text.Length > 70
                ? selected.Text[..67] + "..."
                : selected.Text;
            SelectedTextLabel.IsVisible = true;
            EditSelectedTextButton.IsVisible = true;
            EditorStatusText.Text = "Texto selecionado. Use “Editar seleção” para alterar o conteúdo e a formatação.";
            e.Handled = true;
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = $"Não foi possível selecionar o texto: {ex.Message}";
        }
    }

    private void ClearTextSelection()
    {
        foreach (var surface in _pageSurfaces.Values)
            surface.Overlay.Children.Clear();

        _selectedPageIndex = null;
        SelectedTextLabel.Text = string.Empty;
        SelectedTextLabel.IsVisible = false;
        EditSelectedTextButton.IsVisible = false;
    }

    private async void EditSelectedText_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedPageIndex is not int pageIndex)
            return;

        await EditTextAtPointAsync(pageIndex, _selectedPdfX, _selectedPdfY);
        ClearTextSelection();
    }

    private async Task EditTextAtPointAsync(int pageIndex, double x, double y)
    {
        try
        {
            string? originalText = null;
            Aspose.Pdf.Rectangle? originalBounds = null;
            string? originalFontName = null;
            bool originalFontEmbedded = false;
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
                        var sourceFont = fragment.TextState.Font;
                        originalFontEmbedded = sourceFont?.IsEmbedded == true;
                        string decodedFontName = sourceFont?.DecodedFontName ??
                                                  sourceFont?.BaseFont ??
                                                  fragment.TextState.FontName ??
                                                  string.Empty;
                        originalFontName = NormalizePdfFontFamily(decodedFontName);
                        // CIDFONT+F2 and similar resource labels are PDF internals, not family names.
                        // Keep the embedded font in the PDF and show a standard editing fallback.
                        if (string.IsNullOrWhiteSpace(originalFontName) ||
                            originalFontName.StartsWith("CIDFONT+", StringComparison.OrdinalIgnoreCase) ||
                            originalFontName.Length <= 2)
                            originalFontName = "Helvetica";
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

            var installedFonts = InstalledFontCatalogService.GetInstalledFamilyNames();

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
                Width = 700,
                Height = 560,
                MinWidth = 540,
                MinHeight = 420,
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

            // Antes de gravar, garanta que as fontes necessárias às novas faces/ famílias
            // estejam disponíveis. O download exige autorização e nunca instala fontes no SO.
            var requiredFonts = editedRuns
                .Where(run => !string.IsNullOrWhiteSpace(run.FontFamily) &&
                    (!string.Equals(
                        NormalizePdfFontFamily(run.FontFamily),
                        NormalizePdfFontFamily(originalFontName),
                        StringComparison.OrdinalIgnoreCase) ||
                     run.IsBold != originalBold ||
                     run.IsItalic != originalItalic))
                .Select(run => run.FontFamily!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string fontFamily in requiredFonts)
            {
                bool available = await FontDownloadService.EnsureAvailableAsync(this, fontFamily);
                if (!available)
                {
                    EditorStatusText.Text =
                        $"A fonte '{fontFamily}' não está disponível. A edição foi cancelada para evitar substituir a tipografia silenciosamente.";
                    return;
                }
            }

            var alignment = editedParagraphs.FirstOrDefault()?.TextAlignment
                ?? Avalonia.Media.TextAlignment.Left;

            Aspose.Pdf.Rectangle? expectedBounds = null;

            var formattingTrace = new List<string>();
            ApplyPdfEdit(document =>
            {
                TextFragment target;
                if (originalBounds != null && originalText != null)
                {
                    // A opção RemoveUnusedFonts instrui o mecanismo de edição a
                    // substituir a referência de fonte do trecho absorvido e limpar
                    // recursos antigos que deixarem de ser usados. Isso é essencial
                    // quando o PDF original usa uma fonte CID Type0 incorporada e o
                    // usuário escolhe uma face compatível (por exemplo, Helvetica-Bold).
                    var fontEditOptions = new TextEditOptions(
                        TextEditOptions.FontReplace.RemoveUnusedFonts);
                    var pageAbsorber = new TextFragmentAbsorber(fontEditOptions);
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

                target.HorizontalAlignment = alignment switch
                {
                    Avalonia.Media.TextAlignment.Center => Aspose.Pdf.HorizontalAlignment.Center,
                    Avalonia.Media.TextAlignment.Right => Aspose.Pdf.HorizontalAlignment.Right,
                    _ => Aspose.Pdf.HorizontalAlignment.Left
                };

                // A new TextFragment ainda não está vinculado aos operadores de conteúdo
                // do PDF antes de AppendText. Aplicar TextState nesse momento altera apenas
                // o objeto em memória e o Aspose pode gravar o texto com estilo padrão.
                // Primeiro anexe o fragmento à página; depois aplique os estilos aos
                // segmentos já pertencentes ao documento. Para texto existente, ele já
                // está anexado e a mesma rotina continua funcionando.
                if (originalText == null)
                    new TextBuilder(document.Pages[pageIndex + 1]).AppendText(target);

                ApplyRichTextFormatting(
                    target, editedRuns, originalFontName, originalFontEmbedded,
                    originalBold, originalItalic, formattingTrace);

                var editedRectangle = target.Rectangle;
                if (editedRectangle != null)
                    expectedBounds = new Aspose.Pdf.Rectangle(
                        editedRectangle.LLX, editedRectangle.LLY,
                        editedRectangle.URX, editedRectangle.URY);
                else if (originalBounds != null)
                    expectedBounds = new Aspose.Pdf.Rectangle(
                        originalBounds.LLX, originalBounds.LLY,
                        originalBounds.URX, originalBounds.URY);
            }, originalText == null
                ? $"Texto inserido na página {pageIndex + 1}."
                : "Texto e formatação atualizados.",
                savedDocument =>
                {
                    if (pageIndex + 1 > savedDocument.Pages.Count)
                        return false;

                    var verificationAbsorber = new TextFragmentAbsorber();
                    savedDocument.Pages[pageIndex + 1].Accept(verificationAbsorber);
                    string savedPageText = NormalizePdfText(
                        string.Concat(verificationAbsorber.TextFragments.Select(item => item.Text)));
                    string expectedText = NormalizePdfText(replacementText);
                    if (expectedText.Length == 0 || !savedPageText.Contains(
                        expectedText, StringComparison.Ordinal))
                        return false;

                    string? formattingFailure = VerifyRichTextFormatting(
                        savedDocument, pageIndex, replacementText, editedRuns,
                        originalFontName, originalFontEmbedded,
                        originalBold, originalItalic, expectedBounds);
                    if (formattingFailure != null)
                        throw new InvalidOperationException(
                            "Diagnóstico da formatação: " + formattingFailure +
                            Environment.NewLine + "Rastreamento antes/depois da aplicação do estilo:" +
                            Environment.NewLine + string.Join(Environment.NewLine, formattingTrace));

                    return true;
                });
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = $"Não foi possível concluir a edição: {ex.Message}";
            await ShowEditingDiagnosticAsync(ex);
        }
    }

    private async Task ShowEditingDiagnosticAsync(Exception exception)
    {
        string diagnostic = exception.ToString();
        string? diagnosticPath = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(_sessionDirectory))
            {
                Directory.CreateDirectory(_sessionDirectory);
                diagnosticPath = Path.Combine(
                    _sessionDirectory,
                    $"diagnostico-edicao-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                await File.WriteAllTextAsync(diagnosticPath, diagnostic);
            }
        }
        catch (Exception logException)
        {
            diagnostic += Environment.NewLine + Environment.NewLine +
                "Não foi possível salvar o arquivo de diagnóstico: " + logException;
        }

        string dialogText = diagnosticPath == null
            ? diagnostic
            : diagnostic + Environment.NewLine + Environment.NewLine +
              "Arquivo completo: " + diagnosticPath;

        var detailsBox = new TextBox
        {
            Text = dialogText,
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 430,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        ScrollViewer.SetVerticalScrollBarVisibility(
            detailsBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(
            detailsBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var closeButton = new Button
        {
            Content = "Fechar",
            MinWidth = 110,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        var dialog = new Window
        {
            Title = "Diagnóstico completo da edição",
            Width = 780,
            Height = 560,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "A mensagem completa está abaixo. Você pode rolar, selecionar e copiar o texto.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    detailsBox,
                    closeButton
                }
            }
        };

        closeButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);

        if (diagnosticPath != null)
            EditorStatusText.Text += " Diagnóstico completo salvo em: " + diagnosticPath;
    }

    internal sealed record EditedTextRun(
        int Start,
        int Length,
        string? FontFamily,
        double FontSize,
        bool IsBold,
        bool IsItalic,
        Avalonia.Media.Color? Foreground,
        Avalonia.Media.Color? Background);

    // Aspose.PDF 26.11.0 creates fresh TextSegment instances in
    // IsolateTextSegments, but its CloneSegmentText helper does not restore
    // TextState.OwnerSegment. Without this back-reference, color/font/size
    // setters update only the detached state and never reach the source PDF.
    // Keep this compatibility bridge isolated here; fail clearly if Aspose
    // changes the internal contract in a future version.
    private static readonly PropertyInfo SegmentOwnerStateProperty =
        typeof(Aspose.Pdf.Text.TextState).GetProperty(
            "OwnerSegment",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "A versão do Aspose.PDF não expõe o vínculo interno de segmento esperado.");

    private static void RestoreSegmentGeometry(
        TextFragment fragment,
        IReadOnlyList<(int Start, int End, TextSegment Segment)> sourceSegments)
    {
        int offset = 0;
        foreach (TextSegment segment in fragment.Segments)
        {
            int segmentEnd = checked(offset + (segment.Text?.Length ?? 0));
            var source = sourceSegments.FirstOrDefault(item =>
                offset >= item.Start && segmentEnd <= item.End);

            if (source.Segment is not null)
            {
                int localStart = offset - source.Start;
                if (source.Segment.Position is { } sourcePosition)
                {
                    double prefixWidth = localStart == 0
                        ? 0
                        : source.Segment.PhysicalSegment.MeasureSegment(
                            0, localStart - 1, includeTrailingSpaces: true);
                    segment.Position = new Position(
                        sourcePosition.XIndent + prefixWidth,
                        sourcePosition.YIndent);
                }

                segment.BaselinePosition = source.Segment.BaselinePosition;
            }

            offset = segmentEnd;
        }
    }

    private static void RestoreSegmentStateOwnership(TextFragment fragment)
    {
        foreach (TextSegment segment in fragment.Segments)
            SegmentOwnerStateProperty.SetValue(segment.TextState, segment);
    }

    private static Avalonia.Media.Color? GetAvaloniaSolidColor(Avalonia.Media.IBrush? brush)
    {
        if (brush is Avalonia.Media.ISolidColorBrush solid)
            return solid.Color;
        return null;
    }

    internal static void ApplyRichTextFormatting(
        TextFragment fragment,
        IReadOnlyList<EditedTextRun> runs,
        string? originalFontName,
        bool originalFontEmbedded,
        bool originalBold = false,
        bool originalItalic = false,
        List<string>? formattingTrace = null)
    {
        var orderedRuns = runs
            .Where(item => item.Length > 0)
            .OrderBy(item => item.Start)
            .ToArray();

        if (orderedRuns.Length == 0)
            return;

        // Capture source geometry before isolating. Aspose's CloneSegmentText
        // copies the TextState but drops Position/BaselinePosition. Those anchors
        // are required by its font and colour writers to target the correct Tj/TJ
        // operator after a run is split.
        var sourceGeometry = new List<(int Start, int End, TextSegment Segment)>();
        int sourceOffset = 0;
        foreach (TextSegment sourceSegment in fragment.Segments)
        {
            int sourceEnd = checked(sourceOffset + (sourceSegment.Text?.Length ?? 0));
            sourceGeometry.Add((sourceOffset, sourceEnd, sourceSegment));
            sourceOffset = sourceEnd;
        }

        // IsolateTextSegments rebuilds the fragment's segment collection. Therefore,
        // do not keep TextSegment references returned by one call while isolating the
        // next range: those references can become detached from the final collection.
        // First isolate every range, then re-read the FINAL collection and map each
        // physical segment back to its logical editor run by character offsets.
        // Apply mapped styles in descending character order because font/size setters
        // rewrite the source stream and can move the later text on the same line.
        int previousEnd = 0;
        foreach (var run in orderedRuns)
        {
            int runEnd = checked(run.Start + run.Length);
            if (run.Start < 0 || run.Length <= 0 || runEnd > fragment.Text.Length)
            {
                throw new InvalidOperationException(
                    $"Intervalo de formatação inválido (posição {run.Start}, tamanho {run.Length}, texto com {fragment.Text.Length} caracteres).");
            }

            if (run.Start < previousEnd)
            {
                throw new InvalidOperationException(
                    "O editor retornou intervalos de formatação sobrepostos; a alteração foi cancelada para evitar aplicar estilos ao trecho errado.");
            }

            var isolated = fragment.IsolateTextSegments(run.Start, run.Length);
            if (isolated.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Não foi possível localizar o trecho formatado no PDF (posição {run.Start}, tamanho {run.Length}).");
            }

            previousEnd = runEnd;
        }

        // Restore geometry first, then the state-to-segment back-reference.
        // Both are lost by Aspose's current CloneSegmentText implementation.
        RestoreSegmentGeometry(fragment, sourceGeometry);
        RestoreSegmentStateOwnership(fragment);

        var appliedRuns = new bool[orderedRuns.Length];
        var segmentsToFormat = new List<(int Start, int End, int RunIndex, TextSegment Segment)>();
        int offset = 0;
        foreach (TextSegment segment in fragment.Segments)
        {
            int segmentLength = segment.Text?.Length ?? 0;
            if (segmentLength == 0)
                continue;

            int segmentEnd = checked(offset + segmentLength);
            int runIndex = Array.FindIndex(orderedRuns, run =>
                offset >= run.Start && segmentEnd <= run.Start + run.Length);

            if (runIndex < 0)
            {
                throw new InvalidOperationException(
                    $"O segmento PDF [{offset}, {segmentEnd}) não corresponde integralmente a um trecho formatado do editor. A edição foi cancelada para evitar estilos incorretos.");
            }

            segmentsToFormat.Add((offset, segmentEnd, runIndex, segment));
            offset = segmentEnd;
        }

        if (offset != fragment.Text.Length)
        {
            throw new InvalidOperationException(
                $"A segmentação PDF retornou {offset} caracteres, mas o texto possui {fragment.Text.Length}.");
        }

        // Applying font/size changes rewrites the source content stream and can
        // re-seat text that follows the edited run. Work from right to left so
        // every not-yet-edited segment keeps its original source position.
        foreach (var item in segmentsToFormat.OrderByDescending(item => item.Start))
        {
            ApplyRunStyle(
                item.Segment.TextState,
                orderedRuns[item.RunIndex],
                originalFontName,
                originalFontEmbedded,
                originalBold,
                originalItalic,
                formattingTrace);
            appliedRuns[item.RunIndex] = true;
        }

        for (int i = 0; i < appliedRuns.Length; i++)
        {
            if (!appliedRuns[i])
            {
                throw new InvalidOperationException(
                    $"Nenhum segmento PDF foi associado ao trecho formatado na posição {orderedRuns[i].Start}.");
            }
        }

        // Absorbed fragments deliberately do not infer their fragment-level state
        // from segments (TextFragmentState.SharedBySegments is disabled for them).
        // When the complete text has one uniform run, the writer takes the
        // fragment-level path (one segment) and would otherwise serialize the
        // original fragment size/face/style instead of the segment override.
        // Apply the same style to both levels so existing text and newly appended
        // text use the same serialization path.
        if (orderedRuns.Length == 1 &&
            orderedRuns[0].Start == 0 &&
            orderedRuns[0].Length == fragment.Text.Length)
        {
            ApplyRunStyle(
                fragment.TextState,
                orderedRuns[0],
                originalFontName,
                originalFontEmbedded,
                originalBold,
                originalItalic,
                formattingTrace);
        }
    }

    private static void ApplyRunStyle(
        Aspose.Pdf.Text.TextState state,
        EditedTextRun run,
        string? originalFontName,
        bool originalFontEmbedded,
        bool originalBold,
        bool originalItalic,
        List<string>? formattingTrace)
    {
        // Apply directly to the state owned by the final TextSegment/TextFragment.
        // A detached TextState followed by ApplyChangesFrom loses FontStyleTouched
        // and can route colour/font side effects through the wrong owner.
        string currentFontFamily = state.FontName ?? string.Empty;
        // Every editor run must carry an explicit face, including regular runs.
        // An isolated TextSegment can still inherit the fragment's face when its
        // FontTouched flag is false. That is exactly what caused a plain space
        // between two differently formatted runs to be serialized with the
        // neighbouring run's embedded italic face and size.
        string fontFamily = !string.IsNullOrWhiteSpace(run.FontFamily)
            ? run.FontFamily
            : !string.IsNullOrWhiteSpace(originalFontName)
                ? originalFontName
                : !string.IsNullOrWhiteSpace(currentFontFamily)
                    ? currentFontFamily
                    : "Helvetica";

        FontStyles requestedStyle =
            (run.IsBold ? FontStyles.Bold : FontStyles.Regular) |
            (run.IsItalic ? FontStyles.Italic : FontStyles.Regular);

        // Quando o PDF já contém a fonte incorporada, não force uma busca
        // no catálogo local para reaplicar a mesma família/estilo. O arquivo
        // original já possui os dados necessários e FindFont pode falhar
        // mesmo que a fonte esteja perfeitamente utilizável dentro do PDF.
        // Só preservamos a face incorporada quando o usuário não mudou família,
        // negrito ou itálico; alterações explícitas continuam exigindo uma face
        // instalada/baixada e validada antes da gravação.
        formattingTrace?.Add(
            $"ANTES: runFont='{fontFamily}'; originalFont='{originalFontName}'; " +
            $"originalEmbedded={originalFontEmbedded}; originalBold={originalBold}; " +
            $"requestedBold={run.IsBold}; originalItalic={originalItalic}; " +
            $"requestedItalic={run.IsItalic}; stateFont='{state.FontName}'; " +
            $"stateBold={state.IsBold}; stateStyle={state.FontStyle}; " +
            $"stateFontEmbedded={state.Font?.IsEmbedded}");

        bool preserveEmbeddedFace =
            originalFontEmbedded &&
            string.Equals(
                NormalizePdfFontFamily(fontFamily),
                NormalizePdfFontFamily(originalFontName),
                StringComparison.OrdinalIgnoreCase) &&
            run.IsBold == originalBold &&
            run.IsItalic == originalItalic;

        if (!preserveEmbeddedFace)
        {
            try
            {
                string faceName = GetPdfFontFaceName(fontFamily, run.IsBold, run.IsItalic);

                if (IsStandard14Family(fontFamily))
                {
                    // Assign the actual Standard-14 face through TextState.Font.
                    // FontName is metadata only: setting it does not rewrite the source Tf.
                    // The Font setter routes through TextStateModifier.ModifyFont, which
                    // registers a Type1 resource and switches the absorbed run in the PDF.
                    state.Font = FontRepository.FindFont(faceName);
                }
                else
                {
                    // Use the requested family member; assigning Font also marks the face
                    // as reassigned so the following size setter targets the isolated run.
                    state.Font = FontRepository.FindFont(fontFamily, requestedStyle);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Não foi possível localizar a fonte '{fontFamily}' no estilo " +
                    $"{(run.IsBold && run.IsItalic ? "negrito e itálico" : requestedStyle.ToString())}. " +
                    "Escolha outra família de fonte antes de aplicar a edição.",
                    ex);
            }

            // IsBold/IsItalic alteram apenas os valores em memória.
            // FontStyle também marca o estilo como explícito para o serializador
            // e aciona a atualização do fluxo de conteúdo quando aplicável.
            // Assigning IsBold/IsItalic alone changes the in-memory flags but leaves
            // FontStyleTouched false, so absorbed text is serialized with its old face.
            state.FontStyle = requestedStyle;
        }

        formattingTrace?.Add(
            $"DEPOIS: preservouCID={preserveEmbeddedFace}; stateFont='{state.FontName}'; " +
            $"stateBold={state.IsBold}; stateStyle={state.FontStyle}; " +
            $"fontName='{state.Font?.FontName}'; baseFont='{state.Font?.BaseFont}'; " +
            $"decodedFont='{state.Font?.DecodedFontName}'; " +
            $"fontEmbedded={state.Font?.IsEmbedded}; fontBold={state.Font?.IsBold}");

        // Assign colours directly to the final owned state. The setters retain the
        // values consumed by the PDF writer; on absorbed text they also perform the
        // content-stream update using the restored segment geometry.
        if (run.Foreground is { } foreground)
        {
            state.ForegroundColor = Aspose.Pdf.Color.FromRgb(
                foreground.R / 255d,
                foreground.G / 255d,
                foreground.B / 255d);
        }

        state.BackgroundColor = run.Background is { } background
            ? Aspose.Pdf.Color.FromRgb(
                background.R / 255d,
                background.G / 255d,
                background.B / 255d)
            : null;

        // Font/FontStyle setters can rewrite the source Tf operator for absorbed
        // text. Apply the requested size AFTER those setters so their font swap
        // cannot leave the segment using the fragment's previous size.
        if (run.FontSize > 0)
            state.FontSize = (float)run.FontSize;
    }

    private static bool IsStandard14Family(string family)
    {
        string normalized = NormalizePdfFontFamily(family);
        return normalized.Equals("Helvetica", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Arial", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Courier", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Courier New", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Times", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Times-Roman", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Times New Roman", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Symbol", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("ZapfDingbats", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPdfFontFaceName(string family, bool bold, bool italic)
    {
        string normalized = NormalizePdfFontFamily(family);
        bool isHelvetica = normalized.Equals("Helvetica", StringComparison.OrdinalIgnoreCase) ||
                           normalized.Equals("Arial", StringComparison.OrdinalIgnoreCase);
        bool isCourier = normalized.Equals("Courier", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Equals("Courier New", StringComparison.OrdinalIgnoreCase);
        bool isTimes = normalized.Equals("Times", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Equals("Times-Roman", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Equals("Times New Roman", StringComparison.OrdinalIgnoreCase);

        if (isHelvetica || isCourier)
        {
            string baseName = isHelvetica ? "Helvetica" : "Courier";
            if (bold && italic) return baseName + "-BoldOblique";
            if (bold) return baseName + "-Bold";
            if (italic) return baseName + "-Oblique";
            return baseName;
        }

        if (isTimes)
        {
            if (bold && italic) return "Times-BoldItalic";
            if (bold) return "Times-Bold";
            if (italic) return "Times-Italic";
            return "Times-Roman";
        }

        if (bold && italic) return normalized + "-BoldItalic";
        if (bold) return normalized + "-Bold";
        if (italic) return normalized + "-Italic";
        return normalized;
    }

    internal static bool IsUnchangedEmbeddedFontStyle(
        bool originalFontEmbedded,
        string? requestedFontFamily,
        string? originalFontFamily,
        bool originalBold,
        bool originalItalic,
        bool requestedBold,
        bool requestedItalic,
        bool savedFontEmbedded)
    {
        return originalFontEmbedded &&
               savedFontEmbedded &&
               string.Equals(
                   NormalizePdfFontFamily(requestedFontFamily),
                   NormalizePdfFontFamily(originalFontFamily),
                   StringComparison.OrdinalIgnoreCase) &&
               requestedBold == originalBold &&
               requestedItalic == originalItalic;
    }

    private static string? VerifyRichTextFormatting(
        Document document,
        int pageIndex,
        string expectedText,
        IReadOnlyList<EditedTextRun> expectedRuns,
        string? originalFontName,
        bool originalFontEmbedded,
        bool originalBold,
        bool originalItalic,
        Aspose.Pdf.Rectangle? expectedBounds)
    {
        string Fail(string reason) => reason;

        if (pageIndex < 0 || pageIndex >= document.Pages.Count)
            return Fail($"página inválida ({pageIndex + 1}).");

        var absorber = new TextFragmentAbsorber();
        document.Pages[pageIndex + 1].Accept(absorber);

        string normalizedExpected = NormalizePdfText(expectedText);
        var orderedFragments = absorber.TextFragments
            .Cast<TextFragment>()
            .Where(item => !string.IsNullOrEmpty(item.Text))
            .OrderByDescending(item => item.Rectangle == null
                ? double.MinValue
                : (item.Rectangle.LLY + item.Rectangle.URY) / 2)
            .ThenBy(item => item.Rectangle?.LLX ?? double.MaxValue)
            .ToArray();

        // A troca de fonte pode dividir um único trecho em vários TextFragments.
        // Procure uma sequência contígua cuja concatenação corresponda ao texto
        // editado, em vez de exigir que o Aspose devolva um único bloco.
        var matchingGroups = new List<TextFragment[]>();
        for (int start = 0; start < orderedFragments.Length; start++)
        {
            var group = new List<TextFragment>();
            var combined = new System.Text.StringBuilder();
            for (int end = start; end < orderedFragments.Length; end++)
            {
                group.Add(orderedFragments[end]);
                combined.Append(orderedFragments[end].Text);
                string normalizedCombined = NormalizePdfText(combined.ToString());

                if (normalizedCombined == normalizedExpected)
                {
                    matchingGroups.Add(group.ToArray());
                    break;
                }

                if (normalizedCombined.Length >= normalizedExpected.Length)
                    break;
            }
        }

        if (matchingGroups.Count == 0)
            return Fail("o texto editado não foi encontrado após reabrir o PDF, mesmo considerando blocos divididos.");

        // Se a mesma frase aparecer mais de uma vez, escolha o grupo mais próximo
        // da posição editada. Sem posição, só aceite uma ocorrência inequívoca.
        TextFragment[] selectedGroup;
        if (expectedBounds != null)
        {
            double expectedX = (expectedBounds.LLX + expectedBounds.URX) / 2;
            double expectedY = (expectedBounds.LLY + expectedBounds.URY) / 2;
            selectedGroup = matchingGroups
                .OrderBy(group =>
                {
                    var rectangles = group.Where(item => item.Rectangle != null)
                        .Select(item => item.Rectangle!).ToArray();
                    if (rectangles.Length == 0)
                        return double.MaxValue;
                    double centerX = rectangles.Average(bounds => (bounds.LLX + bounds.URX) / 2);
                    double centerY = rectangles.Average(bounds => (bounds.LLY + bounds.URY) / 2);
                    double dx = centerX - expectedX;
                    double dy = centerY - expectedY;
                    return (dx * dx) + (dy * dy);
                })
                .First();
        }
        else if (matchingGroups.Count == 1)
        {
            selectedGroup = matchingGroups[0];
        }
        else
        {
            return Fail("o texto aparece em mais de uma posição e não foi possível identificar com segurança o trecho editado.");
        }

        var savedSegments = new List<(int Start, int End, TextSegment Segment)>();
        int offset = 0;
        foreach (TextFragment selectedFragment in selectedGroup)
        {
            foreach (TextSegment segment in selectedFragment.Segments)
            {
                int end = offset + (segment.Text?.Length ?? 0);
                savedSegments.Add((offset, end, segment));
                offset = end;
            }
        }

        if (NormalizePdfText(string.Concat(selectedGroup.Select(item => item.Text))) != normalizedExpected)
            return Fail("os blocos recuperados não correspondem ao texto editado.");
        if (offset != expectedText.Length)
            return Fail($"os blocos relidos totalizam {offset} caracteres, mas a prévia enviou {expectedText.Length}; não foi possível mapear os estilos por posição.");

        foreach (var run in expectedRuns.Where(item => item.Length > 0))
        {
            int runEnd = run.Start + run.Length;
            var covered = savedSegments.Where(item =>
                item.Start < runEnd && item.End > run.Start).ToArray();

            if (covered.Length == 0)
                return Fail($"nenhum segmento salvo corresponde ao trecho na posição {run.Start}.");

            foreach (var item in covered)
            {
                var state = item.Segment.TextState;
                if (run.FontSize > 0 && Math.Abs(state.FontSize - run.FontSize) > 0.75)
                    return Fail($"tamanho da fonte divergente na posição {run.Start}: esperado {run.FontSize:0.##} pt, salvo {state.FontSize:0.##} pt.");
                bool unchangedEmbeddedFace = IsUnchangedEmbeddedFontStyle(
                    originalFontEmbedded,
                    run.FontFamily,
                    originalFontName,
                    originalBold,
                    originalItalic,
                    run.IsBold,
                    run.IsItalic,
                    state.Font?.IsEmbedded == true);
                if (state.IsBold != run.IsBold && !unchangedEmbeddedFace)
                {
                    var actualFont = state.Font;
                    string actualFontName = actualFont?.FontName ?? "(sem objeto Font)";
                    string actualBaseFont = actualFont?.BaseFont ?? "(sem BaseFont)";
                    string savedFontName = state.FontName ?? "(nulo)";
                    string fontDetails = actualFont == null
                        ? "Font=null"
                        : $"DecodedFontName='{actualFont.DecodedFontName}'; " +
                          $"ResourceName='{actualFont.ResourceName}'; " +
                          $"Subtype='{actualFont.Subtype}'; " +
                          $"Font.IsBold={actualFont.IsBold}; Font.IsItalic={actualFont.IsItalic}; " +
                          $"Font.IsCid={actualFont.IsCid}; Font.IsEmbedded={actualFont.IsEmbedded}; " +
                          $"Font.IsAccessible={actualFont.IsAccessible}";
                    return Fail(
                        $"negrito divergente na posição {run.Start}: esperado {run.IsBold}, " +
                        $"IsBold salvo={state.IsBold}; FontStyle salvo={state.FontStyle}; " +
                        $"FontName salvo='{savedFontName}'; " +
                        $"Font.FontName='{actualFontName}'; Font.BaseFont='{actualBaseFont}'; " +
                        fontDetails);
                }
                if (state.IsItalic != run.IsItalic && !unchangedEmbeddedFace)
                {
                    string savedFontName = state.FontName ?? "(nulo)";
                    return Fail(
                        $"itálico divergente na posição {run.Start}: esperado {run.IsItalic}, " +
                        $"salvo {state.IsItalic}; FontStyle={state.FontStyle}; FontName='{savedFontName}'.");
                }

                if (!string.IsNullOrWhiteSpace(run.FontFamily) &&
                    !(originalFontEmbedded && string.Equals(
                        run.FontFamily, originalFontName, StringComparison.OrdinalIgnoreCase)) &&
                    !string.Equals(state.FontName, run.FontFamily, StringComparison.OrdinalIgnoreCase))
                    return Fail($"fonte divergente na posição {run.Start}: esperada '{run.FontFamily}', salva '{state.FontName}'.");

                if (run.Foreground is { } foreground)
                {
                    if (state.ForegroundColor is not { } savedForeground)
                        return Fail($"a cor do texto não foi recuperada na posição {run.Start}.");
                    if (Math.Abs(savedForeground.R - foreground.R) > 2 ||
                        Math.Abs(savedForeground.G - foreground.G) > 2 ||
                        Math.Abs(savedForeground.B - foreground.B) > 2)
                        return Fail($"cor do texto divergente na posição {run.Start}: esperada RGB({foreground.R},{foreground.G},{foreground.B}), salva RGB({savedForeground.R},{savedForeground.G},{savedForeground.B}).");
                }

                // O Aspose pode gravar o realce como um operador gráfico separado
                // do texto. Nesse caso BackgroundColor não é reconstruído pelo
                // TextFragmentAbsorber ao reabrir o PDF, embora o fundo esteja visível.
                // A persistência visual do fundo é coberta pelos testes PDFium que
                // renderizam o PDF salvo e verificam os pixels do realce.
                // Não rejeite um PDF válido apenas pela ausência dessa propriedade.
            }
        }

        return null;
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
            }, "Texto atualizado.", savedDocument =>
            {
                var verificationAbsorber = new TextFragmentAbsorber();
                verificationAbsorber.Visit(savedDocument);
                string savedText = NormalizePdfText(
                    string.Concat(verificationAbsorber.TextFragments.Select(item => item.Text)));
                return changed == 0 || savedText.Contains(
                    NormalizePdfText(replacement), StringComparison.Ordinal);
            });
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

    private static string NormalizePdfText(string? value) =>
        new string((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)).ToArray());

    internal static string NormalizePdfFontFamily(string? value)
    {
        string family = (value ?? string.Empty).Trim();
        int separator = family.IndexOf('+');
        if (separator == 6 && family[..6].All(character =>
                character is >= 'A' and <= 'Z'))
            family = family[7..];

        // PDF BaseFont/DecodedFontName often stores the face name, not the
        // family name (for example "BQVIXP+VerdanaBold"). The editor keeps
        // weight and italic as separate properties, so strip the face suffix
        // before resolving the requested family/style in FontRepository.
        string[] faceSuffixes =
        {
            "-BoldItalic", "-BoldOblique", "BoldItalic", "BoldOblique",
            "-BoldMT", "BoldMT", "-ItalicMT", "ItalicMT",
            "-SemiBold", "SemiBold", "-DemiBold", "DemiBold",
            "-Bold", "Bold", "-Italic", "Italic", "-Oblique", "Oblique",
            "-Regular", "Regular", "-Roman", "Roman"
        };

        foreach (string suffix in faceSuffixes)
        {
            if (family.Length > suffix.Length &&
                family.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                family = family[..^suffix.Length].TrimEnd(' ', '-', '_');
                break;
            }
        }

        return family.Trim();
    }

    private void ApplyPdfEdit(
        Action<Document> edit,
        string successMessage,
        Func<Document, bool>? verifySavedDocument = null)
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

            // Reabre o arquivo efetivamente gravado antes de substituir a cópia de trabalho.
            // Isso impede que uma gravação aparentemente concluída, mas sem a alteração,
            // seja apresentada ao usuário como sucesso.
            using (var savedDocument = Document.Open(outputPath))
            {
                if (savedDocument.Pages.Count == 0)
                    throw new IOException("O PDF gerado não contém páginas.");

                if (verifySavedDocument != null && !verifySavedDocument(savedDocument))
                    throw new IOException(
                        "O PDF foi gerado, mas o texto ou a formatação não foram preservados no arquivo salvo. A cópia anterior foi preservada.");
            }

            _document?.Dispose();
            _document = null;

            // A substituição ocorre somente depois da validação do arquivo de saída.
            File.Move(outputPath, _workingCopyPath, true);
            PushHistory(_undoStack, before);
            _redoStack.Clear();
            LoadDocumentPreview();

            EditorStatusText.Text = successMessage +
                " Alteração gravada e visualização atualizada. O PDF original permanece intacto. " +
                "Atenção: qualquer alteração pode invalidar assinaturas digitais já existentes neste PDF.";
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
                EditorStatusText.Text = "Escolha outro nome/local. O PDF original e a cópia de trabalho permanente não podem ser sobrescritos.";
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

}