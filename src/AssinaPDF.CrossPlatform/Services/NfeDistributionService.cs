using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AssinaPDF.MacPrototype.Services;

public sealed record NfeDistributedDocument(
    string Nsu,
    string Schema,
    string XmlContent,
    string DocumentType,
    string AccessKey,
    string InvoiceNumber,
    string IssuerName,
    string IssueDate);

public sealed record NfeDistributionBatch(
    string StatusCode,
    string StatusMessage,
    string LastNsu,
    string MaxNsu,
    IReadOnlyList<NfeDistributedDocument> Documents);

public static class NfeDistributionService
{
    private const string ProductionUrl =
        "https://www1.nfe.fazenda.gov.br/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx";
    private const string HomologationUrl =
        "https://hom.nfe.fazenda.gov.br/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx";

    private static readonly XNamespace Soap12 =
        "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsdl =
        "http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe";
    private static readonly XNamespace Nfe =
        "http://www.portalfiscal.inf.br/nfe";

    public static async Task<NfeDistributionBatch> QueryByNsuAsync(
        X509Certificate2 certificate,
        string cnpj,
        string lastNsu,
        bool production,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        cnpj = NormalizeDigits(cnpj);
        if (cnpj.Length != 14)
            throw new ArgumentException("Informe um CNPJ com 14 dígitos.", nameof(cnpj));
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException(
                "O certificado selecionado não possui chave privada acessível.");
        if (lastNsu.Length > 15 || lastNsu.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("O NSU informado é inválido.", nameof(lastNsu));

        string normalizedNsu = lastNsu.PadLeft(15, '0');
        string requestXml =
            $"<distDFeInt versao=\"1.01\" xmlns=\"{Nfe}\">" +
            $"<tpAmb>{(production ? "1" : "2")}</tpAmb>" +
            $"<CNPJ>{cnpj}</CNPJ>" +
            $"<distNSU><ultNSU>{normalizedNsu}</ultNSU></distNSU>" +
            "</distDFeInt>";

        var requestDocument = XDocument.Parse(requestXml, LoadOptions.None);
        var envelope = new XDocument(
            new XElement(Soap12 + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap12", Soap12),
                new XElement(Soap12 + "Body",
                    new XElement(Wsdl + "nfeDistDFeInteresse",
                        new XElement(Wsdl + "nfeDadosMsg", requestDocument.Root)))));

        var handler = new HttpClientHandler
        {
            CheckCertificateRevocationList = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        handler.ClientCertificates.Add(certificate);

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        using var content = new StringContent(
            envelope.ToString(SaveOptions.DisableFormatting),
            Encoding.UTF8,
            "application/soap+xml");
        using var response = await client.PostAsync(
            production ? ProductionUrl : HomologationUrl,
            content,
            cancellationToken);

        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"A SEFAZ retornou HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                ExtractSoapFault(responseText));
        }

        var responseDocument = LoadSecureXml(responseText);
        var result = responseDocument
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "retDistDFeInt");

        if (result is null)
        {
            var resultText = responseDocument
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "nfeDistDFeInteresseResult")
                ?.Value;

            if (!string.IsNullOrWhiteSpace(resultText))
                result = LoadSecureXml(resultText).Root;
        }

        if (result is null)
            throw new InvalidDataException(
                "A resposta da SEFAZ não contém retDistDFeInt. Verifique o serviço e tente novamente.");

        string status = ChildValue(result, "cStat");
        string reason = ChildValue(result, "xMotivo");
        string last = ChildValue(result, "ultNSU");
        string max = ChildValue(result, "maxNSU");

        var documents = new List<NfeDistributedDocument>();
        foreach (var docZip in result.Descendants().Where(x => x.Name.LocalName == "docZip"))
        {
            string schema = (string?)docZip.Attribute("schema") ?? "documento";
            string nsu = (string?)docZip.Attribute("NSU") ?? "";
            if (string.IsNullOrWhiteSpace(docZip.Value))
                continue;

            byte[] compressed;
            try
            {
                compressed = Convert.FromBase64String(docZip.Value.Trim());
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"A SEFAZ retornou um documento compactado inválido (NSU {nsu}).", ex);
            }

            string xml;
            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                await gzip.CopyToAsync(output, cancellationToken);
                xml = Encoding.UTF8.GetString(output.ToArray());
            }

            documents.Add(ParseDistributedDocument(nsu, schema, xml));
        }

        return new NfeDistributionBatch(status, reason, last, max, documents);
    }

    private static NfeDistributedDocument ParseDistributedDocument(
        string nsu,
        string schema,
        string xml)
    {
        var document = LoadSecureXml(xml);
        var root = document.Root
            ?? throw new InvalidDataException($"Documento recebido vazio (NSU {nsu}).");

        var info = root.DescendantsAndSelf()
            .FirstOrDefault(x => x.Name.LocalName == "infNFe");
        var summary = root.Name.LocalName == "resNFe" ? root : null;
        var protocol = root.DescendantsAndSelf()
            .FirstOrDefault(x => x.Name.LocalName == "infProt");

        string accessKey =
            AttributeValue(info, "Id").Replace("NFe", "", StringComparison.Ordinal) is { Length: > 0 } id
                ? id
                : ChildValue(summary, "chNFe");
        string invoiceNumber = ChildValue(
            root.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName == "ide"), "nNF");
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            invoiceNumber = ChildValue(summary, "nNF");
        string issuer = ChildValue(
            root.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName == "emit"), "xNome");
        if (string.IsNullOrWhiteSpace(issuer))
            issuer = ChildValue(summary, "xNome");
        string issueDate = ChildValue(
            root.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName == "ide"), "dhEmi");
        if (string.IsNullOrWhiteSpace(issueDate))
            issueDate = ChildValue(summary, "dhEmi");
        if (string.IsNullOrWhiteSpace(issueDate))
            issueDate = ChildValue(summary, "dEmi");

        string type = schema.Contains("resNFe", StringComparison.OrdinalIgnoreCase)
            ? "Resumo da NF-e"
            : schema.Contains("procNFe", StringComparison.OrdinalIgnoreCase) ||
              root.Name.LocalName is "nfeProc" or "NFe"
                ? "XML completo da NF-e"
                : "Evento ou outro DF-e";

        return new NfeDistributedDocument(
            nsu,
            schema,
            xml,
            type,
            accessKey,
            invoiceNumber,
            issuer,
            issueDate);
    }

    private static XDocument LoadSecureXml(string xml)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 15_000_000,
            IgnoreComments = true
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string ExtractSoapFault(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return "A resposta não trouxe detalhes.";
        try
        {
            var document = LoadSecureXml(xml);
            var fault = document.Descendants()
                .FirstOrDefault(x => x.Name.LocalName is "Text" or "faultstring");
            return fault?.Value ?? "A resposta não trouxe detalhes.";
        }
        catch
        {
            return "A resposta não trouxe detalhes legíveis.";
        }
    }

    private static string ChildValue(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(x => x.Name.LocalName == localName)?.Value ?? "";

    private static string AttributeValue(XElement? element, string name) =>
        (string?)element?.Attribute(name) ?? "";

    public static string NormalizeDigits(string value) =>
        new(value.Where(char.IsDigit).ToArray());
}
