using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class OfficeToPdfServiceTests
{
    [Theory]
    [InlineData("contrato.doc", true, "Word")]
    [InlineData("contrato.docx", true, "Word")]
    [InlineData("contrato.odt", true, "Word")]
    [InlineData("contrato.rtf", true, "Word")]
    [InlineData("planilha.xls", true, "Excel")]
    [InlineData("planilha.xlsx", true, "Excel")]
    [InlineData("planilha.xlsm", true, "Excel")]
    [InlineData("planilha.ods", true, "Excel")]
    [InlineData("dados.csv", true, "Excel")]
    [InlineData("arquivo.pdf", false, null)]
    [InlineData("imagem.png", false, null)]
    public void Identifies_supported_office_formats(string path, bool expected, string? kind)
    {
        Assert.Equal(expected, OfficeToPdfService.IsSupported(path));
        if (expected)
            Assert.Equal(kind, OfficeToPdfService.GetDocumentKind(path));
        else
            Assert.Throws<NotSupportedException>(() => OfficeToPdfService.GetDocumentKind(path));
    }
}
