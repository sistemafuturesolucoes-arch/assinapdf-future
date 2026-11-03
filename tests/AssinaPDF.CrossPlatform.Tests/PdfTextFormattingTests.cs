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
        using var document = new Document();
        var page = document.Pages.Add();
        var fragment = new TextFragment("Diogo")
        {
            TextState =
            {
                FontName = "Helvetica",
                FontSize = 8.04f
            }
        };

        EditorWindow.ApplyRichTextFormatting(
            fragment,
            new[]
            {
                new EditorWindow.EditedTextRun(
                    0, 5, "Helvetica", 20, true, true,
                    Colors.Red, Colors.Yellow)
            },
            "Helvetica",
            originalFontEmbedded: false);

        new TextBuilder(page).AppendText(fragment);

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
            var state = segment.TextState;
            Assert.InRange(Math.Abs(state.FontSize - 20), 0, 0.01);
            Assert.True(state.IsBold);
            Assert.True(state.IsItalic);
            Assert.Equal(Aspose.Pdf.Color.FromRgb(1, 0, 0).R, state.ForegroundColor!.R);
            Assert.Equal(Aspose.Pdf.Color.FromRgb(1, 0, 0).G, state.ForegroundColor.G);
            Assert.Equal(Aspose.Pdf.Color.FromRgb(1, 0, 0).B, state.ForegroundColor.B);
            Assert.Equal(Aspose.Pdf.Color.FromRgb(1, 1, 0).R, state.BackgroundColor!.R);
            Assert.Equal(Aspose.Pdf.Color.FromRgb(1, 1, 0).G, state.BackgroundColor.G);
            Assert.Equal(Aspose.Pdf.Color.FromRgb(1, 1, 0).B, state.BackgroundColor.B);
        }
    }

    [Fact]
    public void ApplyRichTextFormatting_PreservesDifferentStylesAcrossAdjacentRuns()
    {
        const string text = "Diogo Gross";
        using var document = new Document();
        var page = document.Pages.Add();
        var fragment = new TextFragment(text)
        {
            TextState =
            {
                FontName = "Helvetica",
                FontSize = 8.04f
            }
        };

        EditorWindow.ApplyRichTextFormatting(
            fragment,
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

        new TextBuilder(page).AppendText(fragment);

        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;

        using var reopened = Document.Open(output);
        var absorber = new TextFragmentAbsorber();
        reopened.Pages[1].Accept(absorber);
        var savedFragment = Assert.Single(
            absorber.TextFragments.Cast<TextFragment>()
                .Where(item => item.Text == text));

        var offsets = new List<(int Start, int End, TextSegment Segment)>();
        int offset = 0;
        foreach (TextSegment segment in savedFragment.Segments)
        {
            int end = offset + (segment.Text?.Length ?? 0);
            offsets.Add((offset, end, segment));
            offset = end;
        }

        Assert.Equal(text.Length, offset);
        AssertRun(offsets, 0, 5, 20, bold: true, italic: false);
        AssertRun(offsets, 5, 6, 10, bold: false, italic: false);
        AssertRun(offsets, 6, 11, 12, bold: false, italic: true);
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
            Assert.InRange(Math.Abs(item.Segment.TextState.FontSize - size), 0, 0.01);
            Assert.Equal(bold, item.Segment.TextState.IsBold);
            Assert.Equal(italic, item.Segment.TextState.IsItalic);
        }
    }
}
