using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public class CteXmlReaderTests
{
    private const string AccessKey = "42261067934283000110570010000000041000888801";

    private static string AuthorizedXml() => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <cteProc versao="4.00" xmlns="http://www.portalfiscal.inf.br/cte">
          <CTe>
            <infCte Id="CTe{{AccessKey}}" versao="4.00">
              <ide>
                <mod>57</mod><nCT>4</nCT><serie>1</serie>
                <dhEmi>2026-10-22T15:11:09-03:00</dhEmi><tpAmb>1</tpAmb><modal>01</modal>
              </ide>
              <emit><CNPJ>67934283000110</CNPJ><xNome>TRANSPORTADORA TESTE LTDA</xNome></emit>
              <rem>
                <CNPJ>12345678000190</CNPJ><xNome>REMETENTE TESTE</xNome>
                <enderReme><xMun>Passo Fundo</xMun><UF>RS</UF></enderReme>
              </rem>
              <dest>
                <CNPJ>98765432000110</CNPJ><xNome>DESTINATARIO TESTE</xNome>
                <enderDest><xMun>Porto Alegre</xMun><UF>RS</UF></enderDest>
              </dest>
              <vPrest><vTPrest>350.00</vTPrest></vPrest>
              <infCTeNorm><infCarga><vCarga>12500.00</vCarga><proPred>Mercadorias diversas</proPred></infCarga></infCTeNorm>
            </infCte>
          </CTe>
          <protCTe versao="4.00">
            <infProt><chCTe>{{AccessKey}}</chCTe><nProt>142260000123456</nProt>
              <dhRecbto>2026-10-22T15:11:10-03:00</dhRecbto><cStat>100</cStat>
              <xMotivo>Autorizado o uso do CT-e</xMotivo>
            </infProt>
          </protCTe>
        </cteProc>
        """;

    [Fact]
    public void Parse_AuthorizedCte_ExtractsKeyPartiesAndValues()
    {
        var result = CteXmlReader.Parse(AuthorizedXml(), "cte.xml");

        Assert.Equal(AccessKey, result.AccessKey);
        Assert.Equal("4", result.DocumentNumber);
        Assert.Equal("TRANSPORTADORA TESTE LTDA", result.IssuerName);
        Assert.Equal("REMETENTE TESTE", result.SenderName);
        Assert.Equal("DESTINATARIO TESTE", result.RecipientName);
        Assert.Equal("Passo Fundo", result.OriginCity);
        Assert.Equal("Porto Alegre", result.DestinationCity);
        Assert.Equal(12500.00m, result.CargoValue);
        Assert.Equal(350.00m, result.ServiceValue);
        Assert.Equal("Produção", result.Environment);
    }

    [Fact]
    public void Parse_Model65_IsRejected()
    {
        string xml = AuthorizedXml().Replace("<mod>57</mod>", "<mod>65</mod>");
        var exception = Assert.Throws<InvalidDataException>(() => CteXmlReader.Parse(xml));
        Assert.Contains("modelo 57", exception.Message);
    }

    [Fact]
    public void Parse_NonAuthorizedCte_IsRejected()
    {
        string xml = AuthorizedXml().Replace("<cStat>100</cStat>", "<cStat>110</cStat>");
        var exception = Assert.Throws<InvalidDataException>(() => CteXmlReader.Parse(xml));
        Assert.Contains("não autorizado", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ProtocolWithDifferentKey_IsRejected()
    {
        string xml = AuthorizedXml().Replace($"<chCTe>{AccessKey}</chCTe>", $"<chCTe>{AccessKey[..^1]}3</chCTe>");
        var exception = Assert.Throws<InvalidDataException>(() => CteXmlReader.Parse(xml));
        Assert.Contains("protocolo", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Dtd_IsRejected()
    {
        const string xml = """<!DOCTYPE cteProc [<!ENTITY x "unsafe">]><cteProc xmlns="http://www.portalfiscal.inf.br/cte" versao="4.00">&x;</cteProc>""";
        Assert.Throws<System.Xml.XmlException>(() => CteXmlReader.Parse(xml));
    }
}
