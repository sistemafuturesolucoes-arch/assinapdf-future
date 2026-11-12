internal sealed record PdfDecodedByteOrigin(int RawStart, int RawEnd);

internal sealed record PdfDecodedStringToken(
    string Kind,
    byte[] DecodedBytes,
    IReadOnlyList<PdfDecodedByteOrigin> Origins);

internal static class PdfStringOperandCodec
{
    public static bool TryDecodeToken(
        ReadOnlySpan<byte> token,
        out PdfDecodedStringToken decoded,
        out string reason)
    {
        decoded = null!;
        reason = "Token PDF não suportado.";
        if (token.Length < 2) return false;

        if (token[0] == (byte)'(' && token[^1] == (byte)')')
            return TryDecodeLiteral(token, out decoded, out reason);

        if (token[0] == (byte)'<' && token[^1] == (byte)'>' &&
            !(token.Length >= 4 && token[1] == (byte)'<'))
            return TryDecodeHex(token, out decoded, out reason);

        return false;
    }

    public static bool TryReplaceBytesSameLength(
        ReadOnlySpan<byte> rawToken,
        PdfDecodedStringToken decoded,
        int byteOffset,
        ReadOnlySpan<byte> replacement,
        out byte[] rewrittenToken,
        out string reason)
    {
        rewrittenToken = Array.Empty<byte>();
        reason = "Substituição recusada.";
        if (byteOffset < 0 || replacement.Length == 0 ||
            byteOffset + replacement.Length > decoded.DecodedBytes.Length ||
            decoded.Origins.Count != decoded.DecodedBytes.Length)
        {
            reason = "Intervalo de bytes inválido.";
            return false;
        }

        for (int i = 0; i < replacement.Length; i++)
        {
            if (decoded.DecodedBytes[byteOffset + i] == replacement[i])
            {
                reason = "Substituição não altera nenhum byte.";
                return false;
            }
        }

        int rawStart = decoded.Origins[byteOffset].RawStart;
        int rawEnd = decoded.Origins[byteOffset + replacement.Length - 1].RawEnd;
        if (rawStart < 1 || rawEnd > rawToken.Length - 1 || rawEnd <= rawStart)
        {
            reason = "Origem bruta fora dos limites do token.";
            return false;
        }

        byte[] encodedReplacement;
        if (decoded.Kind == "hex")
        {
            encodedReplacement = System.Text.Encoding.ASCII.GetBytes(
                Convert.ToHexString(replacement));
        }
        else if (decoded.Kind == "literal")
        {
            var output = new List<byte>(replacement.Length * 4);
            foreach (byte value in replacement)
            {
                output.Add((byte)'\\');
                output.Add((byte)('0' + ((value >> 6) & 7)));
                output.Add((byte)('0' + ((value >> 3) & 7)));
                output.Add((byte)('0' + (value & 7)));
            }
            encodedReplacement = output.ToArray();
        }
        else
        {
            reason = "Tipo de token não suportado.";
            return false;
        }

        rewrittenToken = new byte[rawToken.Length - (rawEnd - rawStart) + encodedReplacement.Length];
        rawToken[..rawStart].CopyTo(rewrittenToken);
        encodedReplacement.CopyTo(rewrittenToken.AsSpan(rawStart));
        rawToken[rawEnd..].CopyTo(rewrittenToken.AsSpan(rawStart + encodedReplacement.Length));

        if (!TryDecodeToken(rewrittenToken, out var check, out _) ||
            check.DecodedBytes.Length != decoded.DecodedBytes.Length)
        {
            rewrittenToken = Array.Empty<byte>();
            reason = "A validação de ida e volta do token reescrito falhou.";
            return false;
        }

        for (int i = 0; i < check.DecodedBytes.Length; i++)
        {
            byte expected = i >= byteOffset && i < byteOffset + replacement.Length
                ? replacement[i - byteOffset]
                : decoded.DecodedBytes[i];
            if (check.DecodedBytes[i] != expected)
            {
                rewrittenToken = Array.Empty<byte>();
                reason = "A reescrita alteraria bytes fora da seleção.";
                return false;
            }
        }

        reason = "Token reescrito em memória; bytes decodificados fora da seleção preservados.";
        return true;
    }

    private static bool TryDecodeLiteral(
        ReadOnlySpan<byte> token,
        out PdfDecodedStringToken decoded,
        out string reason)
    {
        var bytes = new List<byte>();
        var origins = new List<PdfDecodedByteOrigin>();
        int i = 1;
        int depth = 1;

        while (i < token.Length - 1)
        {
            int start = i;
            byte value = token[i++];

            if (value == (byte)'\\')
            {
                if (i >= token.Length - 1)
                {
                    decoded = null!;
                    reason = "Escape incompleto em string literal.";
                    return false;
                }

                byte escaped = token[i++];
                if (escaped == 10) continue;
                if (escaped == 13)
                {
                    if (i < token.Length - 1 && token[i] == 10) i++;
                    continue;
                }

                if (escaped is >= (byte)'0' and <= (byte)'7')
                {
                    int octal = escaped - '0';
                    int count = 1;
                    while (count < 3 && i < token.Length - 1 &&
                           token[i] >= '0' && token[i] <= '7')
                    {
                        octal = octal * 8 + token[i++] - '0';
                        count++;
                    }
                    bytes.Add((byte)(octal & 0xFF));
                    origins.Add(new PdfDecodedByteOrigin(start, i));
                    continue;
                }

                byte decodedValue = escaped switch
                {
                    (byte)'n' => 10, (byte)'r' => 13, (byte)'t' => 9,
                    (byte)'b' => 8, (byte)'f' => 12, _ => escaped
                };
                bytes.Add(decodedValue);
                origins.Add(new PdfDecodedByteOrigin(start, i));
                continue;
            }

            if (value == (byte)'(') depth++;
            else if (value == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    decoded = null!;
                    reason = "Fechamento prematuro da string literal.";
                    return false;
                }
            }

            bytes.Add(value);
            origins.Add(new PdfDecodedByteOrigin(start, i));
        }

        if (depth != 1)
        {
            decoded = null!;
            reason = "Parênteses não balanceados.";
            return false;
        }

        decoded = new PdfDecodedStringToken("literal", bytes.ToArray(), origins);
        reason = "String literal decodificada.";
        return true;
    }

    private static bool TryDecodeHex(
        ReadOnlySpan<byte> token,
        out PdfDecodedStringToken decoded,
        out string reason)
    {
        var nibbles = new List<(byte Value, int Position)>();
        for (int i = 1; i < token.Length - 1; i++)
        {
            byte b = token[i];
            if (b is 0 or 9 or 10 or 12 or 13 or 32) continue;
            int value = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                _ => -1
            };
            if (value < 0)
            {
                decoded = null!;
                reason = "Caractere não hexadecimal em string hex.";
                return false;
            }
            nibbles.Add(((byte)value, i));
        }

        var bytes = new List<byte>();
        var origins = new List<PdfDecodedByteOrigin>();
        for (int i = 0; i < nibbles.Count; i += 2)
        {
            byte value = (byte)(nibbles[i].Value << 4);
            int end = nibbles[i].Position + 1;
            if (i + 1 < nibbles.Count)
            {
                value |= nibbles[i + 1].Value;
                end = nibbles[i + 1].Position + 1;
            }
            bytes.Add(value);
            origins.Add(new PdfDecodedByteOrigin(nibbles[i].Position, end));
        }

        decoded = new PdfDecodedStringToken("hex", bytes.ToArray(), origins);
        reason = "String hexadecimal decodificada.";
        return true;
    }
}
