using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public class CteOsXmlReaderTests
{
    private const string AccessKey = "42261067934283000110670010000000041000888804";

    private static string AuthorizedXml() => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <cteOSProc versao="4.00" xmlns="http://www.portalfiscal.inf.br/cte">
          <CTeOS>
            <infCte Id="CTe{{AccessKey}}" versao="4.00">
              <ide>
                <mod>67</mod><nCT>4</nCT><serie>1</serie>
                <dhEmi>2026-10-22T15:11:09-03:00</dhEmi><tpAmb>1</tpAmb><tpServ>6</tpServ>
              </ide>
              <emit><CNPJ>67934283000110</CNPJ><xNome>TRANSPORTADORA TESTE LTDA</xNome></emit>
              <vPrest><vTPrest>350.00</vTPrest></vPrest>
              <infServico><xDescServ>Transporte de passageiros</xDescServ></infServico>
            </infCte>
          </CTeOS>
          <protCTeOS versao="4.00">
            <infProt><chCTe>{{AccessKey}}</chCTe><nProt>142260000123456</nProt>
              <dhRecbto>2026-10-22T15:11:10-03:00</dhRecbto><cStat>100</cStat>
              <xMotivo>Autorizado o uso do CT-e OS</xMotivo>
            </infProt>
          </protCTeOS>
        </cteOSProc>
        """;

    [Fact]
    public void Parse_AuthorizedCteOs_ExtractsModelIssuerAndService()
    {
        var result = CteOsXmlReader.Parse(AuthorizedXml(), "cte-os.xml");

        Assert.Equal("67", result.Model);
        Assert.Equal(AccessKey, result.AccessKey);
        Assert.Equal("4", result.DocumentNumber);
        Assert.Equal("TRANSPORTADORA TESTE LTDA", result.IssuerName);
        Assert.Equal(350.00m, result.ServiceValue);
        Assert.Equal("Transporte de passageiros", result.ServiceDescription);
        Assert.Equal("Produção", result.Environment);
    }

    [Fact]
    public void Parse_Cte57Root_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() => CteOsXmlReader.Parse(
            AuthorizedXml().Replace("cteOSProc", "cteProc").Replace("CTeOS", "CTe").Replace("protCTeOS", "protCTe")));
        Assert.Contains("cteOSProc", exception.Message);
    }

    [Fact]
    public void Parse_NonAuthorizedCteOs_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() => CteOsXmlReader.Parse(
            AuthorizedXml().Replace("<cStat>100</cStat>", "<cStat>110</cStat>")));
        Assert.Contains("não autorizado", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Dtd_IsRejected()
    {
        const string xml = """<!DOCTYPE cteOSProc [<!ENTITY x "unsafe">]><cteOSProc xmlns="http://www.portalfiscal.inf.br/cte" versao="4.00">&x;</cteOSProc>""";
        Assert.Throws<System.Xml.XmlException>(() => CteOsXmlReader.Parse(xml));
    }
}
