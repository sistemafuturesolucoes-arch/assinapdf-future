using Xunit;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.CrossPlatform.Tests;

public class NfeXmlReaderTests
{
    private const string AuthorizedXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <nfeProc versao="4.00" xmlns="http://www.portalfiscal.inf.br/nfe">
          <NFe>
            <infNFe Id="NFe42261067934283000110550010000000041000888804" versao="4.00">
              <ide>
                <mod>55</mod><nNF>4</nNF><serie>1</serie>
                <dhEmi>2026-10-22T15:11:09-03:00</dhEmi><tpAmb>1</tpAmb>
              </ide>
              <emit>
                <CNPJ>67934283000110</CNPJ><xNome>EMITENTE TESTE LTDA</xNome>
                <enderEmit><xLgr>RUA TESTE</xLgr><nro>10</nro><xMun>Chapeco</xMun><UF>SC</UF></enderEmit>
              </emit>
              <dest>
                <CNPJ>03509226000100</CNPJ><xNome>DESTINATARIO TESTE LTDA</xNome>
                <enderDest><xLgr>AVENIDA TESTE</xLgr><nro>20</nro><xMun>Passo Fundo</xMun><UF>RS</UF></enderDest>
              </dest>
              <det nItem="1">
                <prod>
                  <cProd>10</cProd><xProd>Produto de teste</xProd><NCM>39269090</NCM>
                  <CFOP>6101</CFOP><uCom>UN</uCom><qCom>2.0000</qCom>
                  <vUnCom>12.0000000000</vUnCom><vProd>24.00</vProd>
                </prod>
              </det>
              <total><ICMSTot><vProd>24.00</vProd><vNF>24.00</vNF></ICMSTot></total>
            </infNFe>
          </NFe>
          <protNFe versao="4.00">
            <infProt>
              <chNFe>42261067934283000110550010000000041000888804</chNFe>
              <nProt>242260505644231</nProt><dhRecbto>2026-10-22T15:11:10-03:00</dhRecbto>
              <cStat>100</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo>
            </infProt>
          </protNFe>
        </nfeProc>
        """;

    [Fact]
    public void Parse_AuthorizedNfe_ExtractsInvoiceAndTotals()
    {
        var result = NfeXmlReader.Parse(AuthorizedXml, "teste.xml");

        Assert.Equal("4", result.InvoiceNumber);
        Assert.Equal("1", result.Series);
        Assert.Equal("EMITENTE TESTE LTDA", result.IssuerName);
        Assert.Equal("DESTINATARIO TESTE LTDA", result.RecipientName);
        Assert.Equal(24.00m, result.InvoiceTotal);
        Assert.Single(result.Items);
        Assert.Equal("Produto de teste", result.Items[0].Description);
        Assert.Equal("242260505644231", result.ProtocolNumber);
        Assert.Equal("Produção", result.Environment);
    }

    [Fact]
    public void Parse_NonAuthorizedNfe_RejectsDocument()
    {
        string xml = AuthorizedXml.Replace("<cStat>100</cStat>", "<cStat>110</cStat>");
        var exception = Assert.Throws<InvalidDataException>(() => NfeXmlReader.Parse(xml));
        Assert.Contains("não autorizada", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AuthorizedOutOfDeadlineStatus_IsAccepted()
    {
        string xml = AuthorizedXml.Replace("<cStat>100</cStat>", "<cStat>150</cStat>");
        var result = NfeXmlReader.Parse(xml);
        Assert.Equal("4", result.InvoiceNumber);
    }

    [Fact]
    public void Parse_ProtocolWithDifferentAccessKey_RejectsDocument()
    {
        string xml = AuthorizedXml.Replace(
            "<chNFe>42261067934283000110550010000000041000888804</chNFe>",
            "<chNFe>52261067934283000110550010000000041000888804</chNFe>");
        var exception = Assert.Throws<InvalidDataException>(() => NfeXmlReader.Parse(xml));
        Assert.Contains("não corresponde", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InvalidAccessKeyCheckDigit_IsRejected()
    {
        string xml = AuthorizedXml.Replace(
            "NFe42261067934283000110550010000000041000888804",
            "NFe42261067934283000110550010000000041000888805");

        var exception = Assert.Throws<InvalidDataException>(() => NfeXmlReader.Parse(xml));
        Assert.Contains("dígito verificador", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InvalidIssueDate_IsRejected()
    {
        string xml = AuthorizedXml.Replace(
            "2026-10-22T15:11:09-03:00",
            "data-invalida");

        var exception = Assert.Throws<InvalidDataException>(() => NfeXmlReader.Parse(xml));
        Assert.Contains("data de emissão", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InvalidNumericItemValue_IsRejectedInsteadOfBecomingZero()
    {
        string xml = AuthorizedXml.Replace("<vProd>24.00</vProd>", "<vProd>valor</vProd>");

        var exception = Assert.Throws<InvalidDataException>(() => NfeXmlReader.Parse(xml));
        Assert.Contains("vProd", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IssuerDocumentMismatchWithAccessKey_IsRejected()
    {
        string xml = AuthorizedXml.Replace(
            "<CNPJ>67934283000110</CNPJ>",
            "<CNPJ>67934283000111</CNPJ>");

        var exception = Assert.Throws<InvalidDataException>(() => NfeXmlReader.Parse(xml));
        Assert.Contains("CNPJ do emitente", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Dtd_IsRejected()
    {
        const string xml = """<!DOCTYPE nfeProc [<!ENTITY x "unsafe">]><nfeProc xmlns="http://www.portalfiscal.inf.br/nfe" versao="4.00">&x;</nfeProc>""";
        Assert.Throws<System.Xml.XmlException>(() => NfeXmlReader.Parse(xml));
    }
}