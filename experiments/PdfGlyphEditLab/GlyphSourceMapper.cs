namespace PdfGlyphEditLab;

/// <summary>
/// Explicit provenance for one rendered glyph. Byte offsets refer to the encoded string
/// operand, not Unicode character offsets. The producer must obtain these values from a
/// PDF content interpreter; they must never be guessed from visual order.
/// </summary>
public sealed record GlyphSourceSpan(
    int PageIndex,
    string ContentStreamId,
    int OperatorIndex,
    int StringOperandIndex,
    int ByteOffset,
    int ByteLength,
    string FontResourceName)
{
    public int ByteEnd => checked(ByteOffset + ByteLength);

    public bool IsValid =>
        PageIndex >= 0 &&
        !string.IsNullOrWhiteSpace(ContentStreamId) &&
        OperatorIndex >= 0 &&
        StringOperandIndex >= 0 &&
        ByteOffset >= 0 &&
        ByteLength > 0 &&
        !string.IsNullOrWhiteSpace(FontResourceName);
}

/// <summary>A selected visual glyph paired with its exact source-operand byte span.</summary>
public sealed record MappedGlyph(GlyphBox Glyph, GlyphSourceSpan Source);

/// <summary>A contiguous byte range within one PDF text-showing string operand.</summary>
public sealed record OperandEditSpan(
    int PageIndex,
    string ContentStreamId,
    int OperatorIndex,
    int StringOperandIndex,
    int ByteOffset,
    int ByteLength,
    string FontResourceName);

public static class GlyphSourceMapper
{
    /// <summary>
    /// Maps selected PDFium character indices to interpreter-provided source spans.
    /// Fails closed if any glyph is unmapped, ambiguous, or has invalid provenance.
    /// </summary>
    public static IReadOnlyList<OperandEditSpan> BuildEditSpans(
        IEnumerable<GlyphBox> selectedGlyphs,
        IEnumerable<MappedGlyph> sourceMap)
    {
        ArgumentNullException.ThrowIfNull(selectedGlyphs);
        ArgumentNullException.ThrowIfNull(sourceMap);

        var selected = selectedGlyphs.ToArray();
        var map = sourceMap.GroupBy(x => x.Glyph.CharacterIndex).ToArray();
        if (map.Any(g => g.Count() != 1))
            throw new InvalidOperationException("A glyph has ambiguous source mappings.");

        var byIndex = map.ToDictionary(g => g.Key, g => g.Single());
        var mapped = new List<MappedGlyph>(selected.Length);
        foreach (var glyph in selected)
        {
            if (!byIndex.TryGetValue(glyph.CharacterIndex, out var item))
                throw new InvalidOperationException(
                    $"Glyph {glyph.CharacterIndex} has no verified PDF source mapping.");
            if (!item.Source.IsValid)
                throw new InvalidOperationException(
                    $"Glyph {glyph.CharacterIndex} has invalid PDF source coordinates.");
            mapped.Add(item);
        }

        var ordered = mapped.OrderBy(x => x.Source.PageIndex)
            .ThenBy(x => x.Source.ContentStreamId, StringComparer.Ordinal)
            .ThenBy(x => x.Source.OperatorIndex)
            .ThenBy(x => x.Source.StringOperandIndex)
            .ThenBy(x => x.Source.ByteOffset)
            .ToArray();

        var result = new List<OperandEditSpan>();
        foreach (var item in ordered)
        {
            var s = item.Source;
            if (result.Count > 0)
            {
                var last = result[^1];
                bool sameOperand = last.PageIndex == s.PageIndex &&
                    last.ContentStreamId == s.ContentStreamId &&
                    last.OperatorIndex == s.OperatorIndex &&
                    last.StringOperandIndex == s.StringOperandIndex &&
                    last.FontResourceName == s.FontResourceName;
                int lastEnd = checked(last.ByteOffset + last.ByteLength);
                if (sameOperand && s.ByteOffset < lastEnd)
                    throw new InvalidOperationException("Overlapping glyph source byte spans.");
                if (sameOperand && s.ByteOffset == lastEnd)
                {
                    result[^1] = last with { ByteLength = checked(last.ByteLength + s.ByteLength) };
                    continue;
                }
            }

            result.Add(new OperandEditSpan(s.PageIndex, s.ContentStreamId,
                s.OperatorIndex, s.StringOperandIndex, s.ByteOffset, s.ByteLength,
                s.FontResourceName));
        }

        return result;
    }
}
