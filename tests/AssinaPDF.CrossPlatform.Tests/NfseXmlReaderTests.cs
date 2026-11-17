using AssinaPDF.MacPrototype.Services;
using PdfSharp.Pdf.IO;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public class NfseXmlReaderTests
{
    private const string AccessKey = "NFS12345678901234567890123456789012345678901234567890";

    private static string NationalXml(string status = "100", string extra = "") => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <NFSe xmlns="http://www.sped.fazenda.gov.br/nfse" versao="1.00">
          <infNFSe Id="{{AccessKey}}">
            <nNFSe>2026000000123</nNFSe>
            <dhProc>2026-11-16T14:30:00-03:00</dhProc>
            <ambGer>1</ambGer>
            <cStat>{{status}}</cStat>
            <DPS versao="1.00">
              <infDPS Id="DPS123">
                <tpAmb>1</tpAmb><dhEmi>2026-11-16T14:20:00-03:00</dhEmi>
                <serie>900</serie><nDPS>123</nDPS><dCompet>2026-11-01</dCompet><tpEmit>1</tpEmit><finNFSe>0</finNFSe>
                <prest>
                  <CNPJ>12345678000199</CNPJ><IM>12345</IM><xNome>PRESTADOR TESTE LTDA</xNome>
                  <fone>54999990000</fone><email>fiscal@example.com</email>
                  <end><endNac><cMun>4314100</cMun><CEP>99000000</CEP><xLgr>RUA TESTE</xLgr><nro>10</nro><xBairro>CENTRO</xBairro><UF>RS</UF></endNac></end>
                </prest>
                <toma><CPF>12345678901</CPF><xNome>TOMADOR TESTE</xNome><email>tomador@example.com</email></toma>
                <serv>
                  <locPrest><cLocPrestacao>4314100</cLocPrestacao><UF>RS</UF></locPrest>
                  <cServ><cTribNac>010701</cTribNac><cTribMun>001</cTribMun><cNBS>123456789</cNBS><xDescServ>Serviços de desenvolvimento e manutenção de sistemas.</xDescServ></cServ>
                </serv>
                {{extra}}
              </infDPS>
            </DPS>
            <valores>
              <vServPrest><vServ>1500.00</vServ></vServPrest>
              <vDescIncond>100.00</vDescIncond><vDescCond>0.00</vDescCond><vLiq>1400.00</vLiq>
              <trib><tribMun><vISSQN>30.00</vISSQN></tribMun><tribFed><vRetIRRF>15.00</vRetIRRF></tribFed><totTrib><vIBS>2.00</vIBS><vCBS>3.00</vCBS></totTrib></trib>
            </valores>
          </infNFSe>
        </NFSe>
        """;

    [Fact]
    public void Parse_NationalNfse_ExtractsOfficialFields()
    {
        var result = NfseXmlReader.Parse(NationalXml(), "nfse.xml");

        Assert.Equal(AccessKey, result.AccessKey);
        Assert.Equal("2026000000123", result.Number);
        Assert.Equal("2026-11-01", result.Competence);
        Assert.Equal("PRESTADOR TESTE LTDA", result.IssuerName);
        Assert.Equal("12345678000199", result.IssuerDocument);
        Assert.Equal("TOMADOR TESTE", result.RecipientName);
        Assert.Equal("010701", result.ServiceCodeNational);
        Assert.Equal(1500m, result.ServiceAmount);
        Assert.Equal(1400m, result.NetAmount);
        Assert.Equal(30m, result.IssqnAmount!.Value);
        Assert.Equal(15m, result.FederalWithholdings!.Value);
        Assert.Equal(5m, result.IbscbsAmount!.Value);
        Assert.Equal("Produção", result.Environment);
        Assert.Equal("NFS-e gerada", result.Status);
        Assert.Equal("NFS-e regular", result.Purpose);
        Assert.Equal("Sistema próprio do município", result.GeneratingEnvironment);
    }

    [Fact]
    public void Parse_DestinationAtInfDpsRoot_IsReadAccordingToNt009()
    {
        string xml = NationalXml().Replace(
            "</toma>",
            "</toma><indDest>1</indDest><dest><CNPJ>12345678000199</CNPJ><xNome>DESTINATARIO TESTE</xNome><fone>54988887777</fone></dest>");
        var result = NfseXmlReader.Parse(xml);
        Assert.Contains("DESTINATARIO TESTE", result.DestinationDetails);
        Assert.Contains("12345678000199", result.DestinationDetails);
    }

    [Fact]
    public void Parse_LegacyDestinationInsideIbscbs_IsStillRead()
    {
        string xml = NationalXml(extra:
            "<IBSCBS><dest><CNPJ>12345678000199</CNPJ><xNome>DESTINATARIO LEGADO</xNome></dest></IBSCBS>");
        var result = NfseXmlReader.Parse(xml);
        Assert.Contains("DESTINATARIO LEGADO", result.DestinationDetails);
    }

    [Fact]
    public void Parse_AbsentOptionalTaxes_RemainAbsent()
    {
        string xml = NationalXml()
            .Replace("<vDescIncond>100.00</vDescIncond>", "")
            .Replace("<vDescCond>0.00</vDescCond>", "")
            .Replace("<vISSQN>30.00</vISSQN>", "")
            .Replace("<vRetIRRF>15.00</vRetIRRF>", "")
            .Replace("<vIBS>2.00</vIBS><vCBS>3.00</vCBS>", "");
        var result = NfseXmlReader.Parse(xml);
        Assert.Null(result.UnconditionalDiscount);
        Assert.Null(result.ConditionalDiscount);
        Assert.Null(result.IssqnAmount);
        Assert.Null(result.FederalWithholdings);
        Assert.Null(result.IbscbsAmount);
    }

    [Fact]
    public void Parse_CurrentNfseIbsCbsTotalizers_AreReadFromInfNfse()
    {
        string xml = NationalXml().Replace(
            "</valores>",
            "</valores><IBSCBS><totCIBS><gIBS><vIBSTot>12.50</vIBSTot></gIBS><gCBS><vCBS>7.25</vCBS></gCBS></totCIBS></IBSCBS>");
        var result = NfseXmlReader.Parse(xml);
        Assert.Equal(19.75m, result.IbscbsAmount);
    }

    [Fact]
    public void Parse_AlphanumericCnpj_IsAccepted()
    {
        string xml = NationalXml().Replace("12345678000199", "12ABC34501DE35");
        var result = NfseXmlReader.Parse(xml);
        Assert.Equal("12ABC34501DE35", result.IssuerDocument);
    }

    [Fact]
    public void Parse_MunicipalXml_IsRejected()
    {
        const string xml = """<CompNfse xmlns="http://www.abrasf.org.br/nfse.xsd"></CompNfse>""";
        var error = Assert.Throws<InvalidDataException>(() => NfseXmlReader.Parse(xml));
        Assert.Contains("Padrão Nacional", error.Message);
    }

    [Fact]
    public void Parse_Dtd_IsRejected()
    {
        const string xml = """<!DOCTYPE NFSe [<!ENTITY x "unsafe">]><NFSe xmlns="http://www.sped.fazenda.gov.br/nfse" versao="1.00">&x;</NFSe>""";
        Assert.Throws<System.Xml.XmlException>(() => NfseXmlReader.Parse(xml));
    }

    [Fact]
    public void Generate_CreatesSinglePageA4Pdf_AndKeepsOriginalXml()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AssinaPDF-Nfse-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string xmlPath = Path.Combine(directory, "nfse.xml");
        string pdfPath = Path.Combine(directory, "danfse.pdf");
        try
        {
            File.WriteAllText(xmlPath, NationalXml(), System.Text.Encoding.UTF8);
            var summary = NfseXmlReader.ReadFile(xmlPath);
            NfseDanfseGenerator.Generate(summary, pdfPath);

            Assert.True(File.Exists(xmlPath));
            Assert.True(File.Exists(pdfPath));
            Assert.True(new FileInfo(pdfPath).Length > 500);
            using var pdf = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            Assert.Single(pdf.Pages);
            Assert.Equal(595.28, pdf.Pages[0].Width.Point, 1);
            Assert.Equal(841.89, pdf.Pages[0].Height.Point, 1);
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
        }
    }
}