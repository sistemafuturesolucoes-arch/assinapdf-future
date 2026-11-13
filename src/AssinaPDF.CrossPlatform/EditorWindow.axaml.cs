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
using Avalonia.Input.Platform;
using AvaloniaImage = Avalonia.Controls.Image;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

internal sealed record PagePreviewInfo(int PageIndex, float WidthPoints, float HeightPoints, int Rotation);

public partial class EditorWindow : Window
{
    private string _workingCopyPath;
    private string _originalPath;
    private string _sessionDirectory;
    private PdfiumDocument? _document;
    private readonly List<WriteableBitmap> _bitmaps = new();
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private readonly EditorHistoryService _history = new();
    private string? _historyEntryId;
    private bool _hasExistingSignatures;
    private readonly Dictionary<int, (AvaloniaImage Image, Canvas Overlay)> _pageSurfaces = new();
    private readonly Dictionary<int, (Grid Surface, double Width, double Height)> _pageLayouts = new();
    private readonly Dictionary<int, Border> _pageCards = new();
    private double _zoomFactor = 1.0;
    private int? _selectedPageIndex;
    private double _selectedPdfX;
    private double _selectedPdfY;
    private enum EditorToolMode { Select, AddText, AddImage, DrawRectangle, DrawEllipse }
    private enum VisualElementKind { None, Image, Rectangle, Ellipse }
    private EditorToolMode _toolMode = EditorToolMode.Select;
    private bool _isDraggingSelection;
    private bool _isDrawingRectangle;
    private Border? _rectanglePreview;
    private Border? _visualSelectionBorder;
    private VisualElementKind _selectedVisualKind;
    private string? _selectedVisualName;
    private int _selectedImagePlacementIndex = -1;
    private byte[]? _selectedImageBytes;
    private int _selectedImageRotation;
    private (double Left, double Bottom, double Right, double Top)? _selectedVisualBounds;
    private bool _isInlineEditorOpen;
    private bool _autoTextBoxesVisible;
    private Avalonia.Point _selectionStart;
    private Avalonia.Point _selectionEnd;
    private AvaloniaImage? _activeSelectionImage;
    private PagePreviewInfo? _activeSelectionPage;
    private string _selectedText = string.Empty;
    private (double Left, double Bottom, double Right, double Top)? _selectedTextBounds;
    private RichEditorControl? _defaultToolbarEditor;
    private RichEditorToolbar? _formattingToolbar;
    private readonly Dictionary<string, string> _fontSubstitutions =
        new(StringComparer.OrdinalIgnoreCase);

    // Construtor sem parâmetros usado pelo carregador XAML do Avalonia.
    // A janela funcional é criada pelo fluxo do editor com os caminhos explícitos.
    public EditorWindow()
    {
        _workingCopyPath = string.Empty;
        _originalPath = string.Empty;
        _sessionDirectory = Path.Combine(
            Path.GetTempPath(), "FutureSolucoes", "AssinaPDF", "EditorSessions",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionDirectory);
        InitializeComponent();
        InitializeFormattingToolbar();
        KeyDown += EditorWindow_KeyDown;
        Closed += (_, _) => DisposeResources();
        AddPageButton.IsEnabled = false;
        ExportPdfButton.IsEnabled = false;
        EditorStatusText.Text = "Clique em “Abrir PDF” para começar. O documento original será preservado.";
    }

    public EditorWindow(string workingCopyPath, string originalPath, string sessionDirectory)
    {
        InitializeComponent();
        InitializeFormattingToolbar();
        KeyDown += EditorWindow_KeyDown;

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

    private static IReadOnlyList<string> GetEditorFontChoices()
    {
        // Keep the three PDF editor families from Typecraft at the top of the
        // list. The remaining installed families stay available as an extension.
        var installed = InstalledFontCatalogService.GetInstalledFamilyNames();
        return new[] { "Helvetica", "Times", "Courier" }
            .Concat(installed.Where(name =>
                !new[] { "Helvetica", "Times", "Courier" }.Contains(
                    name, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    private void InitializeFormattingToolbar()
    {
        RichEditorToolbar.FontSizes = Enumerable.Range(0, 393)
            .Select(index => 4d + index * 0.5d)
            .ToArray();

        _defaultToolbarEditor = new RichEditorControl
        {
            Document = new RichFlowDocument(),
            FontFamilyChoices = GetEditorFontChoices(),
            DefaultFontSize = 12,
            DefaultForeground = Avalonia.Media.Brushes.Black,
            AllowImages = false,
            AllowTables = false,
            AllowRichPaste = false,
            AllowFindReplace = false,
            ShowPageBoundaries = false
        };

        _formattingToolbar = new RichEditorToolbar
        {
            Target = _defaultToolbarEditor,
            ToolbarLevel = RichToolbarLevel.Normal
        };
        EditorFormattingToolbarHost.Content = _formattingToolbar;
    }

    private void EditorWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        // Do not steal keystrokes from the rich-text editor or property inputs.
        if (EditorInlineEditorHost.IsVisible || e.Source is TextBox or ComboBox)
        {
            if (e.Key == Key.Escape && EditorInlineEditorHost.IsVisible)
                return;
            return;
        }

        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                       e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                Redo_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
            else
                Undo_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (command && e.Key == Key.Y)
        {
            Redo_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectedVisualKind != VisualElementKind.None)
                DeleteSelectedVisualElement();
            else if (!string.IsNullOrWhiteSpace(_selectedText))
                DeleteSelectedText_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            ClearTextSelection();
            _toolMode = EditorToolMode.Select;
            EditorStatusText.Text = "Seleção limpa.";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_selectedText))
        {
            _ = EditSelectedText_ClickAsync();
            e.Handled = true;
            return;
        }

        if (command)
            return;

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            double deltaX = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            double deltaY = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            if (MoveSelectedElementByKeyboard(deltaX, deltaY))
                e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.V:
                SelectMode_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.T:
                AddTextMode_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.R:
                RectangleMode_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private bool MoveSelectedElementByKeyboard(double deltaXPoints, double deltaYPoints)
    {
        if (_selectedPageIndex is not int pageIndex ||
            !_pageSurfaces.TryGetValue(pageIndex, out var surface) ||
            surface.Image.Tag is not PagePreviewInfo pageInfo ||
            surface.Image.Bounds.Width <= 0 ||
            surface.Image.Bounds.Height <= 0)
            return false;

        bool rotated = pageInfo.Rotation is 90 or 270;
        double visibleWidthPoints = rotated ? pageInfo.HeightPoints : pageInfo.WidthPoints;
        double visibleHeightPoints = rotated ? pageInfo.WidthPoints : pageInfo.HeightPoints;
        double scaleX = surface.Image.Bounds.Width / visibleWidthPoints;
        double scaleY = surface.Image.Bounds.Height / visibleHeightPoints;
        double deltaViewX = deltaXPoints * scaleX;
        // Keyboard arrows use the same top-left coordinate convention as the Lovable editor.
        double deltaViewY = deltaYPoints * scaleY;

        if (_selectedVisualKind != VisualElementKind.None &&
            _visualSelectionBorder is { } border)
        {
            double left = Canvas.GetLeft(border);
            double top = Canvas.GetTop(border);
            if (!double.IsFinite(left) || !double.IsFinite(top))
                return false;

            ApplyVisualElementBounds(
                pageInfo, surface.Image,
                left + deltaViewX, top + deltaViewY,
                border.Width, border.Height);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_selectedText) &&
            _selectedTextBounds.HasValue)
        {
            _ = MoveSelectedTextAsync(pageInfo, surface.Image, deltaViewX, deltaViewY);
            return true;
        }

        return false;
    }

    private void TextTool_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AddTextMode_Click(sender, e);
    }

    private async Task InitializeDocumentAsync()
    {
        // A verificação e a obtenção das fontes acontecem antes da primeira
        // renderização, para que a prévia e o editor usem o mesmo conjunto de fontes.
        await VerifyDocumentFontsAsync();

        try
        {
            LoadDocumentPreview();
            await AuditDocumentSignaturesAsync();
            // Preserve the result/warning produced by VerifyDocumentFontsAsync.
            // It is more useful than replacing it with a generic "opened" message.
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    private async Task AuditDocumentSignaturesAsync()
    {
        if (!File.Exists(_workingCopyPath))
            return;

        try
        {
            byte[] pdfBytes = await File.ReadAllBytesAsync(_workingCopyPath);
            var signingService = new PdfSigningService();
            var results = await signingService.ValidateAsync(pdfBytes);

            _hasExistingSignatures = results.Count > 0;
            if (results.Count == 0)
                return;

            int intact = results.Count(result =>
                result.IsIntegrityValid && result.IsSignatureValid);
            int notConfirmed = results.Count - intact;

            EditorStatusText.Text = notConfirmed == 0
                ? $"Auditoria concluída: {results.Count} assinatura(s) digital(is) confirmada(s). O original será preservado; qualquer edição afeta somente a cópia de trabalho e pode invalidar essas assinaturas."
                : $"ATENÇÃO: o PDF contém {results.Count} assinatura(s); {notConfirmed} não tiveram integridade/validade confirmada. O arquivo original permanece preservado.";
        }
        catch (Exception ex)
        {
            _hasExistingSignatures = false;
            EditorStatusText.Text =
                "Não foi possível concluir a auditoria das assinaturas digitais. O PDF original permanece preservado. " +
                ex.Message;
        }
    }

    private static void RegisterDocumentFont(
        Aspose.Pdf.Text.Font? font,
        string? stateFontName,
        IDictionary<string, bool> fonts,
        IDictionary<string, bool> unidentifiedCidFonts)
    {
        string rawName = font?.DecodedFontName ??
                         font?.BaseFont ??
                         stateFontName ??
                         string.Empty;
        string family = NormalizePdfFontFamily(rawName);
        // IsEmbedded pode ser reportado como false por alguns leitores para fontes CID/Type0
        // mesmo quando o programa TrueType está incorporado no PDF. IsAccessible também
        // considera programas de fonte extraídos do próprio documento; evita pedir ao
        // usuário uma fonte externa quando o PDF já contém os glifos necessários.
        bool embedded = font?.IsEmbedded == true || font?.IsAccessible == true;

        // CIDFont+F2 is a PDF resource identifier, not a trustworthy family name.
        // Keep it in the audit instead of silently dropping it or trying to
        // download a fictitious family named after the internal PDF resource.
        bool unidentifiedCid =
            family.StartsWith("CIDFONT+", StringComparison.OrdinalIgnoreCase) ||
            (font?.IsCid == true &&
             (string.IsNullOrWhiteSpace(family) || family.Length <= 2));

        if (unidentifiedCid)
        {
            string descriptor = string.IsNullOrWhiteSpace(rawName)
                ? "fonte CID sem nome de família"
                : rawName.Trim();
            if (unidentifiedCidFonts.TryGetValue(descriptor, out bool wasCidEmbedded))
                unidentifiedCidFonts[descriptor] = wasCidEmbedded || embedded;
            else
                unidentifiedCidFonts[descriptor] = embedded;
            return;
        }

        if (string.IsNullOrWhiteSpace(family) || family.Length <= 2)
            return;

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

        // A auditoria de fontes nunca deve bloquear a abertura do documento.
        // Registra as pastas nativas antes de consultar Aspose.FontRepository.
        InstalledFontCatalogService.RegisterSystemFontDirectories();

        try
        {
            var fonts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var unidentifiedCidFonts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            using (var document = Document.Open(_workingCopyPath))
            {
                foreach (Aspose.Pdf.Page page in document.Pages)
                {
                    var absorber = new TextFragmentAbsorber();
                    page.Accept(absorber);

                    foreach (TextFragment fragment in absorber.TextFragments)
                    {
                        var segments = fragment.Segments.Cast<TextSegment>().ToArray();
                        if (segments.Length == 0)
                        {
                            RegisterDocumentFont(
                                fragment.TextState.Font,
                                fragment.TextState.FontName,
                                fonts,
                                unidentifiedCidFonts);
                            continue;
                        }

                        foreach (TextSegment segment in segments)
                        {
                            RegisterDocumentFont(
                                segment.TextState.Font,
                                segment.TextState.FontName,
                                fonts,
                                unidentifiedCidFonts);
                        }
                    }
                }
            }

            var missing = fonts
                .Where(item => !item.Value && !FontDownloadService.IsAvailable(item.Key))
                .Select(item => item.Key)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            var missingCid = unidentifiedCidFonts
                .Where(item => !item.Value)
                .Select(item => item.Key)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            if (missing.Length == 0 && missingCid.Length == 0)
            {
                EditorStatusText.Text = fonts.Count == 0 && unidentifiedCidFonts.Count == 0
                    ? "PDF aberto. Não foram encontrados textos com fontes identificáveis."
                    : $"Verificação concluída: {fonts.Count} família(s) identificada(s); nenhuma fonte conhecida ausente.";
                return;
            }

            var warnings = new List<string>();
            if (missing.Length > 0)
                warnings.Add("Fontes não disponíveis: " + string.Join(", ", missing) + ".");
            if (missingCid.Length > 0)
                warnings.Add(
                    "Fontes CID não incorporadas (identificadores internos, não nomes comerciais): " +
                    string.Join(", ", missingCid) + ".");

            EditorStatusText.Text =
                "PDF aberto. " + string.Join(" ", warnings) +
                " A verificação não bloqueará a visualização nem abrirá janelas repetidas. " +
                "A aparência pode depender da substituição do mecanismo de visualização. " +
                "Se você editar um trecho que dependa de uma fonte ausente, escolha uma fonte substituta ou instale a fonte original antes de aplicar.";
        }
        catch (Exception ex)
        {
            EditorStatusText.Text =
                "Não foi possível concluir a verificação de fontes. O PDF continua aberto; " +
                "a abertura e a visualização não serão bloqueadas. " + ex.Message;
        }
    }

    private async Task<string?> AskSubstituteFontAsync(string missingFamily)
    {
        var choices = InstalledFontCatalogService.GetInstalledFamilyNames();
        if (choices.Count == 0)
        {
            EditorStatusText.Text =
                $"Não há fontes instaladas disponíveis para substituir '{missingFamily}'.";
            return null;
        }

        var selector = new ComboBox
        {
            ItemsSource = choices,
            SelectedIndex = 0,
            MinWidth = 280
        };
        var dialog = new Window
        {
            Title = "Escolher fonte substituta",
            Width = 580,
            Height = 360,
            MinWidth = 540,
            MinHeight = 340,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        // A janela precisa reservar espaço para a mensagem, a lista e os dois
        // botões. Com 245 px de altura, a linha de comandos ficava cortada.
        var cancel = new Button
        {
            Content = "Continuar sem substituir",
            MinWidth = 205,
            Padding = new Thickness(12, 8)
        };
        var apply = new Button
        {
            Content = "Substituir pela fonte selecionada",
            MinWidth = 220,
            Padding = new Thickness(12, 8)
        };
        cancel.Click += (_, _) => dialog.Close((string?)null);
        apply.Click += (_, _) => dialog.Close(selector.SelectedItem as string);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = $"A fonte utilizada neste PDF é: '{missingFamily}'.\n\nDeseja substituir essa fonte por uma fonte instalada?\n\nSe escolher \"Continuar sem substituir\", nenhuma substituição será aplicada e este trecho não será alterado. Você poderá baixar/instalar a fonte original e abrir o PDF novamente para que o AssinaPDF carregue a fonte correta.\n\nAtenção: nomes iniciados por CIDFont+ são identificadores internos do PDF e não revelam o nome comercial da fonte.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                selector,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, apply }
                }
            }
        };

        return await dialog.ShowDialog<string?>(this);
    }

    private async Task ShowFontDownloadInstructionAsync(string fontName)
    {
        string detail = fontName.StartsWith("CIDFONT+", StringComparison.OrdinalIgnoreCase)
            ? $"O PDF informou o identificador interno '{fontName}', não o nome real da família. Consulte o criador do documento ou obtenha a fonte original usada na geração do PDF."
            : $"A fonte original identificada é '{fontName}'. Baixe-a de uma fonte confiável e instale-a no computador.";

        var close = new Button
        {
            Content = "Entendi",
            MinWidth = 110,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        var dialog = new Window
        {
            Title = "Fonte original necessária",
            Width = 540,
            Height = 250,
            MinWidth = 480,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = detail + Environment.NewLine + Environment.NewLine +
                               "Depois de obter a fonte, feche este editor e abra o PDF novamente. O AssinaPDF fará uma nova leitura das fontes. A edição foi cancelada e o arquivo original permanece preservado.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    close
                }
            }
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void LoadDocumentPreview()
    {
        AddPageButton.IsEnabled = true;
        ExportPdfButton.IsEnabled = true;
        _document?.Dispose();
        _document = null;

        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
        _bitmaps.Clear();
        EditorPagesPanel.Children.Clear();
        EditorThumbnailsPanel.Children.Clear();
        _pageSurfaces.Clear();
        _pageLayouts.Clear();
        _pageCards.Clear();
        _zoomFactor = 1.0;
        EditorZoomText.Text = "100%";
        ClearTextSelection();

        _document = new PdfiumDocument(_workingCopyPath);

        // Limita a memória total usada pelas páginas e miniaturas. Em PDFs
        // extensos, a escala é distribuída entre as páginas em vez de carregar
        // centenas de imagens em resolução máxima ao mesmo tempo.
        const long maximumTotalPreviewPixels = 40_000_000;
        const long minimumPixelsPerPage = 20_000;
        long renderedPixels = 0;
        bool previewReduced = false;

        for (int i = 0; i < _document.PageCount; i++)
        {
            var dimensions = _document.GetPageDimensionsPoints(i);
            double pageArea = Math.Max(1, dimensions.Width * (double)dimensions.Height);
            int pagesRemaining = _document.PageCount - i;
            long remainingBudget = Math.Max(
                minimumPixelsPerPage,
                maximumTotalPreviewPixels - renderedPixels);
            double pageBudget = Math.Max(
                minimumPixelsPerPage,
                remainingBudget / (double)pagesRemaining);
            double renderScale = Math.Min(1.25, Math.Sqrt(pageBudget / pageArea));
            renderScale = Math.Max(0.12, renderScale);
            if (renderScale < 0.50)
                previewReduced = true;

            var rendered = _document.RenderPage(i, (float)renderScale);
            renderedPixels += (long)rendered.Width * rendered.Height;
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
                                    Tag = new PagePreviewInfo(i, rendered.OriginalPageWidthPoints, rendered.OriginalPageHeightPoints, rendered.PageRotation)
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

            pageSurface.Width = rendered.Width;
            pageSurface.Height = rendered.Height;
            var pageImage = pageSurface.Children.OfType<AvaloniaImage>().Single();
            var overlay = pageSurface.Children.OfType<Canvas>().Single();
            _pageLayouts[i] = (pageSurface, rendered.Width, rendered.Height);
            pageImage.Cursor = new Cursor(StandardCursorType.Arrow);
            pageImage.PointerPressed += PageImage_PointerPressed;
            pageImage.PointerMoved += PageImage_PointerMoved;
            pageImage.PointerReleased += PageImage_PointerReleased;
            // Em algumas combinações de Avalonia/driver, a roda não chega ao
            // ScrollViewer quando o ponteiro está sobre a imagem renderizada.
            // Trate-a diretamente e atualize o deslocamento do documento.
            pageImage.PointerWheelChanged += EditorDocumentScrollViewer_PointerWheelChanged;
            page.ContextMenu = CreateSelectionContextMenu();
            pageImage.ContextMenu = CreateSelectionContextMenu();
            _pageSurfaces[i] = (pageImage, overlay);
            _pageCards[i] = page;
            EditorPagesPanel.Children.Add(page);

            if (_autoTextBoxesVisible)
                AddAutoTextBoxesForPage(i);

            // Miniatura reutiliza a mesma imagem renderizada da página principal.
            // Isso evita duplicar a rasterização e mantém a navegação leve.
            var thumbnailButton = new Button
            {
                Tag = i,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(7),
                Background = Avalonia.Media.Brushes.White,
                BorderBrush = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(205, 209, 216)),
                BorderThickness = new Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(5),
                Content = new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new AvaloniaImage
                        {
                            Source = bitmap,
                            Width = 112,
                            MaxHeight = 142,
                            Stretch = Avalonia.Media.Stretch.Uniform
                        },
                        new TextBlock
                        {
                            Text = $"Página {i + 1}",
                            FontSize = 10,
                            Foreground = new Avalonia.Media.SolidColorBrush(
                                Avalonia.Media.Color.FromRgb(76, 82, 92)),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                        }
                    }
                }
            };
            thumbnailButton.Click += Thumbnail_Click;
            EditorThumbnailsPanel.Children.Add(thumbnailButton);
        }

        if (previewReduced)
            EditorStatusText.Text =
                $"Documento com {_document.PageCount} páginas: a resolução das prévias foi reduzida automaticamente para controlar o uso de memória. O PDF original não foi alterado.";
    }

    private void Thumbnail_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int pageIndex)
            return;

        ScrollToPage(pageIndex);
        EditorStatusText.Text = $"Página {pageIndex + 1} de {_pageCards.Count}.";
    }

    private void ScrollToPage(int pageIndex)
    {
        if (!_pageCards.TryGetValue(pageIndex, out Border? pageCard))
            return;

        // Aguarda o layout do Avalonia concluir a medição/arranjo da página.
        // Depois calcula o deslocamento no conteúdo real do ScrollViewer.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(pageCard.Parent, EditorPagesPanel))
                return;

            pageCard.BringIntoView();
            double targetY = pageCard.Bounds.Y;
            double maxOffset = Math.Max(
                0,
                EditorDocumentScrollViewer.Extent.Height -
                EditorDocumentScrollViewer.Viewport.Height);
            double targetOffset = Math.Clamp(targetY, 0, maxOffset);

            EditorDocumentScrollViewer.Offset = new Vector(
                EditorDocumentScrollViewer.Offset.X,
                targetOffset);
        }, DispatcherPriority.Background);
    }

    private void EditorDocumentScrollViewer_PointerWheelChanged(
        object? sender,
        PointerWheelEventArgs e)
    {
        double delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.001)
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SetEditorZoom(_zoomFactor * (delta > 0 ? 1.10 : 1.0 / 1.10));
            e.Handled = true;
            return;
        }

        double maxOffset = Math.Max(
            0,
            EditorDocumentScrollViewer.Extent.Height -
            EditorDocumentScrollViewer.Viewport.Height);
        double targetOffset = Math.Clamp(
            EditorDocumentScrollViewer.Offset.Y - (delta * 72),
            0,
            maxOffset);

        EditorDocumentScrollViewer.Offset = new Vector(
            EditorDocumentScrollViewer.Offset.X,
            targetOffset);
        e.Handled = true;
    }

    private void ZoomOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetEditorZoom(_zoomFactor / 1.10);
    }

    private void ZoomIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetEditorZoom(_zoomFactor * 1.10);
    }

    private void SetEditorZoom(double requestedZoom)
    {
        double nextZoom = Math.Clamp(requestedZoom, 0.50, 3.00);
        if (Math.Abs(nextZoom - _zoomFactor) < 0.001)
            return;

        _zoomFactor = nextZoom;
        foreach (var item in _pageLayouts.Values)
        {
            item.Surface.Width = item.Width * _zoomFactor;
            item.Surface.Height = item.Height * _zoomFactor;
        }

        EditorZoomText.Text = $"{Math.Round(_zoomFactor * 100):0}%";
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

    private ContextMenu CreateSelectionContextMenu()
    {
        var editItem = new MenuItem { Header = "Editar texto" };
        editItem.Click += (_, _) =>
        {
            if (_selectedVisualKind != VisualElementKind.None)
            {
                EditorStatusText.Text =
                    "Imagens e formas podem ser movidas, redimensionadas ou excluídas. A edição de conteúdo é destinada a textos.";
                return;
            }
            _ = EditSelectedText_ClickAsync();
        };
        var copyItem = new MenuItem { Header = "Copiar" };
        copyItem.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(_selectedText))
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(_selectedText);
            }
        };
        return new ContextMenu { Items = { editItem, copyItem } };
    }

    private void PageImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not AvaloniaImage image || image.Tag is not PagePreviewInfo pageInfo)
            return;
        var point = e.GetPosition(image);
        if (image.Bounds.Width <= 0 || image.Bounds.Height <= 0 ||
            point.X < 0 || point.Y < 0 || point.X > image.Bounds.Width || point.Y > image.Bounds.Height)
            return;

        if (e.GetCurrentPoint(image).Properties.IsRightButtonPressed)
        {
            if (string.IsNullOrWhiteSpace(_selectedText))
                SelectTextAtPoint(image, pageInfo, point);
            // Não marcar o evento como tratado: o ContextMenu do Avalonia
            // precisa receber o clique direito para exibir Editar/Copiar.
            return;
        }
        if (!e.GetCurrentPoint(image).Properties.IsLeftButtonPressed)
            return;
        if (_toolMode == EditorToolMode.AddText)
        {
            e.Handled = true;
            _ = AddTextAtPointAsync(pageInfo, point, image);
            return;
        }
        if (_toolMode == EditorToolMode.AddImage)
        {
            e.Handled = true;
            _ = AddImageAtPointAsync(pageInfo, point, image);
            return;
        }
        if (_toolMode is EditorToolMode.DrawRectangle or EditorToolMode.DrawEllipse)
        {
            e.Handled = true;
            _isDrawingRectangle = true;
            _selectionStart = point;
            _selectionEnd = point;
            _activeSelectionImage = image;
            _activeSelectionPage = pageInfo;
            e.Pointer.Capture(image);
            UpdateRectanglePreview(image, pageInfo, point, point);
            return;
        }

        // O segundo clique abre a edição do texto sob o ponteiro. O clique simples
        // continua reservado à seleção por arraste, sem abrir janelas acidentalmente.
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            ClearTextSelection();
            SelectTextAtPoint(image, pageInfo, point);
            if (_selectedPageIndex == pageInfo.PageIndex &&
                !string.IsNullOrWhiteSpace(_selectedText) &&
                _selectedTextBounds is { } selectedBounds)
            {
                _ = EditTextAtPointAsync(
                    pageInfo.PageIndex, _selectedPdfX, _selectedPdfY,
                    _selectedText, selectedBounds);
            }
            else
            {
                EditorStatusText.Text = "Não encontrei texto editável nesse ponto.";
            }
            return;
        }

        ClearTextSelection();
        _isDraggingSelection = true;
        _selectionStart = point;
        _selectionEnd = point;
        _activeSelectionImage = image;
        _activeSelectionPage = pageInfo;
        e.Pointer.Capture(image);
        e.Handled = true;
    }

    private void PageImage_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is AvaloniaImage image && image.Tag is PagePreviewInfo info &&
            _pageSurfaces.TryGetValue(info.PageIndex, out var surface))
        {
            if (_toolMode == EditorToolMode.AddText)
            {
                image.Cursor = new Cursor(StandardCursorType.Ibeam);
                var marker = surface.Overlay.Children.OfType<TextBlock>()
                    .FirstOrDefault(item => Equals(item.Tag, "FutureTextCursor"));
                if (marker == null)
                {
                    marker = new TextBlock
                    {
                        Text = "T", Tag = "FutureTextCursor", FontSize = 16,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        Foreground = Avalonia.Media.Brushes.White,
                        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(76, 52, 130)),
                        Padding = new Thickness(4, 1), IsHitTestVisible = false
                    };
                    surface.Overlay.Children.Add(marker);
                }
                var pointer = e.GetPosition(image);
                Canvas.SetLeft(marker, pointer.X + 9);
                Canvas.SetTop(marker, pointer.Y + 9);
            }
            else
            {
                image.Cursor = new Cursor(_toolMode switch
                {
                    EditorToolMode.AddImage => StandardCursorType.Hand,
                    EditorToolMode.DrawRectangle or EditorToolMode.DrawEllipse => StandardCursorType.Cross,
                    _ => StandardCursorType.Arrow
                });
                foreach (var marker in surface.Overlay.Children.OfType<TextBlock>()
                             .Where(item => Equals(item.Tag, "FutureTextCursor")).ToArray())
                    surface.Overlay.Children.Remove(marker);
            }
        }
        if (_isDrawingRectangle &&
            _activeSelectionImage is AvaloniaImage drawingImage &&
            ReferenceEquals(sender, drawingImage) &&
            _activeSelectionPage is PagePreviewInfo drawingPage)
        {
            _selectionEnd = e.GetPosition(drawingImage);
            UpdateRectanglePreview(drawingImage, drawingPage, _selectionStart, _selectionEnd);
        }

        if (_isDraggingSelection && ReferenceEquals(sender, _activeSelectionImage))
            _selectionEnd = e.GetPosition(_activeSelectionImage);
    }

    private void PageImage_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDrawingRectangle && sender is AvaloniaImage drawingImage &&
            _activeSelectionPage is PagePreviewInfo drawingPage)
        {
            _selectionEnd = e.GetPosition(drawingImage);
            _isDrawingRectangle = false;
            e.Pointer.Capture(null);
            RemoveRectanglePreview(drawingPage.PageIndex);
            AddShapeFromDrag(
                drawingPage, _selectionStart, _selectionEnd, drawingImage,
                _toolMode == EditorToolMode.DrawEllipse
                    ? VisualElementKind.Ellipse
                    : VisualElementKind.Rectangle);
            _activeSelectionImage = null;
            _activeSelectionPage = null;
            e.Handled = true;
            return;
        }

        if (!_isDraggingSelection || sender is not AvaloniaImage image ||
            _activeSelectionPage is not PagePreviewInfo pageInfo)
            return;

        _selectionEnd = e.GetPosition(image);
        _isDraggingSelection = false;
        e.Pointer.Capture(null);

        SelectTextInRectangle(image, pageInfo, _selectionStart, _selectionEnd);

        // Selecionar texto não abre o editor automaticamente. A edição de
        // conteúdo existente é explícita; a ferramenta T insere texto novo.


        _activeSelectionImage = null;
        _activeSelectionPage = null;
        e.Handled = true;
    }

    private void SelectTextAtPoint(AvaloniaImage image, PagePreviewInfo pageInfo, Avalonia.Point point)
    {
        if (!PdfiumPageCoordinateMapper.TryViewToPdf(point.X, point.Y, image.Bounds.Width, image.Bounds.Height,
            pageInfo.WidthPoints, pageInfo.HeightPoints, out double pdfX, out double pdfY, pageInfo.Rotation) || _document == null)
            return;

        try
        {
            // Primeiro tenta selecionar o objeto de texto inteiro do PDF (em vez
            // de somente a palavra sob o mouse). Assim, um texto recém-inserido
            // pode ser clicado e arrastado como um único bloco, inclusive após reabrir.
            using (var source = Document.Open(_workingCopyPath))
            {
                var absorber = new TextFragmentAbsorber();
                source.Pages[pageInfo.PageIndex + 1].Accept(absorber);
                var fragment = absorber.TextFragments
                    .Where(item => item.Rectangle != null && !string.IsNullOrWhiteSpace(item.Text))
                    .Select(item => new
                    {
                        Fragment = item,
                        Bounds = item.Rectangle!,
                        Distance = Math.Pow(
                            pdfX < item.Rectangle!.LLX ? item.Rectangle.LLX - pdfX :
                            pdfX > item.Rectangle.URX ? pdfX - item.Rectangle.URX : 0, 2) +
                            Math.Pow(
                            pdfY < item.Rectangle.LLY ? item.Rectangle.LLY - pdfY :
                            pdfY > item.Rectangle.URY ? pdfY - item.Rectangle.URY : 0, 2)
                    })
                    .Where(item => item.Distance <= 25)
                    .OrderBy(item => item.Distance)
                    .FirstOrDefault();

                if (fragment != null)
                {
                    var objectCharacters = _document.InspectPageCharacters(pageInfo.PageIndex)
                        .Where(ch => ch.Right >= fragment.Bounds.LLX - 1 &&
                                     ch.Left <= fragment.Bounds.URX + 1 &&
                                     ch.Top >= fragment.Bounds.LLY - 1 &&
                                     ch.Bottom <= fragment.Bounds.URY + 1 &&
                                     !string.IsNullOrEmpty(ch.Text))
                        .OrderBy(ch => ch.CharacterIndex)
                        .ToArray();
                    if (objectCharacters.Length > 0)
                    {
                        SetTextSelection(image, pageInfo, objectCharacters, fragment.Fragment.Text);
                        return;
                    }
                }
            }

            var characters = _document.InspectPageCharacters(pageInfo.PageIndex);
            var lines = PdfiumTextGrouping.GroupIntoLines(characters, pageInfo.PageIndex);
            var word = PdfiumTextHitTest.FindWordAtPoint(lines, pageInfo.PageIndex, pdfX, pdfY, 3.0);
            var line = PdfiumTextHitTest.FindLineAtPoint(lines, pageInfo.PageIndex, pdfX, pdfY, 3.0);

            // Em PDFs com mapeamento de texto fragmentado, o agrupador pode
            // reconhecer somente um glifo como palavra. Nesse caso, prioriza
            // a linha completa para evitar que o clique selecione apenas uma letra.
            if (line != null && line.Characters.Count > 1 &&
                (word == null || word.Characters.Count <= 1))
            {
                SetTextSelection(image, pageInfo, line.Characters, line.Text);
                return;
            }
            if (word != null)
            {
                SetTextSelection(image, pageInfo, word.Characters, word.Text);
                return;
            }
            if (line != null && line.Characters.Count > 1)
            {
                SetTextSelection(image, pageInfo, line.Characters, line.Text);
                return;
            }
            var character = PdfiumTextHitTest.FindCharacterAtPoint(characters, pageInfo.PageIndex, pdfX, pdfY, 3.0);
            if (character != null)
            {
                SetTextSelection(image, pageInfo, new[] { character }, character.Text);
                return;
            }

            // Texto tem prioridade sobre imagens de fundo e formas que possam
            // cobrir visualmente a mesma área. Elementos visuais são selecionados
            // quando não há texto sob o ponto clicado.
            TrySelectVisualElementAtPoint(image, pageInfo, pdfX, pdfY);
        }
        catch (Exception ex) { EditorStatusText.Text = $"Não foi possível localizar o texto: {ex.Message}"; }
    }

    private void SelectTextInRectangle(AvaloniaImage image, PagePreviewInfo pageInfo, Avalonia.Point start, Avalonia.Point end)
    {
        if (_document == null) return;
        double left = Math.Min(start.X, end.X), right = Math.Max(start.X, end.X);
        double top = Math.Min(start.Y, end.Y), bottom = Math.Max(start.Y, end.Y);
        if (right - left < 3 && bottom - top < 3)
        {
            SelectTextAtPoint(image, pageInfo, end);
            return;
        }
        if (!PdfiumPageCoordinateMapper.TryViewToPdf(left, top, image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints, out double x1, out double yTop, pageInfo.Rotation) ||
            !PdfiumPageCoordinateMapper.TryViewToPdf(right, bottom, image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints, out double x2, out double yBottom, pageInfo.Rotation))
            return;
        try
        {
            var selected = _document.InspectPageCharacters(pageInfo.PageIndex)
                .Where(ch => ch.Right >= Math.Min(x1, x2) && ch.Left <= Math.Max(x1, x2) &&
                    ch.Top >= Math.Min(yBottom, yTop) && ch.Bottom <= Math.Max(yBottom, yTop) &&
                    !string.IsNullOrEmpty(ch.Text))
                .OrderBy(ch => ch.CharacterIndex).ToArray();
            if (selected.Length == 0)
            {
                EditorStatusText.Text = "Nenhum texto encontrado nessa área. PDFs digitalizados precisam de OCR.";
                return;
            }
            var selectedLines = PdfiumTextGrouping.GroupIntoLines(selected, pageInfo.PageIndex);
            string selectedText = string.Join("\n", selectedLines.Select(line => line.Text.TrimEnd()));
            SetTextSelection(image, pageInfo, selected, selectedText);
        }
        catch (Exception ex) { EditorStatusText.Text = $"Não foi possível selecionar o trecho: {ex.Message}"; }
    }

    private void SetTextSelection(AvaloniaImage image, PagePreviewInfo pageInfo,
        IReadOnlyList<PdfiumCharacterInfo> characters, string text, bool drawHighlight = true)
    {
        if (characters.Count == 0 || !_pageSurfaces.TryGetValue(pageInfo.PageIndex, out var surface))
            return;
        ClearTextSelection();
        double left = characters.Min(ch => ch.Left), bottom = characters.Min(ch => ch.Bottom);
        double right = characters.Max(ch => ch.Right), top = characters.Max(ch => ch.Top);
        _selectedPageIndex = pageInfo.PageIndex;
        _selectedPdfX = (left + right) / 2;
        _selectedPdfY = (bottom + top) / 2;
        _selectedText = text;
        _selectedTextBounds = (left, bottom, right, top);
        var rect = PdfiumPageCoordinateMapper.PdfToViewRect(left, bottom, right, top,
            image.Bounds.Width, image.Bounds.Height, pageInfo.WidthPoints, pageInfo.HeightPoints, pageInfo.Rotation);
        if (drawHighlight)
        {
        var highlight = new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(35, 116, 220)),
            BorderThickness = new Thickness(1.5),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(62, 35, 116, 220)),
            Width = Math.Max(6, rect.Width), Height = Math.Max(6, rect.Height),
            IsHitTestVisible = true, Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        double selectionLeft = rect.X, selectionTop = rect.Y;
        bool draggingText = false;
        Avalonia.Point dragStart = default;
        highlight.PointerPressed += (_, args) =>
        {
            if (args.ClickCount >= 2)
            {
                args.Handled = true;
                if (_selectedTextBounds is { } selectedBounds &&
                    !string.IsNullOrWhiteSpace(_selectedText))
                {
                    _ = EditTextAtPointAsync(
                        pageInfo.PageIndex, _selectedPdfX, _selectedPdfY,
                        _selectedText, selectedBounds);
                }
                return;
            }
            if (!args.GetCurrentPoint(highlight).Properties.IsLeftButtonPressed) return;
            draggingText = true;
            dragStart = args.GetPosition(surface.Overlay);
            args.Pointer.Capture(highlight);
            args.Handled = true;
        };
        highlight.PointerMoved += (_, args) =>
        {
            if (!draggingText) return;
            var current = args.GetPosition(surface.Overlay);
            Canvas.SetLeft(highlight, selectionLeft + current.X - dragStart.X);
            Canvas.SetTop(highlight, selectionTop + current.Y - dragStart.Y);
            args.Handled = true;
        };
        highlight.PointerReleased += (_, args) =>
        {
            if (!draggingText) return;
            draggingText = false;
            var current = args.GetPosition(surface.Overlay);
            args.Pointer.Capture(null);
            double dx = current.X - dragStart.X, dy = current.Y - dragStart.Y;
            if (Math.Abs(dx) >= 2 || Math.Abs(dy) >= 2)
            {
                surface.Overlay.IsHitTestVisible = false;
                _ = MoveSelectedTextAsync(pageInfo, image, dx, dy);
            }
            args.Handled = true;
        };
        Canvas.SetLeft(highlight, rect.X);
        Canvas.SetTop(highlight, rect.Y);
        surface.Overlay.Children.Add(highlight);
        surface.Overlay.IsHitTestVisible = true;
        }
        SelectedTextLabel.Text = text.Length > 70 ? text[..67] + "..." : text;
        SelectedTextLabel.IsVisible = true;
        EditSelectedTextButton.IsVisible = true;
        DeleteSelectedTextButton.IsVisible = true;
        EditorStatusText.Text = "Trecho selecionado. Você pode editar, excluir ou arrastar o texto.";
    }

    private async Task MoveSelectedTextAsync(
        PagePreviewInfo pageInfo,
        AvaloniaImage image,
        double deltaViewX,
        double deltaViewY)
    {
        if (_selectedTextBounds is not { } selectedBounds ||
            string.IsNullOrWhiteSpace(_selectedText))
            return;

        string selectedText = _selectedText;
        string normalizedSelection = NormalizePdfText(selectedText);

        if (image.Bounds.Width <= 0 || image.Bounds.Height <= 0)
        {
            EditorStatusText.Text = "Não foi possível calcular a nova posição do texto.";
            LoadDocumentPreview();
            return;
        }

        // Converta o deslocamento pelos dois pontos da tela usando a rotação
        // real da página. Multiplicar X/Y diretamente pela largura/altura falha
        // em páginas com /Rotate 90 ou 270 graus.
        var selectedViewRect = PdfiumPageCoordinateMapper.PdfToViewRect(
            selectedBounds.Left, selectedBounds.Bottom,
            selectedBounds.Right, selectedBounds.Top,
            image.Bounds.Width, image.Bounds.Height,
            pageInfo.WidthPoints, pageInfo.HeightPoints, pageInfo.Rotation);

        double viewCenterX = selectedViewRect.X + selectedViewRect.Width / 2;
        double viewCenterY = selectedViewRect.Y + selectedViewRect.Height / 2;

        if (!PdfiumPageCoordinateMapper.TryViewToPdf(
                viewCenterX, viewCenterY,
                image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints,
                out double startPdfX, out double startPdfY, pageInfo.Rotation) ||
            !PdfiumPageCoordinateMapper.TryViewToPdf(
                viewCenterX + deltaViewX, viewCenterY + deltaViewY,
                image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints,
                out double endPdfX, out double endPdfY, pageInfo.Rotation))
        {
            EditorStatusText.Text = "O novo posicionamento ultrapassaria a página. O texto não foi movido.";
            LoadDocumentPreview();
            return;
        }

        double dx = endPdfX - startPdfX;
        double dy = endPdfY - startPdfY;
        if (Math.Abs(dx) < 0.1 && Math.Abs(dy) < 0.1)
        {
            LoadDocumentPreview();
            return;
        }

        double newLeft = selectedBounds.Left + dx;
        double newRight = selectedBounds.Right + dx;
        double newBottom = selectedBounds.Bottom + dy;
        double newTop = selectedBounds.Top + dy;
        if (newLeft < 0 || newBottom < 0 ||
            newRight > pageInfo.WidthPoints || newTop > pageInfo.HeightPoints)
        {
            EditorStatusText.Text = "O texto não pode ser movido para fora dos limites da página.";
            LoadDocumentPreview();
            return;
        }

        double expectedCx = (selectedBounds.Left + selectedBounds.Right) / 2 + dx;
        double expectedCy = (selectedBounds.Bottom + selectedBounds.Top) / 2 + dy;

        try
        {
            ApplyPdfEdit(document =>
            {
                var absorber = new TextFragmentAbsorber();
                document.Pages[pageInfo.PageIndex + 1].Accept(absorber);

                var pageFragments = absorber.TextFragments
                    .Where(fragment => fragment.Rectangle != null &&
                        !string.IsNullOrWhiteSpace(fragment.Text))
                    .ToArray();

                var exact = pageFragments
                    .Where(fragment => string.Equals(
                        NormalizePdfText(fragment.Text),
                        normalizedSelection,
                        StringComparison.Ordinal))
                    .Select(fragment => new
                    {
                        Fragment = fragment,
                        Bounds = fragment.Rectangle!,
                        Distance = Math.Pow(
                            (fragment.Rectangle!.LLX + fragment.Rectangle.URX) / 2 -
                            (selectedBounds.Left + selectedBounds.Right) / 2, 2) +
                            Math.Pow(
                            (fragment.Rectangle.LLY + fragment.Rectangle.URY) / 2 -
                            (selectedBounds.Bottom + selectedBounds.Top) / 2, 2)
                    })
                    .Where(item => item.Distance <= 2500)
                    .OrderBy(item => item.Distance)
                    .ToArray();

                List<TextFragment> targets;
                if (exact.Length > 0)
                {
                    if (exact.Length > 1 &&
                        Math.Abs(exact[1].Distance - exact[0].Distance) < 1)
                        throw new InvalidOperationException(
                            "Há blocos idênticos na mesma posição. Nenhum texto foi movido.");

                    targets = new List<TextFragment> { exact[0].Fragment };
                }
                else
                {
                    const double tolerance = 2.0;
                    targets = pageFragments.Where(fragment =>
                    {
                        var rectangle = fragment.Rectangle!;
                        return rectangle.LLX >= selectedBounds.Left - tolerance &&
                               rectangle.LLY >= selectedBounds.Bottom - tolerance &&
                               rectangle.URX <= selectedBounds.Right + tolerance &&
                               rectangle.URY <= selectedBounds.Top + tolerance;
                    }).ToList();

                    string groupedText = string.Concat(targets.Select(fragment => fragment.Text));
                    if (targets.Count == 0 ||
                        !string.Equals(
                            NormalizePdfText(groupedText),
                            normalizedSelection,
                            StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "Não encontrei objetos que correspondam exatamente ao texto selecionado.");

                    bool partialOverlap = pageFragments.Any(fragment =>
                        fragment.Rectangle is { } rectangle &&
                        rectangle.URX >= selectedBounds.Left &&
                        rectangle.LLX <= selectedBounds.Right &&
                        rectangle.URY >= selectedBounds.Bottom &&
                        rectangle.LLY <= selectedBounds.Top &&
                        !targets.Contains(fragment));
                    if (partialOverlap)
                        throw new InvalidOperationException(
                            "Outro objeto de texto cruza a seleção. O movimento foi cancelado para preservar o conteúdo vizinho.");
                }

                foreach (TextFragment target in targets)
                {
                    var bounds = target.Rectangle ??
                        throw new InvalidOperationException("Um dos objetos selecionados perdeu seus limites.");

                    var position = target.Position ??
                        new Position(bounds.LLX, bounds.LLY);
                    target.Position = new Position(
                        position.XIndent + dx,
                        position.YIndent + dy);

                    foreach (TextSegment segment in target.Segments)
                    {
                        if (segment.Position is { } segmentPosition)
                        {
                            segment.Position = new Position(
                                segmentPosition.XIndent + dx,
                                segmentPosition.YIndent + dy);
                        }

                        if (segment.BaselinePosition is { } baseline)
                        {
                            segment.BaselinePosition = new Position(
                                baseline.XIndent + dx,
                                baseline.YIndent + dy);
                        }
                    }
                }
            }, "Texto reposicionado no PDF.", savedDocument =>
            {
                var verify = new TextFragmentAbsorber();
                savedDocument.Pages[pageInfo.PageIndex + 1].Accept(verify);

                var savedFragments = verify.TextFragments
                    .Where(fragment => fragment.Rectangle != null &&
                        !string.IsNullOrWhiteSpace(fragment.Text))
                    .ToArray();

                bool exactAtDestination = savedFragments.Any(fragment =>
                    string.Equals(
                        NormalizePdfText(fragment.Text),
                        normalizedSelection,
                        StringComparison.Ordinal) &&
                    Math.Pow(
                        (fragment.Rectangle!.LLX + fragment.Rectangle.URX) / 2 - expectedCx, 2) +
                    Math.Pow(
                        (fragment.Rectangle.LLY + fragment.Rectangle.URY) / 2 - expectedCy, 2) < 900);

                if (exactAtDestination)
                    return true;

                const double verifyTolerance = 5.0;
                var movedFragments = savedFragments.Where(fragment =>
                {
                    var rectangle = fragment.Rectangle!;
                    return rectangle.LLX >= newLeft - verifyTolerance &&
                           rectangle.LLY >= newBottom - verifyTolerance &&
                           rectangle.URX <= newRight + verifyTolerance &&
                           rectangle.URY <= newTop + verifyTolerance;
                }).ToArray();

                return movedFragments.Length > 0 &&
                    string.Equals(
                        NormalizePdfText(string.Concat(movedFragments.Select(fragment => fragment.Text))),
                        normalizedSelection,
                        StringComparison.Ordinal);
            });
        }
        catch (Exception ex)
        {
            LoadDocumentPreview();
            EditorStatusText.Text =
                $"Não foi possível mover o texto com segurança; a versão anterior foi preservada. {ex.Message}";
        }
    }

    private void AutoTextBoxesMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _autoTextBoxesVisible = !_autoTextBoxesVisible;
        ClearTextSelection();

        int total = 0;
        foreach (int pageIndex in _pageSurfaces.Keys.OrderBy(index => index))
        {
            if (_autoTextBoxesVisible)
                total += AddAutoTextBoxesForPage(pageIndex);
            else if (_pageSurfaces.TryGetValue(pageIndex, out var surface))
            {
                foreach (var box in surface.Overlay.Children
                             .Where(item => Equals(item.Tag, "FutureAutoTextBox")).ToArray())
                    surface.Overlay.Children.Remove(box);
                surface.Overlay.IsHitTestVisible = surface.Overlay.Children.Count > 0;
            }
        }

        AutoTextBoxesModeButton.Background = new Avalonia.Media.SolidColorBrush(
            _autoTextBoxesVisible
                ? Avalonia.Media.Color.FromRgb(233, 227, 245)
                : Avalonia.Media.Color.FromRgb(240, 241, 244));
        AutoTextBoxesModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            _autoTextBoxesVisible
                ? Avalonia.Media.Color.FromRgb(76, 52, 130)
                : Avalonia.Media.Color.FromRgb(52, 56, 62));

        EditorStatusText.Text = _autoTextBoxesVisible
            ? $"Edição por blocos ativada: {total} caixas de texto identificadas. Clique em uma caixa e escolha Editar trecho."
            : "Caixas automáticas ocultadas. O texto original do PDF permanece intacto.";
    }

    private int AddAutoTextBoxesForPage(int pageIndex)
    {
        if (_document == null ||
            !_pageSurfaces.TryGetValue(pageIndex, out var surface) ||
            surface.Image.Tag is not PagePreviewInfo pageInfo ||
            surface.Image.Bounds.Width <= 0 || surface.Image.Bounds.Height <= 0)
            return 0;

        foreach (var existing in surface.Overlay.Children
                     .Where(item => Equals(item.Tag, "FutureAutoTextBox")).ToArray())
            surface.Overlay.Children.Remove(existing);

        var characters = _document.InspectPageCharacters(pageIndex);
        var lines = PdfiumTextGrouping.GroupIntoLines(characters, pageIndex);
        int count = 0;

        foreach (var line in lines)
        {
            if (line.Characters.Count == 0 || string.IsNullOrWhiteSpace(line.Text))
                continue;

            var rect = PdfiumPageCoordinateMapper.PdfToViewRect(
                line.Left, line.Bottom, line.Right, line.Top,
                surface.Image.Bounds.Width, surface.Image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints, pageInfo.Rotation);

            var box = new Border
            {
                Tag = "FutureAutoTextBox",
                Width = Math.Max(5, rect.Width),
                Height = Math.Max(5, rect.Height),
                BorderThickness = new Thickness(1),
                BorderBrush = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(190, 105, 76, 165)),
                Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(18, 105, 76, 165)),
                Cursor = new Cursor(StandardCursorType.Hand),
                IsHitTestVisible = true
            };

            bool draggingBox = false;
            bool boxWasMoved = false;
            Avalonia.Point dragStart = default;
            double initialLeft = rect.X;
            double initialTop = rect.Y;

            box.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
                    return;

                // Não criar uma segunda camada de seleção sobre a caixa: ela interceptaria
                // o segundo clique e impediria o reconhecimento de DoubleTapped.
                SetTextSelection(surface.Image, pageInfo, line.Characters, line.Text, drawHighlight: false);
                dragStart = args.GetPosition(surface.Overlay);
                draggingBox = true;
                boxWasMoved = false;
                initialLeft = Canvas.GetLeft(box);
                initialTop = Canvas.GetTop(box);
                box.Cursor = new Cursor(StandardCursorType.SizeAll);
                args.Pointer.Capture(box);
                // Deixe o roteamento de gestos do Avalonia reconhecer o duplo clique.
            };

            box.PointerMoved += (_, args) =>
            {
                if (!draggingBox ||
                    !args.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
                    return;

                var current = args.GetPosition(surface.Overlay);
                double dx = current.X - dragStart.X;
                double dy = current.Y - dragStart.Y;
                if (Math.Abs(dx) < 3 && Math.Abs(dy) < 3)
                    return;

                boxWasMoved = true;
                Canvas.SetLeft(box, initialLeft + dx);
                Canvas.SetTop(box, initialTop + dy);
                args.Handled = true;
            };

            box.PointerReleased += (_, args) =>
            {
                if (!draggingBox)
                    return;

                var current = args.GetPosition(surface.Overlay);
                double dx = current.X - dragStart.X;
                double dy = current.Y - dragStart.Y;
                draggingBox = false;
                args.Pointer.Capture(null);
                box.Cursor = new Cursor(StandardCursorType.SizeAll);

                if (boxWasMoved)
                {
                    surface.Overlay.IsHitTestVisible = false;
                    _ = MoveSelectedTextAsync(pageInfo, surface.Image, dx, dy);
                }

                // Não marcar o PointerReleased como tratado: DoubleTapped é disparado
                // pelo reconhecedor de gestos após a sequência de dois toques.
            };

            box.DoubleTapped += async (_, args) =>
            {
                draggingBox = false;
                SetTextSelection(surface.Image, pageInfo, line.Characters, line.Text, drawHighlight: false);

                var pdfBounds = (
                    Left: line.Left,
                    Bottom: line.Bottom,
                    Right: line.Right,
                    Top: line.Top);
                double centerX = (line.Left + line.Right) / 2;
                double centerY = (line.Bottom + line.Top) / 2;

                await EditTextAtPointAsync(
                    pageIndex, centerX, centerY, line.Text, pdfBounds);
                ClearTextSelection();
                args.Handled = true;
            };

            Canvas.SetLeft(box, rect.X);
            Canvas.SetTop(box, rect.Y);
            surface.Overlay.Children.Add(box);
            count++;
        }

        surface.Overlay.IsHitTestVisible = surface.Overlay.Children.Count > 0;
        return count;
    }

    private void SelectMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _toolMode = EditorToolMode.Select;
        foreach (var surface in _pageSurfaces.Values)
        {
            surface.Image.Cursor = new Cursor(StandardCursorType.Arrow);
            foreach (var marker in surface.Overlay.Children.OfType<TextBlock>()
                         .Where(item => Equals(item.Tag, "FutureTextCursor")).ToArray())
                surface.Overlay.Children.Remove(marker);
        }
        SelectModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(233, 227, 245));
        SelectModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(76, 52, 130));
        AddTextModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddTextModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        AddImageModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddImageModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        RectangleModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        RectangleModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        EllipseModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        EllipseModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        EditorStatusText.Text = "Modo Selecionar: arraste sobre o texto no PDF; clique com o botão direito para ver as ações.";
    }

    private void AddTextMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _toolMode = EditorToolMode.AddText;
        ClearTextSelection();
        foreach (var surface in _pageSurfaces.Values)
            surface.Image.Cursor = new Cursor(StandardCursorType.Ibeam);
        AddTextModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(233, 227, 245));
        AddTextModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(76, 52, 130));
        SelectModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        SelectModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        AddImageModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddImageModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        RectangleModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        RectangleModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        EllipseModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        EllipseModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        EditorStatusText.Text = "Modo Adicionar texto: clique na página do PDF no ponto em que deseja inserir o texto.";
    }

    private void AddImageMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _toolMode = EditorToolMode.AddImage;
        ClearTextSelection();
        foreach (var surface in _pageSurfaces.Values)
            surface.Image.Cursor = new Cursor(StandardCursorType.Hand);

        AddImageModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(233, 227, 245));
        AddImageModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(76, 52, 130));
        SelectModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddTextModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddTextModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        SelectModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        RectangleModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        RectangleModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        EllipseModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        EllipseModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        EditorStatusText.Text = "Clique na página para escolher uma imagem e inseri-la no PDF.";
    }

    private void RectangleMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _toolMode = EditorToolMode.DrawRectangle;
        ClearTextSelection();
        foreach (var surface in _pageSurfaces.Values)
            surface.Image.Cursor = new Cursor(StandardCursorType.Cross);

        RectangleModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(233, 227, 245));
        RectangleModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(76, 52, 130));
        SelectModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddTextModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddTextModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        AddImageModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddImageModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        SelectModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        EllipseModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        EllipseModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        EditorStatusText.Text = "Arraste sobre a página para desenhar um retângulo.";
    }

    private void EllipseMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _toolMode = EditorToolMode.DrawEllipse;
        ClearTextSelection();
        foreach (var surface in _pageSurfaces.Values)
            surface.Image.Cursor = new Cursor(StandardCursorType.Cross);

        EllipseModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(233, 227, 245));
        EllipseModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(76, 52, 130));
        SelectModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        SelectModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        AddTextModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddTextModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        AddImageModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddImageModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        RectangleModeButton.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(240, 241, 244));
        RectangleModeButton.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(52, 56, 62));
        EditorStatusText.Text = "Arraste sobre a página para desenhar uma elipse.";
    }

    private void UpdateRectanglePreview(
        AvaloniaImage image,
        PagePreviewInfo pageInfo,
        Avalonia.Point start,
        Avalonia.Point end)
    {
        if (!_pageSurfaces.TryGetValue(pageInfo.PageIndex, out var surface))
            return;

        if (_rectanglePreview == null)
        {
            _rectanglePreview = new Border
            {
                Tag = "FutureRectanglePreview",
                BorderBrush = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(109, 75, 195)),
                BorderThickness = new Thickness(2),
                Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(35, 109, 75, 195)),
                CornerRadius = _toolMode == EditorToolMode.DrawEllipse
                    ? new Avalonia.CornerRadius(999)
                    : new Avalonia.CornerRadius(0),
                IsHitTestVisible = false
            };
            surface.Overlay.Children.Add(_rectanglePreview);
        }

        double left = Math.Min(start.X, end.X);
        double top = Math.Min(start.Y, end.Y);
        _rectanglePreview.Width = Math.Max(2, Math.Abs(end.X - start.X));
        _rectanglePreview.Height = Math.Max(2, Math.Abs(end.Y - start.Y));
        Canvas.SetLeft(_rectanglePreview, left);
        Canvas.SetTop(_rectanglePreview, top);
        surface.Overlay.IsHitTestVisible = true;
    }

    private void RemoveRectanglePreview(int pageIndex)
    {
        if (_pageSurfaces.TryGetValue(pageIndex, out var surface))
        {
            foreach (var item in surface.Overlay.Children
                         .Where(item => Equals(item.Tag, "FutureRectanglePreview")).ToArray())
                surface.Overlay.Children.Remove(item);
            surface.Overlay.IsHitTestVisible = surface.Overlay.Children.Count > 0;
        }
        _rectanglePreview = null;
    }

    private void AddShapeFromDrag(
        PagePreviewInfo pageInfo,
        Avalonia.Point start,
        Avalonia.Point end,
        AvaloniaImage image,
        VisualElementKind shapeKind)
    {
        if (!PdfiumPageCoordinateMapper.TryViewToPdf(
                start.X, start.Y, image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints,
                out double startX, out double startY, pageInfo.Rotation) ||
            !PdfiumPageCoordinateMapper.TryViewToPdf(
                end.X, end.Y, image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints,
                out double endX, out double endY, pageInfo.Rotation))
        {
            EditorStatusText.Text = "Não foi possível calcular a área da forma.";
            return;
        }

        double left = Math.Min(startX, endX);
        double right = Math.Max(startX, endX);
        double bottom = Math.Min(startY, endY);
        double top = Math.Max(startY, endY);
        if (right - left < 4 || top - bottom < 4)
        {
            EditorStatusText.Text = "A forma precisa ter pelo menos 4 pontos de largura e altura.";
            return;
        }

        string annotationName = "FutureEditorShape-" + Guid.NewGuid().ToString("N");
        bool isEllipse = shapeKind == VisualElementKind.Ellipse;
        string annotationSubject = isEllipse
            ? "AssinaPDF Editor Ellipse"
            : "AssinaPDF Editor Rectangle";
        try
        {
            SelectMode_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
            ApplyPdfEdit(document =>
            {
                var page = document.Pages[pageInfo.PageIndex + 1];
                Aspose.Pdf.Annotations.MarkupAnnotation annotation = isEllipse
                    ? new Aspose.Pdf.Annotations.CircleAnnotation(
                        page, new Aspose.Pdf.Rectangle(left, bottom, right, top))
                    : new Aspose.Pdf.Annotations.SquareAnnotation(
                        page, new Aspose.Pdf.Rectangle(left, bottom, right, top));

                annotation.Name = annotationName;
                annotation.Title = "AssinaPDF";
                annotation.Subject = annotationSubject;
                annotation.Contents = "Forma inserida pelo AssinaPDF";
                annotation.Color = Aspose.Pdf.Color.FromRgb(1.0, 1.0, 1.0);
                // Mesmo padrão funcional do Typecraft: preenchimento branco
                // totalmente opaco, editável pelo painel de propriedades.
                annotation.InteriorColor = Aspose.Pdf.Color.FromRgb(1.0, 1.0, 1.0);
                annotation.Opacity = 1.0;
                annotation.Flags = Aspose.Pdf.Annotations.AnnotationFlags.Print;
                page.Annotations.Add(annotation);
                annotation.UpdateAppearances();
            }, isEllipse ? "Elipse inserida no PDF." : "Retângulo inserido no PDF.", savedDocument =>
            {
                return savedDocument.Pages[pageInfo.PageIndex + 1].Annotations
                    .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                    .Any(annotation => string.Equals(
                        annotation.Name, annotationName, StringComparison.Ordinal));
            });
        }
        catch (Exception ex)
        {
            EditorStatusText.Text =
                $"Não foi possível inserir a forma. A versão anterior foi preservada. {ex.Message}";
        }
    }

    private async Task AddImageAtPointAsync(
        PagePreviewInfo pageInfo,
        Avalonia.Point point,
        AvaloniaImage image)
    {
        if (!PdfiumPageCoordinateMapper.TryViewToPdf(
                point.X, point.Y, image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints,
                out double pdfX, out double pdfY, pageInfo.Rotation))
            return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecionar imagem para inserir no PDF",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Imagens")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg" }
                    }
                }
            });
            if (files.Count == 0)
                return;

            string imagePath = Path.GetFullPath(files[0].Path.LocalPath);
            using var bitmap = new Bitmap(imagePath);
            if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
                throw new InvalidDataException("A imagem selecionada possui dimensões inválidas.");

            double aspectRatio = (double)bitmap.PixelSize.Width / bitmap.PixelSize.Height;
            double width = 140;
            double height = width / aspectRatio;
            if (height > 140)
            {
                height = 140;
                width = height * aspectRatio;
            }

            if (width > pageInfo.WidthPoints || height > pageInfo.HeightPoints)
                throw new InvalidOperationException("A imagem é maior que a página do PDF.");

            double left = Math.Clamp(pdfX, 0, pageInfo.WidthPoints - width);
            double bottom = Math.Clamp(pdfY - height, 0, pageInfo.HeightPoints - height);
            double right = left + width;
            double top = bottom + height;

            SelectMode_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
            int previousImageCount = -1;
            ApplyPdfEdit(document =>
            {
                var page = document.Pages[pageInfo.PageIndex + 1];
                var beforeImages = new ImagePlacementAbsorber();
                page.Accept(beforeImages);
                previousImageCount = beforeImages.ImagePlacements.Count;

                var stamp = new ImageStamp(imagePath)
                {
                    X = left,
                    Y = bottom,
                    DisplayWidth = width,
                    DisplayHeight = height,
                    Quality = 95,
                    Opacity = 1.0,
                    Background = false,
                    AlternativeText = Path.GetFileName(imagePath)
                };
                page.AddStamp(stamp);
            }, "Imagem inserida no PDF.", savedDocument =>
            {
                var absorber = new ImagePlacementAbsorber();
                savedDocument.Pages[pageInfo.PageIndex + 1].Accept(absorber);
                return absorber.ImagePlacements.Count > previousImageCount &&
                    absorber.ImagePlacements.Any(placement =>
                    placement.Rectangle != null &&
                    placement.Rectangle.LLX <= right &&
                    placement.Rectangle.URX >= left &&
                    placement.Rectangle.LLY <= top &&
                    placement.Rectangle.URY >= bottom);
            });
        }
        catch (Exception ex)
        {
            EditorStatusText.Text =
                $"Não foi possível inserir a imagem. A versão anterior foi preservada. {ex.Message}";
        }
    }

    private async Task AddTextAtPointAsync(PagePreviewInfo pageInfo, Avalonia.Point point, AvaloniaImage image)
    {
        if (!PdfiumPageCoordinateMapper.TryViewToPdf(
            point.X, point.Y, image.Bounds.Width, image.Bounds.Height,
            pageInfo.WidthPoints, pageInfo.HeightPoints,
            out double pdfX, out double pdfY, pageInfo.Rotation))
            return;

        // A ferramenta T usa o mesmo editor rico inline da edição de texto:
        // clique na página, digite e aplique sem abrir uma janela modal.
        await EditTextAtPointAsync(pageInfo.PageIndex, pdfX, pdfY);
    }

    private async void NewBlankPdf_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            string sourceDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FutureSolucoes", "AssinaPDF", "BlankSources");
            Directory.CreateDirectory(sourceDirectory);
            string sourcePath = Path.Combine(
                sourceDirectory, $"Documento-em-branco-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pdf");

            using (var blankDocument = new Document())
            {
                var page = blankDocument.Pages.Add();
                page.SetPageSize(595.28, 841.89);
                blankDocument.Save(sourcePath);
            }

            var entry = _history.CreateWorkingCopy(sourcePath);
            _document?.Dispose();
            _document = null;
            _workingCopyPath = entry.WorkingCopyPath;
            _originalPath = entry.OriginalPath;
            _sessionDirectory = Path.GetDirectoryName(entry.WorkingCopyPath)
                ?? throw new InvalidOperationException("Não foi possível localizar a pasta de trabalho.");
            _historyEntryId = entry.Id;
            _hasExistingSignatures = false;
            _fontSubstitutions.Clear();
            ClearHistoryStack(_undoStack);
            ClearHistoryStack(_redoStack);
            DocumentNameText.Text = "Documento em branco.pdf";
            Title = "AssinaPDF | Editor - Documento em branco";
            LoadDocumentPreview();
            await AuditDocumentSignaturesAsync();
            EditorStatusText.Text =
                "Documento A4 em branco criado. Adicione texto, formas ou imagens e exporte o PDF.";
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = "Não foi possível criar um documento em branco. " + ex.Message;
        }
    }

    private async void OpenPdf_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Abrir PDF para edição", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Documentos PDF") { Patterns = new[] { "*.pdf", "*.PDF" } } }
            });
            if (files.Count == 0) return;
            string source = Path.GetFullPath(files[0].Path.LocalPath);
            var entry = _history.CreateWorkingCopy(source);
            _document?.Dispose();
            _document = null;
            _workingCopyPath = entry.WorkingCopyPath;
            _originalPath = entry.OriginalPath;
            _sessionDirectory = Path.GetDirectoryName(entry.WorkingCopyPath)
                ?? throw new InvalidOperationException("Não foi possível localizar a pasta de trabalho.");
            _historyEntryId = entry.Id;
            ClearHistoryStack(_undoStack);
            ClearHistoryStack(_redoStack);
            DocumentNameText.Text = Path.GetFileName(source);
            Title = $"AssinaPDF | Editor - {Path.GetFileName(source)}";
            await VerifyDocumentFontsAsync();
            LoadDocumentPreview();
            await AuditDocumentSignaturesAsync();
            // Keep the font-audit result (including download/substitution warnings)
            // visible after the page preview is rendered. Do not overwrite it with
            // a generic success message here.
        }
        catch (Exception ex) { EditorStatusText.Text = $"Não foi possível abrir o PDF: {ex.Message}"; }
    }

    private bool TrySelectVisualElementAtPoint(
        AvaloniaImage image,
        PagePreviewInfo pageInfo,
        double pdfX,
        double pdfY)
    {
        try
        {
            using var document = Document.Open(_workingCopyPath);
            var page = document.Pages[pageInfo.PageIndex + 1];

            var shape = page.Annotations
                .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                .Where(annotation =>
                    string.Equals(annotation.Subject, "AssinaPDF Editor Rectangle", StringComparison.Ordinal) ||
                    string.Equals(annotation.Subject, "AssinaPDF Editor Ellipse", StringComparison.Ordinal))
                .FirstOrDefault(annotation =>
                {
                    var bounds = annotation.Rect;
                    if (bounds == null ||
                        pdfX < bounds.LLX || pdfX > bounds.URX ||
                        pdfY < bounds.LLY || pdfY > bounds.URY)
                        return false;

                    if (annotation is Aspose.Pdf.Annotations.CircleAnnotation)
                    {
                        double centerX = (bounds.LLX + bounds.URX) / 2;
                        double centerY = (bounds.LLY + bounds.URY) / 2;
                        double radiusX = Math.Max(0.001, (bounds.URX - bounds.LLX) / 2);
                        double radiusY = Math.Max(0.001, (bounds.URY - bounds.LLY) / 2);
                        double normalizedX = (pdfX - centerX) / radiusX;
                        double normalizedY = (pdfY - centerY) / radiusY;
                        return normalizedX * normalizedX + normalizedY * normalizedY <= 1;
                    }

                    return true;
                });

            if (shape?.Rect is { } shapeBounds &&
                !string.IsNullOrWhiteSpace(shape.Name))
            {
                VisualElementKind shapeKind =
                    shape is Aspose.Pdf.Annotations.CircleAnnotation
                        ? VisualElementKind.Ellipse
                        : VisualElementKind.Rectangle;
                SetVisualElementSelection(
                    image, pageInfo, shapeKind,
                    shape.Name, -1, null, 0,
                    (shapeBounds.LLX, shapeBounds.LLY,
                     shapeBounds.URX, shapeBounds.URY),
                    GetAsposeColorHex(shape.InteriorColor),
                    Math.Clamp(shape.Opacity, 0.05, 1.0));
                return true;
            }

            var absorber = new ImagePlacementAbsorber();
            page.Accept(absorber);
            for (int index = absorber.ImagePlacements.Count - 1; index >= 0; index--)
            {
                var placement = absorber.ImagePlacements[index];
                var bounds = placement.Rectangle;
                if (bounds == null ||
                    pdfX < bounds.LLX || pdfX > bounds.URX ||
                    pdfY < bounds.LLY || pdfY > bounds.URY)
                    continue;

                if (placement.Image == null)
                    throw new InvalidDataException("A imagem selecionada não possui um objeto de imagem editável.");

                // ToPng produz um fluxo PNG real e mantém a transparência da imagem,
                // sem depender de System.Drawing/GDI+, que não é portátil para macOS.
                byte[] imageBytes = placement.Image.ToPng();
                if (imageBytes.Length == 0)
                    throw new InvalidDataException("Não foi possível extrair os dados da imagem selecionada.");

                if (!string.Equals(
                        placement.CompositingParameters.BlendMode.ToString(),
                        "Normal", StringComparison.OrdinalIgnoreCase))
                {
                    EditorStatusText.Text =
                        "Esta imagem usa um modo de mesclagem especial e não pode ser movida com segurança.";
                    return false;
                }

                int rotation = ((int)Math.Round(placement.Rotation) % 360 + 360) % 360;
                if (rotation != 0 && rotation != 90 && rotation != 180 && rotation != 270)
                {
                    EditorStatusText.Text =
                        "Esta imagem possui rotação arbitrária e não pode ser movida com segurança por esta ferramenta.";
                    return false;
                }

                SetVisualElementSelection(
                    image, pageInfo, VisualElementKind.Image,
                    null, index, imageBytes, rotation,
                    (bounds.LLX, bounds.LLY, bounds.URX, bounds.URY));
                return true;
            }
        }
        catch (Exception ex)
        {
            EditorStatusText.Text =
                "Não foi possível selecionar o elemento visual: " + ex.Message;
        }

        return false;
    }

    private void SetVisualElementSelection(
        AvaloniaImage image,
        PagePreviewInfo pageInfo,
        VisualElementKind kind,
        string? name,
        int imagePlacementIndex,
        byte[]? imageBytes,
        int imageRotation,
        (double Left, double Bottom, double Right, double Top) bounds,
        string fillHex = "#FFFFFF",
        double opacity = 1.0)
    {
        ClearTextSelection();
        _selectedPageIndex = pageInfo.PageIndex;
        _selectedVisualKind = kind;
        _selectedVisualName = name;
        _selectedImagePlacementIndex = imagePlacementIndex;
        _selectedImageBytes = imageBytes;
        _selectedImageRotation = imageRotation;
        _selectedVisualBounds = bounds;

        string elementLabel = kind switch
        {
            VisualElementKind.Image => "Imagem selecionada",
            VisualElementKind.Ellipse => "Elipse selecionada",
            _ => "Retângulo selecionado"
        };
        SelectedTextLabel.Text = elementLabel;
        SelectedTextLabel.IsVisible = true;
        EditSelectedTextButton.IsVisible = false;
        DeleteSelectedTextButton.Content = "Excluir elemento";
        DeleteSelectedTextButton.IsVisible = true;
        ShapePropertiesPanel.IsVisible =
            kind is VisualElementKind.Rectangle or VisualElementKind.Ellipse;
        if (ShapePropertiesPanel.IsVisible)
        {
            ShapeFillColorTextBox.Text = fillHex;
            ShapeOpacitySlider.Value = Math.Clamp(opacity, 0.05, 1.0);
            ShapeOpacityValueText.Text =
                $"Opacidade · {Math.Round(ShapeOpacitySlider.Value * 100):0}%";
        }

        if (!_pageSurfaces.TryGetValue(pageInfo.PageIndex, out var surface))
            return;

        var viewRect = PdfiumPageCoordinateMapper.PdfToViewRect(
            bounds.Left, bounds.Bottom, bounds.Right, bounds.Top,
            image.Bounds.Width, image.Bounds.Height,
            pageInfo.WidthPoints, pageInfo.HeightPoints, pageInfo.Rotation);

        var border = new Border
        {
            Width = Math.Max(10, viewRect.Width),
            Height = Math.Max(10, viewRect.Height),
            BorderBrush = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromRgb(35, 116, 220)),
            BorderThickness = new Thickness(2),
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb(12, 35, 116, 220)),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            IsHitTestVisible = true
        };
        _visualSelectionBorder = border;

        var resizeHandle = new Border
        {
            Width = 11,
            Height = 11,
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromRgb(109, 75, 195)),
            BorderBrush = Avalonia.Media.Brushes.White,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };

        double initialLeft = viewRect.X;
        double initialTop = viewRect.Y;
        double initialWidth = Math.Max(10, viewRect.Width);
        double initialHeight = Math.Max(10, viewRect.Height);
        double dragStartX = 0;
        double dragStartY = 0;
        bool dragging = false;
        bool resizing = false;
        bool changed = false;

        void PositionHandle()
        {
            Canvas.SetLeft(resizeHandle,
                Canvas.GetLeft(border) + border.Width - resizeHandle.Width / 2);
            Canvas.SetTop(resizeHandle,
                Canvas.GetTop(border) + border.Height - resizeHandle.Height / 2);
        }

        border.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
                return;

            var local = args.GetPosition(border);
            resizing = local.X >= border.Width - 17 &&
                       local.Y >= border.Height - 17;
            dragging = true;
            changed = false;
            dragStartX = args.GetPosition(surface.Overlay).X;
            dragStartY = args.GetPosition(surface.Overlay).Y;
            initialLeft = Canvas.GetLeft(border);
            initialTop = Canvas.GetTop(border);
            initialWidth = border.Width;
            initialHeight = border.Height;
            args.Pointer.Capture(border);
            args.Handled = true;
        };

        border.PointerMoved += (_, args) =>
        {
            if (!dragging)
                return;

            var current = args.GetPosition(surface.Overlay);
            double dx = current.X - dragStartX;
            double dy = current.Y - dragStartY;
            if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
                return;

            changed = true;
            if (resizing)
            {
                border.Width = Math.Max(10, initialWidth + dx);
                border.Height = Math.Max(10, initialHeight + dy);
            }
            else
            {
                Canvas.SetLeft(border, initialLeft + dx);
                Canvas.SetTop(border, initialTop + dy);
            }

            PositionHandle();
            args.Handled = true;
        };

        border.PointerReleased += (_, args) =>
        {
            if (!dragging)
                return;

            dragging = false;
            args.Pointer.Capture(null);
            if (changed)
            {
                ApplyVisualElementBounds(
                    pageInfo, image,
                    Canvas.GetLeft(border), Canvas.GetTop(border),
                    border.Width, border.Height);
            }
            args.Handled = true;
        };

        Canvas.SetLeft(border, viewRect.X);
        Canvas.SetTop(border, viewRect.Y);
        surface.Overlay.Children.Add(border);
        surface.Overlay.Children.Add(resizeHandle);
        surface.Overlay.IsHitTestVisible = true;
        PositionHandle();
        EditorStatusText.Text =
            elementLabel + ". Arraste para mover; use o puxador roxo no canto para redimensionar.";
    }

    private static string GetAsposeColorHex(Aspose.Pdf.Color? color)
    {
        if (color == null)
            return "#FFFFFF";

        // ToRgb pode retornar um System.Drawing.Color ou outro objeto de cor,
        // conforme a versão do Aspose. A leitura reflexiva mantém o editor
        // compatível com ambas as representações sem depender de GDI+.
        object rgb = color.GetType().GetMethod("ToRgb", Type.EmptyTypes)
            ?.Invoke(color, null) ?? color;
        Type type = rgb.GetType();

        byte ReadChannel(string propertyName, int dataIndex)
        {
            object? raw = type.GetProperty(propertyName)?.GetValue(rgb);
            if (raw != null)
                return Convert.ToByte(raw, CultureInfo.InvariantCulture);

            if (type.GetProperty("Data")?.GetValue(rgb) is Array data &&
                data.Length > dataIndex &&
                data.GetValue(dataIndex) is { } component)
            {
                double value = Convert.ToDouble(component, CultureInfo.InvariantCulture);
                if (value <= 1.0)
                    value *= 255.0;
                return (byte)Math.Clamp(Math.Round(value), 0, 255);
            }

            return 255;
        }

        return $"#{ReadChannel("R", 0):X2}{ReadChannel("G", 1):X2}{ReadChannel("B", 2):X2}";
    }

    private void ShapeColorSwatch_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string color)
            return;

        ShapeFillColorTextBox.Text = color;
        ApplySelectedShapeFillColor();
    }

    private void ApplyShapeFillColor_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e) =>
        ApplySelectedShapeFillColor();

    private void ShapeFillColorTextBox_KeyUp(
        object? sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        ApplySelectedShapeFillColor();
        e.Handled = true;
    }

    private void ShapeOpacitySlider_ValueChanged(
        object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ShapeOpacityValueText.Text =
            $"Opacidade · {Math.Round(e.NewValue * 100):0}%";
    }

    private void ShapeOpacitySlider_PointerReleased(
        object? sender,
        PointerReleasedEventArgs e) =>
        ApplySelectedShapeOpacity();

    private void ShapeOpacitySlider_KeyUp(
        object? sender,
        KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            ApplySelectedShapeOpacity();
    }

    private void ApplySelectedShapeFillColor()
    {
        if (_selectedVisualKind is not (VisualElementKind.Rectangle or VisualElementKind.Ellipse) ||
            _selectedPageIndex is not int pageIndex ||
            string.IsNullOrWhiteSpace(_selectedVisualName))
            return;

        string selectedName = _selectedVisualName;
        string requestedHex = ShapeFillColorTextBox.Text?.Trim() ?? string.Empty;
        if (!TryParseHexColor(requestedHex, out Aspose.Pdf.Color fillColor))
        {
            EditorStatusText.Text = "Informe uma cor hexadecimal válida, por exemplo #2563EB.";
            return;
        }

        try
        {
            ApplyPdfEdit(document =>
            {
                var annotation = document.Pages[pageIndex + 1].Annotations
                    .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                    .FirstOrDefault(item => string.Equals(
                        item.Name, selectedName, StringComparison.Ordinal));
                if (annotation == null)
                    throw new InvalidOperationException("A forma selecionada não existe mais no PDF.");

                annotation.Color = fillColor;
                annotation.InteriorColor = fillColor;
                annotation.UpdateAppearances();
            }, "Cor da forma atualizada.", savedDocument =>
            {
                var annotation = savedDocument.Pages[pageIndex + 1].Annotations
                    .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                    .FirstOrDefault(item => string.Equals(
                        item.Name, selectedName, StringComparison.Ordinal));
                return annotation != null &&
                    string.Equals(
                        GetAsposeColorHex(annotation.InteriorColor),
                        requestedHex.StartsWith("#", StringComparison.Ordinal)
                            ? requestedHex
                            : "#" + requestedHex,
                        StringComparison.OrdinalIgnoreCase);
            });

            RestoreVisualElementSelection(pageIndex, selectedName);
        }
        catch (Exception ex)
        {
            EditorStatusText.Text =
                "Não foi possível aplicar a cor. A versão anterior foi preservada. " + ex.Message;
        }
    }

    private void ApplySelectedShapeOpacity()
    {
        if (_selectedVisualKind is not (VisualElementKind.Rectangle or VisualElementKind.Ellipse) ||
            _selectedPageIndex is not int pageIndex ||
            string.IsNullOrWhiteSpace(_selectedVisualName))
            return;

        string selectedName = _selectedVisualName;
        double requestedOpacity = Math.Clamp(ShapeOpacitySlider.Value, 0.05, 1.0);
        try
        {
            ApplyPdfEdit(document =>
            {
                var annotation = document.Pages[pageIndex + 1].Annotations
                    .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                    .FirstOrDefault(item => string.Equals(
                        item.Name, selectedName, StringComparison.Ordinal));
                if (annotation == null)
                    throw new InvalidOperationException("A forma selecionada não existe mais no PDF.");

                annotation.Opacity = requestedOpacity;
                annotation.UpdateAppearances();
            }, "Opacidade da forma atualizada.", savedDocument =>
            {
                var annotation = savedDocument.Pages[pageIndex + 1].Annotations
                    .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                    .FirstOrDefault(item => string.Equals(
                        item.Name, selectedName, StringComparison.Ordinal));
                return annotation != null &&
                    Math.Abs(annotation.Opacity - requestedOpacity) < 0.01;
            });

            RestoreVisualElementSelection(pageIndex, selectedName);
        }
        catch (Exception ex)
        {
            EditorStatusText.Text =
                "Não foi possível aplicar a opacidade. A versão anterior foi preservada. " + ex.Message;
        }
    }

    private void RestoreVisualElementSelection(int pageIndex, string selectedName)
    {
        if (!_pageSurfaces.TryGetValue(pageIndex, out var surface) ||
            surface.Image.Tag is not PagePreviewInfo pageInfo)
            return;

        try
        {
            using var document = Document.Open(_workingCopyPath);
            var annotation = document.Pages[pageIndex + 1].Annotations
                .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                .FirstOrDefault(item => string.Equals(
                    item.Name, selectedName, StringComparison.Ordinal));
            if (annotation?.Rect is not { } bounds)
                return;

            VisualElementKind kind =
                annotation is Aspose.Pdf.Annotations.CircleAnnotation
                    ? VisualElementKind.Ellipse
                    : VisualElementKind.Rectangle;
            SetVisualElementSelection(
                surface.Image, pageInfo, kind, selectedName, -1, null, 0,
                (bounds.LLX, bounds.LLY, bounds.URX, bounds.URY),
                GetAsposeColorHex(annotation.InteriorColor),
                Math.Clamp(annotation.Opacity, 0.05, 1.0));
        }
        catch (Exception ex)
        {
            EditorStatusText.Text =
                "A forma foi atualizada, mas não foi possível manter a seleção. " + ex.Message;
        }
    }

    private void ApplyVisualElementBounds(
        PagePreviewInfo pageInfo,
        AvaloniaImage image,
        double viewLeft,
        double viewTop,
        double viewWidth,
        double viewHeight)
    {
        if (_selectedVisualBounds is not { } originalBounds ||
            _selectedVisualKind == VisualElementKind.None ||
            viewWidth < 8 || viewHeight < 8)
            return;

        if (!PdfiumPageCoordinateMapper.TryViewToPdf(
                viewLeft, viewTop, image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints,
                out double x1, out double y1, pageInfo.Rotation) ||
            !PdfiumPageCoordinateMapper.TryViewToPdf(
                viewLeft + viewWidth, viewTop + viewHeight,
                image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints,
                out double x2, out double y2, pageInfo.Rotation))
        {
            EditorStatusText.Text = "O novo tamanho/posição ultrapassa a página.";
            LoadDocumentPreview();
            return;
        }

        double left = Math.Min(x1, x2);
        double right = Math.Max(x1, x2);
        double bottom = Math.Min(y1, y2);
        double top = Math.Max(y1, y2);
        if (right - left < 4 || top - bottom < 4 ||
            left < 0 || bottom < 0 ||
            right > pageInfo.WidthPoints || top > pageInfo.HeightPoints)
        {
            EditorStatusText.Text = "O elemento precisa permanecer dentro dos limites da página.";
            LoadDocumentPreview();
            return;
        }

        VisualElementKind kind = _selectedVisualKind;
        string? selectedName = _selectedVisualName;
        int imageIndex = _selectedImagePlacementIndex;
        byte[]? imageBytes = _selectedImageBytes;
        int imageRotation = _selectedImageRotation;
        double expectedCenterX = (left + right) / 2;
        double expectedCenterY = (bottom + top) / 2;

        try
        {
            int originalImageCount = -1;
            ApplyPdfEdit(document =>
            {
                var page = document.Pages[pageInfo.PageIndex + 1];
                if (kind is VisualElementKind.Rectangle or VisualElementKind.Ellipse)
                {
                    var annotation = page.Annotations
                        .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                        .FirstOrDefault(item =>
                            string.Equals(item.Name, selectedName, StringComparison.Ordinal));
                    if (annotation == null)
                        throw new InvalidOperationException("A forma selecionada não existe mais no PDF.");

                    annotation.Rect = new Aspose.Pdf.Rectangle(left, bottom, right, top);
                    annotation.UpdateAppearances();
                    return;
                }

                if (imageBytes == null || imageBytes.Length == 0)
                    throw new InvalidOperationException("Os dados da imagem selecionada não estão disponíveis.");

                var absorber = new ImagePlacementAbsorber();
                page.Accept(absorber);
                originalImageCount = absorber.ImagePlacements.Count;
                if (imageIndex < 0 || imageIndex >= absorber.ImagePlacements.Count)
                    throw new InvalidOperationException("A imagem selecionada não foi encontrada no PDF.");

                var placement = absorber.ImagePlacements[imageIndex];
                var currentBounds = placement.Rectangle;
                if (currentBounds == null ||
                    Math.Abs(currentBounds.LLX - originalBounds.Left) > 2 ||
                    Math.Abs(currentBounds.LLY - originalBounds.Bottom) > 2 ||
                    Math.Abs(currentBounds.URX - originalBounds.Right) > 2 ||
                    Math.Abs(currentBounds.URY - originalBounds.Top) > 2)
                    throw new InvalidOperationException("A imagem mudou desde a seleção. Selecione-a novamente.");

                placement.Hide();

                int normalizedRotation = ((imageRotation % 360) + 360) % 360;
                bool quarterTurn = normalizedRotation is 90 or 270;
                double stampWidth = quarterTurn ? top - bottom : right - left;
                double stampHeight = quarterTurn ? right - left : top - bottom;
                double centerX = (left + right) / 2;
                double centerY = (bottom + top) / 2;

                using var imageStream = new MemoryStream(imageBytes, writable: false);
                var stamp = new ImageStamp(imageStream)
                {
                    X = centerX - stampWidth / 2,
                    Y = centerY - stampHeight / 2,
                    DisplayWidth = stampWidth,
                    DisplayHeight = stampHeight,
                    Quality = 95,
                    Opacity = 1.0,
                    Background = false,
                    Rotate = normalizedRotation switch
                    {
                        90 => Aspose.Pdf.Rotation.on90,
                        180 => Aspose.Pdf.Rotation.on180,
                        270 => Aspose.Pdf.Rotation.on270,
                        _ => Aspose.Pdf.Rotation.None
                    }
                };
                page.AddStamp(stamp);
            }, kind == VisualElementKind.Image
                ? "Imagem movida/redimensionada no PDF."
                : kind == VisualElementKind.Ellipse
                    ? "Elipse movida/redimensionada no PDF."
                    : "Retângulo movido/redimensionado no PDF.", savedDocument =>
            {
                var page = savedDocument.Pages[pageInfo.PageIndex + 1];
                if (kind is VisualElementKind.Rectangle or VisualElementKind.Ellipse)
                {
                    var annotation = page.Annotations
                        .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                        .FirstOrDefault(item =>
                            string.Equals(item.Name, selectedName, StringComparison.Ordinal));
                    return annotation?.Rect is { } rectangle &&
                        Math.Abs(rectangle.LLX - left) < 2 &&
                        Math.Abs(rectangle.LLY - bottom) < 2 &&
                        Math.Abs(rectangle.URX - right) < 2 &&
                        Math.Abs(rectangle.URY - top) < 2;
                }

                var absorber = new ImagePlacementAbsorber();
                page.Accept(absorber);
                return absorber.ImagePlacements.Count == originalImageCount &&
                    absorber.ImagePlacements.Any(placement =>
                    placement.Rectangle is { } rectangle &&
                    Math.Pow((rectangle.LLX + rectangle.URX) / 2 - expectedCenterX, 2) +
                    Math.Pow((rectangle.LLY + rectangle.URY) / 2 - expectedCenterY, 2) < 900);
            });
        }
        catch (Exception ex)
        {
            LoadDocumentPreview();
            EditorStatusText.Text =
                "Não foi possível mover/redimensionar o elemento; a versão anterior foi preservada. " + ex.Message;
        }
    }

    private void DeleteSelectedVisualElement()
    {
        if (_selectedPageIndex is not int pageIndex ||
            _selectedVisualKind == VisualElementKind.None ||
            _selectedVisualBounds is not { } bounds)
            return;

        VisualElementKind kind = _selectedVisualKind;
        string? selectedName = _selectedVisualName;
        int imageIndex = _selectedImagePlacementIndex;
        double centerX = (bounds.Left + bounds.Right) / 2;
        double centerY = (bounds.Bottom + bounds.Top) / 2;

        try
        {
            int originalImageCount = -1;
            ApplyPdfEdit(document =>
            {
                var page = document.Pages[pageIndex + 1];
                if (kind is VisualElementKind.Rectangle or VisualElementKind.Ellipse)
                {
                    var annotation = page.Annotations
                        .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                        .FirstOrDefault(item =>
                            string.Equals(item.Name, selectedName, StringComparison.Ordinal));
                    if (annotation == null)
                        throw new InvalidOperationException("A forma selecionada não existe mais.");
                    page.Annotations.Delete(annotation);
                    return;
                }

                var absorber = new ImagePlacementAbsorber();
                page.Accept(absorber);
                originalImageCount = absorber.ImagePlacements.Count;
                if (imageIndex < 0 || imageIndex >= absorber.ImagePlacements.Count)
                    throw new InvalidOperationException("A imagem selecionada não existe mais.");
                var placement = absorber.ImagePlacements[imageIndex];
                var rectangle = placement.Rectangle;
                if (rectangle == null ||
                    Math.Abs(rectangle.LLX - bounds.Left) > 2 ||
                    Math.Abs(rectangle.LLY - bounds.Bottom) > 2 ||
                    Math.Abs(rectangle.URX - bounds.Right) > 2 ||
                    Math.Abs(rectangle.URY - bounds.Top) > 2)
                    throw new InvalidOperationException("A imagem mudou desde a seleção.");
                placement.Hide();
            }, kind == VisualElementKind.Image
                ? "Imagem excluída do PDF."
                : kind == VisualElementKind.Ellipse
                    ? "Elipse excluída do PDF."
                    : "Retângulo excluído do PDF.", savedDocument =>
            {
                var page = savedDocument.Pages[pageIndex + 1];
                if (kind is VisualElementKind.Rectangle or VisualElementKind.Ellipse)
                    return !page.Annotations
                        .OfType<Aspose.Pdf.Annotations.MarkupAnnotation>()
                        .Any(item => string.Equals(
                            item.Name, selectedName, StringComparison.Ordinal));

                var absorber = new ImagePlacementAbsorber();
                page.Accept(absorber);
                return absorber.ImagePlacements.Count == originalImageCount - 1 &&
                    !absorber.ImagePlacements.Any(placement =>
                    placement.Rectangle is { } rectangle &&
                    Math.Pow((rectangle.LLX + rectangle.URX) / 2 - centerX, 2) +
                    Math.Pow((rectangle.LLY + rectangle.URY) / 2 - centerY, 2) < 900);
            });
        }
        catch (Exception ex)
        {
            EditorStatusText.Text =
                "Não foi possível excluir o elemento; a versão anterior foi preservada. " + ex.Message;
        }
    }

    private void ClearTextSelection()
    {
        foreach (var surface in _pageSurfaces.Values)
        {
            foreach (var child in surface.Overlay.Children
                         .Where(item => !Equals(item.Tag, "FutureAutoTextBox")).ToArray())
                surface.Overlay.Children.Remove(child);
            surface.Overlay.IsHitTestVisible = surface.Overlay.Children.Count > 0;
        }

        _selectedPageIndex = null;
        ShapePropertiesPanel.IsVisible = false;
        SelectedTextLabel.Text = string.Empty;
        SelectedTextLabel.IsVisible = false;
        EditSelectedTextButton.IsVisible = false;
        DeleteSelectedTextButton.IsVisible = false;
        _selectedText = string.Empty;
        _selectedTextBounds = null;
        _visualSelectionBorder = null;
        _selectedVisualKind = VisualElementKind.None;
        _selectedVisualName = null;
        _selectedImagePlacementIndex = -1;
        _selectedImageBytes = null;
        _selectedImageRotation = 0;
        _selectedVisualBounds = null;
        DeleteSelectedTextButton.Content = "Excluir texto";
    }

    private void DeleteSelectedText_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedVisualKind != VisualElementKind.None)
        {
            DeleteSelectedVisualElement();
            return;
        }

        if (_selectedPageIndex is not int pageIndex ||
            _selectedTextBounds is not { } bounds ||
            string.IsNullOrWhiteSpace(_selectedText))
            return;

        string selectedText = _selectedText;
        string normalizedSelection = NormalizePdfText(selectedText);
        double centerX = (bounds.Left + bounds.Right) / 2;
        double centerY = (bounds.Bottom + bounds.Top) / 2;

        try
        {
            ApplyPdfEdit(document =>
            {
                var absorber = new TextFragmentAbsorber();
                document.Pages[pageIndex + 1].Accept(absorber);

                var fragments = absorber.TextFragments
                    .Where(fragment => fragment.Rectangle != null &&
                        !string.IsNullOrWhiteSpace(fragment.Text))
                    .ToArray();

                // Primeiro, procura um único objeto com o texto exato e centro
                // compatível com a seleção do PDFium.
                var exact = fragments
                    .Where(fragment => string.Equals(
                        NormalizePdfText(fragment.Text),
                        normalizedSelection,
                        StringComparison.Ordinal))
                    .Select(fragment => new
                    {
                        Fragment = fragment,
                        Bounds = fragment.Rectangle!,
                        Distance = Math.Pow(
                            (fragment.Rectangle!.LLX + fragment.Rectangle.URX) / 2 - centerX, 2) +
                            Math.Pow(
                            (fragment.Rectangle.LLY + fragment.Rectangle.URY) / 2 - centerY, 2)
                    })
                    .Where(item => item.Distance <= 400)
                    .OrderBy(item => item.Distance)
                    .ToArray();

                if (exact.Length > 0)
                {
                    if (exact.Length > 1 &&
                        Math.Abs(exact[1].Distance - exact[0].Distance) < 1)
                        throw new InvalidOperationException(
                            "Há textos idênticos na mesma posição. Nenhum conteúdo foi excluído.");

                    exact[0].Fragment.Text = string.Empty;
                    return;
                }

                // Quando a seleção é composta por mais de um TextFragment,
                // só removemos os fragmentos inteiramente contidos no retângulo
                // e cuja concatenação corresponde exatamente ao texto selecionado.
                const double tolerance = 2.0;
                var contained = fragments.Where(fragment =>
                {
                    var rectangle = fragment.Rectangle!;
                    return rectangle.LLX >= bounds.Left - tolerance &&
                           rectangle.LLY >= bounds.Bottom - tolerance &&
                           rectangle.URX <= bounds.Right + tolerance &&
                           rectangle.URY <= bounds.Top + tolerance;
                }).ToArray();

                string containedText = string.Concat(contained.Select(fragment => fragment.Text));
                if (contained.Length == 0 ||
                    !string.Equals(
                        NormalizePdfText(containedText),
                        normalizedSelection,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A seleção não corresponde exatamente a um objeto ou conjunto de objetos de texto do PDF. " +
                        "Para evitar apagar conteúdo vizinho, a exclusão foi cancelada.");
                }

                foreach (TextFragment fragment in contained)
                    fragment.Text = string.Empty;
            }, "Texto selecionado excluído do PDF.", savedDocument =>
            {
                var verify = new TextFragmentAbsorber();
                savedDocument.Pages[pageIndex + 1].Accept(verify);
                return !verify.TextFragments.Any(fragment =>
                    fragment.Rectangle is { } rectangle &&
                    string.Equals(
                        NormalizePdfText(fragment.Text),
                        normalizedSelection,
                        StringComparison.Ordinal) &&
                    Math.Pow((rectangle.LLX + rectangle.URX) / 2 - centerX, 2) +
                    Math.Pow((rectangle.LLY + rectangle.URY) / 2 - centerY, 2) <= 400);
            });
            ClearTextSelection();
        }
        catch (Exception ex)
        {
            EditorStatusText.Text =
                $"Não foi possível excluir com segurança a seleção. A versão anterior foi preservada. {ex.Message}";
        }
    }

    private async void EditSelectedText_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        await EditSelectedText_ClickAsync();
    }

    private async Task EditSelectedText_ClickAsync()
    {
        if (_selectedVisualKind != VisualElementKind.None ||
            _selectedPageIndex is not int pageIndex)
            return;

        await EditTextAtPointAsync(pageIndex, _selectedPdfX, _selectedPdfY,
            _selectedText, _selectedTextBounds);
        ClearTextSelection();
    }

    private async Task EditTextAtPointAsync(
        int pageIndex,
        double x,
        double y,
        string? requestedSelection = null,
        (double Left, double Bottom, double Right, double Top)? requestedBounds = null)
    {
        if (_isInlineEditorOpen)
        {
            EditorStatusText.Text = "Já existe uma edição de texto aberta. Conclua ou cancele antes de iniciar outra.";
            return;
        }
        _isInlineEditorOpen = true;
        try
        {
            await EditTextAtPointCoreAsync(pageIndex, x, y, requestedSelection, requestedBounds);
        }
        finally { _isInlineEditorOpen = false; }
    }

    private async Task EditTextAtPointCoreAsync(
        int pageIndex, double x, double y, string? requestedSelection,
        (double Left, double Bottom, double Right, double Top)? requestedBounds)
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
            bool originalFontIsUnidentifiedCid = false;
            string? originalPdfFontResourceName = null;
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
                    if (bounds == null || string.IsNullOrWhiteSpace(fragment.Text))
                        continue;

                    bool intersectsSelection = requestedBounds is { } selectedBounds &&
                        bounds.URX >= selectedBounds.Left &&
                        bounds.LLX <= selectedBounds.Right &&
                        bounds.URY >= selectedBounds.Bottom &&
                        bounds.LLY <= selectedBounds.Top;

                    double dx = x < bounds.LLX ? bounds.LLX - x :
                        x > bounds.URX ? x - bounds.URX : 0;
                    double dy = y < bounds.LLY ? bounds.LLY - y :
                        y > bounds.URY ? y - bounds.URY : 0;
                    double distance = Math.Sqrt(dx * dx + dy * dy);

                    // A seleção do PDFium é a fonte da verdade para o texto que
                    // o usuário marcou. O Aspose é consultado apenas para recuperar
                    // a fonte/estilo de origem, não para reduzir a seleção ao
                    // fragmento mais próximo do clique.
                    bool candidateMatches = requestedBounds.HasValue
                        ? intersectsSelection
                        : distance <= 5.0;

                    if (candidateMatches && distance < bestDistance)
                    {
                        bestDistance = distance;
                        originalText = requestedSelection ?? fragment.Text;
                        var sourceFont = fragment.TextState.Font ??
                            fragment.Segments.Cast<TextSegment>()
                                .Select(segment => segment.TextState.Font)
                                .FirstOrDefault(font => font != null);
                        originalFontEmbedded = sourceFont?.IsEmbedded == true;
                        string decodedFontName = sourceFont?.DecodedFontName ??
                                                  sourceFont?.BaseFont ??
                                                  fragment.TextState.FontName ??
                                                  string.Empty;
                        originalPdfFontResourceName = decodedFontName;
                        originalFontName = NormalizePdfFontFamily(decodedFontName);
                        originalFontIsUnidentifiedCid =
                            originalFontName.StartsWith("CIDFONT+", StringComparison.OrdinalIgnoreCase) ||
                            (sourceFont?.IsCid == true &&
                             (string.IsNullOrWhiteSpace(originalFontName) || originalFontName.Length <= 2));

                        // Do not silently assume Arial for an unidentified CID resource.
                        // The user must explicitly choose a replacement before editing it.
                        if (originalFontIsUnidentifiedCid)
                            originalFontName = originalFontEmbedded ? "Arial" : null;
                        else if (string.IsNullOrWhiteSpace(originalFontName) || originalFontName.Length <= 2)
                            originalFontName = "Arial";
                        originalFontSize = fragment.TextState.FontSize > 0
                            ? fragment.TextState.FontSize
                            : 12;
                        originalBold = fragment.TextState.IsBold ||
                       CidFontOperatorFormatter.IsBoldFace(sourceFont);
                        originalItalic = fragment.TextState.IsItalic;

                        if (fragment.TextState.ForegroundColor is { } foreground)
                            originalForeground = Avalonia.Media.Color.FromRgb(
                                foreground.R, foreground.G, foreground.B);
                        if (fragment.TextState.BackgroundColor is { } background)
                            originalBackground = Avalonia.Media.Color.FromRgb(
                                background.R, background.G, background.B);

                        originalBounds = requestedBounds is { } selectedRectangle
                            ? new Aspose.Pdf.Rectangle(
                                selectedRectangle.Left, selectedRectangle.Bottom,
                                selectedRectangle.Right, selectedRectangle.Top)
                            : new Aspose.Pdf.Rectangle(
                                bounds.LLX, bounds.LLY, bounds.URX, bounds.URY);
                    }
                }
            }

            if (originalFontIsUnidentifiedCid && !originalFontEmbedded)
            {
                string resourceKey = string.IsNullOrWhiteSpace(originalPdfFontResourceName)
                    ? "fonte CID sem identificação"
                    : originalPdfFontResourceName;
                if (!_fontSubstitutions.TryGetValue(resourceKey, out string? replacement))
                {
                    replacement = await AskSubstituteFontAsync(resourceKey);
                    if (string.IsNullOrWhiteSpace(replacement))
                    {
                        EditorStatusText.Text =
                            $"Edição cancelada sem substituir a fonte '{resourceKey}'. O PDF não foi alterado. Baixe/instale a fonte original e abra o PDF novamente para carregá-la antes de editar este trecho.";
                        await ShowFontDownloadInstructionAsync(resourceKey);
                        return;
                    }
                    _fontSubstitutions[resourceKey] = replacement;
                }
                originalFontName = replacement;
            }
            else if (originalFontIsUnidentifiedCid && originalFontEmbedded)
            {
                // O nome CID é um identificador interno, não uma família instalável.
                // Arial é apenas a prévia do editor; a gravação preserva a fonte CID
                // incorporada e, quando possível, alterna para a variante Bold já existente.
                originalFontName = "Arial";
            }
            else if (!string.IsNullOrWhiteSpace(originalFontName) &&
                     _fontSubstitutions.TryGetValue(originalFontName, out string? mappedFamily))
            {
                originalFontName = mappedFamily;
            }

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
                FontFamilyChoices = GetEditorFontChoices(),
                DefaultFontSize = originalFontSize,
                DefaultForeground = Avalonia.Media.Brushes.Black,
                AllowImages = false,
                AllowTables = false,
                AllowRichPaste = false,
                AllowFindReplace = false,
                MinHeight = 250,
                ShowPageBoundaries = false
            };

            RichEditorToolbar.FontSizes = Enumerable.Range(0, 393)
                .Select(index => 4d + index * 0.5d)
                .ToArray();

            if (_formattingToolbar == null)
                InitializeFormattingToolbar();
            _formattingToolbar!.Target = richEditor;

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
                        Padding = new Thickness(12, 8),
                        Background = new Avalonia.Media.SolidColorBrush(
                            Avalonia.Media.Color.FromRgb(76, 52, 130)),
                        Foreground = Avalonia.Media.Brushes.White
                    }
                }
            };

            var cancelButton = (Button)actionPanel.Children[0];
            var applyButton = (Button)actionPanel.Children[1];

            double insertionPdfX = x;
            double insertionPdfY = y;

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
                },
                MinHeight = 280
            };

            var inlineEditor = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(37, 44, 55)),
                BorderBrush = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(76, 52, 130)),
                BorderThickness = new Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Thickness(8),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = originalText == null ? "NOVO TEXTO" : "EDITAR TEXTO",
                            FontSize = 10,
                            FontWeight = Avalonia.Media.FontWeight.Bold,
                            Foreground = new Avalonia.Media.SolidColorBrush(
                                Avalonia.Media.Color.FromRgb(196, 181, 253))
                        },
                        editorSurface,
                        actionPanel
                    }
                }
            };

            // O editor e sua barra de formatação ficam no painel lateral, como no
            // Typecraft. A página permanece livre para visualizar o resultado.
            EditorInlineEditorHost.Content = inlineEditor;
            EditorInlineEditorHost.IsVisible = true;
            EditorInstructionsPanel.IsVisible = false;
            EditorFormattingToolbarHost.IsVisible = true;
            EditSelectedTextButton.IsVisible = false;
            DeleteSelectedTextButton.IsVisible = false;
            EditorStatusText.Text = originalText == null
                ? "Digite e formate o novo texto no painel lateral."
                : "Edite o conteúdo e a formatação no painel lateral.";

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            applyButton.Click += (_, _) => completion.TrySetResult(true);
            cancelButton.Click += (_, _) => completion.TrySetResult(false);
            richEditor.Focus();

            bool accepted = await completion.Task;

            EditorInlineEditorHost.Content = null;
            EditorInlineEditorHost.IsVisible = false;
            EditorInstructionsPanel.IsVisible = true;
            EditorFormattingToolbarHost.IsVisible = false;
            if (_formattingToolbar != null && _defaultToolbarEditor != null)
                _formattingToolbar.Target = _defaultToolbarEditor;

            // Cancelar fecha o editor sem gerar qualquer alteração no PDF.
            if (!accepted)
            {
                EditorStatusText.Text = "Edição cancelada. O PDF permaneceu sem alterações.";
                return;
            }

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
            if (originalText == null && string.IsNullOrWhiteSpace(replacementText))
            {
                EditorStatusText.Text = "Digite algum texto antes de inserir.";
                return;
            }

            // Para uma alteração exclusivamente Regular -> Bold em uma fonte CID
            // incorporada, tentaremos reutilizar a variante Bold que já existe no PDF.
            // Nesse caminho não é necessário baixar/instalar uma fonte substituta.
            bool cidBoldOnlyEdit =
                originalText != null &&
                originalFontIsUnidentifiedCid &&
                originalFontEmbedded &&
                string.Equals(replacementText, originalText, StringComparison.Ordinal) &&
                editedRuns.Count == 1 &&
                editedRuns[0].Start == 0 &&
                editedRuns[0].Length == replacementText.Length &&
                editedRuns[0].IsBold &&
                !originalBold &&
                editedRuns[0].IsItalic == originalItalic &&
                Math.Abs(editedRuns[0].FontSize - originalFontSize) <= 0.75 &&
                editedRuns[0].Background == originalBackground &&
                string.Equals(
                    NormalizePdfFontFamily(editedRuns[0].FontFamily),
                    NormalizePdfFontFamily(originalFontName),
                    StringComparison.OrdinalIgnoreCase);

            // An italic-only change on an embedded CID font must not replace
            // the CID resource with an unrelated TrueType face. The formatter
            // applies a local CTM shear to the selected TJ glyphs instead.
            bool cidSyntheticItalicEdit =
                originalText != null &&
                originalFontIsUnidentifiedCid &&
                originalFontEmbedded &&
                string.Equals(replacementText, originalText, StringComparison.Ordinal) &&
                editedRuns.Count == 1 &&
                editedRuns[0].Start == 0 &&
                editedRuns[0].Length == replacementText.Length &&
                editedRuns[0].IsItalic &&
                !originalItalic &&
                editedRuns[0].IsBold == originalBold &&
                Math.Abs(editedRuns[0].FontSize - originalFontSize) <= 0.75 &&
                editedRuns[0].Background == originalBackground &&
                string.Equals(
                    NormalizePdfFontFamily(editedRuns[0].FontFamily),
                    NormalizePdfFontFamily(originalFontName),
                    StringComparison.OrdinalIgnoreCase);

            // Antes de gravar, garanta que as fontes necessárias às novas faces/famílias
            // estejam disponíveis. O download exige autorização e nunca instala fontes no SO.
            var requiredFonts = cidBoldOnlyEdit || cidSyntheticItalicEdit
                ? Array.Empty<string>()
                : editedRuns
                    .Where(run => !string.IsNullOrWhiteSpace(run.FontFamily) &&
                        (!string.Equals(
                            NormalizePdfFontFamily(run.FontFamily),
                            NormalizePdfFontFamily(originalFontName),
                            StringComparison.OrdinalIgnoreCase) ||
                         (!IsPdfInternalFontResource(originalPdfFontResourceName) &&
                          (run.IsBold != originalBold || run.IsItalic != originalItalic))))
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

            var formattingTrace = new List<string>
            {
                $"DIAGNÓSTICO DO EDITOR: parágrafos={editedParagraphs.Count}; " +
                $"tamanhos=[{string.Join(",", editedParagraphs.Select(paragraph =>
                    paragraph.Inlines.OfType<RichRun>().Sum(run => run.Text?.Length ?? 0)))}]; " +
                $"texto enviado='{replacementText.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n")}'; " +
                $"comprimento={replacementText.Length}; " +
                $"códigos=[{string.Join(" ", replacementText.Select(character => $"U+{(int)character:X4}"))}]; " +
                $"fontes=[{string.Join("; ", editedRuns.Select(run =>
                    $"início={run.Start}, tamanho={run.Length}, fonte={run.FontFamily}, pt={run.FontSize:0.##}"))}]"
            };
            bool deletionByBoundsFallback = false;
            bool replacementByBoundsFallback = false;
            ApplyPdfEdit(document =>
            {
                bool cidFontSwapApplied = false;
                TextFragment target;
                if (originalBounds != null && originalText != null)
                {
                    // Use o absorvedor padrão para preservar a associação entre
                    // TextFragment, segmentos e operadores do fluxo PDF. O diagnóstico
                    // mostra que a opção RemoveUnusedFonts pode deixar a saída ainda
                    // vinculada ao recurso CID/Type0 original, mesmo após atribuir a
                    // nova face TrueType incorporada.
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

                        if (string.Equals(
                                NormalizePdfText(fragment.Text),
                                NormalizePdfText(originalText),
                                StringComparison.Ordinal) &&
                            distance < closest)
                        {
                            closest = distance;
                            target = fragment;
                        }
                    }

                    if (target == null || closest > 8.0)
                    {
                        target = null!;
                        // A seleção pode reunir vários TextFragments (por exemplo,
                        // uma linha com estilos diferentes). Só reconstruímos o trecho
                        // quando os fragmentos inteiramente contidos na seleção,
                        // concatenados, reproduzem exatamente o texto selecionado.
                        var exactCandidate = pageAbsorber.TextFragments
                            .Where(fragment => fragment.Rectangle != null &&
                                string.Equals(
                                    NormalizePdfText(fragment.Text),
                                    NormalizePdfText(originalText),
                                    StringComparison.Ordinal))
                            .Select(fragment => new
                            {
                                Fragment = fragment,
                                Bounds = fragment.Rectangle!,
                                Distance = Math.Pow(
                                    (fragment.Rectangle!.LLX + fragment.Rectangle.URX -
                                     originalBounds.LLX - originalBounds.URX) / 2, 2) +
                                    Math.Pow(
                                    (fragment.Rectangle.LLY + fragment.Rectangle.URY -
                                     originalBounds.LLY - originalBounds.URY) / 2, 2)
                            })
                            .Where(item => item.Distance <= 64)
                            .OrderBy(item => item.Distance)
                            .ToArray();

                        if (exactCandidate.Length > 0 &&
                            (exactCandidate.Length == 1 ||
                             Math.Abs(exactCandidate[1].Distance - exactCandidate[0].Distance) >= 1))
                        {
                            target = exactCandidate[0].Fragment;
                        }
                    }

                    if (target == null)
                    {
                        const double selectionTolerance = 2.0;
                        var selectedFragments = pageAbsorber.TextFragments
                            .Where(fragment => fragment.Rectangle != null &&
                                !string.IsNullOrWhiteSpace(fragment.Text) &&
                                fragment.Rectangle.LLX >= originalBounds.LLX - selectionTolerance &&
                                fragment.Rectangle.LLY >= originalBounds.LLY - selectionTolerance &&
                                fragment.Rectangle.URX <= originalBounds.URX + selectionTolerance &&
                                fragment.Rectangle.URY <= originalBounds.URY + selectionTolerance)
                            .ToArray();

                        string selectedFragmentsText = string.Concat(
                            selectedFragments.Select(fragment => fragment.Text));
                        if (selectedFragments.Length == 0 ||
                            !string.Equals(
                                NormalizePdfText(selectedFragmentsText),
                                NormalizePdfText(originalText),
                                StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "A seleção não corresponde exatamente aos objetos de texto encontrados no PDF. " +
                                "A edição foi cancelada para não alterar texto vizinho.");
                        }

                        bool hasPartiallyOverlappingText = pageAbsorber.TextFragments.Any(fragment =>
                            fragment.Rectangle is { } rectangle &&
                            !string.IsNullOrWhiteSpace(fragment.Text) &&
                            rectangle.URX >= originalBounds.LLX &&
                            rectangle.LLX <= originalBounds.URX &&
                            rectangle.URY >= originalBounds.LLY &&
                            rectangle.LLY <= originalBounds.URY &&
                            !selectedFragments.Contains(fragment));

                        if (hasPartiallyOverlappingText)
                            throw new InvalidOperationException(
                                "Há outro objeto de texto que cruza a área selecionada. " +
                                "A edição foi cancelada para preservar o conteúdo vizinho.");

                        if (cidBoldOnlyEdit || cidSyntheticItalicEdit)
                            throw new InvalidOperationException(
                                "A formatação especial de fonte CID não pode ser aplicada com segurança a uma seleção composta por vários objetos. " +
                                "Edite um único bloco por vez.");

                        foreach (TextFragment fragment in selectedFragments)
                            fragment.Text = string.Empty;

                        if (replacementText.Length == 0)
                        {
                            deletionByBoundsFallback = true;
                            return;
                        }

                        target = new TextFragment(replacementText)
                        {
                            Position = new Position(originalBounds.LLX, originalBounds.LLY)
                        };
                        replacementByBoundsFallback = true;
                    }
                    if (cidBoldOnlyEdit)
                    {
                        var boldRun = editedRuns[0];
                        Aspose.Pdf.Color? boldForeground = boldRun.Foreground is { } boldColor
                            ? Aspose.Pdf.Color.FromRgb(
                                boldColor.R / 255d,
                                boldColor.G / 255d,
                                boldColor.B / 255d)
                            : null;
                        cidFontSwapApplied = CidFontOperatorFormatter.TryApplyBoldVariant(
                            document, pageIndex + 1, target, boldForeground,
                            out string cidSwapReason);
                        formattingTrace.Add(
                            cidFontSwapApplied
                                ? "FORMATAÇÃO CID: " + cidSwapReason
                                : "FORMATAÇÃO CID não aplicada: " + cidSwapReason +
                                  " Será tentada a substituição TrueType incorporada.");
                    }
                    else if (cidSyntheticItalicEdit)
                    {
                        var italicRun = editedRuns[0];
                        Aspose.Pdf.Color? italicForeground = italicRun.Foreground is { } requestedColor
                            ? Aspose.Pdf.Color.FromRgb(
                                requestedColor.R / 255d,
                                requestedColor.G / 255d,
                                requestedColor.B / 255d)
                            : null;
                        cidFontSwapApplied = CidFontOperatorFormatter.TryApplySyntheticItalicFallback(
                            document, pageIndex + 1, target, italicForeground, out string cidItalicReason);
                        formattingTrace.Add(
                            cidFontSwapApplied
                                ? "FORMATAÇÃO CID: " + cidItalicReason
                                : "FORMATAÇÃO CID itálico não aplicado: " + cidItalicReason +
                                  " Será tentada a substituição TrueType incorporada.");
                    }

                    if (!cidFontSwapApplied)
                    {
                        // O TextFragment possui opções próprias para operações de
                        // substituição de texto/fonte. Configurá-las somente no caminho
                        // Aspose evita reconstruir os operadores que a troca CID preserva.
                        target.TextEditOptions = new TextEditOptions(
                            TextEditOptions.FontReplace.RemoveUnusedFonts)
                        {
                            NoCharacterBehavior = TextEditOptions.NoCharacterAction.ReplaceFonts
                        };

                        // Não regrave o conteúdo quando o usuário alterou apenas a formatação.
                        // Em PDFs com fontes CID/Type0, atribuir o mesmo texto novamente faz
                        // o Aspose reconstruir os operadores Tj/TJ antes da troca de fonte.
                        if (!string.Equals(target.Text, replacementText, StringComparison.Ordinal))
                            target.Text = replacementText;
                    }
                }
                else
                {
                    target = new TextFragment(replacementText)
                    {
                        Position = new Position(insertionPdfX, insertionPdfY)
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
                if (originalText == null || replacementByBoundsFallback)
                    new TextBuilder(document.Pages[pageIndex + 1]).AppendText(target);

                if (!cidFontSwapApplied && replacementText.Length > 0)
                {
                    ApplyRichTextFormatting(
                        target, editedRuns, originalFontName, originalFontEmbedded,
                        originalBold, originalItalic, formattingTrace);
                }

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
                    if (expectedText.Length == 0)
                    {
                        // Excluir todo o conteúdo da caixa é uma edição válida.
                        // Confirme que o fragmento original deixou de existir na
                        // região selecionada; não use uma verificação global vazia.
                        if (originalText == null || originalBounds == null)
                            return false;

                        bool originalStillPresent;
                        if (deletionByBoundsFallback)
                        {
                            // RemoveAllText pode deixar fragmentos vizinhos repartidos.
                            // Confirme que a área selecionada não contém mais texto.
                            originalStillPresent = verificationAbsorber.TextFragments.Any(fragment =>
                                fragment.Rectangle is { } rectangle &&
                                rectangle.URX >= originalBounds.LLX &&
                                rectangle.LLX <= originalBounds.URX &&
                                rectangle.URY >= originalBounds.LLY &&
                                rectangle.LLY <= originalBounds.URY &&
                                !string.IsNullOrWhiteSpace(fragment.Text));
                        }
                        else
                        {
                            originalStillPresent = verificationAbsorber.TextFragments.Any(fragment =>
                                string.Equals(
                                    NormalizePdfText(fragment.Text),
                                    NormalizePdfText(originalText),
                                    StringComparison.Ordinal) &&
                                fragment.Rectangle is { } rectangle &&
                                rectangle.URX >= originalBounds.LLX &&
                                rectangle.LLX <= originalBounds.URX &&
                                rectangle.URY >= originalBounds.LLY &&
                                rectangle.LLY <= originalBounds.URY);
                        }

                        return !originalStillPresent;
                    }

                    if (!savedPageText.Contains(expectedText, StringComparison.Ordinal))
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
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("TYPE3_FONT_STYLE_UNSUPPORTED:", StringComparison.Ordinal))
        {
            if (_formattingToolbar != null && _defaultToolbarEditor != null)
                _formattingToolbar.Target = _defaultToolbarEditor;
            EditorStatusText.Text = ex.Message["TYPE3_FONT_STYLE_UNSUPPORTED:".Length..].Trim();
        }
        catch (Exception ex)
        {
            if (_formattingToolbar != null && _defaultToolbarEditor != null)
                _formattingToolbar.Target = _defaultToolbarEditor;
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

        // A reprodução mínima com Aspose 26.11 confirmou que um fragmento
        // inteiro pode trocar de fonte CID/Type0 para uma face TrueType
        // incorporada sem IsolateTextSegments. Nesse cenário, isolar o intervalo
        // completo só recria segmentos e introduz risco de perder o vínculo com
        // os operadores originais do fluxo de conteúdo. Preserve os segmentos
        // originais e aplique o estilo diretamente aos estados que já pertencem
        // ao documento, como no teste de gravação/reabertura.
        if (orderedRuns.Length == 1 &&
            orderedRuns[0].Start == 0 &&
            orderedRuns[0].Length == fragment.Text.Length)
        {
            EditedTextRun wholeRun = orderedRuns[0];

            // Reafirme o vínculo entre cada TextState e seu segmento antes de
            // alterar a fonte. Sem esse vínculo, o Aspose pode manter o estado
            // atualizado em memória, mas serializar a face anterior no PDF.
            RestoreSegmentStateOwnership(fragment);

            foreach (TextSegment segment in fragment.Segments)
            {
                ApplyRunStyle(
                    segment.TextState,
                    wholeRun,
                    originalFontName,
                    originalFontEmbedded,
                    originalBold,
                    originalItalic,
                    formattingTrace);
            }

            ApplyRunStyle(
                fragment.TextState,
                wholeRun,
                originalFontName,
                originalFontEmbedded,
                originalBold,
                originalItalic,
                formattingTrace);
            return;
        }

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
                // O editor representa quebras entre parágrafos como '\n', mas
                // não cria um RichRun para esse separador. O PDF pode, por sua
                // vez, expor esse caractere como um TextSegment independente.
                // Preserve o estado original desse segmento estrutural e avance
                // o cursor; não tente aplicar a ele o estilo do run vizinho.
                if (string.IsNullOrWhiteSpace(segment.Text))
                {
                    offset = segmentEnd;
                    continue;
                }

                throw new InvalidOperationException(
                    $"O segmento PDF [{offset}, {segmentEnd}) ('{segment.Text}') não corresponde integralmente a um trecho formatado do editor. A edição foi cancelada para evitar estilos incorretos.");
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
            // As opções no TextFragment não são necessariamente herdadas pelos
            // TextSegment que o Aspose reescreve individualmente. Configure também
            // o segmento físico para que a substituição por cobertura de glifos seja
            // aplicada exatamente ao trecho cujo TextState está sendo alterado.
            item.Segment.TextEditOptions = new TextEditOptions(
                TextEditOptions.FontReplace.RemoveUnusedFonts)
            {
                NoCharacterBehavior = TextEditOptions.NoCharacterAction.ReplaceFonts
            };

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

    private static Font ResolveEmbeddedTrueTypeFont(
        string requestedFamily,
        FontStyles requestedStyle,
        bool allowCidFallback,
        out string resolvedFamily)
    {
        bool wantsBold = (requestedStyle & FontStyles.Bold) == FontStyles.Bold;
        bool wantsItalic = (requestedStyle & FontStyles.Italic) == FontStyles.Italic;

        var failures = new List<string>();
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Aspose's FindFont can return the correct PostScript face name while
        // exposing stale IsBold/IsItalic flags. Open the physical system font
        // file directly first; the font program's internal names are used to
        // verify family and style before it can be embedded.
        foreach (string filePath in InstalledFontCatalogService.GetCandidateFontFiles(requestedFamily))
        {
            if (!attempted.Add(filePath))
                continue;

            try
            {
                Font physicalFont = FontRepository.OpenFont(filePath);
                if (!FontProgramMatchesRequest(
                        physicalFont, requestedFamily, wantsBold, wantsItalic))
                {
                    failures.Add(
                        $"{Path.GetFileName(filePath)}: a face física não corresponde à família/estilo " +
                        $"(BaseFont='{physicalFont.BaseFont}', FontName='{physicalFont.FontName}', " +
                        $"Bold={physicalFont.IsBold}, Italic={physicalFont.IsItalic}).");
                    continue;
                }

                return PrepareFontForEmbedding(physicalFont, filePath, requestedFamily, out resolvedFamily);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        // Keep Aspose's indexed lookup as a secondary route for font sources
        // that are not exposed as ordinary TTF/OTF files (for example, platform
        // font collections). Never accept a face whose actual names contradict
        // the requested family or style.
        var families = allowCidFallback
            ? new[] { requestedFamily, "Arial", "Helvetica Neue", "Noto Sans", "Liberation Sans", "DejaVu Sans" }
            : new[] { requestedFamily };

        foreach (string family in families.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string explicitFace = GetPdfFontFaceName(family, wantsBold, wantsItalic);
            var lookups = new (string Label, Func<Font> Resolve)[]
            {
                (explicitFace, () => FontRepository.FindFont(explicitFace)),
                ($"{family} [{requestedStyle}]", () => FontRepository.FindFont(family, requestedStyle))
            };

            foreach (var lookup in lookups)
            {
                if (!attempted.Add(lookup.Label))
                    continue;

                try
                {
                    Font font = lookup.Resolve();
                    if (!FontProgramMatchesRequest(font, family, wantsBold, wantsItalic))
                    {
                        throw new InvalidOperationException(
                            $"a face retornada não corresponde ao estilo solicitado " +
                            $"(esperado Bold={wantsBold}, Italic={wantsItalic}; " +
                            $"encontrado Bold={font.IsBold}, Italic={font.IsItalic}; " +
                            $"FontName='{font.FontName}', BaseFont='{font.BaseFont}').");
                    }

                    return PrepareFontForEmbedding(font, null, family, out resolvedFamily);
                }
                catch (Exception ex)
                {
                    failures.Add($"{lookup.Label}: {ex.Message}");
                }
            }
        }

        throw new InvalidOperationException(
            "Não foi encontrada uma face de fonte incorporável que corresponda exatamente ao estilo solicitado. " +
            $"Família='{requestedFamily}', negrito={wantsBold}, itálico={wantsItalic}. " +
            "A edição foi interrompida para não gravar uma fonte visualmente diferente da prévia. " +
            string.Join(" | ", failures));
    }

    private static Font PrepareFontForEmbedding(
        Font font,
        string? sourcePath,
        string requestedFamily,
        out string resolvedFamily)
    {
        font.IsEmbedded = true;
        using var embeddedProgram = new MemoryStream();
        font.Save(embeddedProgram);
        if (embeddedProgram.Length < 12)
        {
            string embeddingError = font.GetLastFontEmbeddingError()
                ?? "o mecanismo não forneceu o programa da fonte";
            throw new InvalidOperationException(
                $"A fonte '{requestedFamily}' não pôde ser incorporada: {embeddingError}" +
                (sourcePath is null ? string.Empty : $" Arquivo: '{sourcePath}'."));
        }

        resolvedFamily = NormalizePdfFontFamily(font.FontName);
        if (string.IsNullOrWhiteSpace(resolvedFamily))
            resolvedFamily = requestedFamily;

        return font;
    }

    private static bool FontProgramMatchesRequest(
        Font font,
        string requestedFamily,
        bool wantsBold,
        bool wantsItalic)
    {
        string?[] names = { font.BaseFont, font.FontName, font.DecodedFontName };
        foreach (string? name in names)
        {
            if (InstalledFontCatalogService.FontNameMatchesRequestedStyle(
                    name, requestedFamily, wantsBold, wantsItalic,
                    font.IsBold, font.IsItalic))
                return true;
        }

        return false;
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
            $"stateFontSize={state.FontSize:0.##}; stateFontEmbedded={state.Font?.IsEmbedded}");

        bool preserveEmbeddedFace =
            originalFontEmbedded &&
            string.Equals(
                NormalizePdfFontFamily(fontFamily),
                NormalizePdfFontFamily(originalFontName),
                StringComparison.OrdinalIgnoreCase) &&
            run.IsBold == originalBold &&
            run.IsItalic == originalItalic;

        if (!preserveEmbeddedFace &&
            IsType3FontResourceName(originalFontName) &&
            string.Equals(
                NormalizePdfFontFamily(fontFamily),
                NormalizePdfFontFamily(originalFontName),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "TYPE3_FONT_STYLE_UNSUPPORTED: Esta página usa uma fonte Type 3 (" +
                $"{originalFontName}), cujos glifos são desenhos incorporados ao PDF e não possuem uma face Bold/Italic TrueType para substituição automática. " +
                "Para editar este trecho, escolha explicitamente outra família de fonte na barra de formatação. O arquivo não foi alterado.");
        }

        if (!preserveEmbeddedFace)
        {
            try
            {
                string faceName = GetPdfFontFaceName(fontFamily, run.IsBold, run.IsItalic);

                if (IsStandard14Family(fontFamily))
                {
                    // Standard-14 faces are Type1 resources and are not embedded.
                    // Keep this route only when the user explicitly chooses one of them.
                    state.Font = FontRepository.FindFont(faceName);
                }
                else
                {
                    // Resolve the real installed TrueType/OpenType face (not a PDF
                    // Standard-14 alias), request embedding, then assign the Font object.
                    // This is essential for CID/Type0 source text: changing only FontName
                    // or selecting Helvetica-Bold can leave the original CID CMap/resource
                    // attached to the content stream after save.
                    bool isCidFallback =
                        originalFontEmbedded &&
                        string.Equals(originalFontName, "Arial", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(fontFamily, "Arial", StringComparison.OrdinalIgnoreCase);

                    var embeddedFont = ResolveEmbeddedTrueTypeFont(
                        fontFamily, requestedStyle, isCidFallback, out string resolvedFamily);
                    state.Font = embeddedFont;

                    formattingTrace?.Add(
                        $"FONTE RESOLVIDA: solicitada='{fontFamily}'; " +
                        $"resolvida='{resolvedFamily}'; " +
                        $"font='{embeddedFont.FontName}'; embedded={embeddedFont.IsEmbedded}; " +
                        $"CIDFallback={isCidFallback}");
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
            $"fontEmbedded={state.Font?.IsEmbedded}; fontBold={state.Font?.IsBold}; " +
            $"stateFontSize={state.FontSize:0.##}");

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

    private static bool IsType3FontResourceName(string? fontName) =>
        !string.IsNullOrWhiteSpace(fontName) &&
        fontName.StartsWith("T3Font_", StringComparison.OrdinalIgnoreCase);

    private static bool IsStandard14Family(string family)
    {
        string normalized = NormalizePdfFontFamily(family);
        return normalized.Equals("Helvetica", StringComparison.OrdinalIgnoreCase) ||
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
        bool isHelvetica = normalized.Equals("Helvetica", StringComparison.OrdinalIgnoreCase);
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

    // Some embedded TrueType fonts are correctly saved as a bold face in the PDF
    // resource name (for example, "Agency FB Bold" / "AgencyFBBold"), while
    // Aspose.PDF reopens their TextState with IsBold=false and FontStyle=Regular.
    // The selected PDF font program is authoritative for rendering in that case.
    private static bool PdfFontFaceHasStyle(Aspose.Pdf.Text.Font? font, bool bold)
    {
        if (font == null)
            return false;

        if (bold && CidFontOperatorFormatter.IsBoldFace(font))
            return true;

        string[] names = { font.BaseFont, font.FontName, font.DecodedFontName };
        string[] suffixes = bold
            ? new[] { "BoldItalic", "BoldOblique", "SemiBold", "DemiBold", "BoldMT", "Bold" }
            : new[] { "BoldItalic", "BoldOblique", "ItalicMT", "Italic", "Oblique" };

        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim().Replace(" ", string.Empty, StringComparison.Ordinal))
            .Any(name => suffixes.Any(suffix =>
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
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

        // Mantenha os offsets brutos para diagnóstico, mas faça a associação
        // dos estilos por caracteres significativos. O Aspose pode reabrir o PDF
        // com espaços/quebras diferentes dos enviados pelo editor; NormalizePdfText
        // já considera esses textos equivalentes para localizar o grupo.
        var savedSegments = new List<(
            int Start, int End, int NormalizedStart, int NormalizedEnd, TextSegment Segment)>();
        int offset = 0;
        int normalizedOffset = 0;
        foreach (TextFragment selectedFragment in selectedGroup)
        {
            foreach (TextSegment segment in selectedFragment.Segments)
            {
                string segmentText = segment.Text ?? string.Empty;
                int end = checked(offset + segmentText.Length);
                int normalizedEnd = checked(
                    normalizedOffset + NormalizePdfText(segmentText).Length);
                savedSegments.Add((
                    offset, end, normalizedOffset, normalizedEnd, segment));
                offset = end;
                normalizedOffset = normalizedEnd;
            }
        }

        if (NormalizePdfText(string.Concat(selectedGroup.Select(item => item.Text))) != normalizedExpected)
            return Fail("os blocos recuperados não correspondem ao texto editado.");
        if (normalizedOffset != normalizedExpected.Length)
            return Fail(
                $"os blocos relidos totalizam {normalizedOffset} caracteres significativos, " +
                $"mas o texto normalizado da prévia possui {normalizedExpected.Length}; " +
                "não foi possível mapear os estilos por posição.");

        foreach (var run in expectedRuns.Where(item => item.Length > 0))
        {
            int runEnd = run.Start + run.Length;
            if (run.Start < 0 || runEnd > expectedText.Length)
                return Fail($"intervalo de formatação inválido na posição {run.Start}.");

            string runText = expectedText.Substring(run.Start, run.Length);
            int normalizedRunStart = NormalizePdfText(expectedText.Substring(0, run.Start)).Length;
            int normalizedRunEnd = NormalizePdfText(expectedText.Substring(0, runEnd)).Length;

            // Um intervalo composto somente por espaços/quebras não possui glifos
            // cuja fonte possa ser verificada no PDF reaberto.
            if (normalizedRunStart == normalizedRunEnd)
                continue;

            var covered = savedSegments.Where(item =>
                item.NormalizedStart < normalizedRunEnd &&
                item.NormalizedEnd > normalizedRunStart).ToArray();

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
                bool savedBold = state.IsBold ||
                    PdfFontFaceHasStyle(state.Font, bold: true) ||
                    state.RenderingMode == Aspose.Pdf.Text.TextRenderingMode.FillThenStrokeText;
                bool savedItalic = state.IsItalic ||
                    PdfFontFaceHasStyle(state.Font, bold: false) ||
                    (state.Font?.IsCid == true &&
                     CidFontOperatorFormatter.HasSyntheticItalicOperator(
                         document.Pages[pageIndex + 1], state.Font, runText));
                if (savedBold != run.IsBold && !unchangedEmbeddedFace)
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
                          $"FaceBold={PdfFontFaceHasStyle(actualFont, bold: true)}; " +
                          $"FaceItalic={PdfFontFaceHasStyle(actualFont, bold: false)}; " +
                          $"Font.IsCid={actualFont.IsCid}; Font.IsEmbedded={actualFont.IsEmbedded}; " +
                          $"Font.IsAccessible={actualFont.IsAccessible}";
                    return Fail(
                        $"negrito divergente na posição {run.Start}: esperado {run.IsBold}, " +
                        $"IsBold salvo={state.IsBold}; estilo efetivo salvo={savedBold}; " +
                        $"FontStyle salvo={state.FontStyle}; FontName salvo='{savedFontName}'; " +
                        $"Font.FontName='{actualFontName}'; Font.BaseFont='{actualBaseFont}'; " +
                        fontDetails);
                }
                if (savedItalic != run.IsItalic && !unchangedEmbeddedFace)
                {
                    string savedFontName = state.FontName ?? "(nulo)";
                    return Fail(
                        $"itálico divergente na posição {run.Start}: esperado {run.IsItalic}, " +
                        $"estilo efetivo salvo={savedItalic}; IsItalic={state.IsItalic}; " +
                        $"FontStyle={state.FontStyle}; FontName='{savedFontName}'.");
                }

                bool expectsEmbeddedTrueTypeReplacement =
                    originalFontEmbedded &&
                    string.Equals(originalFontName, "Arial", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(run.FontFamily, "Arial", StringComparison.OrdinalIgnoreCase) &&
                    (run.IsBold != originalBold || run.IsItalic != originalItalic);

                if (expectsEmbeddedTrueTypeReplacement && state.Font?.IsEmbedded != true)
                {
                    string embeddingError = state.Font?.GetLastFontEmbeddingError() ?? "sem detalhe informado";
                    return Fail(
                        $"a fonte TrueType substituta não permaneceu incorporada na posição {run.Start}. " +
                        $"Fonte salva='{state.FontName}'; erro de incorporação='{embeddingError}'.");
                }

                bool acceptedEmbeddedCidSubstitute =
                    expectsEmbeddedTrueTypeReplacement && state.Font?.IsEmbedded == true;

                if (!string.IsNullOrWhiteSpace(run.FontFamily) &&
                    !(originalFontEmbedded && string.Equals(
                        run.FontFamily, originalFontName, StringComparison.OrdinalIgnoreCase)) &&
                    !acceptedEmbeddedCidSubstitute &&
                    !string.Equals(
                        NormalizePdfFontFamily(state.FontName),
                        NormalizePdfFontFamily(run.FontFamily),
                        StringComparison.OrdinalIgnoreCase))
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

    private static bool IsPdfInternalFontResource(string? value)
    {
        string name = (value ?? string.Empty).Trim();
        if (name.StartsWith("CIDFONT+", StringComparison.OrdinalIgnoreCase))
            return true;

        // Type 3 fonts may be exposed by Aspose with resource names such as T3Font_1.
        int marker = name.IndexOf("Font_", StringComparison.OrdinalIgnoreCase);
        return marker > 1 &&
               name.StartsWith("T", StringComparison.OrdinalIgnoreCase) &&
               name.AsSpan(1, marker - 1).ToString().All(char.IsDigit) &&
               name.AsSpan(marker + 5).Length > 0 &&
               name.AsSpan(marker + 5).ToString().All(char.IsDigit);
    }

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

    private void AddPage_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            int newPageCount = 0;
            ApplyPdfEdit(document =>
            {
                var lastPage = document.Pages[document.Pages.Count];
                double width = lastPage.Rect.Width;
                double height = lastPage.Rect.Height;
                var newPage = document.Pages.Add();
                newPage.SetPageSize(width, height);
                newPageCount = document.Pages.Count;
            }, "Nova página adicionada ao final do documento.",
            savedDocument => savedDocument.Pages.Count == newPageCount,
            allowPageCountChange: true);
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = "Não foi possível adicionar a página. " + ex.Message;
        }
    }

    private void ApplyPdfEdit(
        Action<Document> edit,
        string successMessage,
        Func<Document, bool>? verifySavedDocument = null,
        bool allowPageCountChange = false)
    {
        string outputPath = Path.Combine(_sessionDirectory, $"edit-{Guid.NewGuid():N}.pdf");
        string historySnapshotPath = Path.Combine(
            _sessionDirectory, $"undo-{Guid.NewGuid():N}.pdf");
        bool snapshotCreated = false;
        bool snapshotStored = false;
        int originalPageCount;
        int expectedPageCount;

        try
        {
            // O histórico usa arquivos temporários, não arrays de bytes em RAM.
            // Assim, desfazer/refazer não mantém até dez PDFs completos na memória.
            File.Copy(_workingCopyPath, historySnapshotPath, overwrite: false);
            snapshotCreated = true;

            using (var document = Document.Open(_workingCopyPath))
            {
                originalPageCount = document.Pages.Count;
                edit(document);
                expectedPageCount = document.Pages.Count;
                if (!allowPageCountChange && expectedPageCount != originalPageCount)
                    throw new IOException("Esta operação não pode alterar a quantidade de páginas.");
                document.Save(outputPath);
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new IOException("A biblioteca não gerou um PDF de saída válido.");

            using (var savedDocument = Document.Open(outputPath))
            {
                if (savedDocument.Pages.Count == 0)
                    throw new IOException("O PDF gerado não contém páginas.");
                if (savedDocument.Pages.Count != expectedPageCount)
                    throw new IOException(
                        "A quantidade de páginas do PDF salvo não corresponde à alteração solicitada. A cópia anterior foi preservada.");

                if (verifySavedDocument != null && !verifySavedDocument(savedDocument))
                    throw new IOException(
                        "O PDF foi gerado, mas o texto, elemento ou formatação não foi preservado no arquivo salvo. A cópia anterior foi preservada.");
            }

            // O arquivo precisa abrir e renderizar em todas as páginas no PDFium
            // antes de substituir a cópia de trabalho.
            ValidatePdfWithPdfium(outputPath, expectedPageCount);

            _document?.Dispose();
            _document = null;

            File.Move(outputPath, _workingCopyPath, true);
            if (!string.IsNullOrWhiteSpace(_historyEntryId))
                _history.Touch(_historyEntryId);

            PushHistory(_undoStack, historySnapshotPath);
            snapshotStored = true;
            ClearHistoryStack(_redoStack);
            LoadDocumentPreview();

            EditorStatusText.Text = successMessage +
                " Alteração gravada e visualização atualizada. O PDF original permanece intacto. " +
                (_hasExistingSignatures
                    ? "ATENÇÃO: este documento possuía assinaturas digitais; a cópia editada pode ter perdido a validade criptográfica. O arquivo original não foi alterado."
                    : "Qualquer alteração pode invalidar assinaturas digitais existentes no PDF.");
            _ = AuditDocumentSignaturesAsync();
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); }
            catch { /* Limpeza temporária best-effort. */ }

            if (snapshotCreated && !snapshotStored)
            {
                try { if (File.Exists(historySnapshotPath)) File.Delete(historySnapshotPath); }
                catch { /* Limpeza temporária best-effort. */ }
            }
        }
    }

    private static void ValidatePdfWithPdfium(string pdfPath, int? expectedPageCount = null)
    {
        using var preview = new PdfiumDocument(pdfPath);
        if (preview.PageCount <= 0)
            throw new InvalidDataException("O PDF não contém páginas válidas para visualização.");
        if (expectedPageCount.HasValue && preview.PageCount != expectedPageCount.Value)
            throw new InvalidDataException("O PDFium identificou uma quantidade de páginas diferente da original.");

        for (int pageIndex = 0; pageIndex < preview.PageCount; pageIndex++)
        {
            RenderedPdfPage page = preview.RenderPage(pageIndex, 0.25f);
            if (page.Width <= 0 || page.Height <= 0 ||
                page.Pixels.Length < checked(page.Stride * page.Height))
            {
                throw new InvalidDataException(
                    $"O PDFium não conseguiu validar a renderização da página {pageIndex + 1}.");
            }
        }
    }

    private static void PushHistory(Stack<string> stack, string snapshotPath)
    {
        stack.Push(snapshotPath);
        if (stack.Count <= 10)
            return;

        string[] entries = stack.ToArray();
        stack.Clear();
        foreach (string retained in entries.Take(10).Reverse())
            stack.Push(retained);

        foreach (string obsolete in entries.Skip(10))
        {
            try
            {
                if (File.Exists(obsolete))
                    File.Delete(obsolete);
            }
            catch
            {
                // A falha ao limpar um snapshot antigo não deve cancelar a edição atual.
            }
        }
    }

    private static void ClearHistoryStack(Stack<string> stack)
    {
        foreach (string snapshot in stack)
        {
            try
            {
                if (File.Exists(snapshot))
                    File.Delete(snapshot);
            }
            catch
            {
                // Limpeza best-effort ao trocar/fechar documento.
            }
        }
        stack.Clear();
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

    private void RestoreHistory(
        Stack<string> source,
        Stack<string> destination,
        string message)
    {
        if (source.Count == 0)
            return;

        string targetSnapshot = source.Pop();
        string currentSnapshot = Path.Combine(
            _sessionDirectory, $"history-current-{Guid.NewGuid():N}.pdf");
        string restorePath = Path.Combine(
            _sessionDirectory, $"history-restore-{Guid.NewGuid():N}.pdf");
        bool destinationStored = false;
        bool targetConsumed = false;

        try
        {
            File.Copy(_workingCopyPath, currentSnapshot, overwrite: false);
            File.Copy(targetSnapshot, restorePath, overwrite: false);
            ValidatePdfWithPdfium(restorePath);

            _document?.Dispose();
            _document = null;
            File.Move(restorePath, _workingCopyPath, overwrite: true);

            PushHistory(destination, currentSnapshot);
            destinationStored = true;

            if (File.Exists(targetSnapshot))
                File.Delete(targetSnapshot);
            targetConsumed = true;

            if (!string.IsNullOrWhiteSpace(_historyEntryId))
                _history.Touch(_historyEntryId);

            LoadDocumentPreview();
            EditorStatusText.Text = message + " O PDF original permanece preservado.";
        }
        catch (Exception ex)
        {
            if (!targetConsumed && File.Exists(targetSnapshot))
                source.Push(targetSnapshot);

            EditorStatusText.Text =
                $"Não foi possível restaurar a versão; o estado anterior foi preservado. {ex.Message}";
        }
        finally
        {
            try
            {
                if (File.Exists(restorePath))
                    File.Delete(restorePath);
            }
            catch { /* Limpeza temporária best-effort. */ }

            if (!destinationStored)
            {
                try
                {
                    if (File.Exists(currentSnapshot))
                        File.Delete(currentSnapshot);
                }
                catch { /* Limpeza temporária best-effort. */ }
            }
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

            string temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var sourceStream = new FileStream(
                    _workingCopyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destinationStream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    sourceStream.CopyTo(destinationStream);
                    destinationStream.Flush(flushToDisk: true);
                }

                ValidatePdfWithPdfium(temporaryPath);

                // CreateNew/Move sem overwrite impede substituir um arquivo do usuário.
                File.Move(temporaryPath, targetPath, overwrite: false);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // Limpeza temporária best-effort; o destino só existe após validação.
                }
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
        ClearHistoryStack(_undoStack);
        ClearHistoryStack(_redoStack);
        _document?.Dispose();
        _document = null;
        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
        _bitmaps.Clear();
    }

}