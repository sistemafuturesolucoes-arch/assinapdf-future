using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class NfeXmlReaderAlphanumericCnpjTests
{
    [Fact]
    public void Parse_AuthorizedNfeWithAlphanumericCnpj_ValidatesCnpjAndAccessKey()
    {
        string fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "NfeAuthorized.Synthetic.xml");
        string xml = File.ReadAllText(fixture);

        const string oldKey = "42261011222333000181550010000000041123456787";
        const string newKey = "42261012ABC34501DE35550010000000041123456788";

        xml = xml.Replace(oldKey, newKey, StringComparison.Ordinal)
            .Replace(
                "<CNPJ>11222333000181</CNPJ>",
                "<CNPJ>12ABC34501DE35</CNPJ>",
                StringComparison.Ordinal);

        var result = NfeXmlReader.Parse(xml, "cnpj-alfanumerico.xml");

        Assert.Equal("12ABC34501DE35", result.IssuerDocument);
        Assert.Equal(newKey, result.AccessKey);
    }

    [Fact]
    public void NormalizeCnpj_PreservesLettersAndRemovesFormatting()
    {
        Assert.Equal(
            "12ABC34501DE35",
            NfeDistributionService.NormalizeCnpj("12.ABC.345/01DE-35"));
    }

    [Fact]
    public void Parse_NfeWithInvalidAlphanumericCnpjCheckDigits_IsRejected()
    {
        string fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "NfeAuthorized.Synthetic.xml");
        string xml = File.ReadAllText(fixture)
            .Replace(
                "42261011222333000181550010000000041123456787",
                "42261012ABC34501DE35550010000000041123456788",
                StringComparison.Ordinal)
            .Replace(
                "<CNPJ>11222333000181</CNPJ>",
                "<CNPJ>12ABC34501DE34</CNPJ>",
                StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => NfeXmlReader.Parse(xml));
        Assert.Contains("CNPJ", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
