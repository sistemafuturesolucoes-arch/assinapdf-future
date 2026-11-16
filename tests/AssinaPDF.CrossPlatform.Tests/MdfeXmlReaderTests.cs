using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public class MdfeXmlReaderTests
{
    private const string AccessKey = "43261112345678000123580010000000011000000009";

    private static string AuthorizedXml() => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <mdfeProc versao="3.00" xmlns="http://www.portalfiscal.inf.br/mdfe">
          <MDFe>
            <infMDFe Id="MDFe{{AccessKey}}" versao="3.00">
              <ide>
                <cUF>43</cUF><dhEmi>2026-11-16T08:30:00-03:00</dhEmi>
                <mod>58</mod><serie>1</serie><nMDF>1</nMDF><tpAmb>1</tpAmb>
                <modal>1</modal><UFIni>RS</UFIni><UFFim>SC</UFFim>
              </ide>
              <emit><CNPJ>12345678000123</CNPJ><xNome>TRANSPORTADORA TESTE LTDA</xNome></emit>
              <infModal versaoModal="3.00">
                <rodo><veicTracao><cInt>01</cInt><placa>ABC1D23</placa><RENAVAM>12345678901</RENAVAM><UF>RS</UF>
                    <condutor><xNome>Motorista Teste</xNome><CPF>12345678901</CPF></condutor>
                  </veicTracao>
                </rodo>
              </infModal>
              <tot><qCTe>2</qCTe><qNFe>1</qNFe><vCarga>12500.50</vCarga><cUnid>01</cUnid><qCarga>850.0000</qCarga></tot>
              <infDoc>
                <infMunDescarga><cMunDescarga>4305108</cMunDescarga><xMunDescarga>Caxias do Sul</xMunDescarga>
                  <infCTe><chCTe>00000000000000000000000000000000000000000000</chCTe></infCTe>
                  <infCTe><chCTe>00000000000000000000000000000000000000000000</chCTe></infCTe>
                  <infNFe><chNFe>00000000000000000000000000000000000000000000</chNFe></infNFe>
                </infMunDescarga>
              </infDoc>
            </infMDFe>
          </MDFe>
          <protMDFe versao="3.00"><infProt><tpAmb>1</tpAmb><chMDFe>{{AccessKey}}</chMDFe>
            <nProt>143260000123456</nProt><dhRecbto>2026-11-16T08:30:02-03:00</dhRecbto>
            <cStat>100</cStat><xMotivo>Autorizado o uso do MDF-e</xMotivo>
          </infProt></protMDFe>
        </mdfeProc>
        """;

    [Fact]
    public void Parse_AuthorizedMdfe_ExtractsIssuerRouteVehicleAndTotals()
    {
        var result = MdfeXmlReader.Parse(AuthorizedXml(), "mdfe.xml");

        Assert.Equal("58", result.Model);
        Assert.Equal(AccessKey, result.AccessKey);
        Assert.Equal("1", result.DocumentNumber);
        Assert.Equal("TRANSPORTADORA TESTE LTDA", result.IssuerName);
        Assert.Equal("RS", result.OriginState);
        Assert.Equal("SC", result.DestinationState);
        Assert.Equal("ABC1D23", result.VehiclePlate);
        Assert.Equal("Motorista Teste", result.DriverName);
        Assert.Equal(12500.50m, result.CargoValue);
        Assert.Equal(850.0000m, result.CargoQuantity);
        Assert.Equal(2, result.CteCount);
        Assert.Equal(1, result.NfeCount);
        Assert.Equal("Produção", result.Environment);
    }

    [Fact]
    public void Parse_NonAuthorizedMdfe_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() => MdfeXmlReader.Parse(
            AuthorizedXml().Replace("<cStat>100</cStat>", "<cStat>110</cStat>")));
        Assert.Contains("não autorizado", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_WrongModel_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() => MdfeXmlReader.Parse(
            AuthorizedXml().Replace("<mod>58</mod>", "<mod>57</mod>")));
        Assert.Contains("MDF-e modelo 58", exception.Message);
    }

    [Fact]
    public void Parse_InvalidAccessKey_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() => MdfeXmlReader.Parse(
            AuthorizedXml().Replace(AccessKey, "43261112345678000123580010000000011000000000")));
        Assert.Contains("dígito verificador", exception.Message);
    }


    [Fact]
    public void Parse_AlphanumericCnpjAccessKey_UsesAsciiMinus48ForCheckDigit()
    {
        const string alphanumericKey = "43261112ABC34501DE35580010000000011000000000";
        const string alphanumericCnpj = "12ABC34501DE35";
        var xml = AuthorizedXml()
            .Replace(AccessKey, alphanumericKey, StringComparison.Ordinal)
            .Replace("12345678000123", alphanumericCnpj, StringComparison.Ordinal);

        var result = MdfeXmlReader.Parse(xml, "mdfe-cnpj-alfa.xml");

        Assert.Equal(alphanumericKey, result.AccessKey);
        Assert.Equal(alphanumericCnpj, result.IssuerDocument);
    }


    [Fact]
    public void Generate_ProducesReadablePdfWithOfficialBarcodeAndQrAreas()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AssinaPDF-Mdfe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string xmlPath = Path.Combine(directory, "mdfe.xml");
        string pdfPath = Path.Combine(directory, "damdfe.pdf");
        try
        {
            string xml = AuthorizedXml().Replace(
                "</MDFe>",
                "<infMDFeSupl><qrCodMDFe>https://dfe-portal.svrs.rs.gov.br/MDFe/Consulta</qrCodMDFe></infMDFeSupl></MDFe>",
                StringComparison.Ordinal);
            File.WriteAllText(xmlPath, xml);
            var summary = MdfeXmlReader.ReadFile(xmlPath);

            MdfeDamDfeGenerator.Generate(summary, pdfPath);

            Assert.True(File.Exists(pdfPath));
            Assert.True(new FileInfo(pdfPath).Length > 1_000);
            using var stream = File.OpenRead(pdfPath);
            Span<byte> header = stackalloc byte[5];
            Assert.Equal(5, stream.Read(header));
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(header));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    public void Parse_Dtd_IsRejected()
    {
        const string xml = """<!DOCTYPE mdfeProc [<!ENTITY x "unsafe">]><mdfeProc xmlns="http://www.portalfiscal.inf.br/mdfe" versao="3.00">&x;</mdfeProc>""";
        Assert.Throws<System.Xml.XmlException>(() => MdfeXmlReader.Parse(xml));
    }
}