using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class NfeDanfeGeneratorTests
{
    private static string GetFixturePath()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "NfeAuthorized.Synthetic.xml");

        Assert.True(File.Exists(path),
            $"Fixture sintética não encontrada: {path}");

        return path;
    }

    [Fact]
    public void Generate_CreatesPdfForAuthorizedInvoice()
    {
        var invoice = NfeXmlReader.ReadFile(GetFixturePath());

        string path = Path.Combine(
            Path.GetTempPath(),
            $"danfe-{Guid.NewGuid():N}.pdf");

        try
        {
            NfeDanfeGenerator.Generate(invoice, path);

            Assert.True(File.Exists(path));

            byte[] header = File.ReadAllBytes(path)
                .Take(5)
                .ToArray();

            Assert.Equal("%PDF-"u8.ToArray(), header);
            Assert.True(new FileInfo(path).Length > 1000);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Generate_RejectsInvalidAccessKey()
    {
        var invoice = NfeXmlReader.ReadFile(GetFixturePath())
            with { AccessKey = "123" };

        string path = Path.Combine(
            Path.GetTempPath(),
            $"invalid-danfe-{Guid.NewGuid():N}.pdf");

        Assert.Throws<InvalidDataException>(() =>
            NfeDanfeGenerator.Generate(invoice, path));

        Assert.False(File.Exists(path));
    }
}