using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class CertificateCnpjExtractorTests
{
    [Fact]
    public void Extract_CompanyNameColonNumericCnpj_ReturnsCnpj()
    {
        const string subject = "CN=POLICARPO E OLIVEIRA VIANDAS LTDA:21579429000155";
        Assert.Equal("21579429000155", CertificateCnpjExtractor.Extract(subject));
    }

    [Fact]
    public void Extract_SerialNumberCnpj_ReturnsCnpj()
    {
        const string subject = "CN=EMPRESA LTDA, SERIALNUMBER=21579429000155";
        Assert.Equal("21579429000155", CertificateCnpjExtractor.Extract(subject));
    }

    [Fact]
    public void Extract_AlphanumericCnpj_ReturnsCnpj()
    {
        const string subject = "CN=EMPRESA LTDA:12ABC34501DE35";
        Assert.Equal("12ABC34501DE35", CertificateCnpjExtractor.Extract(subject));
    }

    [Fact]
    public void Extract_InvalidCnpj_ReturnsEmpty()
    {
        const string subject = "CN=EMPRESA LTDA:21579429000154";
        Assert.Equal(string.Empty, CertificateCnpjExtractor.Extract(subject));
    }
}
