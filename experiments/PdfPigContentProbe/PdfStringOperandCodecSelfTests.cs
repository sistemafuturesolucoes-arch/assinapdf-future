internal static class PdfStringOperandCodecSelfTests
{
    public static int Run()
    {
        int passed = 0, failed = 0;

        Check("decodes literal escapes and tracks raw byte origins", () =>
        {
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(A\\050B\\051\\101\\nC)");
            return PdfStringOperandCodec.TryDecodeToken(raw, out var decoded, out _) &&
                   System.Text.Encoding.Latin1.GetString(decoded.DecodedBytes) == "A(B)A\nC" &&
                   decoded.Origins.Count == decoded.DecodedBytes.Length;
        });

        Check("decodes nested literal parentheses", () =>
        {
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(a(b)c)");
            return PdfStringOperandCodec.TryDecodeToken(raw, out var decoded, out _) &&
                   System.Text.Encoding.ASCII.GetString(decoded.DecodedBytes) == "a(b)c";
        });

        Check("decodes hex strings with whitespace and odd nibble", () =>
        {
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("<41 4\n2 F>");
            return PdfStringOperandCodec.TryDecodeToken(raw, out var decoded, out _) &&
                   Convert.ToHexString(decoded.DecodedBytes) == "4142F0";
        });

        Check("replaces only selected literal bytes and preserves neighbors", () =>
        {
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(A\\101B)");
            PdfStringOperandCodec.TryDecodeToken(raw, out var decoded, out _);
            return PdfStringOperandCodec.TryReplaceBytesSameLength(
                       raw, decoded, 1, new byte[] { 0x42 }, out var rewritten, out _) &&
                   PdfStringOperandCodec.TryDecodeToken(rewritten, out var after, out _) &&
                   Convert.ToHexString(after.DecodedBytes) == "414242" &&
                   System.Text.Encoding.ASCII.GetString(rewritten).StartsWith("(A", StringComparison.Ordinal) &&
                   System.Text.Encoding.ASCII.GetString(rewritten).EndsWith("B)", StringComparison.Ordinal);
        });

        Check("replaces selected hex bytes and preserves surrounding token", () =>
        {
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("<41 42 43>");
            PdfStringOperandCodec.TryDecodeToken(raw, out var decoded, out _);
            return PdfStringOperandCodec.TryReplaceBytesSameLength(
                       raw, decoded, 1, new byte[] { 0x58 }, out var rewritten, out _) &&
                   PdfStringOperandCodec.TryDecodeToken(rewritten, out var after, out _) &&
                   Convert.ToHexString(after.DecodedBytes) == "415843" &&
                   System.Text.Encoding.ASCII.GetString(rewritten).StartsWith("<41 ", StringComparison.Ordinal) &&
                   System.Text.Encoding.ASCII.GetString(rewritten).EndsWith(" 43>", StringComparison.Ordinal);
        });

        Check("rejects replacement that changes decoded byte count", () =>
        {
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(AB)");
            PdfStringOperandCodec.TryDecodeToken(raw, out var decoded, out _);
            return !PdfStringOperandCodec.TryReplaceBytesSameLength(
                raw, decoded, 0, new byte[] { 1, 2, 3 }, out _, out _);
        });

        Check("rejects malformed literal and hex tokens", () =>
        {
            return !PdfStringOperandCodec.TryDecodeToken(
                       System.Text.Encoding.ASCII.GetBytes("(unterminated"), out _, out _) &&
                   !PdfStringOperandCodec.TryDecodeToken(
                       System.Text.Encoding.ASCII.GetBytes("<41XZ>"), out _, out _);
        });

        Check("rejects a replacement that leaves selected bytes unchanged", () =>
        {
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(AB)");
            PdfStringOperandCodec.TryDecodeToken(raw, out var decoded, out _);
            return !PdfStringOperandCodec.TryReplaceBytesSameLength(
                raw, decoded, 0, new byte[] { 0x41 }, out _, out _);
        });

        Check("normalizes literal CR and CRLF to LF", () =>
        {
            byte[] raw = new byte[] { 40, 65, 13, 66, 13, 10, 67, 41 };
            return PdfStringOperandCodec.TryDecodeToken(raw, out var decoded, out _) &&
                   Convert.ToHexString(decoded.DecodedBytes) == "410A420A43" &&
                   decoded.Origins[1].RawEnd - decoded.Origins[1].RawStart == 1 &&
                   decoded.Origins[3].RawEnd - decoded.Origins[3].RawStart == 2;
        });

        Check("ignores escaped line continuation in literal strings", () =>
        {
            byte[] raw = new byte[] { 40, 65, 92, 13, 10, 66, 41 };
            return PdfStringOperandCodec.TryDecodeToken(raw, out var decoded, out _) &&
                   Convert.ToHexString(decoded.DecodedBytes) == "4142";
        });

        Check("rejects a byte-origin map from a different raw token", () =>
        {
            byte[] source = System.Text.Encoding.ASCII.GetBytes("(AB)");
            byte[] other = System.Text.Encoding.ASCII.GetBytes("(CD)");
            PdfStringOperandCodec.TryDecodeToken(source, out var decoded, out _);
            return !PdfStringOperandCodec.TryReplaceBytesSameLength(
                other, decoded, 0, new byte[] { 0x58 }, out _, out _);
        });

        Console.WriteLine($"PDF raw-string codec self-tests: {passed}/{passed + failed} passed.");
        return failed;

        void Check(string name, Func<bool> test)
        {
            try
            {
                if (test()) { passed++; Console.WriteLine($"  PASS: {name}"); }
                else { failed++; Console.WriteLine($"  FAIL: {name}"); }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL: {name} ({ex.GetType().Name}: {ex.Message})");
            }
        }
    }
}
