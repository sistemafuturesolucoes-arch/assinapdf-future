using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public class NfceXmlReaderTests
{
    private const string AccessKey = "42261067934283000110650010000000041000888807";

    private static string AuthorizedXml(string destination = "") => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <nfeProc versao="4.00" xmlns="http://www.portalfiscal.inf.br/nfe">
          <NFe>
            <infNFe Id="NFe{{AccessKey}}" versao="4.00">
              <ide>
                <mod>65</mod><nNF>4</nNF><serie>1</serie>
                <dhEmi>2026-10-22T15:11:09-03:00</dhEmi><tpAmb>1</tpAmb>
              </ide>
              <emit>
                <CNPJ>67934283000110</CNPJ><xNome>EMITENTE NFC-E TESTE LTDA</xNome>
                <enderEmit><xLgr>RUA TESTE</xLgr><nro>10</nro><xMun>Passo Fundo</xMun><UF>RS</UF></enderEmit>
              </emit>
              {{destination}}
              <det nItem="1">
                <prod>
                  <cProd>10</cProd><xProd>Produto NFC-e</xProd><NCM>39269090</NCM>
                  <CFOP>5102</CFOP><uCom>UN</uCom><qCom>2.0000</qCom>
                  <vUnCom>12.0000000000</vUnCom><vProd>24.00</vProd>
                </prod>
              </det>
              <total><ICMSTot><vProd>24.00</vProd><vNF>24.00</vNF></ICMSTot></total>
            </infNFe>
            <infNFeSupl><qrCode>https://www.sefaz.rs.gov.br/NFCE/NFCE-COM.aspx?p={{AccessKey}}|2|1|1|ABC</qrCode></infNFeSupl>
          </NFe>
          <protNFe versao="4.00">
            <infProt>
              <chNFe>{{AccessKey}}</chNFe>
              <nProt>242260505644231</nProt><dhRecbto>2026-10-22T15:11:10-03:00</dhRecbto>
              <cStat>100</cStat><xMotivo>Autorizado o uso da NFC-e</xMotivo>
            </infProt>
          </protNFe>
        </nfeProc>
        """;

    [Fact]
    public void GeneratePdf_AuthorizedNfce_CreatesReadableDanfce()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AssinaPDF-Nfce-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string xmlPath = Path.Combine(directory, "nfce.xml");
        string pdfPath = Path.Combine(directory, "danfce.pdf");

        try
        {
            File.WriteAllText(xmlPath, AuthorizedXml(), System.Text.Encoding.UTF8);
            var summary = NfceXmlReader.ReadFile(xmlPath);

            NfceDanfeGenerator.Generate(summary, pdfPath);

            Assert.True(File.Exists(pdfPath));
            Assert.True(new FileInfo(pdfPath).Length > 5);
            using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(pdfPath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.ReadOnly);
            Assert.NotEmpty(pdf.Pages);
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch { }
        }
    }

    [Fact]
    public void Parse_AuthorizedNfce_ExtractsFiscalDataAndQrCode()
    {
        string destination = """
            <dest><CPF>12345678901</CPF><xNome>CONSUMIDOR TESTE</xNome></dest>
            """;

        var result = NfceXmlReader.Parse(AuthorizedXml(destination), "nfce.xml");

        Assert.Equal(AccessKey, result.AccessKey);
        Assert.Equal("4", result.InvoiceNumber);
        Assert.Equal("1", result.Series);
        Assert.Equal("EMITENTE NFC-E TESTE LTDA", result.IssuerName);
        Assert.Equal("CONSUMIDOR TESTE", result.RecipientName);
        Assert.Equal(24.00m, result.InvoiceTotal);
        Assert.Single(result.Items);
        Assert.Equal("Produto NFC-e", result.Items[0].Description);
        Assert.Contains(AccessKey, result.QrCodeUrl);
        Assert.Equal("Produção", result.Environment);
    }

    [Fact]
    public void Parse_NfceWithoutIdentifiedRecipient_IsAccepted()
    {
        var result = NfceXmlReader.Parse(AuthorizedXml());

        Assert.Equal("", result.RecipientName);
        Assert.Equal("", result.RecipientDocument);
    }

    [Fact]
    public void Parse_NfeModel55_IsRejected()
    {
        string xml = AuthorizedXml().Replace("<mod>65</mod>", "<mod>55</mod>");
        var exception = Assert.Throws<InvalidDataException>(() => NfceXmlReader.Parse(xml));
        Assert.Contains("modelo 65", exception.Message);
    }

    [Fact]
    public void Parse_NonAuthorizedNfce_IsRejected()
    {
        string xml = AuthorizedXml().Replace("<cStat>100</cStat>", "<cStat>110</cStat>");
        var exception = Assert.Throws<InvalidDataException>(() => NfceXmlReader.Parse(xml));
        Assert.Contains("não autorizada", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InvalidAccessKeyCheckDigit_IsRejected()
    {
        string xml = AuthorizedXml().Replace(AccessKey, AccessKey[..^1] + "8");
        var exception = Assert.Throws<InvalidDataException>(() => NfceXmlReader.Parse(xml));
        Assert.Contains("dígito verificador", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Dtd_IsRejected()
    {
        const string xml = """<!DOCTYPE nfeProc [<!ENTITY x "unsafe">]><nfeProc xmlns="http://www.portalfiscal.inf.br/nfe" versao="4.00">&x;</nfeProc>""";
        Assert.Throws<System.Xml.XmlException>(() => NfceXmlReader.Parse(xml));
    }
}
