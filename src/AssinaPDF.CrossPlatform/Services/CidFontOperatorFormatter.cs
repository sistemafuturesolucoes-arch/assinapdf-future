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
        out string reason) =>
        TryApplyBoldVariantCore(document, pageNumber, target, forceSyntheticBold: false, forceSyntheticItalic: false, foregroundOverride: null, out reason);

    internal static bool TryApplySyntheticBoldFallback(
        Document document,
        int pageNumber,
        TextFragment target,
        out string reason) =>
        TryApplyBoldVariantCore(document, pageNumber, target, forceSyntheticBold: true, forceSyntheticItalic: false, foregroundOverride: null, out reason);

    internal static bool TryApplySyntheticItalicFallback(
        Document document,
        int pageNumber,
        TextFragment target,
        Aspose.Pdf.Color? foregroundOverride,
        out string reason) =>
        TryApplyBoldVariantCore(document, pageNumber, target, forceSyntheticBold: false, forceSyntheticItalic: true, foregroundOverride, out reason);

    internal static bool HasSyntheticItalicOperator(Page page)
    {
        bool insideSavedGraphicsState = false;
        foreach (Aspose.Pdf.Operator operation in page.Contents)
        {
            if (operation is GSave)
            {
                insideSavedGraphicsState = true;
                continue;
            }

            if (insideSavedGraphicsState &&
                operation is ConcatenateMatrix matrix &&
                Math.Abs(matrix.Matrix.C) >= 0.05 &&
                Math.Abs(matrix.Matrix.B) < 0.0001)
                return true;

            if (operation is GRestore)
                insideSavedGraphicsState = false;
        }

        return false;
    }

    private static bool TryApplyBoldVariantCore(
        Document document,
        int pageNumber,
        TextFragment target,
        bool forceSyntheticBold,
        bool forceSyntheticItalic,
        Aspose.Pdf.Color? foregroundOverride,
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
        if (sourceWeight is null || sourceWeight.Value < 100 || sourceWeight.Value > 900)
            return Fail("Não foi possível identificar o peso da fonte CID incorporada.", out reason);

        if (!TryReadToUnicodeMap(sourceFont, out var sourceMap, out string cmapDiagnostic))
            return Fail(
                $"Não foi possível ler o mapa ToUnicode da fonte CID {sourceFont.ResourceName}: {cmapDiagnostic}",
                out reason);

        if (!TryEncodeIdentityCidText(target.Text, sourceMap, out string encodedTarget, out reason))
            return false;

        var page = document.Pages[pageNumber];
        var boldCandidates = new List<Font>();
        if (!forceSyntheticBold && sourceWeight.Value < 600)
        {
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
                    !TryReadToUnicodeMap(candidate, out var candidateMap, out _))
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
        }

        if (boldCandidates.Count > 1)
            return Fail(
                "Há mais de uma fonte CID Bold compatível; a troca foi cancelada por ambiguidade.",
                out reason);

        Font? boldFont = boldCandidates.SingleOrDefault();
        bool syntheticBold = sourceWeight.Value < 600 && (forceSyntheticBold || boldFont == null);
        bool syntheticItalic = forceSyntheticItalic;
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

        // TJ.Text is a serialized CID byte string, but GlyphPositions is a
        // glyph array. Do not convert a UTF-16 string offset to a glyph index
        // by dividing by two: Aspose can expose glyph chunks whose Text length
        // differs from the assumed two-character width. Resolve both ends of
        // the selected byte range against actual glyph boundaries instead.
        int startGlyph = -1;
        int endGlyph = -1;
        int textOffset = 0;
        int selectedTextEnd = encodedOffset + encodedTarget.Length;
        for (int index = 0; index < glyphs.Length; index++)
        {
            int glyphStart = textOffset;
            textOffset += glyphs[index].Text?.Length ?? 0;
            int glyphEnd = textOffset;

            if (glyphStart == encodedOffset)
                startGlyph = index;
            if (glyphEnd == selectedTextEnd)
                endGlyph = index + 1;
        }

        if (startGlyph < 0 || endGlyph <= startGlyph || endGlyph > glyphs.Length)
            return Fail(
                $"O intervalo CID não coincide com limites de glifos do operador TJ " +
                $"(offset={encodedOffset}, comprimento={encodedTarget.Length}, " +
                $"texto={operatorText.Length}, glifos={glyphs.Length}).",
                out reason);

        int targetGlyphCount = endGlyph - startGlyph;
        GlyphPosition[] prefix = glyphs.Take(startGlyph).Select(CloneGlyph).ToArray();
        GlyphPosition[] selected = glyphs.Skip(startGlyph).Take(targetGlyphCount).Select(CloneGlyph).ToArray();
        GlyphPosition[] suffix = glyphs.Skip(endGlyph).Select(CloneGlyph).ToArray();

        if (!string.Equals(
                string.Concat(selected.Select(glyph => glyph.Text)),
                encodedTarget,
                StringComparison.Ordinal))
            return Fail("Os glifos selecionados não correspondem exatamente ao texto solicitado.", out reason);

        var replacementOperators = new List<Aspose.Pdf.Operator>(12);
        double strokeWidth = syntheticBold
            ? Math.Clamp(fontSize * 0.035, 0.20, 0.65)
            : 0;

        if (prefix.Length > 0)
            replacementOperators.Add(new SetGlyphsPositionShowText(prefix));

        if (syntheticBold || syntheticItalic)
            replacementOperators.Add(new GSave());

        if (syntheticItalic)
        {
            // Shear only the selected glyph painting through the current
            // transformation matrix. The text matrix and TJ advances remain
            // unchanged; q/Q restores the original page geometry afterwards.
            replacementOperators.Add(new ConcatenateMatrix(1, 0, 0.2126, 1, 0, 0));
        }

        if (foregroundOverride is { } requestedForeground)
        {
            replacementOperators.Add(new SetRGBColor(
                requestedForeground.R / 255d,
                requestedForeground.G / 255d,
                requestedForeground.B / 255d));
        }

        if (syntheticBold)
        {
            // Keep the original CID font and glyph codes. Fill-and-stroke paints
            // the same embedded glyph outlines with a small pen, producing a
            // synthetic bold without substituting Arial or rebuilding text.
            Aspose.Pdf.Color foreground = foregroundOverride ??
                segments[0].TextState.ForegroundColor ?? Aspose.Pdf.Color.FromRgb(0, 0, 0);
            replacementOperators.Add(new SetTextRenderingMode(2));
            replacementOperators.Add(new SetLineWidth(strokeWidth));
            replacementOperators.Add(new SetRGBColorStroke(
                foreground.R / 255d, foreground.G / 255d, foreground.B / 255d));
        }
        else if (boldFont != null)
        {
            replacementOperators.Add(new SelectFont(boldFont.ResourceName!, fontSize));
        }

        replacementOperators.Add(new SetGlyphsPositionShowText(selected));

        // Restore the graphics state and original face after the selected run;
        // adjacent glyphs retain their original appearance and positioning.
        if (syntheticBold || syntheticItalic)
            replacementOperators.Add(new GRestore());
        replacementOperators.Add(new SelectFont(sourceFont.ResourceName!, fontSize));
        if (suffix.Length > 0)
            replacementOperators.Add(new SetGlyphsPositionShowText(suffix));

        page.Contents.Delete(operatorIndex);
        page.Contents.Insert(operatorIndex, replacementOperators.ToArray());

        string boldDiagnostic = syntheticBold
            ? $"negrito sintético (traço {strokeWidth:0.###} pt)"
            : boldFont != null
                ? $"variante CID Bold {boldFont.ResourceName}"
                : "peso Bold original preservado";
        string italicDiagnostic = syntheticItalic
            ? "itálico sintético (inclinação 12°)"
            : "sem alteração de itálico";
        reason = $"Estilo CID aplicado: {boldDiagnostic}; {italicDiagnostic}; " +
                 $"{targetGlyphCount} glifos preservados e " +
                 $"{prefix.Length + suffix.Length} glifos adjacentes mantidos.";
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
        out Dictionary<ushort, string> map,
        out string diagnostic)
    {
        map = new Dictionary<ushort, string>();
        diagnostic = string.Empty;
        object? fontInfo = FindFontInfoOwner(font.iPdfFont);
        object? fontDictionary = ReadField(fontInfo, "_fontDict");
        object? reader = ReadField(fontInfo, "_reader");
        if (fontDictionary == null || reader == null)
        {
            diagnostic = $"fontInfo={fontInfo?.GetType().FullName ?? "null"}; " +
                $"fontDictionary={fontDictionary?.GetType().FullName ?? "null"}; " +
                $"reader={reader?.GetType().FullName ?? "null"}";
            return false;
        }

        object? entries = ReadField(fontDictionary, "_entries");
        if (entries is not IDictionary dictionary)
        {
            diagnostic = $"_entries não é IDictionary: {entries?.GetType().FullName ?? "null"}";
            return false;
        }

        object? rawReference = GetDictionaryEntry(entries, "ToUnicode");
        if (rawReference == null)
        {
            diagnostic = $"chave ToUnicode não localizada em _entries ({entries.GetType().FullName}); " +
                $"quantidade de entradas={dictionary.Count}";
            return false;
        }

        object? stream = Resolve(reader, rawReference);
        if (stream == null)
        {
            diagnostic = $"referência ToUnicode não resolvida; tipo bruto={rawReference.GetType().FullName}";
            return false;
        }

        byte[]? rawData = ReadDecodedStreamData(reader, stream);
        if (rawData == null || rawData.Length == 0)
        {
            diagnostic = $"RawData ausente/vazio; stream={stream.GetType().FullName}; " +
                $"referência={rawReference.GetType().FullName}";
            return false;
        }

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

        if (map.Count == 0)
        {
            diagnostic = $"CMap lido ({rawData.Length} bytes), mas nenhum par bfchar reconhecido; " +
                $"início={Encoding.ASCII.GetString(rawData, 0, Math.Min(100, rawData.Length))}";
            return false;
        }

        diagnostic = $"CMap válido: {map.Count} mapeamentos; {rawData.Length} bytes";
        return true;
    }

    private static bool HasIdentityCidToGidMap(Font font)
    {
        object? fontInfo = FindFontInfoOwner(font.iPdfFont);
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

    private static object? FindFontInfoOwner(object? root)
    {
        if (root == null)
            return null;

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<(object Value, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.Count > 0 && visited.Count < 500)
        {
            var (current, depth) = pending.Dequeue();
            if (!visited.Add(current))
                continue;

            if (ReadField(current, "_fontDict") != null &&
                ReadField(current, "_reader") != null)
                return current;

            if (depth >= 7)
                continue;

            Type type = current.GetType();
            const BindingFlags flags = BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly;

            for (Type? cursor = type; cursor != null && cursor != typeof(object); cursor = cursor.BaseType)
            {
                foreach (FieldInfo field in cursor.GetFields(flags))
                {
                    if (field.FieldType.IsPrimitive || field.FieldType.IsEnum ||
                        field.FieldType == typeof(string) || field.FieldType.IsPointer)
                        continue;

                    object? value;
                    try { value = field.GetValue(current); }
                    catch { continue; }

                    if (value == null || value is Type || value is Delegate ||
                        value is Stream || value is byte[])
                        continue;

                    if (value is IEnumerable enumerable)
                    {
                        int count = 0;
                        foreach (object? item in enumerable)
                        {
                            if (item != null && count++ < 100)
                                pending.Enqueue((item, depth + 1));
                        }
                    }
                    else
                    {
                        string? ns = value.GetType().Namespace;
                        if (ns?.StartsWith("Aspose.Pdf", StringComparison.Ordinal) == true ||
                            value.GetType().Name.Contains("Font", StringComparison.OrdinalIgnoreCase) ||
                            value.GetType().Name.Contains("Pdf", StringComparison.OrdinalIgnoreCase))
                            pending.Enqueue((value, depth + 1));
                    }
                }
            }
        }

        return null;
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

    private static byte[]? ReadDecodedStreamData(object reader, object stream)
    {
        // Aspose.PDF.FOSS keeps PdfReader internal, but exposes DecodeStream
        // as a public instance method on that reader. This applies the stream's
        // decryption and declared PDF filters before parsing the ToUnicode CMap.
        for (Type? type = reader.GetType(); type != null; type = type.BaseType)
        {
            MethodInfo? decodeMethod = type.GetMethods(
                    ReflectionFlags | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "DecodeStream", StringComparison.Ordinal))
                        return false;

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 3 &&
                           parameters[0].ParameterType.IsInstanceOfType(stream) &&
                           parameters[1].ParameterType == typeof(int) &&
                           parameters[2].ParameterType == typeof(int);
                });

            if (decodeMethod == null)
                continue;

            try
            {
                if (decodeMethod.Invoke(reader, new object[] { stream, 0, 0 }) is byte[] decoded &&
                    LooksLikeCMap(decoded))
                    return decoded;
            }
            catch
            {
                // Use the raw-data fallback only if it is already a textual CMap.
            }
        }

        return ReadRawStreamData(stream);
    }

    private static byte[]? ReadRawStreamData(object? stream)
    {
        if (stream == null)
            return null;

        // Aspose may expose the decoded PDF stream separately from RawData.
        // Prefer decoded members so Flate/LZW/ASCII85 filters are not parsed
        // as if their compressed bytes were a textual CMap.
        string[] decodedPropertyNames =
        {
            "DecodedData", "DecodedBytes", "DecodedStreamData", "Data"
        };

        foreach (string name in decodedPropertyNames)
        {
            object? value = ReadProperty(stream, name);
            if (TryConvertStreamValue(value, out byte[]? decoded) &&
                decoded != null && LooksLikeCMap(decoded))
                return decoded;
        }

        string[] decodeMethodNames =
        {
            "Decode", "DecodeStream", "GetDecodedData", "GetDecodedBytes",
            "Uncompress", "Decompress", "GetData"
        };

        for (Type? type = stream.GetType(); type != null; type = type.BaseType)
        {
            foreach (MethodInfo method in type.GetMethods(
                         ReflectionFlags | BindingFlags.DeclaredOnly))
            {
                if (!decodeMethodNames.Contains(method.Name, StringComparer.Ordinal) ||
                    method.GetParameters().Length != 0)
                    continue;

                try
                {
                    object? value = method.Invoke(stream, null);
                    if (TryConvertStreamValue(value, out byte[]? decoded) &&
                        decoded != null && LooksLikeCMap(decoded))
                        return decoded;

                    // Some stream APIs decode in place and return void/bool.
                    if (method.ReturnType == typeof(void) || method.ReturnType == typeof(bool))
                    {
                        foreach (string propertyName in decodedPropertyNames)
                        {
                            value = ReadProperty(stream, propertyName);
                            if (TryConvertStreamValue(value, out decoded) &&
                                decoded != null && LooksLikeCMap(decoded))
                                return decoded;
                        }
                    }
                }
                catch
                {
                    // Try the next compatible API member. The caller fails
                    // closed if no decoded CMap can be obtained.
                }
            }
        }

        byte[]? rawData = ReadRawData(stream);
        return rawData != null && LooksLikeCMap(rawData) ? rawData : null;
    }

    private static byte[]? ReadRawData(object stream)
    {
        PropertyInfo? property = stream.GetType().GetProperty("RawData", ReflectionFlags);
        if (property?.GetValue(stream) is byte[] data)
            return data;

        return ReadField(stream, "<RawData>k__BackingField") as byte[];
    }

    private static object? ReadProperty(object instance, string name)
    {
        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            PropertyInfo? property = type.GetProperty(
                name, ReflectionFlags | BindingFlags.DeclaredOnly);
            if (property == null || property.GetIndexParameters().Length != 0)
                continue;

            try { return property.GetValue(instance); }
            catch { }
        }

        return null;
    }

    private static bool TryConvertStreamValue(object? value, out byte[]? data)
    {
        switch (value)
        {
            case byte[] bytes when bytes.Length > 0:
                data = bytes;
                return true;
            case Stream stream:
                try
                {
                    using var copy = new MemoryStream();
                    if (stream.CanSeek)
                        stream.Position = 0;
                    stream.CopyTo(copy);
                    data = copy.ToArray();
                    return data.Length > 0;
                }
                catch
                {
                    data = null;
                    return false;
                }
            default:
                data = null;
                return false;
        }
    }

    private static bool LooksLikeCMap(byte[] data)
    {
        if (data.Length == 0)
            return false;

        string prefix = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 4096));
        return prefix.Contains("begincmap", StringComparison.Ordinal) ||
               prefix.Contains("beginbfchar", StringComparison.Ordinal) ||
               prefix.Contains("beginbfrange", StringComparison.Ordinal) ||
               prefix.Contains("begincodespacerange", StringComparison.Ordinal);
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
