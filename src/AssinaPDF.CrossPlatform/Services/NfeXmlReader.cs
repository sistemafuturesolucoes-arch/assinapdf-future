using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AssinaPDF.MacPrototype.Services;

public sealed record NfeItemSummary(
    int Number,
    string ProductCode,
    string Description,
    string Ncm,
    string Cfop,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal Total);

public sealed record NfeDocumentSummary(
    string SourcePath,
    string AccessKey,
    string InvoiceNumber,
    string Series,
    string IssueDate,
    string IssuerName,
    string IssuerDocument,
    string IssuerAddress,
    string RecipientName,
    string RecipientDocument,
    string RecipientAddress,
    decimal ProductTotal,
    decimal InvoiceTotal,
    string ProtocolNumber,
    string AuthorizationDate,
    string AuthorizationReason,
    string Environment,
    IReadOnlyList<NfeItemSummary> Items)
{
    public string DisplayName =>
        $"NF-e {InvoiceNumber} · {IssuerName} · {InvoiceTotal.ToString("C", CultureInfo.GetCultureInfo("pt-BR"))}";
}

public static class NfeXmlReader
{
    private static readonly XNamespace NfeNamespace = "http://www.portalfiscal.inf.br/nfe";

    public static NfeDocumentSummary ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Parse(stream, Path.GetFullPath(path));
    }

    public static NfeDocumentSummary Parse(string xml, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, SecureReaderSettings());
        var document = XDocument.Load(reader, LoadOptions.None);
        return ParseDocument(document, sourcePath);
    }

    private static NfeDocumentSummary Parse(Stream stream, string sourcePath)
    {
        using var reader = XmlReader.Create(stream, SecureReaderSettings());
        var document = XDocument.Load(reader, LoadOptions.None);
        return ParseDocument(document, sourcePath);
    }

    private static XmlReaderSettings SecureReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = 10_000_000
    };

    private static NfeDocumentSummary ParseDocument(XDocument document, string sourcePath)
    {
        var root = document.Root ?? throw new InvalidDataException("O XML está vazio.");
        if (root.Name != NfeNamespace + "nfeProc")
            throw new InvalidDataException("Arquivo não reconhecido como NF-e processada (nfeProc).");

        string version = Attr(root, "versao");
        if (version != "4.00")
            throw new InvalidDataException($"Versão do nfeProc não suportada: {version}.");

        var nfe = root.Element(NfeNamespace + "NFe")
            ?? throw new InvalidDataException("O XML não contém o elemento NFe.");
        var info = nfe.Element(NfeNamespace + "infNFe")
            ?? throw new InvalidDataException("O XML não contém infNFe.");

        if (Attr(info, "versao") != "4.00")
            throw new InvalidDataException("A versão de infNFe não é 4.00.");

        string accessKey = Attr(info, "Id");
        if (accessKey.StartsWith("NFe", StringComparison.Ordinal))
            accessKey = accessKey[3..];
        if (accessKey.Length != 44 || accessKey.Any(c => !char.IsAsciiDigit(c)))
            throw new InvalidDataException("A chave de acesso da NF-e não possui 44 dígitos.");

        var ide = Required(info, "ide");
        if (Value(ide, "mod") != "55")
            throw new InvalidDataException("O XML não é de NF-e modelo 55.");

        var protocol = root.Element(NfeNamespace + "protNFe")?.Element(NfeNamespace + "infProt")
            ?? throw new InvalidDataException("O XML não contém o protocolo de autorização (protNFe).");

        string protocolKey = Value(protocol, "chNFe");
        if (!string.Equals(accessKey, protocolKey, StringComparison.Ordinal))
            throw new InvalidDataException("A chave de acesso do protocolo não corresponde à chave da NF-e.");

        string status = Value(protocol, "cStat");
        if (status != "100")
            throw new InvalidDataException($"NF-e não autorizada. Status {status}: {Value(protocol, "xMotivo")}");

        var issuer = Required(info, "emit");
        var recipient = Required(info, "dest");
        var issuerAddress = issuer.Element(NfeNamespace + "enderEmit");
        var recipientAddress = recipient.Element(NfeNamespace + "enderDest");

        var items = info.Elements(NfeNamespace + "det").Select(det =>
        {
            var product = Required(det, "prod");
            return new NfeItemSummary(
                ParseInt(Attr(det, "nItem")),
                Value(product, "cProd"),
                Value(product, "xProd"),
                Value(product, "NCM"),
                Value(product, "CFOP"),
                ParseDecimal(Value(product, "qCom")),
                Value(product, "uCom"),
                ParseDecimal(Value(product, "vUnCom")),
                ParseDecimal(Value(product, "vProd")));
        }).ToArray();

        var totals = Required(Required(info, "total"), "ICMSTot");
        string environmentCode = Value(ide, "tpAmb");
        string environment = environmentCode == "1" ? "Produção" :
            environmentCode == "2" ? "Homologação" : $"Desconhecido ({environmentCode})";

        return new NfeDocumentSummary(
            sourcePath,
            accessKey,
            Value(ide, "nNF"),
            Value(ide, "serie"),
            Value(ide, "dhEmi"),
            Value(issuer, "xNome"),
            FirstValue(issuer, "CNPJ", "CPF"),
            FormatAddress(issuerAddress),
            Value(recipient, "xNome"),
            FirstValue(recipient, "CNPJ", "CPF"),
            FormatAddress(recipientAddress),
            ParseDecimal(Value(totals, "vProd")),
            ParseDecimal(Value(totals, "vNF")),
            Value(protocol, "nProt"),
            Value(protocol, "dhRecbto"),
            Value(protocol, "xMotivo"),
            environment,
            items);
    }

    private static XElement Required(XElement parent, string name) =>
        parent.Element(NfeNamespace + name)
        ?? throw new InvalidDataException($"O XML não contém o campo obrigatório {name}.");

    private static string Value(XElement parent, string name) =>
        parent.Element(NfeNamespace + name)?.Value.Trim() ?? string.Empty;

    private static string Attr(XElement element, string name) =>
        (string?)element.Attribute(name) ?? string.Empty;

    private static string FirstValue(XElement parent, string first, string second)
    {
        string value = Value(parent, first);
        return value.Length > 0 ? value : Value(parent, second);
    }

    private static decimal ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

    private static string FormatAddress(XElement? address)
    {
        if (address == null) return string.Empty;
        string street = Value(address, "xLgr");
        string number = Value(address, "nro");
        string complement = Value(address, "xCpl");
        string district = Value(address, "xBairro");
        string city = Value(address, "xMun");
        string state = Value(address, "UF");
        string postalCode = Value(address, "CEP");

        var parts = new[] {
            string.Join(", ", new[] { street, number }.Where(s => s.Length > 0)),
            complement,
            district,
            string.Join(" - ", new[] { city, state }.Where(s => s.Length > 0)),
            postalCode
        };
        return string.Join(" · ", parts.Where(s => s.Length > 0));
    }
}
