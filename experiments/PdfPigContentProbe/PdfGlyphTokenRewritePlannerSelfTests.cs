internal static class PdfGlyphTokenRewritePlannerSelfTests
{
    public static int Run()
    {
        int passed = 0, failed = 0;

        Check("plans one-byte literal replacement and preserves neighboring bytes", () =>
        {
            var cmap = MakeCMap();
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(ABC)");
            var selected = new PdfGlyphByteSpan(1, 1, "42", "B");
            return PdfGlyphTokenRewritePlanner.TryPlanSingleScalarReplacement(
                       raw, cmap, selected, new byte[] { 0x44 }, "D",
                       out var plan, out _) &&
                   plan.OriginalUnicode == "B" && plan.ReplacementUnicode == "D" &&
                   PdfStringOperandCodec.TryDecodeToken(plan.RewrittenToken, out var decoded, out _) &&
                   Convert.ToHexString(decoded.DecodedBytes) == "414443";
        });

        Check("plans two-byte hexadecimal code replacement", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <8100> <81FF> endcodespacerange " +
                "2 beginbfchar <8101> <0041> <8102> <0042> endbfchar");
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("<81018101>");
            var selected = new PdfGlyphByteSpan(2, 2, "8101", "A");
            return PdfGlyphTokenRewritePlanner.TryPlanSingleScalarReplacement(
                       raw, cmap, selected, new byte[] { 0x81, 0x02 }, "B",
                       out var plan, out _) &&
                   PdfStringOperandCodec.TryDecodeToken(plan.RewrittenToken, out var decoded, out _) &&
                   Convert.ToHexString(decoded.DecodedBytes) == "81018102";
        });

        Check("rejects partial edit of multi-scalar source code", () =>
        {
            var cmap = MakeCMap();
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(A)");
            var selected = new PdfGlyphByteSpan(0, 1, "41", "AB");
            return !PdfGlyphTokenRewritePlanner.TryPlanSingleScalarReplacement(
                raw, cmap, selected, new byte[] { 0x44 }, "D", out _, out _);
        });

        Check("rejects replacement code without exact ToUnicode mapping", () =>
        {
            var cmap = MakeCMap();
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(A)");
            var selected = new PdfGlyphByteSpan(0, 1, "41", "A");
            return !PdfGlyphTokenRewritePlanner.TryPlanSingleScalarReplacement(
                raw, cmap, selected, new byte[] { 0x45 }, "E", out _, out _);
        });

        Check("rejects replacement code with different byte length", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "2 begincodespacerange <00> <7F> <8100> <81FF> endcodespacerange " +
                "2 beginbfchar <41> <0041> <8102> <0042> endbfchar");
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(A)");
            var selected = new PdfGlyphByteSpan(0, 1, "41", "A");
            return !PdfGlyphTokenRewritePlanner.TryPlanSingleScalarReplacement(
                raw, cmap, selected, new byte[] { 0x81, 0x02 }, "B", out _, out _);
        });

        Check("rejects token bytes that do not match selected source code", () =>
        {
            var cmap = MakeCMap();
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(C)");
            var selected = new PdfGlyphByteSpan(0, 1, "41", "A");
            return !PdfGlyphTokenRewritePlanner.TryPlanSingleScalarReplacement(
                raw, cmap, selected, new byte[] { 0x44 }, "D", out _, out _);
        });

        Check("rejects replacement Unicode containing multiple scalars", () =>
        {
            var cmap = MakeCMap();
            byte[] raw = System.Text.Encoding.ASCII.GetBytes("(A)");
            var selected = new PdfGlyphByteSpan(0, 1, "41", "A");
            return !PdfGlyphTokenRewritePlanner.TryPlanSingleScalarReplacement(
                raw, cmap, selected, new byte[] { 0x44 }, "DE", out _, out _);
        });

        Console.WriteLine($"PDF glyph token rewrite planner self-tests: {passed}/{passed + failed} passed.");
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

    private static CMapParseResult MakeCMap() =>
        ToUnicodeCMapParser.Parse(
            "1 begincodespacerange <00> <7F> endcodespacerange " +
            "4 beginbfchar <41> <0041> <42> <0042> <43> <0043> <44> <0044> endbfchar");
}
