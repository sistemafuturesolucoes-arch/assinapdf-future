internal static class ToUnicodeCMapParserSelfTests
{
    public static int Run()
    {
        int passed = 0, failed = 0;

        Check("multiple bfchar pairs on one line", () =>
        {
            var result = ToUnicodeCMapParser.Parse("2 beginbfchar <01> <0041> <02> <0042> endbfchar");
            return result.Mappings.Count == 2 &&
                   result.Mappings["01"] == "A" && result.Mappings["02"] == "B" &&
                   result.MalformedEntries == 0;
        });

        Check("comments and arbitrary whitespace", () =>
        {
            var result = ToUnicodeCMapParser.Parse("% header\n2 beginbfchar\n<0001>\t<0061> % comment\n<0002> <0062>\nendbfchar");
            return result.Mappings.Count == 2 &&
                   result.Mappings["0001"] == "a" && result.Mappings["0002"] == "b";
        });

        Check("sequential bfrange preserves UTF-16 width", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfrange <0001> <0002> <0041> endbfrange");
            return result.Mappings.Count == 2 &&
                   result.Mappings["0001"] == "A" && result.Mappings["0002"] == "B";
        });

        Check("bfrange destination array across lines", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfrange\n<01> <03> [\n<0041>\n<0042> <0043>\n]\nendbfrange");
            return result.Mappings.Count == 3 &&
                   result.Mappings["01"] == "A" &&
                   result.Mappings["02"] == "B" &&
                   result.Mappings["03"] == "C";
        });

        Check("code spaces split mixed one-byte and two-byte codes", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "2 begincodespacerange <00> <7F> <8100> <81FF> endcodespacerange " +
                "2 beginbfchar <41> <0041> <8101> <0042> endbfchar");
            return ToUnicodeCMapParser.TryDecodeBytes(
                       new byte[] { 0x41, 0x81, 0x01 }, cmap, out var decoded) &&
                   decoded.Count == 2 &&
                   decoded[0] == new CMapDecodedCode("41", "A") &&
                   decoded[1] == new CMapDecodedCode("8101", "B");
        });

        Check("byte decoding fails closed when code has no Unicode mapping", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <00> <7F> endcodespacerange " +
                "1 beginbfchar <41> <0041> endbfchar");
            return !ToUnicodeCMapParser.TryDecodeBytes(new byte[] { 0x42 }, cmap, out _);
        });

        Check("overlapping code-space lengths are treated as ambiguous", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "2 begincodespacerange <00> <FF> <0000> <00FF> endcodespacerange " +
                "2 beginbfchar <00> <0041> <0001> <0042> endbfchar");
            return !ToUnicodeCMapParser.TryDecodeBytes(new byte[] { 0x00, 0x01 }, cmap, out _);
        });

        Check("high-bit source codes remain unsigned and fixed-width", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfrange <80> <81> <0041> endbfrange");
            return result.Mappings.Count == 2 &&
                   result.Mappings["80"] == "A" && result.Mappings["81"] == "B";
        });

        Check("source leading zeros are preserved", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfchar <000A> <0058> endbfchar");
            return result.Mappings.ContainsKey("000A");
        });

        Check("one source code may map to multiple Unicode characters", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfchar <21AF> <004E0049> endbfchar");
            return result.Mappings.TryGetValue("21AF", out string? value) && value == "NI";
        });

        Check("supplementary Unicode surrogate pair", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfchar <01> <D83DDE00> endbfchar");
            return result.Mappings.TryGetValue("01", out string? value) && value == "😀";
        });

        Check("unpaired UTF-16 surrogate is rejected", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfchar <01> <D800> endbfchar");
            return result.Mappings.Count == 0 && result.MalformedEntries == 1;
        });

        Check("odd-length source code is rejected", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfchar <1> <0041> endbfchar");
            return result.Mappings.Count == 0 && result.MalformedEntries > 0;
        });

        Check("descending source range is rejected", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfrange <03> <01> <0041> endbfrange");
            return result.Mappings.Count == 0 && result.MalformedEntries > 0;
        });

        Check("range expansion is bounded", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfrange <0000> <2000> <0041> endbfrange");
            return result.Mappings.Count == 0 && result.MalformedEntries > 0;
        });

        Check("destination overflow is rejected", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfrange <01> <02> <FFFF> endbfrange");
            return result.Mappings.Count == 1 && result.MalformedEntries == 1;
        });

        Check("array item count must match source range", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfrange <01> <03> [<0041> <0042>] endbfrange");
            return result.Mappings.Count == 2 && result.MalformedEntries == 1;
        });


        Check("whitespace inside hexadecimal strings is ignored", () =>
        {
            var result = ToUnicodeCMapParser.Parse("1 beginbfchar <00 01> <00 41> endbfchar");
            return result.Mappings.TryGetValue("0001", out string? value) && value == "A";
        });

        Check("malformed bfchar pair does not shift following entries", () =>
        {
            var result = ToUnicodeCMapParser.Parse("2 beginbfchar <1> <0041> <02> <0042> endbfchar");
            return result.Mappings.Count == 1 && result.Mappings["02"] == "B" &&
                   result.MalformedEntries == 1;
        });

        Check("duplicate source mapping is rejected", () =>
        {
            var result = ToUnicodeCMapParser.Parse("2 beginbfchar <01> <0041> <01> <0042> endbfchar");
            return result.Mappings.Count == 1 && result.MalformedEntries == 1;
        });

        Check("mixed-width source codes decode to exact Unicode values", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "2 begincodespacerange <00> <7F> <8100> <81FF> endcodespacerange " +
                "2 beginbfchar <41> <0041> <8102> <00E9> endbfchar");
            return ToUnicodeCMapParser.TryDecodeBytes(
                       new byte[] { 0x41, 0x81, 0x02 }, cmap, out var decoded) &&
                   decoded.SequenceEqual(new[]
                   {
                       new CMapDecodedCode("41", "A"),
                       new CMapDecodedCode("8102", "é")
                   });
        });

        Check("multi-character Unicode mapping remains one source code", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <2100> <21FF> endcodespacerange " +
                "1 beginbfchar <21AF> <004E0049> endbfchar");
            return ToUnicodeCMapParser.TryDecodeBytes(
                       new byte[] { 0x21, 0xAF }, cmap, out var decoded) &&
                   decoded.Count == 1 && decoded[0].Unicode == "NI";
        });

        Check("trailing bytes outside every code space fail closed", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <00> <7F> endcodespacerange " +
                "1 beginbfchar <41> <0041> endbfchar");
            return !ToUnicodeCMapParser.TryDecodeBytes(new byte[] { 0x41, 0xFF }, cmap, out _);
        });

        Check("code-space match without a ToUnicode entry fails closed", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <00> <7F> endcodespacerange " +
                "1 beginbfchar <41> <0041> endbfchar");
            return !ToUnicodeCMapParser.TryDecodeBytes(new byte[] { 0x42 }, cmap, out _);
        });

        Check("empty source byte sequence decodes to an empty result", () =>
        {
            var cmap = ToUnicodeCMapParser.Parse(
                "1 begincodespacerange <00> <7F> endcodespacerange");
            return ToUnicodeCMapParser.TryDecodeBytes(
                       ReadOnlySpan<byte>.Empty, cmap, out var decoded) &&
                   decoded.Count == 0;
        });

        Console.WriteLine($"ToUnicode CMap self-tests: {passed}/{passed + failed} passed.");
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
