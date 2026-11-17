using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AssinaPDF.MacPrototype.Services;

public sealed record MdfeDocumentSummary(
    string SourcePath,
    string AccessKey,
    string DocumentNumber,
    string Series,
    string IssueDate,
    string IssuerName,
    string IssuerDocument,
    string OriginState,
    string DestinationState,
    string Modal,
    string VehiclePlate,
    string VehicleState,
    string DriverName,
    string DriverDocument,
    decimal CargoValue,
    string CargoUnit,
    decimal CargoQuantity,
    int CteCount,
    int NfeCount,
    string ProtocolNumber,
    string AuthorizationDate,
    string Environment,
    string Model = "58");

/// <summary>
/// Leitor seguro de MDF-e processado (mdfeProc), modelo 58.
/// Não aceita XML sem protocolo de autorização válido.
/// </summary>
public static class MdfeXmlReader
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/mdfe";

    public static MdfeDocumentSummary ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Parse(stream, Path.GetFullPath(path));
    }

    public static MdfeDocumentSummary Parse(string xml, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, SecureSettings());
        return ParseDocument(XDocument.Load(reader, LoadOptions.None), sourcePath);
    }

    private static MdfeDocumentSummary Parse(Stream stream, string sourcePath)
    {
        using var reader = XmlReader.Create(stream, SecureSettings());
        return ParseDocument(XDocument.Load(reader, LoadOptions.None), sourcePath);
    }

    private static XmlReaderSettings SecureSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = 20_000_000
    };

    private static MdfeDocumentSummary ParseDocument(XDocument document, string sourcePath)
    {
        var root = document.Root ?? throw new InvalidDataException("O XML está vazio.");
        if (root.Name != Ns + "mdfeProc")
            throw new InvalidDataException("Arquivo não reconhecido como MDF-e processado (mdfeProc).");
        if (Attr(root, "versao") != "3.00")
            throw new InvalidDataException("Versão do mdfeProc não suportada. Esperada versão 3.00.");

        var mdfe = Required(root, "MDFe");
        var info = Required(mdfe, "infMDFe");
        if (Attr(info, "versao") != "3.00")
            throw new InvalidDataException("A versão de infMDFe não é 3.00.");

        string key = Attr(info, "Id");
        if (key.StartsWith("MDFe", StringComparison.Ordinal)) key = key[4..];
        ValidateAccessKey(key);

        var ide = Required(info, "ide");
        if (Value(ide, "mod") != "58")
            throw new InvalidDataException("O XML não corresponde ao modelo MDF-e 58.");

        string numberText = RequiredValue(ide, "nMDF");
        string seriesText = RequiredValue(ide, "serie");
        int number = PositiveInt(numberText, "nMDF");
        int series = NonNegativeInt(seriesText, "serie");
        string issueText = RequiredValue(ide, "dhEmi");
        if (!DateTimeOffset.TryParse(issueText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var issueDate))
            throw new InvalidDataException("A data de emissão do MDF-e é inválida.");

        var protocol = Required(Required(root, "protMDFe"), "infProt");
        if (!string.Equals(key, RequiredValue(protocol, "chMDFe"), StringComparison.Ordinal))
            throw new InvalidDataException("A chave do protocolo não corresponde à chave do MDF-e.");
        string status = RequiredValue(protocol, "cStat");
        if (status is not ("100" or "150"))
            throw new InvalidDataException($"MDF-e não autorizado. Status {status}: {Value(protocol, "xMotivo")}");

        var issuer = Required(info, "emit");
        string issuerDocument = FirstValue(issuer, "CNPJ", "CPF");
        ValidateDocument(issuerDocument);
        ValidateKeyMatchesDocument(key, issueDate, issuerDocument, number, series);

        string environmentCode = RequiredValue(protocol, "tpAmb");
        string environment = environmentCode switch
        {
            "1" => "Produção",
            "2" => "Homologação",
            _ => throw new InvalidDataException($"Ambiente do MDF-e inválido: {environmentCode}.")
        };

        var totals = Required(info, "tot");
        var road = info.Element(Ns + "infModal")?.Element(Ns + "rodo");
        var vehicle = road?.Element(Ns + "veicTracao");
        var driver = road?.Elements(Ns + "condutor").FirstOrDefault();
        var cargoValue = Decimal(Value(totals, "vCarga"), "vCarga");
        var cargoQuantity = Decimal(Value(totals, "qCarga"), "qCarga");
        if (cargoValue < 0 || cargoQuantity < 0)
            throw new InvalidDataException("Os valores e quantidades da carga não podem ser negativos.");

        int cteCount = CountDocuments(info, "infDoc", "infMunDescarga", "infCTe");
        int nfeCount = CountDocuments(info, "infDoc", "infMunDescarga", "infNFe");

        return new MdfeDocumentSummary(
            sourcePath,
            key,
            numberText,
            seriesText,
            issueDate.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
            RequiredValue(issuer, "xNome"),
            issuerDocument,
            RequiredValue(ide, "UFIni"),
            RequiredValue(ide, "UFFim"),
            RequiredValue(ide, "modal"),
            vehicle is null ? "" : Value(vehicle, "placa"),
            vehicle is null ? "" : Value(vehicle, "UF"),
            driver is null ? "" : Value(driver, "xNome"),
            driver is null ? "" : FirstValue(driver, "CPF", "CNPJ"),
            cargoValue,
            Value(totals, "cUnid"),
            cargoQuantity,
            cteCount,
            nfeCount,
            Value(protocol, "nProt"),
            Value(protocol, "dhRecbto"),
            environment);
    }

    private static int CountDocuments(XElement info, string first, string second, string documentName)
    {
        var group = info.Element(Ns + first);
        if (group is null) return 0;
        return group.Elements(Ns + second)
            .Sum(city => city.Elements(Ns + documentName).Count());
    }

    private static void ValidateAccessKey(string key)
    {
        if (key.Length != 44 || key.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException("A chave de acesso do MDF-e deve conter 44 dígitos.");
        if (key.Substring(20, 2) != "58")
            throw new InvalidDataException("A chave de acesso não identifica o modelo MDF-e 58.");
        int sum = 0, weight = 2;
        for (int i = 42; i >= 0; i--)
        {
            sum += (key[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        int digit = 11 - sum % 11;
        if (digit >= 10) digit = 0;
        if (digit != key[43] - '0')
            throw new InvalidDataException("A chave de acesso do MDF-e possui dígito verificador inválido.");
    }

    private static void ValidateKeyMatchesDocument(string key, DateTimeOffset date, string document, int number, int series)
    {
        if (key.Substring(2, 4) != date.ToString("yyMM", CultureInfo.InvariantCulture))
            throw new InvalidDataException("A data de emissão não corresponde à chave de acesso do MDF-e.");
        if (key.Substring(6, 14) != document.PadLeft(14, '0'))
            throw new InvalidDataException("O documento do emitente não corresponde à chave de acesso do MDF-e.");
        if (int.Parse(key.Substring(22, 3), CultureInfo.InvariantCulture) != series ||
            int.Parse(key.Substring(25, 9), CultureInfo.InvariantCulture) != number)
            throw new InvalidDataException("Série ou número não corresponde à chave de acesso do MDF-e.");
    }

    private static void ValidateDocument(string value)
    {
        if (value.Length is not (11 or 14) || value.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException("Documento do emitente inválido no XML do MDF-e.");
    }

    private static XElement Required(XElement parent, string name) =>
        parent.Element(Ns + name) ?? throw new InvalidDataException($"O XML do MDF-e não contém {name}.");

    private static string RequiredValue(XElement parent, string name)
    {
        string value = Value(parent, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"O campo {name} é obrigatório no XML do MDF-e.")
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