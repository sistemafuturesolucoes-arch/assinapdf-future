using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Aspose.Pdf;
using Aspose.Pdf.Operators;
using Aspose.Pdf.Text;
using AssinaPDF.MacPrototype;
using Xunit;
using Xunit.Abstractions;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class CidFontOperatorFormatterTests
{
    private readonly ITestOutputHelper _output;

    public CidFontOperatorFormatterTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReuseEmbeddedCidBoldVariantAndPersistAfterReopen()
    {
        string? inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("POC ignorada: defina ASSINAPDF_CID_TEST_PDF.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF não encontrado: {inputPath}");
        const string targetText = "Informações de Cheques Sem Fundos do Banco Central";
        string outputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_SERVICE_OUTPUT")
            ?? inputPath + ".cid-bold-service-poc.pdf";
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        using (var document = new Document(inputPath))
        {
            var absorber = new TextFragmentAbsorber(targetText);
            document.Pages.Accept(absorber);
            var fragment = absorber.TextFragments.Cast<TextFragment>().Single();
            int pageNumber = checked((int)fragment.Page.Number);
            string? sourceResource = fragment.Segments
                .Cast<TextSegment>()
                .Select(segment => segment.TextState.Font?.ResourceName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

            var formatterType = typeof(MainWindow).Assembly.GetType(
                "AssinaPDF.MacPrototype.Services.CidFontOperatorFormatter");
            Assert.NotNull(formatterType);
            MethodInfo? method = formatterType!.GetMethod(
                "TryApplyBoldVariant",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object?[] arguments = { document, pageNumber, fragment, null };
            bool applied = (bool)method!.Invoke(null, arguments)!;
            string reason = arguments[3]?.ToString() ?? "(sem diagnóstico)";
            _output.WriteLine($"SOURCE={sourceResource}; RESULT={applied}; DETAIL={reason}");
            Assert.True(applied, reason);

            document.Save(outputPath);
        }

        Assert.True(File.Exists(outputPath), $"PDF de saída não criado: {outputPath}");
        using var reopened = new Document(outputPath);
        var verifyAbsorber = new TextFragmentAbsorber(targetText);
        reopened.Pages.Accept(verifyAbsorber);
        var savedFragment = verifyAbsorber.TextFragments.Cast<TextFragment>().Single();
        var savedSegments = savedFragment.Segments.Cast<TextSegment>().ToArray();

        Assert.Equal(targetText, savedFragment.Text);
        Assert.NotEmpty(savedSegments);
        Assert.All(savedSegments, segment =>
        {
            var font = segment.TextState.Font;
            Assert.NotNull(font);
            Assert.NotEqual("F2", font!.ResourceName);
            Assert.True(ReadEmbeddedTrueTypeWeight(font) >= 600,
                $"A fonte salva não é Bold: recurso={font.ResourceName}; peso={ReadEmbeddedTrueTypeWeight(font)}.");
        });

        _output.WriteLine($"OUTPUT={outputPath}");
        _output.WriteLine($"REOPEN_TEXT='{savedFragment.Text}'; SEGMENTS={savedSegments.Length}");
        _output.WriteLine("RESULTADO: a variante CID Bold permaneceu após salvar e reabrir.");
    }

    [Fact]
    public void SyntheticBoldFallbackKeepsCidFontAndPersistsAfterReopen()
    {
        string? inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("POC ignorada: defina ASSINAPDF_CID_TEST_PDF.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF não encontrado: {inputPath}");
        const string targetText = "Informações de Cheques Sem Fundos do Banco Central";
        string outputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_SYNTHETIC_OUTPUT")
            ?? inputPath + ".cid-synthetic-bold-poc.pdf";
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        int pageNumber;
        using (var document = new Document(inputPath))
        {
            var absorber = new TextFragmentAbsorber(targetText);
            document.Pages.Accept(absorber);
            var fragment = absorber.TextFragments.Cast<TextFragment>().Single();
            pageNumber = checked((int)fragment.Page.Number);

            var formatterType = typeof(MainWindow).Assembly.GetType(
                "AssinaPDF.MacPrototype.Services.CidFontOperatorFormatter");
            Assert.NotNull(formatterType);
            MethodInfo? method = formatterType!.GetMethod(
                "TryApplySyntheticBoldFallback",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object?[] arguments = { document, pageNumber, fragment, null };
            bool applied = (bool)method!.Invoke(null, arguments)!;
            string reason = arguments[3]?.ToString() ?? "(sem diagnóstico)";
            _output.WriteLine($"SYNTHETIC CID BOLD: RESULT={applied}; DETAIL={reason}");
            Assert.True(applied, reason);

            document.Save(outputPath);
        }

        Assert.True(File.Exists(outputPath), $"PDF de saída não criado: {outputPath}");
        using var reopened = new Document(outputPath);
        var verifyAbsorber = new TextFragmentAbsorber(targetText);
        reopened.Pages.Accept(verifyAbsorber);
        var savedFragment = verifyAbsorber.TextFragments.Cast<TextFragment>().Single();
        var savedSegments = savedFragment.Segments.Cast<TextSegment>().ToArray();

        Assert.Equal(targetText, savedFragment.Text);
        Assert.NotEmpty(savedSegments);
        Assert.All(savedSegments, segment =>
        {
            var font = segment.TextState.Font;
            Assert.NotNull(font);
            Assert.True(font!.IsCid, "A fonte CID original foi substituída.");
            Assert.True(font.IsEmbedded, "A fonte CID deixou de estar incorporada.");
            Assert.Equal(
                TextRenderingMode.FillThenStrokeText,
                segment.TextState.RenderingMode);
        });

        var operators = reopened.Pages[pageNumber].Contents.Cast<Aspose.Pdf.Operator>().ToArray();
        Assert.Contains(operators, operation =>
            operation is SetTextRenderingMode mode && mode.RenderingMode == 2);
        Assert.Contains(operators, operation =>
            operation is SetLineWidth width && width.LineWidth > 0);
        Assert.Contains(operators, operation => operation is GSave);
        Assert.Contains(operators, operation => operation is GRestore);

        _output.WriteLine($"OUTPUT={outputPath}");
        _output.WriteLine($"REOPEN_TEXT='{savedFragment.Text}'; SEGMENTS={savedSegments.Length}");
        _output.WriteLine("RESULTADO: o negrito sintético permaneceu após salvar e reabrir sem substituir a fonte CID.");
    }

    [Fact]
    public void SyntheticItalicFallbackKeepsCidFontAndPersistsAfterReopen()
    {
        string? inputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_TEST_PDF");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            _output.WriteLine("POC ignorada: defina ASSINAPDF_CID_TEST_PDF.");
            return;
        }

        Assert.True(File.Exists(inputPath), $"PDF não encontrado: {inputPath}");
        const string targetText = "Informações de Cheques Sem Fundos do Banco Central";
        string outputPath = Environment.GetEnvironmentVariable("ASSINAPDF_CID_ITALIC_OUTPUT")
            ?? inputPath + ".cid-synthetic-italic-poc.pdf";
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        int pageNumber;
        using (var document = new Document(inputPath))
        {
            var absorber = new TextFragmentAbsorber(targetText);
            document.Pages.Accept(absorber);
            var fragment = absorber.TextFragments.Cast<TextFragment>().Single();
            pageNumber = checked((int)fragment.Page.Number);

            var formatterType = typeof(MainWindow).Assembly.GetType(
                "AssinaPDF.MacPrototype.Services.CidFontOperatorFormatter");
            Assert.NotNull(formatterType);
            MethodInfo? method = formatterType!.GetMethod(
                "TryApplySyntheticItalicFallback",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object?[] arguments = { document, pageNumber, fragment, Aspose.Pdf.Color.FromRgb(1, 0, 0), null };
            bool applied = (bool)method!.Invoke(null, arguments)!;
            string reason = arguments[4]?.ToString() ?? "(sem diagnóstico)";
            _output.WriteLine($"SYNTHETIC CID ITALIC: RESULT={applied}; DETAIL={reason}");
            Assert.True(applied, reason);

            document.Save(outputPath);
        }

        Assert.True(File.Exists(outputPath), $"PDF de saída não criado: {outputPath}");
        using var reopened = new Document(outputPath);
        var verifyAbsorber = new TextFragmentAbsorber(targetText);
        reopened.Pages.Accept(verifyAbsorber);
        var savedFragment = verifyAbsorber.TextFragments.Cast<TextFragment>().Single();
        var savedSegments = savedFragment.Segments.Cast<TextSegment>().ToArray();

        Assert.Equal(targetText, savedFragment.Text);
        Assert.NotEmpty(savedSegments);
        Assert.All(savedSegments, segment =>
        {
            var font = segment.TextState.Font;
            Assert.NotNull(font);
            Assert.True(font!.IsCid, "A fonte CID original foi substituída.");
            Assert.True(font.IsEmbedded, "A fonte CID deixou de estar incorporada.");
        });

        var operators = reopened.Pages[pageNumber].Contents.Cast<Aspose.Pdf.Operator>().ToArray();
        Assert.Contains(operators, operation => operation is GSave);
        Assert.Contains(operators, operation => operation is GRestore);
        Assert.Contains(operators, operation =>
            operation is ConcatenateMatrix matrix &&
            matrix.Matrix.C >= 0.05 &&
            Math.Abs(matrix.Matrix.B) < 0.0001);
        Assert.Contains(operators, operation =>
            operation is SetRGBColor color &&
            color.R > 0.99 && color.G < 0.01 && color.B < 0.01);

        _output.WriteLine($"OUTPUT={outputPath}");
        _output.WriteLine($"REOPEN_TEXT='{savedFragment.Text}'; SEGMENTS={savedSegments.Length}");
        _output.WriteLine("RESULTADO: o itálico sintético permaneceu como transformação local sem substituir a fonte CID.");
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

        int tableCount = ReadUInt16(data, 4);
        for (int index = 0; index < tableCount; index++)
        {
            int recordOffset = 12 + (index * 16);
            if (recordOffset + 16 > data.Length)
                return null;

            if (!string.Equals(
                    Encoding.ASCII.GetString(data, recordOffset, 4),
                    "OS/2",
                    StringComparison.Ordinal))
                continue;

            uint tableOffset = ReadUInt32(data, recordOffset + 8);
            uint tableLength = ReadUInt32(data, recordOffset + 12);
            if (tableLength < 6 || tableOffset + 6 > data.Length)
                return null;

            return ReadUInt16(data, checked((int)tableOffset + 4));
        }

        return null;
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        checked((ushort)((data[offset] << 8) | data[offset + 1]));

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];
}
