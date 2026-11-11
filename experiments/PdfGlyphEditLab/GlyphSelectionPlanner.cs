namespace PdfGlyphEditLab;

/// <summary>
/// Geometry-only experiment. It deliberately does not modify PDF content streams.
/// CharacterIndex is the stable PDFium character index within one page.
/// </summary>
public sealed record GlyphBox(
    int CharacterIndex,
    string Text,
    double Left,
    double Bottom,
    double Right,
    double Top);

public readonly record struct SelectionRect(
    double Left,
    double Bottom,
    double Right,
    double Top);

public static class GlyphSelectionPlanner
{
    /// <summary>
    /// Selects glyphs by intersection-over-glyph-area. A 50% threshold avoids
    /// selecting adjacent glyphs merely because the drag rectangle touches an edge.
    /// Coordinates are PDF points, origin at the lower-left.
    /// </summary>
    public static IReadOnlyList<GlyphBox> Select(
        IEnumerable<GlyphBox> glyphs,
        SelectionRect selection,
        double minimumGlyphOverlap = 0.50)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        if (!double.IsFinite(minimumGlyphOverlap) ||
            minimumGlyphOverlap <= 0 || minimumGlyphOverlap > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumGlyphOverlap));

        if (!double.IsFinite(selection.Left) || !double.IsFinite(selection.Bottom) ||
            !double.IsFinite(selection.Right) || !double.IsFinite(selection.Top) ||
            selection.Right <= selection.Left || selection.Top <= selection.Bottom)
            return Array.Empty<GlyphBox>();

        return glyphs
            .Where(IsValid)
            .Select(glyph => new
            {
                Glyph = glyph,
                OverlapRatio = GetOverlapRatio(glyph, selection)
            })
            .Where(item => item.OverlapRatio >= minimumGlyphOverlap)
            .OrderBy(item => item.Glyph.CharacterIndex)
            .Select(item => item.Glyph)
            .ToArray();
    }

    private static bool IsValid(GlyphBox glyph) =>
        !string.IsNullOrEmpty(glyph.Text) &&
        double.IsFinite(glyph.Left) && double.IsFinite(glyph.Bottom) &&
        double.IsFinite(glyph.Right) && double.IsFinite(glyph.Top) &&
        glyph.Right > glyph.Left && glyph.Top > glyph.Bottom;

    private static double GetOverlapRatio(GlyphBox glyph, SelectionRect selection)
    {
        double width = Math.Max(0, Math.Min(glyph.Right, selection.Right) -
                                   Math.Max(glyph.Left, selection.Left));
        double height = Math.Max(0, Math.Min(glyph.Top, selection.Top) -
                                    Math.Max(glyph.Bottom, selection.Bottom));
        double glyphArea = (glyph.Right - glyph.Left) * (glyph.Top - glyph.Bottom);
        return glyphArea <= 0 ? 0 : (width * height) / glyphArea;
    }
}
