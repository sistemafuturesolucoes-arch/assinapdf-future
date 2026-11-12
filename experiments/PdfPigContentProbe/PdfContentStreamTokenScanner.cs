internal sealed record PdfContentStringOperand(
    int TokenStart,
    int TokenEnd,
    int OperatorStart,
    string Kind,
    byte[] DecodedBytes);

internal sealed record PdfContentScanResult(
    IReadOnlyList<PdfContentStringOperand> TjOperands,
    bool IsComplete,
    string Reason);

internal static class PdfContentStreamTokenScanner
{
    public static PdfContentScanResult Scan(ReadOnlySpan<byte> content)
    {
        var operands = new List<PdfContentStringOperand>();
        int i = 0;
        int previousStringStart = -1, previousStringEnd = -1;
        string? previousStringKind = null;
        byte[]? previousStringBytes = null;

        while (i < content.Length)
        {
            byte b = content[i];
            if (IsWhite(b)) { i++; continue; }
            if (b == '%')
            {
                while (i < content.Length && content[i] is not 10 and not 13) i++;
                continue;
            }

            int start = i;
            if (b == '(')
            {
                if (!TryReadLiteral(content, ref i, out var decoded, out string error))
                    return new PdfContentScanResult(operands, false, error);
                previousStringStart = start;
                previousStringEnd = i;
                previousStringKind = "literal";
                previousStringBytes = decoded;
                continue;
            }

            if (b == '<')
            {
                if (i + 1 < content.Length && content[i + 1] == '<')
                {
                    i += 2;
                    previousStringStart = previousStringEnd = -1;
                    previousStringKind = null;
                    previousStringBytes = null;
                    continue;
                }

                if (!TryReadHex(content, ref i, out var decoded, out string error))
                    return new PdfContentScanResult(operands, false, error);
                previousStringStart = start;
                previousStringEnd = i;
                previousStringKind = "hex";
                previousStringBytes = decoded;
                continue;
            }

            if (b == '>' && i + 1 < content.Length && content[i + 1] == '>')
            {
                i += 2;
                previousStringStart = previousStringEnd = -1;
                previousStringKind = null;
                previousStringBytes = null;
                continue;
            }

            if (b == '[' || b == ']' || b == '/' || b == '{' || b == '}' ||
                b == ')' || b == '>')
            {
                i++;
                previousStringStart = previousStringEnd = -1;
                previousStringKind = null;
                previousStringBytes = null;
                continue;
            }

            while (i < content.Length && !IsWhite(content[i]) && !IsDelimiter(content[i])) i++;
            if (i == start)
            {
                return new PdfContentScanResult(operands, false,
                    $"Delimitador inesperado no offset {i}.");
            }

            string word = System.Text.Encoding.ASCII.GetString(content[start..i]);
            if (word == "BI")
                return new PdfContentScanResult(operands, false,
                    $"Inline image (BI) encontrado no offset {start}; varredura interrompida por segurança.");

            if (word == "Tj" && previousStringStart >= 0 &&
                previousStringKind is not null && previousStringBytes is not null)
            {
                operands.Add(new PdfContentStringOperand(
                    previousStringStart, previousStringEnd, start,
                    previousStringKind, previousStringBytes));
            }

            previousStringStart = previousStringEnd = -1;
            previousStringKind = null;
            previousStringBytes = null;
        }

        return new PdfContentScanResult(operands, true, "Varredura lexical concluída.");
    }

    private static bool TryReadLiteral(
        ReadOnlySpan<byte> data, ref int i, out byte[] decoded, out string reason)
    {
        var output = new List<byte>();
        i++;
        int depth = 1;
        while (i < data.Length && depth > 0)
        {
            byte b = data[i++];
            if (b == '\\')
            {
                if (i >= data.Length)
                {
                    decoded = Array.Empty<byte>();
                    reason = "Escape incompleto em string literal.";
                    return false;
                }

                byte escaped = data[i++];
                if (escaped == 10) continue;
                if (escaped == 13)
                {
                    if (i < data.Length && data[i] == 10) i++;
                    continue;
                }

                if (escaped is >= (byte)'0' and <= (byte)'7')
                {
                    int octal = escaped - '0', count = 1;
                    while (count < 3 && i < data.Length &&
                           data[i] is >= (byte)'0' and <= (byte)'7')
                    {
                        octal = octal * 8 + data[i++] - '0';
                        count++;
                    }
                    output.Add((byte)(octal & 0xFF));
                    continue;
                }

                output.Add(escaped switch
                {
                    (byte)'n' => 10, (byte)'r' => 13, (byte)'t' => 9,
                    (byte)'b' => 8, (byte)'f' => 12, _ => escaped
                });
                continue;
            }

            if (b == '(') { depth++; output.Add(b); continue; }
            if (b == ')')
            {
                depth--;
                if (depth > 0) output.Add(b);
                continue;
            }

            if (b == 13)
            {
                if (i < data.Length && data[i] == 10) i++;
                output.Add(10);
                continue;
            }
            output.Add(b);
        }

        if (depth != 0)
        {
            decoded = Array.Empty<byte>();
            reason = "String literal não terminada.";
            return false;
        }

        decoded = output.ToArray();
        reason = "OK";
        return true;
    }

    private static bool TryReadHex(
        ReadOnlySpan<byte> data, ref int i, out byte[] decoded, out string reason)
    {
        i++;
        var nibbles = new List<int>();
        while (i < data.Length && data[i] != '>')
        {
            byte b = data[i++];
            if (IsWhite(b)) continue;
            int value = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                _ => -1
            };
            if (value < 0)
            {
                decoded = Array.Empty<byte>();
                reason = "Caractere inválido em string hexadecimal.";
                return false;
            }
            nibbles.Add(value);
        }

        if (i >= data.Length)
        {
            decoded = Array.Empty<byte>();
            reason = "String hexadecimal não terminada.";
            return false;
        }
        i++;

        var output = new byte[(nibbles.Count + 1) / 2];
        for (int n = 0; n < nibbles.Count; n += 2)
            output[n / 2] = (byte)((nibbles[n] << 4) |
                (n + 1 < nibbles.Count ? nibbles[n + 1] : 0));
        decoded = output;
        reason = "OK";
        return true;
    }

    private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or
             (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or
             (byte)'/' or (byte)'%';
}
