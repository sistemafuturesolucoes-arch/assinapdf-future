internal static class PdfGlyphByteMapperSelfTests
{
    public static int Run()
    {
        int passed = 0, failed = 0;

        Check("maps each source code to its exact byte interval", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <00> <7F> endcodespacerange " +
                "2 beginbfchar <41> <0041> <42> <0042> endbfchar");
            return PdfGlyphByteMapper.TryMapOperand(
                       new byte[] { 0x41, 0x42 }, cmap, "AB", out var spans, out _) &&
                   spans.Count == 2 &&
                   spans[0] == new PdfGlyphByteSpan(0, 1, "41", "A") &&
                   spans[1] == new PdfGlyphByteSpan(1, 1, "42", "B");
        });

        Check("maps mixed one-byte and two-byte source codes", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "2 begincodespacerange <00> <7F> <8100> <81FF> endcodespacerange " +
                "2 beginbfchar <41> <0041> <8102> <00E9> endbfchar");
            return PdfGlyphByteMapper.TryMapOperand(
                       new byte[] { 0x41, 0x81, 0x02 }, cmap, "Aé", out var spans, out _) &&
                   spans.Count == 2 &&
                   spans[0].ByteOffset == 0 && spans[0].ByteLength == 1 &&
                   spans[1].ByteOffset == 1 && spans[1].ByteLength == 2;
        });

        Check("rejects a mismatch with PdfPig extracted Unicode", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <00> <7F> endcodespacerange " +
                "1 beginbfchar <41> <0041> endbfchar");
            return !PdfGlyphByteMapper.TryMapOperand(
                new byte[] { 0x41 }, cmap, "B", out var spans, out _) &&
                spans.Count == 0;
        });

        Check("rejects bytes without a unique ToUnicode mapping", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <00> <7F> endcodespacerange " +
                "1 beginbfchar <41> <0041> endbfchar");
            return !PdfGlyphByteMapper.TryMapOperand(
                new byte[] { 0x42 }, cmap, "B", out _, out _);
        });

        Check("keeps multi-character Unicode mapping as one PDF code span", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <2100> <21FF> endcodespacerange " +
                "1 beginbfchar <21AF> <004E0049> endbfchar");
            return PdfGlyphByteMapper.TryMapOperand(
                       new byte[] { 0x21, 0xAF }, cmap, "NI", out var spans, out _) &&
                   spans.Count == 1 &&
                   spans[0].ByteOffset == 0 && spans[0].ByteLength == 2 &&
                   spans[0].Unicode == "NI" && spans[0].UnicodeScalarCount == 2 &&
                   !spans[0].IsSingleUnicodeScalar;
        });

        Check("accepts an empty operand only for empty extracted text", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <00> <7F> endcodespacerange");
            return PdfGlyphByteMapper.TryMapOperand(
                       ReadOnlySpan<byte>.Empty, cmap, "", out var spans, out _) &&
                   spans.Count == 0 &&
                   !PdfGlyphByteMapper.TryMapOperand(
                       ReadOnlySpan<byte>.Empty, cmap, "A", out _, out _);
        });

        Console.WriteLine($"PDF glyph byte-mapper self-tests: {passed}/{passed + failed} passed.");
        return failed;

        void Check(string name, Func<bool> test)
        {
            try
            {
                if (test())
                {
                    passed++;
                    Console.WriteLine($"  PASS: {name}");
                }
                else
                {
                    failed++;
                    Console.WriteLine($"  FAIL: {name}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL: {name} ({ex.GetType().Name}: {ex.Message})");
            }
        }
    }
}
