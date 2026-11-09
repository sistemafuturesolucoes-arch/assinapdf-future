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
    private string _workingCopyPath;
    private string _originalPath;
    private string _sessionDirectory;
    private PdfiumDocument? _document;
    private readonly List<WriteableBitmap> _bitmaps = new();
    private readonly Stack<byte[]> _undoStack = new();
    private readonly Stack<byte[]> _redoStack = new();
    private readonly EditorHistoryService _history = new();
    private string? _historyEntryId;
    private readonly Dictionary<int, (AvaloniaImage Image, Canvas Overlay)> _pageSurfaces = new();
    private int? _selectedPageIndex;
    private double _selectedPdfX;
    private double _selectedPdfY;
    private enum EditorToolMode { Select, AddText }
    private EditorToolMode _toolMode = EditorToolMode.Select;
    private bool _isDraggingSelection;
    private Avalonia.Point _selectionStart;
    private Avalonia.Point _selectionEnd;
    private AvaloniaImage? _activeSelectionImage;
    private PagePreviewInfo? _activeSelectionPage;
    private string _selectedText = string.Empty;
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
        Closed += (_, _) => DisposeResources();
        EditorStatusText.Text = "Clique em “Abrir PDF” para começar. O documento original será preservado.";
    }

    public EditorWindow(string workingCopyPath, string originalPath, string sessionDirectory)
    {
        InitializeComponent();
        InitializeFormattingToolbar();

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

    private void InitializeFormattingToolbar()
    {
        RichEditorToolbar.FontSizes = new[]
        {
            6d, 7d, 8d, 9d, 10d, 11d, 12d, 14d, 16d,
            18d, 20d, 22d, 24d, 26d, 28d, 36d, 48d, 72d
        };

        _defaultToolbarEditor = new RichEditorControl
        {
            Document = new RichFlowDocument(),
            FontFamilyChoices = InstalledFontCatalogService.GetInstalledFamilyNames(),
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

        // Registra as pastas nativas antes de consultar Aspose.FontRepository.
        // Assim, fontes já instaladas não são tratadas como ausentes.
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
                        // Um fragmento pode conter vários segmentos com fontes
                        // diferentes. A pré-verificação precisa inspecionar cada
                        // segmento, não apenas a fonte herdada pelo fragmento.
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

            string cidNote = unidentifiedCidFonts.Count == 0
                ? string.Empty
                : " Fontes CID sem família identificável: " +
                  string.Join(", ", unidentifiedCidFonts.Select(item =>
                      $"{item.Key} ({(item.Value ? "incorporada no PDF" : "não incorporada")})")) +
                  ". O nome CID é um identificador interno do PDF, não o nome real da família. " +
                  "Fontes incorporadas acompanham o documento; fontes CID não incorporadas precisam de substituição explícita.";

            var unavailable = new List<string>();
            var substitutions = new List<string>();
            var downloadedFamilies = new List<string>();

            foreach (string family in missing)
            {
                bool downloaded = false;
                try
                {
                    downloaded = await FontDownloadService.EnsureAvailableAsync(this, family);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"AssinaPDF: não foi possível obter a fonte '{family}': {ex.Message}");
                }

                if (downloaded)
                {
                    downloadedFamilies.Add(family);
                    continue;
                }

                string? substitute = await AskSubstituteFontAsync(family);
                if (string.IsNullOrWhiteSpace(substitute))
                {
                    unavailable.Add(family);
                    await ShowFontDownloadInstructionAsync(family);
                    continue;
                }

                _fontSubstitutions[family] = substitute;
                substitutions.Add($"{family} → {substitute}");
            }

            // Uma fonte CID não incorporada não pode ser baixada pelo nome
            // "CIDFont+F2". Solicite uma família instalada e guarde o vínculo
            // com o identificador interno para a edição posterior desse texto.
            foreach (var cidFont in unidentifiedCidFonts.Where(item => !item.Value))
            {
                if (_fontSubstitutions.ContainsKey(cidFont.Key))
                    continue;

                string? substitute = await AskSubstituteFontAsync(cidFont.Key);
                if (string.IsNullOrWhiteSpace(substitute))
                {
                    unavailable.Add(cidFont.Key + " (CID não incorporada)");
                    await ShowFontDownloadInstructionAsync(cidFont.Key);
                    continue;
                }

                _fontSubstitutions[cidFont.Key] = substitute;
                substitutions.Add($"{cidFont.Key} → {substitute}");
            }

            if (unavailable.Count == 0)
            {
                if (missing.Length == 0 && substitutions.Count == 0)
                {
                    EditorStatusText.Text = fonts.Count == 0 && unidentifiedCidFonts.Count == 0
                        ? "PDF aberto. Não foram encontrados textos com fontes identificáveis."
                        : $"Verificação concluída: {fonts.Count} família(s) identificada(s); nenhuma fonte conhecida ausente." +
                          cidNote;
                    return;
                }

                string resolutionNote = downloadedFamilies.Count > 0 && substitutions.Count > 0
                    ? " As fontes ausentes foram baixadas ou substituídas conforme sua escolha."
                    : downloadedFamilies.Count > 0
                        ? " As fontes ausentes foram obtidas para uso interno do AssinaPDF."
                        : " As substituições escolhidas serão usadas ao editar os trechos correspondentes.";
                string substitutionNote = substitutions.Count == 0
                    ? string.Empty
                    : " Substituições: " + string.Join(", ", substitutions) + ".";
                EditorStatusText.Text = $"Verificação concluída: {fonts.Count} família(s) identificada(s)." +
                    resolutionNote + substitutionNote + cidNote +
                    " Nenhuma fonte foi instalada no sistema.";
                return;
            }

            string names = string.Join(", ", unavailable);
            string substitutionsDone = substitutions.Count == 0
                ? string.Empty
                : " Substituições escolhidas: " + string.Join(", ", substitutions) + ".";
            EditorStatusText.Text = $"Atenção: {unavailable.Count} fonte(s) não estão disponíveis ({names}). O PDF continua aberto e será exibido com a substituição de fonte do mecanismo de visualização. Não edite os trechos que usam essas fontes até obter a fonte original ou escolher uma substituta; espaçamento e quebras de linha podem mudar." +
                substitutionsDone + cidNote;
        }
        catch (Exception ex)
        {
            EditorStatusText.Text = "Não foi possível concluir a verificação de fontes. O PDF continua aberto; a aparência original da visualização foi mantida. " + ex.Message;
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
                                    IsHitTestVisible = true
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
            pageImage.PointerMoved += PageImage_PointerMoved;
            pageImage.PointerReleased += PageImage_PointerReleased;
            pageImage.ContextMenu = CreateSelectionContextMenu();
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

    private ContextMenu CreateSelectionContextMenu()
    {
        var editItem = new MenuItem { Header = "Editar texto" };
        editItem.Click += EditSelectedText_Click;
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
        if (_isDraggingSelection && ReferenceEquals(sender, _activeSelectionImage))
            _selectionEnd = e.GetPosition(_activeSelectionImage);
    }

    private void PageImage_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSelection || sender is not AvaloniaImage image ||
            _activeSelectionPage is not PagePreviewInfo pageInfo)
            return;

        _selectionEnd = e.GetPosition(image);
        _isDraggingSelection = false;
        e.Pointer.Capture(null);

        double width = Math.Abs(_selectionEnd.X - _selectionStart.X);
        double height = Math.Abs(_selectionEnd.Y - _selectionStart.Y);
        bool isClick = width < 4 && height < 4;

        SelectTextInRectangle(image, pageInfo, _selectionStart, _selectionEnd);

        // Um clique simples abre a edição diretamente sobre a página.
        // Arrastar continua permitindo selecionar um trecho maior antes de editar.
        if (isClick && _selectedPageIndex is int pageIndex)
            _ = EditTextAtPointAsync(pageIndex, _selectedPdfX, _selectedPdfY);

        _activeSelectionImage = null;
        _activeSelectionPage = null;
        e.Handled = true;
    }

    private void SelectTextAtPoint(AvaloniaImage image, PagePreviewInfo pageInfo, Avalonia.Point point)
    {
        if (!PdfiumPageCoordinateMapper.TryViewToPdf(point.X, point.Y, image.Bounds.Width, image.Bounds.Height,
            pageInfo.WidthPoints, pageInfo.HeightPoints, out double pdfX, out double pdfY) || _document == null)
            return;
        try
        {
            var characters = _document.InspectPageCharacters(pageInfo.PageIndex);
            var lines = PdfiumTextGrouping.GroupIntoLines(characters, pageInfo.PageIndex);
            var word = PdfiumTextHitTest.FindWordAtPoint(lines, pageInfo.PageIndex, pdfX, pdfY, 3.0);
            if (word != null)
            {
                SetTextSelection(image, pageInfo, word.Characters, word.Text);
                return;
            }
            var character = PdfiumTextHitTest.FindCharacterAtPoint(characters, pageInfo.PageIndex, pdfX, pdfY, 3.0);
            if (character != null)
                SetTextSelection(image, pageInfo, new[] { character }, character.Text);
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
                pageInfo.WidthPoints, pageInfo.HeightPoints, out double x1, out double yTop) ||
            !PdfiumPageCoordinateMapper.TryViewToPdf(right, bottom, image.Bounds.Width, image.Bounds.Height,
                pageInfo.WidthPoints, pageInfo.HeightPoints, out double x2, out double yBottom))
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
            SetTextSelection(image, pageInfo, selected, string.Concat(selected.Select(ch => ch.Text)));
        }
        catch (Exception ex) { EditorStatusText.Text = $"Não foi possível selecionar o trecho: {ex.Message}"; }
    }

    private void SetTextSelection(AvaloniaImage image, PagePreviewInfo pageInfo,
        IReadOnlyList<PdfiumCharacterInfo> characters, string text)
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
        var rect = PdfiumPageCoordinateMapper.PdfToViewRect(left, bottom, right, top,
            image.Bounds.Width, image.Bounds.Height, pageInfo.WidthPoints, pageInfo.HeightPoints);
        var highlight = new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(35, 116, 220)),
            BorderThickness = new Thickness(1.5),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(62, 35, 116, 220)),
            Width = Math.Max(3, rect.Width), Height = Math.Max(3, rect.Height), IsHitTestVisible = false
        };
        Canvas.SetLeft(highlight, rect.X);
        Canvas.SetTop(highlight, rect.Y);
        surface.Overlay.Children.Add(highlight);
        SelectedTextLabel.Text = text.Length > 70 ? text[..67] + "..." : text;
        SelectedTextLabel.IsVisible = true;
        EditSelectedTextButton.IsVisible = true;
        EditorStatusText.Text = "Trecho selecionado no PDF. Clique com o botão direito para editar ou copiar.";
    }

    private void SelectMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _toolMode = EditorToolMode.Select;
        SelectModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(233, 227, 245));
        SelectModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(76, 52, 130));
        AddTextModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        AddTextModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        EditorStatusText.Text = "Modo Selecionar: arraste sobre o texto no PDF; clique com o botão direito para ver as ações.";
    }

    private void AddTextMode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _toolMode = EditorToolMode.AddText;
        ClearTextSelection();
        AddTextModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(233, 227, 245));
        AddTextModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(76, 52, 130));
        SelectModeButton.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(240, 241, 244));
        SelectModeButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(52, 56, 62));
        EditorStatusText.Text = "Modo Adicionar texto: clique na página do PDF no ponto em que deseja inserir o texto.";
    }

    private async Task AddTextAtPointAsync(PagePreviewInfo pageInfo, Avalonia.Point point, AvaloniaImage image)
    {
        if (!PdfiumPageCoordinateMapper.TryViewToPdf(
            point.X, point.Y, image.Bounds.Width, image.Bounds.Height,
            pageInfo.WidthPoints, pageInfo.HeightPoints,
            out double pdfX, out double pdfY))
            return;

        // A ferramenta T usa o mesmo editor rico inline da edição de texto:
        // clique na página, digite e aplique sem abrir uma janela modal.
        await EditTextAtPointAsync(pageInfo.PageIndex, pdfX, pdfY);
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
            _undoStack.Clear();
            _redoStack.Clear();
            DocumentNameText.Text = Path.GetFileName(source);
            Title = $"AssinaPDF | Editor - {Path.GetFileName(source)}";
            await VerifyDocumentFontsAsync();
            LoadDocumentPreview();
            // Keep the font-audit result (including download/substitution warnings)
            // visible after the page preview is rendered. Do not overwrite it with
            // a generic success message here.
        }
        catch (Exception ex) { EditorStatusText.Text = $"Não foi possível abrir o PDF: {ex.Message}"; }
    }

    private void ClearTextSelection()
    {
        foreach (var surface in _pageSurfaces.Values)
            surface.Overlay.Children.Clear();

        _selectedPageIndex = null;
        SelectedTextLabel.Text = string.Empty;
        SelectedTextLabel.IsVisible = false;
        EditSelectedTextButton.IsVisible = false;
        _selectedText = string.Empty;
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
                        originalPdfFontResourceName = decodedFontName;
                        originalFontName = NormalizePdfFontFamily(decodedFontName);
                        originalFontIsUnidentifiedCid =
                            originalFontName.StartsWith("CIDFONT+", StringComparison.OrdinalIgnoreCase) ||
                            (sourceFont?.IsCid == true &&
                             (string.IsNullOrWhiteSpace(originalFontName) || originalFontName.Length <= 2));

                        // Do not silently assume Arial for an unidentified CID resource.
                        // The user must explicitly choose a replacement before editing it.
                        if (originalFontIsUnidentifiedCid)
                            originalFontName = null;
                        else if (string.IsNullOrWhiteSpace(originalFontName) || originalFontName.Length <= 2)
                            originalFontName = "Arial";
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

            if (originalFontIsUnidentifiedCid)
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
            else if (!string.IsNullOrWhiteSpace(originalFontName) &&
                     _fontSubstitutions.TryGetValue(originalFontName, out string? mappedFamily))
            {
                originalFontName = mappedFamily;
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
                Height = 220
            };

            if (!_pageSurfaces.TryGetValue(pageIndex, out var pageSurface) ||
                pageSurface.Image.Tag is not PagePreviewInfo inlinePageInfo)
            {
                if (_formattingToolbar != null && _defaultToolbarEditor != null)
                    _formattingToolbar.Target = _defaultToolbarEditor;
                return;
            }

            double editorWidth = Math.Max(280, Math.Min(560, pageSurface.Image.Bounds.Width - 20));
            var anchor = originalBounds != null
                ? PdfiumPageCoordinateMapper.PdfToViewRect(
                    originalBounds.LLX, originalBounds.LLY,
                    originalBounds.URX, originalBounds.URY,
                    pageSurface.Image.Bounds.Width, pageSurface.Image.Bounds.Height,
                    inlinePageInfo.WidthPoints, inlinePageInfo.HeightPoints)
                : PdfiumPageCoordinateMapper.PdfToViewRect(
                    x - 3, y - 3, x + 3, y + 3,
                    pageSurface.Image.Bounds.Width, pageSurface.Image.Bounds.Height,
                    inlinePageInfo.WidthPoints, inlinePageInfo.HeightPoints);

            double editorLeft = Math.Clamp(
                anchor.X, 8, Math.Max(8, pageSurface.Image.Bounds.Width - editorWidth - 8));
            double editorTop = anchor.Y + anchor.Height + 8;
            if (editorTop + 250 > pageSurface.Image.Bounds.Height)
                editorTop = Math.Max(8, anchor.Y - 250);

            double insertionPdfX = x;
            double insertionPdfY = y;
            var dragHandle = new TextBlock
            {
                Text = originalText == null ? "↕  Arraste para posicionar o texto" : "Editando texto selecionado",
                FontSize = 11,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(76, 52, 130)),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll)
            };

            var inlineEditor = new Border
            {
                Width = editorWidth,
                Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(255, 255, 255)),
                BorderBrush = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(76, 52, 130)),
                BorderThickness = new Thickness(2),
                CornerRadius = new Avalonia.CornerRadius(5),
                Padding = new Thickness(7),
                Child = new StackPanel
                {
                    Spacing = 7,
                    Children =
                    {
                        dragHandle,
                        editorSurface,
                        actionPanel
                    }
                }
            };

            bool draggingEditor = false;
            Avalonia.Point dragStart = default;
            double dragStartLeft = editorLeft;
            double dragStartTop = editorTop;
            dragHandle.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(dragHandle).Properties.IsLeftButtonPressed)
                    return;
                draggingEditor = true;
                dragStart = args.GetPosition(pageSurface.Overlay);
                dragStartLeft = editorLeft;
                dragStartTop = editorTop;
                args.Pointer.Capture(dragHandle);
                args.Handled = true;
            };
            dragHandle.PointerMoved += (_, args) =>
            {
                if (!draggingEditor)
                    return;
                var current = args.GetPosition(pageSurface.Overlay);
                editorLeft = Math.Clamp(
                    dragStartLeft + current.X - dragStart.X,
                    4, Math.Max(4, pageSurface.Image.Bounds.Width - editorWidth - 4));
                editorTop = Math.Clamp(
                    dragStartTop + current.Y - dragStart.Y,
                    4, Math.Max(4, pageSurface.Image.Bounds.Height - 300));
                Canvas.SetLeft(inlineEditor, editorLeft);
                Canvas.SetTop(inlineEditor, editorTop);
                args.Handled = true;
            };
            dragHandle.PointerReleased += (_, args) =>
            {
                if (!draggingEditor)
                    return;
                draggingEditor = false;
                args.Pointer.Capture(null);
                if (originalText == null)
                {
                    var insertionAnchor = new Avalonia.Point(
                        Math.Clamp(editorLeft + 12, 0, pageSurface.Image.Bounds.Width),
                        Math.Clamp(editorTop + 48, 0, pageSurface.Image.Bounds.Height));
                    PdfiumPageCoordinateMapper.TryViewToPdf(
                        insertionAnchor.X, insertionAnchor.Y,
                        pageSurface.Image.Bounds.Width, pageSurface.Image.Bounds.Height,
                        inlinePageInfo.WidthPoints, inlinePageInfo.HeightPoints,
                        out insertionPdfX, out insertionPdfY);
                }
                args.Handled = true;
            };

            Canvas.SetLeft(inlineEditor, editorLeft);
            Canvas.SetTop(inlineEditor, editorTop);
            pageSurface.Overlay.Children.Add(inlineEditor);
            pageSurface.Overlay.IsHitTestVisible = true;
            EditorStatusText.Text = "Edite o trecho diretamente na página. A barra superior permanece disponível para formatação.";

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            applyButton.Click += (_, _) => completion.TrySetResult(true);
            cancelButton.Click += (_, _) => completion.TrySetResult(false);
            richEditor.Focus();

            bool accepted = await completion.Task;

            pageSurface.Overlay.Children.Remove(inlineEditor);
            pageSurface.Overlay.IsHitTestVisible = false;
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

                        if (string.Equals(fragment.Text, originalText, StringComparison.Ordinal) &&
                            distance < closest)
                        {
                            closest = distance;
                            target = fragment;
                        }
                    }

                    if (target == null || closest > 8.0)
                        throw new InvalidOperationException("O texto selecionado não foi localizado novamente no PDF.");

                    // O TextFragment possui opções próprias para operações de
                    // substituição de texto/fonte. Configurá-las no fragmento antes
                    // de alterar o conteúdo permite que o Aspose trate a troca da
                    // fonte CID/Type0 no caminho de edição, em vez de apenas alterar
                    // o TextState em memória. RemoveUnusedFonts também limpa recursos
                    // que deixarem de ser referenciados após a substituição.
                    target.TextEditOptions = new TextEditOptions(
                        TextEditOptions.FontReplace.RemoveUnusedFonts);
                    target.Text = replacementText;
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

    private static Font ResolveEmbeddedTrueTypeFont(
        string requestedFamily,
        FontStyles requestedStyle,
        bool allowCidFallback,
        out string resolvedFamily)
    {
        // Arial é a primeira escolha para substituir estilos de fontes CID/Type0.
        // Em macOS, Arial pode não estar instalado; nesses casos tentamos famílias
        // TrueType/OpenType comuns, sem recorrer às fontes Standard-14 do PDF.
        string[] candidates = allowCidFallback
            ? new[] { requestedFamily, "Helvetica Neue", "Noto Sans", "Liberation Sans", "DejaVu Sans" }
            : new[] { requestedFamily };

        var failures = new List<string>();
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Font font = FontRepository.FindFont(candidate, requestedStyle);
                font.IsEmbedded = true;

                if (!font.IsEmbedded)
                {
                    string reason = font.GetLastFontEmbeddingError() ?? "a biblioteca não confirmou a incorporação";
                    throw new InvalidOperationException(reason);
                }

                resolvedFamily = NormalizePdfFontFamily(font.FontName);
                if (string.IsNullOrWhiteSpace(resolvedFamily))
                    resolvedFamily = candidate;

                return font;
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "Não foi possível localizar e incorporar uma fonte TrueType/OpenType compatível. " +
            string.Join(" | ", failures));
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
                bool savedBold = state.IsBold || PdfFontFaceHasStyle(state.Font, bold: true);
                bool savedItalic = state.IsItalic || PdfFontFaceHasStyle(state.Font, bold: false);
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
            if (!string.IsNullOrWhiteSpace(_historyEntryId))
                _history.Touch(_historyEntryId);
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