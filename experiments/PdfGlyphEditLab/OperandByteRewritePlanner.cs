namespace PdfGlyphEditLab;

/// <summary>
/// A byte-preservation plan for one encoded PDF string operand.
/// This does not emit PDF syntax or alter text positioning; a PDF content writer
/// must preserve the original text advance when applying the plan.
/// </summary>
public sealed record OperandByteSegment(int Offset, int Length);

public static class OperandByteRewritePlanner
{
    public static IReadOnlyList<OperandByteSegment> KeepUnselectedBytes(
        int operandLength,
        IEnumerable<OperandEditSpan> removals)
    {
        ArgumentNullException.ThrowIfNull(removals);
        if (operandLength < 0)
            throw new ArgumentOutOfRangeException(nameof(operandLength));

        var ranges = removals
            .Select(x => new OperandByteSegment(x.ByteOffset, x.ByteLength))
            .OrderBy(x => x.Offset)
            .ToArray();

        var result = new List<OperandByteSegment>();
        int cursor = 0;
        foreach (var range in ranges)
        {
            if (range.Offset < 0 || range.Length <= 0 ||
                range.Offset > operandLength - range.Length)
                throw new InvalidOperationException("Removal range is outside the encoded PDF string.");
            if (range.Offset < cursor)
                throw new InvalidOperationException("Removal ranges overlap.");

            if (range.Offset > cursor)
                result.Add(new OperandByteSegment(cursor, range.Offset - cursor));

            cursor = checked(range.Offset + range.Length);
        }

        if (cursor < operandLength)
            result.Add(new OperandByteSegment(cursor, operandLength - cursor));

        return result;
    }
}
