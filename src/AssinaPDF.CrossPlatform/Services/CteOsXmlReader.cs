using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Leitor seguro de CT-e OS processado, modelo 67, com conferência da chave
/// e do protocolo de autorização antes de permitir a geração do DACTE OS.
/// </summary>
public static class CteOsXmlReader
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/cte";

    public static CteDocumentSummary ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, SecureSettings());
        return ParseDocument(XDocument.Load(reader, LoadOptions.None), Path.GetFullPath(path));
    }

    public static CteDocumentSummary Parse(string xml, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, SecureSettings());
        return ParseDocument(XDocument.Load(reader, LoadOptions.None), sourcePath);
    }

    private static XmlReaderSettings SecureSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = 10_000_000
    };

    private static CteDocumentSummary ParseDocument(XDocument document, string sourcePath)
    {
        var root = document.Root ?? throw new InvalidDataException("O XML está vazio.");
        if (root.Name != Ns + "cteOSProc")
            throw new InvalidDataException("Arquivo não reconhecido como CT-e OS processado (cteOSProc).");
        if (Attr(root, "versao") != "4.00")
            throw new InvalidDataException("Versão do cteOSProc não suportada.");

        var cte = Required(root, "CTeOS");
        var info = Required(cte, "infCte");
        if (Attr(info, "versao") != "4.00")
            throw new InvalidDataException("A versão de infCte não é 4.00.");

        string key = Attr(info, "Id");
        if (key.StartsWith("CTe", StringComparison.Ordinal)) key = key[3..];
        ValidateAccessKey(key);

        var ide = Required(info, "ide");
        if (Value(ide, "mod") != "67")
            throw new InvalidDataException("O XML não é de CT-e OS modelo 67.");

        string numberText = RequiredValue(ide, "nCT");
        string seriesText = RequiredValue(ide, "serie");
        int number = PositiveInt(numberText, "nCT");
        int series = NonNegativeInt(seriesText, "serie");
        string issueText = RequiredValue(ide, "dhEmi");
        if (!DateTimeOffset.TryParse(issueText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var issueDate))
            throw new InvalidDataException("A data de emissão do CT-e OS é inválida.");

        var protocol = Required(Required(root, "protCTe"), "infProt");
        if (!string.Equals(key, RequiredValue(protocol, "chCTe"), StringComparison.Ordinal))
            throw new InvalidDataException("A chave do protocolo não corresponde à chave do CT-e OS.");
        string status = RequiredValue(protocol, "cStat");
        if (status is not ("100" or "150"))
            throw new InvalidDataException($"CT-e OS não autorizado. Status {status}: {Value(protocol, "xMotivo")}");

        var issuer = Required(info, "emit");
        string issuerDocument = FirstValue(issuer, "CNPJ", "CPF");
        ValidateDocument(issuerDocument, "emitente");
        ValidateKeyMatchesDocument(key, issueDate, issuerDocument, number, series);

        var service = info.Element(Ns + "infCTeNorm")?.Element(Ns + "infServico");
        var values = Required(info, "vPrest");
        decimal serviceValue = Decimal(RequiredValue(values, "vTPrest"), "vTPrest");
        if (serviceValue < 0)
            throw new InvalidDataException("O valor do serviço do CT-e OS não pode ser negativo.");

        string environment = RequiredValue(protocol, "tpAmb") switch
        {
            "1" => "Produção",
            "2" => "Homologação",
            var code => throw new InvalidDataException($"Ambiente do CT-e OS inválido: {code}.")
        };

        return new CteDocumentSummary(
            sourcePath, key, numberText, seriesText,
            issueDate.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
            RequiredValue(issuer, "xNome"), issuerDocument,
            "", "", "", "", "", "", "", "",
            "", service is null ? "" : Value(service, "xDescServ"),
            0m, serviceValue,
            Value(protocol, "nProt"), Value(protocol, "dhRecbto"), Value(protocol, "xMotivo"),
            environment, "67");
    }

    private static void ValidateAccessKey(string key)
    {
        if (key.Length != 44 || key.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException("A chave de acesso do CT-e OS deve conter 44 dígitos.");
        if (key.Substring(20, 2) != "67")
            throw new InvalidDataException("A chave de acesso não identifica o modelo CT-e OS 67.");
        int sum = 0, weight = 2;
        for (int i = 42; i >= 0; i--)
        {
            sum += (key[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        int digit = 11 - sum % 11;
        if (digit >= 10) digit = 0;
        if (digit != key[43] - '0')
            throw new InvalidDataException("A chave de acesso do CT-e OS possui dígito verificador inválido.");
    }

    private static void ValidateKeyMatchesDocument(string key, DateTimeOffset date, string document, int number, int series)
    {
        if (key.Substring(2, 4) != date.ToString("yyMM", CultureInfo.InvariantCulture))
            throw new InvalidDataException("A data de emissão não corresponde à chave de acesso do CT-e OS.");
        if (key.Substring(6, 14) != document.PadLeft(14, '0'))
            throw new InvalidDataException("O documento do emitente não corresponde à chave de acesso do CT-e OS.");
        if (int.Parse(key.Substring(22, 3), CultureInfo.InvariantCulture) != series ||
            int.Parse(key.Substring(25, 9), CultureInfo.InvariantCulture) != number)
            throw new InvalidDataException("Série ou número não corresponde à chave de acesso do CT-e OS.");
    }

    private static void ValidateDocument(string value, string role)
    {
        if (value.Length is not (11 or 14) || value.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException($"Documento do {role} inválido no XML do CT-e OS.");
    }

    private static XElement Required(XElement parent, string name) =>
        parent.Element(Ns + name) ?? throw new InvalidDataException($"O XML do CT-e OS não contém {name}.");

    private static string RequiredValue(XElement parent, string name)
    {
        string value = Value(parent, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"O campo {name} é obrigatório no XML do CT-e OS.")
            : value;
    }

    private static string Value(XElement parent, string name) => parent.Element(Ns + name)?.Value.Trim() ?? "";
    private static string Attr(XElement element, string name) => element.Attribute(name)?.Value.Trim() ?? "";

    private static string FirstValue(XElement parent, params string[] names)
    {
        foreach (string name in names)
        {
            string value = Value(parent, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static decimal Decimal(string value, string field) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var result)
            ? result : throw new InvalidDataException($"O campo numérico {field} é inválido.");

    private static int PositiveInt(string value, string field) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) && result > 0
            ? result : throw new InvalidDataException($"O campo {field} deve ser um número positivo.");

    private static int NonNegativeInt(string value, string field) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) && result >= 0
            ? result : throw new InvalidDataException($"O campo {field} deve ser um número não negativo.");
}
