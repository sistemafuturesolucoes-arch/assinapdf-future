using System;
using System.IO;
using System.Linq;
using System.Text;
using Aspose.Pdf;
using Aspose.Pdf.Text;
using Xunit;
using Xunit.Abstractions;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class PdfCidBoldReplacementPocTests
{
    private readonly ITestOutputHelper _output;

    public PdfCidBoldReplacementPocTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SetEachTextSegmentFontToExplicitBold_PersistsAfterSaveAndReopen()
    {
        // Opt-in: define ASSINAPDF_CID_TEST_PDF to run against the real sample PDF.
        var inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("POC ignorada: defina ASSINAPDF_CID_TEST_PDF com o caminho do PDF de teste.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF de teste não encontrado: {inputPath}");

        const string targetText = "Informações de Cheques Sem Fundos do Banco Central";
        var outputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(
                Path.GetDirectoryName(inputPath)!,
                Path.GetFileNameWithoutExtension(inputPath) + ".bold-segments-poc.pdf");
        }

        Assert.NotEqual(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath));

        using (var sourceDocument = new Document(inputPath))
        {
            var absorber = new TextFragmentAbsorber(targetText);
            sourceDocument.Pages.Accept(absorber);

            var sourceFragment = absorber.TextFragments.Cast<TextFragment>().FirstOrDefault();
            Assert.NotNull(sourceFragment);

            var sourceSegments = sourceFragment!.Segments.Cast<TextSegment>().ToArray();
            Assert.NotEmpty(sourceSegments);

            _output.WriteLine(
                $"ANTES: página={sourceFragment.Page.Number}; texto='{sourceFragment.Text}'; " +
                $"segmentos={sourceSegments.Length}; fonte do fragmento='{sourceFragment.TextState.Font?.FontName}'");

            for (var index = 0; index < sourceSegments.Length; index++)
            {
                var segment = sourceSegments[index];
                var weight = ReadEmbeddedTrueTypeWeight(segment.TextState.Font);
                _output.WriteLine(
                    $"SEGMENTO ANTES[{index + 1}]: texto='{segment.Text}'; " +
                    $"fonte='{segment.TextState.Font?.FontName}'; recurso='{segment.TextState.Font?.ResourceName}'; " +
                    $"IsBold={segment.TextState.IsBold}; peso={weight?.ToString() ?? "indisponível"}");

                Assert.True(
                    weight is >= 350 and <= 500,
                    $"O segmento {index + 1} não parece regular. Peso TTF: {weight?.ToString() ?? "indisponível"}.");
            }

            var boldFont = FontRepository.FindFont("Arial", FontStyles.Bold);
            Assert.NotNull(boldFont);
            boldFont.IsEmbedded = true;

            // Change each physical text run independently. TextFragment-level
            // changes can rebuild/merge the underlying segment collection.
            foreach (var segment in sourceSegments)
            {
                segment.TextState.Font = boldFont;
                segment.TextState.FontStyle = FontStyles.Bold;
                segment.TextState.IsBold = true;
            }

            for (var index = 0; index < sourceSegments.Length; index++)
            {
                var segment = sourceSegments[index];
                _output.WriteLine(
                    $"APLICADO[{index + 1}]: texto='{segment.Text}'; fonte='{segment.TextState.Font?.FontName}'; " +
                    $"recurso='{segment.TextState.Font?.ResourceName}'; IsBold={segment.TextState.IsBold}; " +
                    $"peso={ReadEmbeddedTrueTypeWeight(segment.TextState.Font)?.ToString() ?? "indisponível"}");
            }

            sourceDocument.Save(outputPath);
        }

        Assert.True(File.Exists(outputPath), $"O PDF de saída não foi criado: {outputPath}");

        using (var savedDocument = new Document(outputPath))
        {
            var savedAbsorber = new TextFragmentAbsorber(targetText);
            savedDocument.Pages.Accept(savedAbsorber);

            var savedFragment = savedAbsorber.TextFragments.Cast<TextFragment>().FirstOrDefault();
            Assert.NotNull(savedFragment);

            var savedSegments = savedFragment!.Segments.Cast<TextSegment>().ToArray();
            Assert.NotEmpty(savedSegments);

            _output.WriteLine(
                $"DEPOIS: arquivo='{outputPath}'; página={savedFragment.Page.Number}; " +
                $"texto='{savedFragment.Text}'; segmentos={savedSegments.Length}");

            var savedWeights = savedSegments
                .Select((segment, index) =>
                {
                    var weight = ReadEmbeddedTrueTypeWeight(segment.TextState.Font);
                    _output.WriteLine(
                        $"SEGMENTO DEPOIS[{index + 1}]: texto='{segment.Text}'; " +
                        $"fonte='{segment.TextState.Font?.FontName}'; recurso='{segment.TextState.Font?.ResourceName}'; " +
                        $"IsBold={segment.TextState.IsBold}; Font.IsBold={segment.TextState.Font?.IsBold}; " +
                        $"embedded={segment.TextState.Font?.IsEmbedded}; peso={weight?.ToString() ?? "indisponível"}");
                    return weight;
                })
                .ToArray();

            Assert.Equal(targetText, savedFragment.Text);
            Assert.All(
                savedWeights,
                weight => Assert.True(
                    weight >= 600,
                    $"O PDF foi salvo/reaberto, mas ao menos um segmento não usa fonte Bold. Peso TTF OS/2: {weight?.ToString() ?? "indisponível"}."));
        }

        _output.WriteLine("POC aprovada: a fonte Bold permaneceu nos segmentos após salvar e reabrir o PDF.");
    }

    [Fact]
    public void DumpCidPageContentOperatorsForBoldDiagnosis()
    {
        // Read-only diagnostic: dumps the first page content operators without modifying the PDF.
        var inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("Diagnóstico ignorado: defina ASSINAPDF_CID_TEST_PDF com o caminho do PDF.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF de teste não encontrado: {inputPath}");

        using var document = new Document(inputPath);
        var page = document.Pages[1];
        _output.WriteLine($"PDF={inputPath}");
        _output.WriteLine($"PÁGINA=1; OPERADORES={page.Contents.Count}");

        var index = 0;
        foreach (var op in page.Contents)
        {
            index++;
            _output.WriteLine($"OP[{index:D4}] TYPE={op.GetType().FullName} :: {op}");
        }

        _output.WriteLine("FIM DO DUMP DE OPERADORES (nenhuma alteração foi feita no PDF).");
    }

    [Fact]
    public void DumpCidTextRunsWithFontAndTextMatrix()
    {
        var inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("Diagnóstico ignorado: defina ASSINAPDF_CID_TEST_PDF.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF de teste não encontrado: {inputPath}");

        using var document = new Document(inputPath);
        var absorber = new TextFragmentAbsorber("Informações de Cheques Sem Fundos do Banco Central");
        document.Pages.Accept(absorber);
        var fragment = absorber.TextFragments.Cast<TextFragment>().FirstOrDefault();
        Assert.NotNull(fragment);

        var rect = fragment!.Rectangle;
        _output.WriteLine($"ALVO: página={fragment.Page.Number}; texto='{fragment.Text}'; " +
            $"retângulo=({rect.LLX:F3},{rect.LLY:F3})-({rect.URX:F3},{rect.URY:F3})");

        var page = document.Pages[fragment.Page.Number];
        var selectedFont = "(fonte não identificada)";
        var textMatrix = "(matriz não identificada)";
        var index = 0;

        foreach (var op in page.Contents)
        {
            index++;
            var typeName = op.GetType().Name;
            var value = op.ToString();

            if (typeName.Contains("SelectFont", StringComparison.Ordinal))
                selectedFont = value;
            else if (typeName.Contains("SetTextMatrix", StringComparison.Ordinal))
                textMatrix = value;
            else if (typeName.Contains("SetGlyphsPositionShowText", StringComparison.Ordinal))
                _output.WriteLine($"RUN OP[{index:D4}] FONT={selectedFont} MATRIX={textMatrix} TEXT={value}");
        }

        _output.WriteLine("FIM DOS RUNS DE TEXTO (somente leitura; PDF não foi alterado).");
    }


    [Fact]
    public void DumpCidFontResourceMetadataForF1AndF2()
    {
        var inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("Diagnóstico ignorado: defina ASSINAPDF_CID_TEST_PDF.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF de teste não encontrado: {inputPath}");

        using var document = new Document(inputPath);
        var page = document.Pages[1];
        foreach (var font in page.Resources.Fonts
                     .Where(font => font.ResourceName is "F1" or "F2")
                     .OrderBy(font => font.ResourceName))
        {
            using var stream = new MemoryStream();
            font.Save(stream);
            var bytes = stream.ToArray();
            var hash = bytes.Length == 0 ? "vazio" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

            _output.WriteLine(
                $"FONT {font.ResourceName}: BaseFont={font.BaseFont}; FontName={font.FontName}; " +
                $"Decoded={font.DecodedFontName}; Subtype={font.Subtype}; Encoding={font.Encoding}; " +
                $"CID={font.IsCid}; Embedded={font.IsEmbedded}; Subset={font.IsSubset}; " +
                $"Bold={font.IsBold}; Italic={font.IsItalic}; Bytes={bytes.Length}; SHA256={hash}; " +
                $"Weight={ReadEmbeddedTrueTypeWeight(font)?.ToString() ?? "indisponível"}");

            DumpPublicScalarProperties(font.iPdfFont, $"  iPdfFont {font.ResourceName}");
            DumpPublicScalarProperties(font.FontOptions, $"  FontOptions {font.ResourceName}");
            DumpReflectionMembers(font, $"  Font internals {font.ResourceName}", 0);
            DumpPdfObjectTree(font, $"  PDF dictionary {font.ResourceName}", 0, new System.Collections.Generic.HashSet<object>(ReferenceEqualityComparer.Instance));
        }
    }

    private void DumpPublicScalarProperties(object? instance, string label)
    {
        if (instance == null)
        {
            _output.WriteLine($"{label}: null");
            return;
        }

        var type = instance.GetType();
        _output.WriteLine($"{label}: {type.FullName}");
        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                     .Where(property => property.GetIndexParameters().Length == 0)
                     .OrderBy(property => property.Name))
        {
            try
            {
                var value = property.GetValue(instance);
                if (value == null)
                    continue;

                var valueType = value.GetType();
                if (value is string || valueType.IsPrimitive || valueType.IsEnum || value is decimal)
                    _output.WriteLine($"    {property.Name}={value}");
                else
                    _output.WriteLine($"    {property.Name}=<{valueType.FullName}>");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"    {property.Name}=<erro:{ex.GetType().Name}>");
            }
        }
    }

    private void DumpReflectionMembers(object? instance, string label, int depth)
    {
        if (instance == null || depth > 2)
            return;

        var type = instance.GetType();
        _output.WriteLine($"{label}: {type.FullName}");

        var flags = System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly;

        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(flags).OrderBy(field => field.Name).Take(40))
            {
                try
                {
                    var value = field.GetValue(instance);
                    if (value == null)
                        continue;

                    var valueType = value.GetType();
                    if (value is string || valueType.IsPrimitive || valueType.IsEnum || value is decimal)
                    {
                        _output.WriteLine($"    FIELD {current.Name}.{field.Name}={value}");
                    }
                    else
                    {
                        _output.WriteLine($"    FIELD {current.Name}.{field.Name}=<{valueType.FullName}>");
                        if (depth < 2 && !valueType.IsArray &&
                            !typeof(System.Collections.IEnumerable).IsAssignableFrom(valueType))
                        {
                            DumpReflectionMembers(value, $"      {field.Name}", depth + 1);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"    FIELD {current.Name}.{field.Name}=<erro:{ex.GetType().Name}>");
                }
            }
        }
    }

    private void DumpPdfObjectTree(object? instance, string label, int depth, System.Collections.Generic.HashSet<object> visited)
    {
        if (instance == null || depth > 4 || !visited.Add(instance))
            return;

        var type = instance.GetType();
        _output.WriteLine($"{label}: {type.FullName}");

        if (instance is System.Collections.IDictionary dictionary)
        {
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? "(null)";
                var value = entry.Value;
                if (value == null)
                {
                    _output.WriteLine($"{new string(' ', (depth + 1) * 2)}{key}=null");
                    continue;
                }

                var valueType = value.GetType();
                var scalar = value is string || valueType.IsPrimitive || valueType.IsEnum || value is decimal;
                _output.WriteLine($"{new string(' ', (depth + 1) * 2)}{key}: {(scalar ? value : valueType.FullName + " :: " + value)}");
                if (!scalar)
                    DumpPdfObjectTree(value, key, depth + 1, visited);
            }
            return;
        }

        if (instance is System.Collections.IEnumerable enumerable && instance is not string)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                if (index >= 80) { _output.WriteLine($"{new string(' ', (depth + 1) * 2)}..."); break; }
                DumpPdfObjectTree(item, $"[{index++}]", depth + 1, visited);
            }
            return;
        }

        var flags = System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;
        var entriesField = type.GetFields(flags).FirstOrDefault(field => field.Name == "_entries");
        if (entriesField?.GetValue(instance) is System.Collections.IDictionary entries)
        {
            DumpPdfObjectTree(entries, "_entries", depth + 1, visited);
            return;
        }

        if (type.Name.Contains("PdfArray", StringComparison.Ordinal))
        {
            foreach (var property in type.GetProperties(flags).Where(property =>
                         property.GetIndexParameters().Length == 0 && property.Name is "Count" or "Length"))
            {
                try { _output.WriteLine($"{new string(' ', (depth + 1) * 2)}{property.Name}={property.GetValue(instance)}"); }
                catch { }
            }
        }
    }

    private static ushort? ReadEmbeddedTrueTypeWeight(Font? font)
    {
        if (font == null)
            return null;

        using var stream = new MemoryStream();
        font.Save(stream);
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
}
