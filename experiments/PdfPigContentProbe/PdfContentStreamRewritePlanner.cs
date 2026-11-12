internal sealed record PdfContentStreamRewritePlan(
    byte[] RewrittenStream,
    int OriginalTokenOffset,
    int OriginalTokenLength,
    int RewrittenTokenLength,
    int OperatorOffset,
    string OriginalDecodedHex,
    string ReplacementDecodedHex);

internal static class PdfContentStreamRewritePlanner
{
    public static bool TryPlanSingleTjReplacement(
        ReadOnlySpan<byte> decodedContentStream,
        int selectedTokenOffset,
        int selectedByteOffset,
        ReadOnlySpan<byte> expectedOriginalBytes,
        ReadOnlySpan<byte> replacementBytes,
        out PdfContentStreamRewritePlan plan,
        out string reason)
    {
        plan = null!;
        if (expectedOriginalBytes.Length == 0 || replacementBytes.Length == 0 ||
            expectedOriginalBytes.Length != replacementBytes.Length)
        {
            reason = "A substituição deve ter comprimento não vazio e manter a quantidade de bytes do código PDF.";
            return false;
        }

        PdfContentScanResult scan = PdfContentStreamTokenScanner.Scan(decodedContentStream);
        if (!scan.IsComplete)
        {
            reason = $"Stream não pôde ser validado integralmente: {scan.Reason}";
            return false;
        }

        var matches = scan.TjOperands
            .Where(x => x.TokenStart == selectedTokenOffset)
            .ToArray();
        if (matches.Length != 1)
        {
            reason = $"Offset selecionado não identifica exatamente um operando string Tj (correspondências: {matches.Length}).";
            return false;
        }

        PdfContentStringOperand selected = matches[0];
        if (!selected.DecodedBytes.AsSpan().SequenceEqual(expectedOriginalBytes))
        {
            reason = "Bytes decodificados no offset não correspondem ao código original selecionado.";
            return false;
        }

        byte[] rawToken = decodedContentStream[selected.TokenStart..selected.TokenEnd].ToArray();
        if (!PdfStringOperandCodec.TryDecodeToken(rawToken, out var token, out reason))
            return false;
        if (selectedByteOffset < 0 ||
            selectedByteOffset + expectedOriginalBytes.Length > token.DecodedBytes.Length ||
            !token.DecodedBytes.AsSpan(selectedByteOffset, expectedOriginalBytes.Length)
                .SequenceEqual(expectedOriginalBytes))
        {
            reason = "Intervalo selecionado no operando não corresponde aos bytes originais esperados.";
            return false;
        }

        if (!PdfStringOperandCodec.TryReplaceBytesSameLength(
            rawToken, token, selectedByteOffset, replacementBytes,
            out byte[] rewrittenToken, out reason))
            return false;

        byte[] rewrittenStream = new byte[
            decodedContentStream.Length - (selected.TokenEnd - selected.TokenStart) + rewrittenToken.Length];
        decodedContentStream[..selected.TokenStart].CopyTo(rewrittenStream);
        rewrittenToken.CopyTo(rewrittenStream.AsSpan(selected.TokenStart));
        decodedContentStream[selected.TokenEnd..].CopyTo(
            rewrittenStream.AsSpan(selected.TokenStart + rewrittenToken.Length));

        int suffixStart = selected.TokenStart + rewrittenToken.Length;
        if (!rewrittenStream.AsSpan(0, selected.TokenStart)
                .SequenceEqual(decodedContentStream[..selected.TokenStart]) ||
            !rewrittenStream.AsSpan(suffixStart)
                .SequenceEqual(decodedContentStream[selected.TokenEnd..]))
        {
            reason = "A reconstrução alterou bytes fora do token selecionado.";
            return false;
        }

        PdfContentScanResult after = PdfContentStreamTokenScanner.Scan(rewrittenStream);
        if (!after.IsComplete)
        {
            reason = $"Stream reescrito não passou na varredura completa: {after.Reason}";
            return false;
        }

        var rewrittenMatch = after.TjOperands
            .Where(x => x.TokenStart == selected.TokenStart)
            .ToArray();
        byte[] expectedWholeOperand = token.DecodedBytes.ToArray();
        replacementBytes.CopyTo(expectedWholeOperand.AsSpan(selectedByteOffset));
        if (rewrittenMatch.Length != 1 ||
            !rewrittenMatch[0].DecodedBytes.AsSpan().SequenceEqual(expectedWholeOperand))
        {
            reason = "O operando Tj reescrito não foi recuperado exatamente na validação de ida e volta.";
            return false;
        }

        if (after.TjOperands.Count != scan.TjOperands.Count)
        {
            reason = "A quantidade de operandos Tj mudou após a reescrita.";
            return false;
        }

        plan = new PdfContentStreamRewritePlan(
            rewrittenStream,
            selected.TokenStart,
            selected.TokenEnd - selected.TokenStart,
            rewrittenToken.Length,
            selected.OperatorStart,
            Convert.ToHexString(expectedOriginalBytes),
            Convert.ToHexString(replacementBytes));
        reason = "Plano de stream decodificado validado: somente o token selecionado mudou; prefixo, sufixo e contagem Tj preservados.";
        return true;
    }
}
