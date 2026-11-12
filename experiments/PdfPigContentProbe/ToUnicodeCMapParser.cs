using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

internal sealed record CMapParseResult(
    IReadOnlyDictionary<string, string> Mappings,
    int MalformedEntries,
    int ParsedBfCharEntries,
    int ParsedBfRangeEntries);

internal static class ToUnicodeCMapParser
{
    private const int MaxRangeExpansion = 4096;
    private const int MaxSourceBytes = 4;

    private static readonly Regex TokenPattern = new(
        @"<[^>]*>|\[|\]|[A-Za-z][A-Za-z0-9]*|[0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CMapParseResult Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string withoutComments = Regex.Replace(source, @"%[^\r\n]*", "");
        var tokens = TokenPattern.Matches(withoutComments)
            .Select(match => match.Value)
            .ToArray();

        var mappings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        int malformed = 0, bfCharCount = 0, bfRangeCount = 0;

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "beginbfchar")
            {
                i = ParseBfCharBlock(tokens, i + 1, mappings, ref malformed, ref bfCharCount);
            }
            else if (tokens[i] == "beginbfrange")
            {
                i = ParseBfRangeBlock(tokens, i + 1, mappings, ref malformed, ref bfRangeCount);
            }
        }

        return new CMapParseResult(mappings, malformed, bfCharCount, bfRangeCount);
    }

    private static int ParseBfCharBlock(
        string[] tokens, int index, SortedDictionary<string, string> mappings,
        ref int malformed, ref int parsed)
    {
        while (index < tokens.Length && tokens[index] != "endbfchar")
        {
            if (index + 1 >= tokens.Length)
            {
                malformed++;
                break;
            }

            if (!TryHexToken(tokens[index], out string source) ||
                !TryHexToken(tokens[index + 1], out string destination))
            {
                malformed++;
                index += 2;
                continue;
            }

            if (TryNormalizeSource(source, out string normalized) &&
                TryDecodeUnicode(destination, out string unicode) &&
                !mappings.ContainsKey(normalized))
            {
                mappings.Add(normalized, unicode);
                parsed++;
            }
            else malformed++;

            index += 2;
        }

        return index;
    }

    private static int ParseBfRangeBlock(
        string[] tokens, int index, SortedDictionary<string, string> mappings,
        ref int malformed, ref int parsed)
    {
        while (index < tokens.Length && tokens[index] != "endbfrange")
        {
            if (index + 2 >= tokens.Length)
            {
                malformed++;
                break;
            }

            if (!TryHexToken(tokens[index], out string firstHex) ||
                !TryHexToken(tokens[index + 1], out string lastHex) ||
                !TryNormalizeSource(firstHex, out _) ||
                firstHex.Length != lastHex.Length ||
                !TryParseSource(firstHex, out BigInteger first) ||
                !TryParseSource(lastHex, out BigInteger last) ||
                last < first || last - first > MaxRangeExpansion)
            {
                malformed++;
                index += 3;
                continue;
            }

            int count = (int)(last - first + 1);
            int sourceWidth = firstHex.Length;

            if (tokens[index + 2] == "[")
            {
                index += 3;
                int added = 0;
                while (index < tokens.Length && tokens[index] != "]" && tokens[index] != "endbfrange")
                {
                    if (!TryHexToken(tokens[index], out string destination) ||
                        added >= count ||
                        !TryAddMapping(mappings, first + added, sourceWidth, destination))
                    {
                        malformed++;
                    }
                    else parsed++;

                    added++;
                    index++;
                }

                if (index >= tokens.Length || tokens[index] != "]" || added != count)
                    malformed++;
                if (index < tokens.Length && tokens[index] == "]") index++;
            }
            else
            {
                if (!TryHexToken(tokens[index + 2], out string destinationStart) ||
                    !TryDecodeUnicode(destinationStart, out _))
                {
                    malformed++;
                    index += 3;
                    continue;
                }

                byte[] startBytes = Convert.FromHexString(destinationStart);
                for (int offset = 0; offset < count; offset++)
                {
                    byte[] current = startBytes.ToArray();
                    if (!TryIncrementBigEndian(current, offset) ||
                        !TryAddMapping(mappings, first + offset, sourceWidth, Convert.ToHexString(current)))
                    {
                        malformed++;
                    }
                    else parsed++;
                }

                index += 3;
            }
        }

        return index;
    }

    private static bool TryAddMapping(
        SortedDictionary<string, string> mappings, BigInteger source,
        int sourceWidthHex, string destination)
    {
        if (!TryDecodeUnicode(destination, out string unicode)) return false;
        string key = source.ToString("X", CultureInfo.InvariantCulture);
        if (key.Length > sourceWidthHex) return false;
        key = key.PadLeft(sourceWidthHex, '0');
        if (mappings.ContainsKey(key)) return false;
        mappings.Add(key, unicode);
        return true;
    }

    private static bool TryIncrementBigEndian(byte[] bytes, int amount)
    {
        for (int step = 0; step < amount; step++)
        {
            int index = bytes.Length - 1;
            while (index >= 0 && bytes[index] == byte.MaxValue)
            {
                bytes[index] = 0;
                index--;
            }
            if (index < 0) return false;
            bytes[index]++;
        }
        return true;
    }

    private static bool TryHexToken(string token, out string hex)
    {
        hex = "";
        if (token.Length < 3 || token[0] != '<' || token[^1] != '>') return false;
        hex = new string(token[1..^1].Where(character => !char.IsWhiteSpace(character)).ToArray());
        return hex.Length > 0 && hex.Length % 2 == 0 &&
               hex.All(Uri.IsHexDigit);
    }

    private static bool TryNormalizeSource(string source, out string normalized)
    {
        normalized = source.ToUpperInvariant();
        return normalized.Length > 0 && normalized.Length % 2 == 0 &&
               normalized.Length <= MaxSourceBytes * 2 &&
               normalized.All(Uri.IsHexDigit);
    }

    private static bool TryParseSource(string hex, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (!TryNormalizeSource(hex, out _)) return false;
        value = new BigInteger(Convert.FromHexString("00" + hex), isUnsigned: true, isBigEndian: true);
        return true;
    }

    private static bool TryDecodeUnicode(string hex, out string value)
    {
        value = "";
        if (hex.Length == 0 || hex.Length % 4 != 0 || !hex.All(Uri.IsHexDigit))
            return false;

        try
        {
            value = new UnicodeEncoding(
                bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true)
                .GetString(Convert.FromHexString(hex));
            return value.Length > 0;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
