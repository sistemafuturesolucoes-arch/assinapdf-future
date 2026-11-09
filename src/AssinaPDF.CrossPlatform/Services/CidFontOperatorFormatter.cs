using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Aspose.Pdf;
using Aspose.Pdf.Operators;
using Aspose.Pdf.Text;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Preserves an embedded CID font when a PDF already contains a compatible
/// bold face. It changes only the font resource used by the selected glyphs;
/// CID codes, glyph positioning adjustments and surrounding text are retained.
/// The operation is intentionally fail-closed for unsupported PDF structures.
/// </summary>
internal static class CidFontOperatorFormatter
{
    private const BindingFlags ReflectionFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Regex BfCharBlock = new(
        @"\d+\s+beginbfchar(?<body>.*?)endbfchar",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CMapPair = new(
        @"<(?<cid>[0-9A-Fa-f]{4})>\s*<(?<unicode>[0-9A-Fa-f]+)>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool TryApplyBoldVariant(
        Document document,
        int pageNumber,
        TextFragment target,
        out string reason)
    {
        reason = string.Empty;
        if (pageNumber < 1 || pageNumber > document.Pages.Count)
            return Fail("Página fora dos limites.", out reason);

        if (string.IsNullOrEmpty(target.Text))
            return Fail("O trecho selecionado está vazio.", out reason);

        var segments = target.Segments.Cast<TextSegment>().ToArray();
        if (segments.Length == 0)
            return Fail("O trecho não possui segmentos físicos de texto.", out reason);

        var sourceFont = segments[0].TextState.Font;
        if (sourceFont == null ||
            !sourceFont.IsCid ||
            !sourceFont.IsEmbedded ||
            !string.Equals(sourceFont.Encoding?.ToString(), "Identity-H", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(sourceFont.ResourceName))
            return Fail("A fonte de origem não é uma fonte CID Identity-H incorporada.", out reason);

        if (segments.Any(segment =>
                !string.Equals(
                    segment.TextState.Font?.ResourceName,
                    sourceFont.ResourceName,
                    StringComparison.Ordinal)))
            return Fail("O trecho utiliza mais de um recurso de fonte; a troca segmentada não é segura.", out reason);

        ushort? sourceWeight = ReadEmbeddedTrueTypeWeight(sourceFont);
        if (sourceWeight is null || sourceWeight.Value < 350 || sourceWeight.Value > 500)
            return Fail("Não foi possível confirmar que a fonte CID de origem é Regular.", out reason);

        if (!TryReadToUnicodeMap(sourceFont, out var sourceMap) ||
            !TryEncodeIdentityCidText(target.Text, sourceMap, out string encodedTarget, out reason))
            return false;

        var page = document.Pages[pageNumber];
        var boldCandidates = new List<Font>();
        foreach (Font candidate in page.Resources.Fonts)
        {
            if (string.Equals(candidate.ResourceName, sourceFont.ResourceName, StringComparison.Ordinal) ||
                !candidate.IsCid ||
                !candidate.IsEmbedded ||
                !string.Equals(candidate.Encoding?.ToString(), "Identity-H", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(candidate.ResourceName))
                continue;

            ushort? weight = ReadEmbeddedTrueTypeWeight(candidate);
            if (weight is null || weight.Value < 600)
                continue;

            if (!HasIdentityCidToGidMap(candidate) ||
                !TryReadToUnicodeMap(candidate, out var candidateMap))
                continue;

            bool sameMappings = true;
            foreach (char character in target.Text)
            {
                if (!TryFindCid(sourceMap, character.ToString(), out ushort sourceCid) ||
                    !candidateMap.TryGetValue(sourceCid, out string? candidateUnicode) ||
                    !string.Equals(candidateUnicode, character.ToString(), StringComparison.Ordinal))
                {
                    sameMappings = false;
                    break;
                }
            }

            if (sameMappings)
                boldCandidates.Add(candidate);
        }

        if (boldCandidates.Count != 1)
            return Fail(
                boldCandidates.Count == 0
                    ? "Não foi encontrada uma fonte CID Bold incorporada com o mesmo mapa de caracteres."
                    : "Há mais de uma fonte CID Bold compatível; a troca foi cancelada por ambiguidade.",
                out reason);

        Font boldFont = boldCandidates[0];
        if (!HasIdentityCidToGidMap(sourceFont))
            return Fail("A fonte CID de origem não utiliza CIDToGIDMap Identity.", out reason);

        if (!TryFindOperator(
                page,
                sourceFont.ResourceName!,
                encodedTarget,
                out int operatorIndex,
                out double fontSize,
                out SetGlyphsPositionShowText? matchingOperator,
                out int encodedOffset,
                out reason))
            return false;

        if (matchingOperator == null || fontSize <= 0 || encodedOffset % 2 != 0)
            return Fail("O operador de texto não pôde ser dividido com segurança.", out reason);

        GlyphPosition[] glyphs = matchingOperator.GlyphPositions.ToArray();
        string operatorText = matchingOperator.Text ?? string.Empty;
        if (!string.Equals(
                string.Concat(glyphs.Select(glyph => glyph.Text)),
                operatorText,
                StringComparison.Ordinal) ||
            operatorText.Length % 2 != 0)
            return Fail("A sequência de glifos não corresponde aos bytes do operador TJ.", out reason);

        int startGlyph = encodedOffset / 2;
        int targetGlyphCount = encodedTarget.Length / 2;
        if (startGlyph < 0 || targetGlyphCount <= 0 ||
            startGlyph + targetGlyphCount > glyphs.Length)
            return Fail("O intervalo CID está fora dos limites do operador TJ.", out reason);

        GlyphPosition[] prefix = glyphs.Take(startGlyph).Select(CloneGlyph).ToArray();
        GlyphPosition[] selected = glyphs.Skip(startGlyph).Take(targetGlyphCount).Select(CloneGlyph).ToArray();
        GlyphPosition[] suffix = glyphs.Skip(startGlyph + targetGlyphCount).Select(CloneGlyph).ToArray();

        if (!string.Equals(
                string.Concat(selected.Select(glyph => glyph.Text)),
                encodedTarget,
                StringComparison.Ordinal))
            return Fail("Os glifos selecionados não correspondem exatamente ao texto solicitado.", out reason);

        var replacementOperators = new List<Aspose.Pdf.Operator>(5);
        if (prefix.Length > 0)
            replacementOperators.Add(new SetGlyphsPositionShowText(prefix));

        replacementOperators.Add(new SelectFont(boldFont.ResourceName!, fontSize));
        replacementOperators.Add(new SetGlyphsPositionShowText(selected));

        // Restore the original face after the selected text even when the
        // selected run is the final content in the original TJ operator.
        replacementOperators.Add(new SelectFont(sourceFont.ResourceName!, fontSize));
        if (suffix.Length > 0)
            replacementOperators.Add(new SetGlyphsPositionShowText(suffix));

        page.Contents.Delete(operatorIndex);
        page.Contents.Insert(operatorIndex, replacementOperators.ToArray());

        reason = $"Fonte CID Regular {sourceFont.ResourceName} substituída por Bold {boldFont.ResourceName}; " +
                 $"{targetGlyphCount} glifos preservados e {prefix.Length + suffix.Length} glifos adjacentes mantidos.";
        return true;
    }

    internal static bool IsBoldFace(Font? font) =>
        ReadEmbeddedTrueTypeWeight(font) is >= 600;

    private static bool TryFindOperator(
        Page page,
        string sourceResource,
        string encodedTarget,
        out int operatorIndex,
        out double fontSize,
        out SetGlyphsPositionShowText? matchingOperator,
        out int encodedOffset,
        out string reason)
    {
        operatorIndex = -1;
        fontSize = 0;
        matchingOperator = null;
        encodedOffset = -1;
        reason = string.Empty;

        string? activeFont = null;
        double activeSize = 0;
        var matches = new List<(int Index, double Size, SetGlyphsPositionShowText Operator, int Offset)>();

        for (int index = 1; index <= page.Contents.Count; index++)
        {
            Aspose.Pdf.Operator operation = page.Contents[index];
            if (operation is SelectFont selectFont)
            {
                activeFont = selectFont.FontName;
                activeSize = selectFont.Size;
                continue;
            }

            if (!string.Equals(activeFont, sourceResource, StringComparison.Ordinal) ||
                operation is not SetGlyphsPositionShowText tj)
                continue;

            string shown = tj.Text ?? string.Empty;
            int offset = shown.IndexOf(encodedTarget, StringComparison.Ordinal);
            if (offset >= 0)
                matches.Add((index, activeSize, tj, offset));
        }

        if (matches.Count != 1)
        {
            reason = matches.Count == 0
                ? "A sequência CID completa não foi encontrada em um único operador TJ."
                : "A sequência CID aparece em mais de um operador TJ; não foi possível identificar uma ocorrência inequívoca.";
            return false;
        }

        var match = matches[0];
        operatorIndex = match.Index;
        fontSize = match.Size;
        matchingOperator = match.Operator;
        encodedOffset = match.Offset;
        return true;
    }

    private static bool TryEncodeIdentityCidText(
        string text,
        IReadOnlyDictionary<ushort, string> cidToUnicode,
        out string encoded,
        out string reason)
    {
        var builder = new StringBuilder(text.Length * 2);
        foreach (char character in text)
        {
            var matchingCids = cidToUnicode
                .Where(item => string.Equals(item.Value, character.ToString(), StringComparison.Ordinal))
                .Select(item => item.Key)
                .Take(2)
                .ToArray();
            if (matchingCids.Length != 1)
            {
                encoded = string.Empty;
                reason = matchingCids.Length == 0
                    ? $"O mapa ToUnicode não contém o caractere U+{(int)character:X4}."
                    : $"O mapa ToUnicode possui mais de um CID para U+{(int)character:X4}.";
                return false;
            }

            ushort cid = matchingCids[0];
            builder.Append((char)(cid >> 8));
            builder.Append((char)(cid & 0xFF));
        }

        encoded = builder.ToString();
        reason = string.Empty;
        return true;
    }

    private static bool TryFindCid(
        IReadOnlyDictionary<ushort, string> map,
        string unicode,
        out ushort cid)
    {
        foreach (var item in map)
        {
            if (string.Equals(item.Value, unicode, StringComparison.Ordinal))
            {
                cid = item.Key;
                return true;
            }
        }

        cid = 0;
        return false;
    }

    private static bool TryReadToUnicodeMap(
        Font font,
        out Dictionary<ushort, string> map)
    {
        map = new Dictionary<ushort, string>();
        object? fontInfo = font.iPdfFont;
        object? fontDictionary = ReadField(fontInfo, "_fontDict");
        object? reader = ReadField(fontInfo, "_reader");
        if (fontDictionary == null || reader == null)
            return false;

        object? entries = ReadField(fontDictionary, "_entries");
        object? rawReference = GetDictionaryEntry(entries, "ToUnicode");
        object? stream = Resolve(reader, rawReference);
        byte[]? rawData = ReadRawStreamData(stream);
        if (rawData == null || rawData.Length == 0)
            return false;

        string cmap = Encoding.ASCII.GetString(rawData);
        foreach (Match block in BfCharBlock.Matches(cmap))
        {
            foreach (Match pair in CMapPair.Matches(block.Groups["body"].Value))
            {
                if (!ushort.TryParse(
                        pair.Groups["cid"].Value,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out ushort cid))
                    continue;

                string destination = pair.Groups["unicode"].Value;
                if (destination.Length == 0 || destination.Length % 4 != 0)
                    continue;

                byte[] unicodeBytes;
                try { unicodeBytes = Convert.FromHexString(destination); }
                catch (FormatException) { continue; }

                string unicode = Encoding.BigEndianUnicode.GetString(unicodeBytes);
                map[cid] = unicode;
            }
        }

        return map.Count > 0;
    }

    private static bool HasIdentityCidToGidMap(Font font)
    {
        object? fontInfo = font.iPdfFont;
        object? fontDictionary = ReadField(fontInfo, "_fontDict");
        object? reader = ReadField(fontInfo, "_reader");
        if (fontDictionary == null || reader == null)
            return false;

        object? entries = ReadField(fontDictionary, "_entries");
        object? rawArray = GetDictionaryEntry(entries, "DescendantFonts");
        object? array = Resolve(reader, rawArray);
        object? rawDescendant = GetArrayItem(array, 0);
        object? descendant = Resolve(reader, rawDescendant);
        object? descendantEntries = ReadField(descendant, "_entries");
        object? cidToGid = GetDictionaryEntry(descendantEntries, "CIDToGIDMap");
        return string.Equals(
            cidToGid?.ToString()?.Trim().TrimStart('/'),
            "Identity",
            StringComparison.OrdinalIgnoreCase);
    }

    private static object? GetArrayItem(object? array, int index)
    {
        if (array == null)
            return null;

        var property = array.GetType().GetProperty(
            "Item", ReflectionFlags, binder: null, returnType: null,
            types: new[] { typeof(int) }, modifiers: null);
        return property?.GetValue(array, new object[] { index });
    }

    private static object? GetDictionaryEntry(object? entries, string key)
    {
        if (entries is not IDictionary dictionary)
            return null;

        foreach (DictionaryEntry entry in dictionary)
        {
            string name = entry.Key?.ToString()?.Trim().TrimStart('/') ?? string.Empty;
            if (string.Equals(name, key, StringComparison.Ordinal))
                return entry.Value;
        }

        return null;
    }

    private static object? Resolve(object? reader, object? value)
    {
        if (reader == null || value == null)
            return null;

        MethodInfo? method = reader.GetType().GetMethods(ReflectionFlags)
            .FirstOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return candidate.Name == "Resolve" &&
                       parameters.Length == 1 &&
                       parameters[0].ParameterType.IsInstanceOfType(value);
            });

        if (method != null)
            return method.Invoke(reader, new[] { value });

        object? objectNumber = ReadField(value, "ObjectNumber");
        object? generation = ReadField(value, "Generation");
        if (objectNumber is int number && generation is int gen)
        {
            method = reader.GetType().GetMethod(
                "ResolveRef", ReflectionFlags, binder: null,
                types: new[] { typeof(int), typeof(int) }, modifiers: null);
            if (method != null)
                return method.Invoke(reader, new object[] { number, gen });
        }

        return value;
    }

    private static object? ReadField(object? instance, string name)
    {
        for (Type? type = instance?.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(
                name, ReflectionFlags | BindingFlags.DeclaredOnly);
            if (field != null)
                return field.GetValue(instance);
        }

        return null;
    }

    private static byte[]? ReadRawStreamData(object? stream)
    {
        if (stream == null)
            return null;

        PropertyInfo? property = stream.GetType().GetProperty("RawData", ReflectionFlags);
        if (property?.GetValue(stream) is byte[] data)
            return data;

        return ReadField(stream, "<RawData>k__BackingField") as byte[];
    }

    private static ushort? ReadEmbeddedTrueTypeWeight(Font? font)
    {
        if (font == null)
            return null;

        using var stream = new MemoryStream();
        try { font.Save(stream); }
        catch { return null; }

        byte[] data = stream.ToArray();
        if (data.Length < 12)
            return null;

        int tableCount = ReadUInt16BigEndian(data, 4);
        for (int index = 0; index < tableCount; index++)
        {
            int recordOffset = 12 + (index * 16);
            if (recordOffset + 16 > data.Length)
                return null;

            string tag = Encoding.ASCII.GetString(data, recordOffset, 4);
            if (!string.Equals(tag, "OS/2", StringComparison.Ordinal))
                continue;

            uint tableOffset = ReadUInt32BigEndian(data, recordOffset + 8);
            uint tableLength = ReadUInt32BigEndian(data, recordOffset + 12);
            if (tableLength < 6 || tableOffset + 6 > data.Length)
                return null;

            return ReadUInt16BigEndian(data, checked((int)tableOffset + 4));
        }

        return null;
    }

    private static ushort ReadUInt16BigEndian(byte[] data, int offset) =>
        checked((ushort)((data[offset] << 8) | data[offset + 1]));

    private static uint ReadUInt32BigEndian(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];

    private static GlyphPosition CloneGlyph(GlyphPosition glyph) =>
        glyph.HasPosition
            ? new GlyphPosition(glyph.Text, glyph.Position)
            : new GlyphPosition(glyph.Text);

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }
}
