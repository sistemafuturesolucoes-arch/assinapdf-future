using System.Text;

internal sealed record PdfGlyphByteSpan(
    int ByteOffset,
    int ByteLength,
    string CodeHex,
    string Unicode)
{
    public int UnicodeScalarCount => Unicode.EnumerateRunes().Count();
    public bool IsSingleUnicodeScalar => UnicodeScalarCount == 1;
}

internal static class PdfGlyphByteMapper
{
    public static bool TryMapOperand(
        ReadOnlySpan<byte> operandBytes,
        CMapParseResult cmap,
        string expectedUnicode,
        out IReadOnlyList<PdfGlyphByteSpan> spans,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(cmap);
        ArgumentNullException.ThrowIfNull(expectedUnicode);

        if (!ToUnicodeCMapParser.TryDecodeBytes(operandBytes, cmap, out var decoded))
        {
            spans = Array.Empty<PdfGlyphByteSpan>();
            reason = "Bytes não puderam ser divididos em códigos únicos com mapeamento ToUnicode.";
            return false;
        }

        var mapped = new List<PdfGlyphByteSpan>(decoded.Count);
        var reconstructed = new StringBuilder();
        int byteOffset = 0;

        foreach (var item in decoded)
        {
            int byteLength = item.CodeHex.Length / 2;
            if (byteLength <= 0 || byteOffset + byteLength > operandBytes.Length)
            {
                spans = Array.Empty<PdfGlyphByteSpan>();
                reason = "Intervalo de bytes inválido durante a reconstrução.";
                return false;
            }

            mapped.Add(new PdfGlyphByteSpan(
                byteOffset, byteLength, item.CodeHex, item.Unicode));
            reconstructed.Append(item.Unicode);
            byteOffset += byteLength;
        }

        if (byteOffset != operandBytes.Length ||
            !string.Equals(reconstructed.ToString(), expectedUnicode, StringComparison.Ordinal))
        {
            spans = Array.Empty<PdfGlyphByteSpan>();
            reason = "Unicode reconstruído pelo CMap não corresponde exatamente ao texto extraído pelo PdfPig.";
            return false;
        }

        spans = mapped;
        reason = "Mapeamento exato por código PDF; a associação geométrica a cada Letter ainda deve ser validada separadamente.";
        return true;
    }
}
