namespace PdfGlyphEditLab;

public enum PdfContentTokenKind
{
    LiteralString, HexString, ArrayStart, ArrayEnd, Name, Word, Number
}

public sealed record PdfContentToken(PdfContentTokenKind Kind, int Offset, int Length, string Raw)
{
    public int End => checked(Offset + Length);
}

/// <summary>
/// Structural tokenizer for decoded PDF page content streams. It identifies string
/// operands and operator boundaries only; it does not decode font character codes,
/// interpret graphics/text state, or map PDFium glyphs to source bytes.
/// </summary>
public static class PdfContentTokenizer
{
    public static IReadOnlyList<PdfContentToken> Tokenize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var tokens = new List<PdfContentToken>();
        int i = 0;

        while (i < content.Length)
        {
            char ch = content[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '%')
            {
                while (i < content.Length && content[i] is not '\r' and not '\n') i++;
                continue;
            }

            int start = i;
            if (ch == '(')
            {
                i++;
                int depth = 1;
                while (i < content.Length && depth > 0)
                {
                    if (content[i] == '\\')
                    {
                        i++;
                        if (i < content.Length && content[i] == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i += 2;
                        else if (i < content.Length) i++;
                        continue;
                    }
                    if (content[i] == '(') depth++;
                    else if (content[i] == ')') depth--;
                    i++;
                }
                if (depth != 0) throw new FormatException("Unterminated PDF literal string.");
                tokens.Add(Make(PdfContentTokenKind.LiteralString, content, start, i));
                continue;
            }

            if (ch == '<' && i + 1 < content.Length && content[i + 1] != '<')
            {
                i++;
                while (i < content.Length && content[i] != '>') i++;
                if (i >= content.Length) throw new FormatException("Unterminated PDF hex string.");
                i++;
                tokens.Add(Make(PdfContentTokenKind.HexString, content, start, i));
                continue;
            }

            if (ch == '[') { i++; tokens.Add(Make(PdfContentTokenKind.ArrayStart, content, start, i)); continue; }
            if (ch == ']') { i++; tokens.Add(Make(PdfContentTokenKind.ArrayEnd, content, start, i)); continue; }

            if (ch == '/')
            {
                i++;
                while (i < content.Length && !IsDelimiter(content[i])) i++;
                tokens.Add(Make(PdfContentTokenKind.Name, content, start, i));
                continue;
            }

            while (i < content.Length && !char.IsWhiteSpace(content[i]) && !IsDelimiter(content[i])) i++;
            if (i == start) { i++; continue; }

            string raw = content[start..i];
            var kind = double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _)
                ? PdfContentTokenKind.Number : PdfContentTokenKind.Word;
            tokens.Add(Make(kind, content, start, i));
        }

        return tokens;
    }

    private static bool IsDelimiter(char c) =>
        c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    private static PdfContentToken Make(PdfContentTokenKind kind, string source, int start, int end) =>
        new(kind, start, end - start, source[start..end]);
}
