internal static class PdfContentStreamRewritePlannerSelfTests
{
    public static int Run()
    {
        int passed = 0, failed = 0;

        Check("finds literal Tj operand with byte-accurate offsets", () =>
        {
            byte[] stream = System.Text.Encoding.ASCII.GetBytes("BT (ABC) Tj ET");
            var scan = PdfContentStreamTokenScanner.Scan(stream);
            return scan.IsComplete && scan.TjOperands.Count == 1 &&
                   scan.TjOperands[0].TokenStart == 3 &&
                   scan.TjOperands[0].TokenEnd == 8 &&
                   scan.TjOperands[0].OperatorStart == 9 &&
                   System.Text.Encoding.ASCII.GetString(scan.TjOperands[0].DecodedBytes) == "ABC";
        });

        Check("ignores comments and strings not followed by Tj", () =>
        {
            byte[] stream = System.Text.Encoding.ASCII.GetBytes(
                "% (comment) Tj\n(ignored) TjX [(array)] TJ /Name (shown) Tj");
            var scan = PdfContentStreamTokenScanner.Scan(stream);
            return scan.IsComplete && scan.TjOperands.Count == 1 &&
                   System.Text.Encoding.ASCII.GetString(scan.TjOperands[0].DecodedBytes) == "shown";
        });

        Check("plans one selected byte rewrite inside a literal Tj operand", () =>
        {
            byte[] stream = System.Text.Encoding.ASCII.GetBytes("BT (ABC) Tj ET");
            return PdfContentStreamRewritePlanner.TryPlanSingleTjReplacement(
                       stream, 3, 1, new byte[] { 0x42 }, new byte[] { 0x44 },
                       out var plan, out _) &&
                   plan.OriginalDecodedHex == "42" &&
                   plan.ReplacementDecodedHex == "44" &&
                   System.Text.Encoding.ASCII.GetString(plan.RewrittenStream).StartsWith("BT ", StringComparison.Ordinal) &&
                   System.Text.Encoding.ASCII.GetString(plan.RewrittenStream).EndsWith(" Tj ET", StringComparison.Ordinal) &&
                   PdfContentStreamTokenScanner.Scan(plan.RewrittenStream).TjOperands.Count == 1;
        });

        Check("plans selected byte rewrite inside a hex Tj operand", () =>
        {
            byte[] stream = System.Text.Encoding.ASCII.GetBytes("BT <414243> Tj ET");
            return PdfContentStreamRewritePlanner.TryPlanSingleTjReplacement(
                       stream, 3, 1, new byte[] { 0x42 }, new byte[] { 0x58 },
                       out var plan, out _) &&
                   PdfContentStreamTokenScanner.Scan(plan.RewrittenStream).TjOperands[0]
                       .DecodedBytes.SequenceEqual(new byte[] { 0x41, 0x58, 0x43 });
        });

        Check("rejects a string that is not a Tj operand", () =>
        {
            byte[] stream = System.Text.Encoding.ASCII.GetBytes("(ABC) TJ");
            return !PdfContentStreamRewritePlanner.TryPlanSingleTjReplacement(
                stream, 0, 1, new byte[] { 0x42 }, new byte[] { 0x44 }, out _, out _);
        });

        Check("rejects mismatching selected source bytes", () =>
        {
            byte[] stream = System.Text.Encoding.ASCII.GetBytes("(ABC) Tj");
            return !PdfContentStreamRewritePlanner.TryPlanSingleTjReplacement(
                stream, 0, 1, new byte[] { 0x58 }, new byte[] { 0x44 }, out _, out _);
        });

        Check("rejects stream containing inline image syntax", () =>
        {
            byte[] stream = System.Text.Encoding.ASCII.GetBytes("BI /W 1 /H 1 ID x EI (A) Tj");
            return !PdfContentStreamRewritePlanner.TryPlanSingleTjReplacement(
                stream, 23, 0, new byte[] { 0x41 }, new byte[] { 0x42 }, out _, out _);
        });

        Check("preserves all other operands and operators in stream", () =>
        {
            byte[] stream = System.Text.Encoding.ASCII.GetBytes("BT (LEFT) Tj 10 0 Td <414243> Tj ET");
            var scan = PdfContentStreamTokenScanner.Scan(stream);
            int offset = scan.TjOperands[1].TokenStart;
            return PdfContentStreamRewritePlanner.TryPlanSingleTjReplacement(
                       stream, offset, 1, new byte[] { 0x42 }, new byte[] { 0x58 },
                       out var plan, out _) &&
                   plan.RewrittenStream.AsSpan(0, offset).SequenceEqual(stream.AsSpan(0, offset)) &&
                   plan.RewrittenStream.AsSpan(offset + plan.RewrittenTokenLength)
                       .SequenceEqual(stream.AsSpan(scan.TjOperands[1].TokenEnd));
        });

        Console.WriteLine($"PDF content stream rewrite planner self-tests: {passed}/{passed + failed} passed.");
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
