using Avalonia.Media;
using Aspose.Pdf;
using Aspose.Pdf.Text;
using AssinaPDF.MacPrototype;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfTextFormattingTests
{
    [Fact]
    public void ApplyRichTextFormatting_PersistsCombinedStyleAfterSaveAndReload()
    {
        using var original = new Document();
        var originalPage = original.Pages.Add();
        var originalFragment = new TextFragment("Diogo");
        originalFragment.TextState.FontName = "Helvetica";
        originalFragment.TextState.FontSize = 8.04f;
        new TextBuilder(originalPage).AppendText(originalFragment);

        using var initialPdf = new MemoryStream();
        original.Save(initialPdf);
        initialPdf.Position = 0;

        using var editable = Document.Open(initialPdf);
        var pageAbsorber = new TextFragmentAbsorber("Diogo");
        editable.Pages[1].Accept(pageAbsorber);
        var target = Assert.Single(pageAbsorber.TextFragments.Cast<TextFragment>());

        EditorWindow.ApplyRichTextFormatting(
            target,
            new[]
            {
                new EditorWindow.EditedTextRun(
                    0, 5, "Helvetica", 20, true, true,
                    Colors.Red, Colors.Yellow)
            },
            "Helvetica",
            originalFontEmbedded: false);

        var inMemoryCombinedSegments = target.Segments.Cast<TextSegment>().ToArray();
        Assert.NotEmpty(inMemoryCombinedSegments);
        foreach (var segment in inMemoryCombinedSegments)
        {
            string details = DescribeSegment(segment);
            Assert.True(Math.Abs(segment.TextState.FontSize - 20) <= 0.01,
                $"Antes de salvar, tamanho incorreto: {details}");
            Assert.True(segment.TextState.IsBold && segment.TextState.IsItalic,
                $"Antes de salvar, estilo incorreto: {details}");
            AssertColor(segment.TextState.ForegroundColor, 255, 0, 0, details);
            AssertColor(segment.TextState.BackgroundColor, 255, 255, 0, details);
        }
        AssertColor(target.TextState.BackgroundColor, 255, 255, 0,
            "Estado do fragmento antes de salvar: " + target.Text);

        using var output = new MemoryStream();
        editable.Save(output);
        output.Position = 0;

        using var reopened = Document.Open(output);
        var absorber = new TextFragmentAbsorber("Diogo");
        reopened.Pages[1].Accept(absorber);

        var savedFragment = Assert.Single(absorber.TextFragments.Cast<TextFragment>());
        var savedSegments = savedFragment.Segments.Cast<TextSegment>().ToArray();
        Assert.Equal("Diogo", string.Concat(savedSegments.Select(segment => segment.Text)));

        foreach (var segment in savedSegments)
        {
            var state = segment.TextState;
            Assert.InRange(Math.Abs(state.FontSize - 20), 0, 0.01);
            Assert.True(state.IsBold);
            Assert.True(state.IsItalic);
            AssertColor(state.ForegroundColor, 255, 0, 0, DescribeSegment(segment));
            AssertColor(state.BackgroundColor, 255, 255, 0, DescribeSegment(segment));
        }
    }


    [Fact]
    public void ApplyRichTextFormatting_PersistsStyleForNewlyAppendedSingleRun()
    {
        using var document = new Document();
        var page = document.Pages.Add();
        var target = new TextFragment("Diogo")
        {
            Position = new Position(50, 700)
        };
        target.TextState.FontName = "Helvetica";
        target.TextState.FontSize = 8.04f;
        new TextBuilder(page).AppendText(target);

        EditorWindow.ApplyRichTextFormatting(
            target,
            new[]
            {
                new EditorWindow.EditedTextRun(
                    0, 5, "Helvetica", 20, true, true,
                    Colors.Red, Colors.Yellow)
            },
            "Helvetica",
            originalFontEmbedded: false);

        var inMemorySingleSegments = target.Segments.Cast<TextSegment>().ToArray();
        Assert.NotEmpty(inMemorySingleSegments);
        foreach (var segment in inMemorySingleSegments)
        {
            string details = DescribeSegment(segment);
            Assert.True(Math.Abs(segment.TextState.FontSize - 20) <= 0.01,
                $"Antes de salvar, tamanho incorreto: {details}");
            Assert.True(segment.TextState.IsBold && segment.TextState.IsItalic,
                $"Antes de salvar, estilo incorreto: {details}");
            AssertColor(segment.TextState.ForegroundColor, 255, 0, 0, details);
            AssertColor(segment.TextState.BackgroundColor, 255, 255, 0, details);
        }
        AssertColor(target.TextState.BackgroundColor, 255, 255, 0,
            "Estado do fragmento antes de salvar: " + target.Text);

        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;

        using var reopened = Document.Open(output);
        var absorber = new TextFragmentAbsorber("Diogo");
        reopened.Pages[1].Accept(absorber);
        var savedFragment = Assert.Single(absorber.TextFragments.Cast<TextFragment>());
        var savedSegments = savedFragment.Segments.Cast<TextSegment>().ToArray();

        Assert.Equal("Diogo", string.Concat(savedSegments.Select(segment => segment.Text)));
        foreach (var segment in savedSegments)
        {
            Assert.InRange(Math.Abs(segment.TextState.FontSize - 20), 0, 0.01);
            Assert.True(segment.TextState.IsBold);
            Assert.True(segment.TextState.IsItalic);
            AssertColor(segment.TextState.ForegroundColor, 255, 0, 0, DescribeSegment(segment));
            AssertColor(segment.TextState.BackgroundColor, 255, 255, 0, DescribeSegment(segment));
        }
    }

    [Fact]
    public void ApplyRichTextFormatting_PreservesDifferentStylesAcrossAdjacentRuns()
    {
        const string text = "Diogo Gross";
        using var original = new Document();
        var originalPage = original.Pages.Add();
        var originalFragment = new TextFragment(text);
        originalFragment.TextState.FontName = "Helvetica";
        originalFragment.TextState.FontSize = 8.04f;
        new TextBuilder(originalPage).AppendText(originalFragment);

        using var initialPdf = new MemoryStream();
        original.Save(initialPdf);
        initialPdf.Position = 0;

        using var editable = Document.Open(initialPdf);
        var pageAbsorber = new TextFragmentAbsorber(text);
        editable.Pages[1].Accept(pageAbsorber);
        var target = Assert.Single(pageAbsorber.TextFragments.Cast<TextFragment>());

        EditorWindow.ApplyRichTextFormatting(
            target,
            new[]
            {
                new EditorWindow.EditedTextRun(
                    0, 5, "Helvetica", 20, true, false,
                    Colors.Red, Colors.Yellow),
                new EditorWindow.EditedTextRun(
                    5, 1, "Helvetica", 10, false, false,
                    Colors.Black, null),
                new EditorWindow.EditedTextRun(
                    6, 5, "Helvetica", 12, false, true,
                    Colors.Blue, null)
            },
            "Helvetica",
            originalFontEmbedded: false);

        var beforeSaveOffsets = new List<(int Start, int End, TextSegment Segment)>();
        int beforeSaveOffset = 0;
        foreach (TextSegment segment in target.Segments)
        {
            int end = beforeSaveOffset + (segment.Text?.Length ?? 0);
            beforeSaveOffsets.Add((beforeSaveOffset, end, segment));
            beforeSaveOffset = end;
        }
        Assert.Equal(text.Length, beforeSaveOffset);
        AssertRun(beforeSaveOffsets, 0, 5, 20, bold: true, italic: false);
        AssertRun(beforeSaveOffsets, 5, 6, 10, bold: false, italic: false);
        AssertRun(beforeSaveOffsets, 6, 11, 12, bold: false, italic: true);
        AssertRunColors(beforeSaveOffsets, 0, 5, Colors.Red, Colors.Yellow);
        AssertRunColors(beforeSaveOffsets, 5, 6, Colors.Black, null);
        AssertRunColors(beforeSaveOffsets, 6, 11, Colors.Blue, null);

        using var output = new MemoryStream();
        editable.Save(output);
        output.Position = 0;

        using var reopened = Document.Open(output);
        var absorber = new TextFragmentAbsorber(text);
        reopened.Pages[1].Accept(absorber);
        var savedFragment = Assert.Single(absorber.TextFragments.Cast<TextFragment>());

        var offsets = new List<(int Start, int End, TextSegment Segment)>();
        int offset = 0;
        foreach (TextSegment segment in savedFragment.Segments)
        {
            int end = offset + (segment.Text?.Length ?? 0);
            offsets.Add((offset, end, segment));
            offset = end;
        }

        Assert.Equal(text.Length, offset);
        Assert.All(offsets, item => Assert.NotNull(item.Segment.Position));
        AssertRun(offsets, 0, 5, 20, bold: true, italic: false);
        AssertRun(offsets, 5, 6, 10, bold: false, italic: false);
        AssertRun(offsets, 6, 11, 12, bold: false, italic: true);
        AssertRunColors(offsets, 0, 5, Colors.Red, Colors.Yellow);
        AssertRunColors(offsets, 5, 6, Colors.Black, null);
        AssertRunColors(offsets, 6, 11, Colors.Blue, null);
    }

    private static void AssertRunColors(
        IReadOnlyList<(int Start, int End, TextSegment Segment)> segments,
        int start,
        int end,
        Avalonia.Media.Color foreground,
        Avalonia.Media.Color? background)
    {
        var covered = segments
            .Where(item => item.Start < end && item.End > start)
            .ToArray();

        Assert.NotEmpty(covered);
        foreach (var item in covered)
        {
            AssertColor(item.Segment.TextState.ForegroundColor,
                foreground.R, foreground.G, foreground.B, DescribeSegment(item.Segment));
            if (background is { } expectedBackground)
            {
                AssertColor(item.Segment.TextState.BackgroundColor,
                    expectedBackground.R, expectedBackground.G, expectedBackground.B, DescribeSegment(item.Segment));
            }
            else
            {
                Assert.Null(item.Segment.TextState.BackgroundColor);
            }
        }
    }

    private static void AssertRun(
        IReadOnlyList<(int Start, int End, TextSegment Segment)> segments,
        int start,
        int end,
        double size,
        bool bold,
        bool italic)
    {
        var covered = segments
            .Where(item => item.Start < end && item.End > start)
            .ToArray();

        Assert.NotEmpty(covered);
        foreach (var item in covered)
        {
            Assert.True(item.Start >= start && item.End <= end);
            string details = DescribeSegment(item.Segment);
            Assert.True(Math.Abs(item.Segment.TextState.FontSize - size) <= 0.01,
                $"Tamanho esperado {size}; segmento [{item.Start},{item.End}): {details}");
            Assert.True(item.Segment.TextState.IsBold == bold,
                $"Negrito esperado {bold}; segmento [{item.Start},{item.End}): {details}");
            Assert.True(item.Segment.TextState.IsItalic == italic,
                $"Itálico esperado {italic}; segmento [{item.Start},{item.End}): {details}");
        }
    }

    private static void AssertColor(
        Aspose.Pdf.Color? actual,
        byte red,
        byte green,
        byte blue,
        string context = "")
    {
        Assert.True(actual is not null,
            $"Cor esperada RGB({red},{green},{blue}), mas veio null. {context}");
        Assert.InRange(Math.Abs(actual!.R - red), 0, 2);
        Assert.InRange(Math.Abs(actual.G - green), 0, 2);
        Assert.InRange(Math.Abs(actual.B - blue), 0, 2);
    }

    private static string DescribeSegment(TextSegment segment)
    {
        var state = segment.TextState;
        var position = segment.Position is { } p ? $"({p.XIndent:0.##},{p.YIndent:0.##})" : "null";
        return $"Texto='{segment.Text}', FontName='{state.FontName}', BaseFont='{state.Font?.BaseFont}', " +
               $"Size={state.FontSize:0.##}, Bold={state.IsBold}, Italic={state.IsItalic}, " +
               $"Foreground={state.ForegroundColor}, Background={state.BackgroundColor}, Position={position}";
    }
}
