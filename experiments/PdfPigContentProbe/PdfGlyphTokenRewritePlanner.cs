using System.Text;

internal sealed record PdfGlyphTokenRewritePlan(
    byte[] RewrittenToken,
    int DecodedByteOffset,
    int DecodedByteLength,
    string OriginalCodeHex,
    string ReplacementCodeHex,
    string OriginalUnicode,
    string ReplacementUnicode);

internal static class PdfGlyphTokenRewritePlanner
{
    public static bool TryPlanSingleScalarReplacement(
        ReadOnlySpan<byte> rawToken,
        CMapParseResult cmap,
        PdfGlyphByteSpan selectedCode,
        ReadOnlySpan<byte> replacementCode,
        string replacementUnicode,
        out PdfGlyphTokenRewritePlan plan,
        out string reason)
    {
        plan = null!;
        ArgumentNullException.ThrowIfNull(cmap);
        ArgumentNullException.ThrowIfNull(selectedCode);
        ArgumentNullException.ThrowIfNull(replacementUnicode);

        if (!selectedCode.IsSingleUnicodeScalar)
        {
            reason = "O código selecionado representa mais de um escalar Unicode; edição parcial recusada.";
            return false;
        }

        if (replacementUnicode.EnumerateRunes().Count() != 1)
        {
            reason = "A substituição deve representar exatamente um escalar Unicode.";
            return false;
        }

        if (replacementCode.Length != selectedCode.ByteLength ||
            selectedCode.ByteOffset < 0)
        {
            reason = "O código substituto precisa ter o mesmo comprimento em bytes do código selecionado.";
            return false;
        }

        if (!PdfStringOperandCodec.TryDecodeToken(rawToken, out var token, out reason))
            return false;

        if (selectedCode.ByteOffset + selectedCode.ByteLength > token.DecodedBytes.Length)
        {
            reason = "Intervalo do código selecionado excede o operando.";
            return false;
        }

        byte[] selectedBytes = token.DecodedBytes
            .AsSpan(selectedCode.ByteOffset, selectedCode.ByteLength).ToArray();
        if (!string.Equals(Convert.ToHexString(selectedBytes), selectedCode.CodeHex, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Bytes do token não correspondem ao código de origem selecionado.";
            return false;
        }

        if (!ToUnicodeCMapParser.TryDecodeBytes(replacementCode, cmap, out var replacementMappings) ||
            replacementMappings.Count != 1 ||
            !string.Equals(replacementMappings[0].Unicode, replacementUnicode, StringComparison.Ordinal))
        {
            reason = "Código substituto não possui mapeamento ToUnicode único para o caractere solicitado.";
            return false;
        }

        if (!PdfStringOperandCodec.TryReplaceBytesSameLength(
            rawToken, token, selectedCode.ByteOffset, replacementCode,
            out byte[] rewritten, out reason))
            return false;

        plan = new PdfGlyphTokenRewritePlan(
            rewritten,
            selectedCode.ByteOffset,
            selectedCode.ByteLength,
            selectedCode.CodeHex,
            Convert.ToHexString(replacementCode),
            selectedCode.Unicode,
            replacementUnicode);
        reason = "Plano em memória validado: código de mesmo comprimento, Unicode confirmado e bytes vizinhos preservados.";
        return true;
    }
}
