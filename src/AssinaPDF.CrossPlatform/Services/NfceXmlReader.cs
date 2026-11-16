using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AssinaPDF.MacPrototype.Services;

public sealed record NfceItemSummary(
    int Number,
    string ProductCode,
    string Description,
    string Ncm,
    string Cfop,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal Total);

public sealed record NfceDocumentSummary(
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
    decimal ProductTotal,
    decimal InvoiceTotal,
    string ProtocolNumber,
    string AuthorizationDate,
    string AuthorizationReason,
    string Environment,
    string QrCodeUrl,
    IReadOnlyList<NfceItemSummary> Items)
{
    public string DisplayName =>
        $"NFC-e {InvoiceNumber} · {IssuerName} · {InvoiceTotal.ToString("C", CultureInfo.GetCultureInfo("pt-BR"))}";
}

/// <summary>
/// Leitor estrito de NFC-e autorizada (modelo 65). Não altera nem reutiliza o leitor de NF-e modelo 55.
/// </summary>
public static class NfceXmlReader
{
    private static readonly XNamespace NfeNamespace = "http://www.portalfiscal.inf.br/nfe";

    public static NfceDocumentSummary ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Parse(stream, Path.GetFullPath(path));
    }

    public static NfceDocumentSummary Parse(string xml, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, SecureReaderSettings());
        return ParseDocument(XDocument.Load(reader, LoadOptions.None), sourcePath);
    }

    private static NfceDocumentSummary Parse(Stream stream, string sourcePath)
    {
        using var reader = XmlReader.Create(stream, SecureReaderSettings());
        return ParseDocument(XDocument.Load(reader, LoadOptions.None), sourcePath);
    }

    private static XmlReaderSettings SecureReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = 10_000_000
    };

    private static NfceDocumentSummary ParseDocument(XDocument document, string sourcePath)
    {
        var root = document.Root ?? throw new InvalidDataException("O XML está vazio.");
        if (root.Name != NfeNamespace + "nfeProc")
            throw new InvalidDataException("Arquivo não reconhecido como NFC-e processada (nfeProc).");

        if (Attr(root, "versao") != "4.00")
            throw new InvalidDataException("Versão do nfeProc não suportada.");

        var nfe = root.Element(NfeNamespace + "NFe")
            ?? throw new InvalidDataException("O XML não contém o elemento NFe.");
        var info = nfe.Element(NfeNamespace + "infNFe")
            ?? throw new InvalidDataException("O XML não contém infNFe.");

        if (Attr(info, "versao") != "4.00")
            throw new InvalidDataException("A versão de infNFe não é 4.00.");

        string accessKey = Attr(info, "Id");
        if (accessKey.StartsWith("NFe", StringComparison.Ordinal))
            accessKey = accessKey[3..];
        ValidateAccessKey(accessKey);

        var ide = Required(info, "ide");
        if (Value(ide, "mod") != "65")
            throw new InvalidDataException("O XML não é de NFC-e modelo 65.");

        string invoiceNumber = RequiredValue(ide, "nNF");
        string series = RequiredValue(ide, "serie");
        int number = ParsePositiveInt(invoiceNumber, "nNF");
        int seriesNumber = ParseNonNegativeInt(series, "serie");

        string issueDateText = RequiredValue(ide, "dhEmi");
        if (!DateTimeOffset.TryParse(issueDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var issueDate))
            throw new InvalidDataException("A data de emissão da NFC-e é inválida.");

        var protocol = root.Element(NfeNamespace + "protNFe")?.Element(NfeNamespace + "infProt")
            ?? throw new InvalidDataException("O XML não contém o protocolo de autorização (protNFe).");

        string protocolKey = RequiredValue(protocol, "chNFe");
        if (!string.Equals(accessKey, protocolKey, StringComparison.Ordinal))
            throw new InvalidDataException("A chave de acesso do protocolo não corresponde à chave da NFC-e.");

        string status = RequiredValue(protocol, "cStat");
        if (status is not ("100" or "150"))
            throw new InvalidDataException($"NFC-e não autorizada. Status {status}: {Value(protocol, "xMotivo")}");

        var issuer = Required(info, "emit");
        string issuerDocument = FirstValue(issuer, "CNPJ", "CPF");
        ValidateDocument(issuerDocument, "emitente");
        ValidateKeyMatchesInvoice(accessKey, issueDate, issuerDocument, number, seriesNumber);

        string issuerName = RequiredValue(issuer, "xNome");
        var items = info.Elements(NfeNamespace + "det").Select(det =>
        {
            var product = Required(det, "prod");
            int itemNumber = ParsePositiveInt(Attr(det, "nItem"), "nItem");
            string description = RequiredValue(product, "xProd");
            decimal quantity = ParseDecimal(RequiredValue(product, "qCom"), "qCom");
            decimal unitPrice = ParseDecimal(RequiredValue(product, "vUnCom"), "vUnCom");
            decimal total = ParseDecimal(RequiredValue(product, "vProd"), "vProd");
            if (quantity <= 0 || unitPrice < 0 || total < 0)
                throw new InvalidDataException($"O item {itemNumber} possui quantidade ou valor inválido.");

            return new NfceItemSummary(
                itemNumber, Value(product, "cProd"), description, Value(product, "NCM"),
                Value(product, "CFOP"), quantity, Value(product, "uCom"), unitPrice, total);
        }).ToArray();

        if (items.Length == 0)
            throw new InvalidDataException("A NFC-e não contém itens.");
        if (items.Select(x => x.Number).Distinct().Count() != items.Length)
            throw new InvalidDataException("A NFC-e contém números de itens repetidos.");

        var totals = Required(Required(info, "total"), "ICMSTot");
        decimal productTotal = ParseDecimal(RequiredValue(totals, "vProd"), "vProd");
        decimal invoiceTotal = ParseDecimal(RequiredValue(totals, "vNF"), "vNF");
        if (productTotal < 0 || invoiceTotal < 0)
            throw new InvalidDataException("Os totais da NFC-e não podem ser negativos.");

        string environmentCode = RequiredValue(ide, "tpAmb");
        string environment = environmentCode == "1" ? "Produção" :
            environmentCode == "2" ? "Homologação" :
            throw new InvalidDataException($"Ambiente da NFC-e inválido: {environmentCode}.");

        // NFC-e pode não identificar o consumidor; nesse caso o grupo dest é legalmente omitido.
        var recipient = info.Element(NfeNamespace + "dest");
        string recipientName = recipient is null ? "" : Value(recipient, "xNome");
        string recipientDocument = recipient is null ? "" : FirstValue(recipient, "CNPJ", "CPF");
        string qrCodeUrl = Value(Required(nfe, "infNFeSupl"), "qrCode");
        if (string.IsNullOrWhiteSpace(qrCodeUrl))
            throw new InvalidDataException("O XML da NFC-e não contém a URL do QR Code.");

        return new NfceDocumentSummary(
            sourcePath, accessKey, invoiceNumber, series,
            issueDate.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
            issuerName, issuerDocument, FormatAddress(issuer.Element(NfeNamespace + "enderEmit")),
            recipientName, recipientDocument, productTotal, invoiceTotal,
            Value(protocol, "nProt"), Value(protocol, "dhRecbto"), Value(protocol, "xMotivo"),
            environment, qrCodeUrl, items);
    }

    private static void ValidateAccessKey(string key)
    {
        if (key.Length != 44 || key.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException("A chave de acesso da NFC-e deve conter 44 dígitos.");
        if (key.Substring(20, 2) != "65")
            throw new InvalidDataException("A chave de acesso não identifica o modelo NFC-e 65.");

        int sum = 0, weight = 2;
        for (int i = 42; i >= 0; i--)
        {
            sum += (key[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        int digit = 11 - sum % 11;
        if (digit >= 10) digit = 0;
        if (digit != key[43] - '0')
            throw new InvalidDataException("A chave de acesso da NFC-e possui dígito verificador inválido.");
    }

    private static void ValidateKeyMatchesInvoice(string key, DateTimeOffset issueDate, string issuerDocument, int number, int series)
    {
        string expectedAamm = issueDate.ToString("yyMM", CultureInfo.InvariantCulture);
        if (key.Substring(2, 4) != expectedAamm)
            throw new InvalidDataException("A data de emissão não corresponde à chave de acesso da NFC-e.");
        if (key.Substring(6, 14) != issuerDocument.PadLeft(14, '0'))
            throw new InvalidDataException("O CNPJ/CPF do emitente não corresponde à chave de acesso.");
        if (int.Parse(key.Substring(22, 3), CultureInfo.InvariantCulture) != series ||
            int.Parse(key.Substring(25, 9), CultureInfo.InvariantCulture) != number)
            throw new InvalidDataException("Série ou número não corresponde à chave de acesso da NFC-e.");
    }

    private static void ValidateDocument(string value, string role)
    {
        if (value.Length is not (11 or 14) || value.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException($"Documento do {role} inválido no XML da NFC-e.");
    }

    private static XElement Required(XElement parent, string name) =>
        parent.Element(NfeNamespace + name) ?? throw new InvalidDataException($"O XML da NFC-e não contém {name}.");

    private static string RequiredValue(XElement parent, string name)
    {
        string value = Value(parent, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"O campo {name} é obrigatório no XML da NFC-e.")
            : value;
    }

    private static string Value(XElement parent, string name) =>
        parent.Element(NfeNamespace + name)?.Value.Trim() ?? "";

    private static string Attr(XElement element, string name) =>
        element.Attribute(name)?.Value.Trim() ?? "";

    private static string FirstValue(XElement parent, params string[] names)
    {
        foreach (string name in names)
        {
            string value = Value(parent, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static decimal ParseDecimal(string value, string field)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out decimal result))
            throw new InvalidDataException($"O campo numérico {field} é inválido.");
        return result;
    }

    private static int ParsePositiveInt(string value, string field) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) && result > 0
            ? result : throw new InvalidDataException($"O campo {field} deve ser um número positivo.");

    private static int ParseNonNegativeInt(string value, string field) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) && result >= 0
            ? result : throw new InvalidDataException($"O campo {field} deve ser um número não negativo.");

    private static string FormatAddress(XElement? address)
    {
        if (address is null) return "";
        string[] parts = ["xLgr", "nro", "xCpl", "xBairro", "xMun", "UF", "CEP"];
        return string.Join(", ", parts.Select(name => Value(address, name)).Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
