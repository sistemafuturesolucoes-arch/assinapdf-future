using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class NfeLocalXmlSearchServiceTests
{
    [Fact]
    public void FindIssued_FiltersByIssuerCnpj_AndDoesNotReturnRecipientInvoices()
    {
        string fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "NfeAuthorized.Synthetic.xml");
        Assert.True(File.Exists(fixture), "A fixture sintética de NF-e não foi copiada para a saída dos testes.");

        var source = NfeXmlReader.ReadFile(fixture);
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "AssinaPDF-NfeSearch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            File.Copy(fixture, Path.Combine(temporaryDirectory, "emitida.xml"));

            var issued = NfeLocalXmlSearchService.FindIssued(
                source.IssuerDocument,
                new[] { temporaryDirectory });

            var notIssued = NfeLocalXmlSearchService.FindIssued(
                source.RecipientDocument,
                new[] { temporaryDirectory });

            Assert.Single(issued.Documents);
            Assert.Equal(source.AccessKey, issued.Documents[0].AccessKey);
            Assert.Empty(notIssued.Documents);
            Assert.Equal(1, issued.FilesExamined);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("12.345.678/0001-90", "12345678000190")]
    [InlineData("12 345 678 0001 90", "12345678000190")]
    public void NormalizeDigits_RemovesCnpjFormatting(string input, string expected)
    {
        Assert.Equal(expected, NfeDistributionService.NormalizeDigits(input));
    }

    [Fact]
    public void FindIssued_RejectsInvalidCnpj()
    {
        Assert.Throws<ArgumentException>(() =>
            NfeLocalXmlSearchService.FindIssued("123", Array.Empty<string>()));
    }
}
