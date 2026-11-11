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

        ValidateAccessKey(accessKey);

        var ide = Required(info, "ide");
        if (Value(ide, "mod") != "55")
            throw new InvalidDataException("O XML não é de NF-e modelo 55.");

        string invoiceNumber = RequiredValue(ide, "nNF");
        string series = RequiredValue(ide, "serie");
        string issueDateText = RequiredValue(ide, "dhEmi");
        if (!DateTimeOffset.TryParse(
            issueDateText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var issueDate))
        {
            throw new InvalidDataException("A data de emissão da NF-e é inválida.");
        }

        var protocol = root.Element(NfeNamespace + "protNFe")?.Element(NfeNamespace + "infProt")
            ?? throw new InvalidDataException("O XML não contém o protocolo de autorização (protNFe).");

        string protocolKey = RequiredValue(protocol, "chNFe");
        if (!string.Equals(accessKey, protocolKey, StringComparison.Ordinal))
            throw new InvalidDataException("A chave de acesso do protocolo não corresponde à chave da NF-e.");

        string status = RequiredValue(protocol, "cStat");
        if (status != "100")
            throw new InvalidDataException($"NF-e não autorizada. Status {status}: {Value(protocol, "xMotivo")}");

        var issuer = Required(info, "emit");
        var recipient = Required(info, "dest");
        string issuerDocument = FirstValue(issuer, "CNPJ", "CPF");
        string recipientDocument = FirstValue(recipient, "CNPJ", "CPF");
        ValidateDocument(issuerDocument, "emitente");
        ValidateDocument(recipientDocument, "destinatário");
        ValidateKeyMatchesInvoice(accessKey, issueDate, issuerDocument, invoiceNumber, series);

        string issuerName = RequiredValue(issuer, "xNome");
        string recipientName = RequiredValue(recipient, "xNome");
        var issuerAddress = issuer.Element(NfeNamespace + "enderEmit");
        var recipientAddress = recipient.Element(NfeNamespace + "enderDest");

        var detailElements = info.Elements(NfeNamespace + "det").ToArray();
        if (detailElements.Length == 0)
            throw new InvalidDataException("A NF-e não contém itens de produto.");

        var items = detailElements.Select(det =>
        {
            var product = Required(det, "prod");
            int number = ParsePositiveInt(Attr(det, "nItem"), "nItem");
            string description = RequiredValue(product, "xProd");
            string quantityText = RequiredValue(product, "qCom");
            string unitPriceText = RequiredValue(product, "vUnCom");
            string totalText = RequiredValue(product, "vProd");

            decimal quantity = ParseDecimal(quantityText, "qCom");
            decimal unitPrice = ParseDecimal(unitPriceText, "vUnCom");
            decimal total = ParseDecimal(totalText, "vProd");
            if (quantity <= 0 || unitPrice < 0 || total < 0)
                throw new InvalidDataException($"O item {number} possui quantidade ou valor inválido.");

            return new NfeItemSummary(
                number,
                Value(product, "cProd"),
                description,
                Value(product, "NCM"),
                Value(product, "CFOP"),
                quantity,
                Value(product, "uCom"),
                unitPrice,
                total);
        }).ToArray();

        if (items.Select(x => x.Number).Distinct().Count() != items.Length)
            throw new InvalidDataException("A NF-e contém números de itens repetidos.");

        var totals = Required(Required(info, "total"), "ICMSTot");
        decimal productTotal = ParseDecimal(RequiredValue(totals, "vProd"), "vProd");
        decimal invoiceTotal = ParseDecimal(RequiredValue(totals, "vNF"), "vNF");
        if (productTotal < 0 || invoiceTotal < 0)
            throw new InvalidDataException("Os totais da NF-e não podem ser negativos.");

        string environmentCode = RequiredValue(ide, "tpAmb");
        string environment = environmentCode == "1" ? "Produção" :
            environmentCode == "2" ? "Homologação" :
            throw new InvalidDataException($"Ambiente da NF-e inválido: {environmentCode}.");

        return new NfeDocumentSummary(
            sourcePath,
            accessKey,
            invoiceNumber,
            series,
            issueDate.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
            issuerName,
            issuerDocument,
            FormatAddress(issuerAddress),
            recipientName,
            recipientDocument,
            FormatAddress(recipientAddress),
            productTotal,
            invoiceTotal,
            Value(protocol, "nProt"),
            Value(protocol, "dhRecbto"),
            Value(protocol, "xMotivo"),
            environment,
            items);
    }

    private static void ValidateAccessKey(string accessKey)
    {
        if (accessKey.Length != 44 || accessKey.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException("A chave de acesso da NF-e não possui 44 dígitos numéricos.");

        int sum = 0;
        int weight = 2;
        for (int index = 42; index >= 0; index--)
        {
            sum += (accessKey[index] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        int remainder = sum % 11;
        int expectedDigit = remainder is 0 or 1 ? 0 : 11 - remainder;
        int actualDigit = accessKey[43] - '0';
        if (actualDigit != expectedDigit)
            throw new InvalidDataException("O dígito verificador da chave de acesso da NF-e é inválido.");
    }

    private static void ValidateDocument(string document, string role)
    {
        string digits = DigitsOnly(document);
        if (digits.Length is not (11 or 14))
            throw new InvalidDataException($"O CPF/CNPJ do {role} deve conter 11 ou 14 dígitos.");
    }

    private static void ValidateKeyMatchesInvoice(
        string accessKey,
        DateTimeOffset issueDate,
        string issuerDocument,
        string invoiceNumber,
        string series)
    {
        string keyIssuer = accessKey.Substring(6, 14);
        string issuerDigits = DigitsOnly(issuerDocument);
        if (issuerDigits.Length == 14 &&
            !string.Equals(keyIssuer, issuerDigits, StringComparison.Ordinal))
            throw new InvalidDataException("O CNPJ do emitente não corresponde ao CNPJ da chave de acesso.");

        string keyYearMonth = accessKey.Substring(2, 4);
        if (keyYearMonth != issueDate.ToString("yyMM", CultureInfo.InvariantCulture))
            throw new InvalidDataException("O mês/ano de emissão não corresponde à chave de acesso.");

        if (accessKey.Substring(20, 2) != "55")
            throw new InvalidDataException("O modelo informado na chave de acesso não é 55.");

        if (int.TryParse(invoiceNumber, NumberStyles.None, CultureInfo.InvariantCulture, out int invoice) &&
            int.TryParse(accessKey.Substring(25, 9), NumberStyles.None, CultureInfo.InvariantCulture, out int keyInvoice) &&
            invoice != keyInvoice)
            throw new InvalidDataException("O número da NF-e não corresponde à chave de acesso.");

        if (int.TryParse(series, NumberStyles.None, CultureInfo.InvariantCulture, out int seriesNumber) &&
            int.TryParse(accessKey.Substring(22, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int keySeries) &&
            seriesNumber != keySeries)
            throw new InvalidDataException("A série da NF-e não corresponde à chave de acesso.");
    }

    private static XElement Required(XElement parent, string name) =>
        parent.Element(NfeNamespace + name)
        ?? throw new InvalidDataException($"O XML não contém o campo obrigatório {name}.");

    private static string Value(XElement parent, string name) =>
        parent.Element(NfeNamespace + name)?.Value.Trim() ?? string.Empty;

    private static string RequiredValue(XElement parent, string name)
    {
        string value = Value(parent, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"O XML não contém o valor obrigatório {name}.");
        return value;
    }

    private static string Attr(XElement element, string name) =>
        (string?)element.Attribute(name) ?? string.Empty;

    private static string FirstValue(XElement parent, string first, string second)
    {
        string value = Value(parent, first);
        return value.Length > 0 ? value : Value(parent, second);
    }

    private static decimal ParseDecimal(string value, string field)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException($"O campo numérico {field} possui valor inválido: {value}.");
        return result;
    }

    private static int ParsePositiveInt(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) || result <= 0)
            throw new InvalidDataException($"O campo {field} possui valor inválido.");
        return result;
    }

    private static string DigitsOnly(string? value) =>
        new((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

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