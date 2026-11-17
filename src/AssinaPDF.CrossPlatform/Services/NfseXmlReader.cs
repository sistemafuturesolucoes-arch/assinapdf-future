using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AssinaPDF.MacPrototype.Services;

public sealed record NfseDocumentSummary(
    string SourcePath,
    string AccessKey,
    string Number,
    string Competence,
    string ProcessedAt,
    string DpsNumber,
    string DpsSeries,
    string DpsIssuedAt,
    string IssuerType,
    string Status,
    string Purpose,
    string Environment,
    string GeneratingEnvironment,
    string IssuerName,
    string IssuerDocument,
    string IssuerMunicipalRegistration,
    string IssuerPhone,
    string IssuerEmail,
    string IssuerAddress,
    string IssuerCity,
    string IssuerState,
    string RecipientName,
    string RecipientDocument,
    string RecipientPhone,
    string RecipientEmail,
    string RecipientAddress,
    string RecipientCity,
    string RecipientState,
    string ServiceCodeNational,
    string ServiceCodeMunicipal,
    string NbsCode,
    string ServiceLocation,
    string ServiceDescription,
    string DestinationDetails,
    string IntermediaryDetails,
    decimal ServiceAmount,
    decimal? UnconditionalDiscount,
    decimal? ConditionalDiscount,
    decimal NetAmount,
    decimal? IssqnAmount,
    decimal? FederalWithholdings,
    decimal? IbscbsAmount,
    string AdditionalInformation)
{
    public decimal? TotalRetentions { get; init; }

    public string DisplayName =>
        $"NFS-e {Number} · {IssuerName} · {NetAmount.ToString("C", CultureInfo.GetCultureInfo("pt-BR"))}";
}

public static class NfseXmlReader
{
    public const string NationalNamespace = "http://www.sped.fazenda.gov.br/nfse";
    private static readonly XNamespace Ns = NationalNamespace;
    private const long MaxXmlCharacters = 10_000_000;

    public static NfseDocumentSummary ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, SecureSettings());
        return ParseDocument(XDocument.Load(reader, LoadOptions.None), Path.GetFullPath(path));
    }

    public static NfseDocumentSummary Parse(string xml, string sourcePath = "")
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
        MaxCharactersInDocument = MaxXmlCharacters,
        MaxCharactersFromEntities = 1024,
        IgnoreComments = true,
        IgnoreWhitespace = true
    };

    private static NfseDocumentSummary ParseDocument(XDocument document, string sourcePath)
    {
        var root = document.Root ?? throw new InvalidDataException("O XML está vazio.");
        if (root.Name != Ns + "NFSe")
            throw new InvalidDataException("Arquivo não reconhecido como NFS-e do Padrão Nacional (elemento NFSe).");
        if (root.Attribute("versao") is null || string.IsNullOrWhiteSpace(root.Attribute("versao")!.Value))
            throw new InvalidDataException("O XML nacional não informa a versão da NFS-e.");

        var info = root.Element(Ns + "infNFSe")
            ?? throw new InvalidDataException("O XML nacional não contém infNFSe.");
        string accessKey = Attr(info, "Id");
        if (accessKey.StartsWith("NFS", StringComparison.Ordinal)) accessKey = accessKey[3..];
        if (accessKey.Length != 50 || accessKey.Any(c => !char.IsAsciiLetterOrDigit(c) || char.IsLower(c)))
            throw new InvalidDataException("A chave de acesso da NFS-e nacional deve conter 50 caracteres alfanuméricos em maiúsculas.");

        var dps = Child(info, "DPS");
        var infDps = Child(dps, "infDPS");
        string environmentCode = Required(infDps, "tpAmb");
        string environment = environmentCode switch
        {
            "1" => "Produção",
            "2" => "Homologação",
            _ => throw new InvalidDataException($"Ambiente da NFS-e inválido: {environmentCode}.")
        };

        var prest = Child(infDps, "prest");
        var toma = OptionalChild(infDps, "toma");
        var ibsBlock = OptionalChild(infDps, "IBSCBS");
        // NT 009 v1.01 reposicionou indDest/dest para a raiz de infDPS,
        // logo após toma. Mantém fallback para XMLs de leiautes anteriores.
        var dest = OptionalChild(infDps, "dest") ?? OptionalChild(ibsBlock, "dest");
        var intermediary = OptionalChild(infDps, "interm");
        var serv = Child(infDps, "serv");
        var cServ = Child(serv, "cServ");
        var locPrest = OptionalChild(serv, "locPrest");
        var values = Child(info, "valores");
        var trib = OptionalChild(values, "trib");
        var tribMun = trib is null ? null : OptionalChild(trib, "tribMun");
        var tribFed = trib is null ? null : OptionalChild(trib, "tribFed");
        var tribIbs = trib is null ? null : OptionalChild(trib, "totTrib");
        // Os totalizadores oficiais de IBS/CBS ficam em infNFSe/IBSCBS/totCIBS.
        // O grupo totTrib legado é mantido como fallback para XMLs anteriores.
        var ibsNfse = OptionalChild(info, "IBSCBS");
        var ibsTotals = OptionalChild(ibsNfse, "totCIBS");
        var emit = OptionalChild(info, "emit");

        string issuerDocument = First(prest, "CNPJ", "CPF", "NIF");
        if (string.IsNullOrWhiteSpace(issuerDocument))
            issuerDocument = First(emit, "CNPJ", "CPF", "NIF");
        if (string.IsNullOrWhiteSpace(issuerDocument))
            throw new InvalidDataException("O XML nacional não identifica o prestador/emitente.");
        if (string.IsNullOrWhiteSpace(Value(prest, "NIF")) && string.IsNullOrWhiteSpace(Value(emit, "NIF")))
            ValidateBrazilianDocument(issuerDocument, "prestador");

        string issuerName = First(prest, "xNome");
        if (string.IsNullOrWhiteSpace(issuerName)) issuerName = First(emit, "xNome");
        if (string.IsNullOrWhiteSpace(issuerName))
            throw new InvalidDataException("O XML nacional não informa o nome do prestador.");

        string processedAt = Value(info, "dhProc");
        string number = Required(info, "nNFSe");
        string competence = Required(infDps, "dCompet");
        string dpsNumber = Required(infDps, "nDPS");
        string dpsSeries = Required(infDps, "serie");
        string dpsIssuedAt = Required(infDps, "dhEmi");

        var prestAddress = ReadAddress(OptionalChild(prest, "end"));
        var tomaAddress = ReadAddress(OptionalChild(toma, "end"));
        var prestCity = ReadCity(OptionalChild(prest, "end"));
        var tomaCity = ReadCity(OptionalChild(toma, "end"));

        string serviceAmountText = FirstDeepValue(values, "vServ", "vBC");
        if (string.IsNullOrWhiteSpace(serviceAmountText))
            throw new InvalidDataException("O XML nacional não informa o valor da prestação de serviço.");
        string netAmountText = FirstDeepValue(values, "vLiq", "vLiqNFS");
        if (string.IsNullOrWhiteSpace(netAmountText))
            throw new InvalidDataException("O XML nacional não informa o valor líquido da NFS-e.");
        decimal serviceAmount = Decimal(serviceAmountText);
        decimal? unconditionalDiscount = OptionalDecimal(DeepValue(values, "vDescIncond"));
        decimal? conditionalDiscount = OptionalDecimal(DeepValue(values, "vDescCond"));
        decimal netAmount = Decimal(netAmountText);
        decimal? issqnAmount = OptionalDecimal(FirstDeepValue(tribMun, "vISSQN", "vISSQNapur"));
        decimal? federalWithholdings = SumDeepDecimalsOrNull(tribFed, "vRetIRRF", "vRetCP", "vRetCSLL", "vRetPIS", "vRetCOFINS");
        decimal? ibscbsAmount = SumDeepDecimalsOrNull(ibsTotals, "vIBSTot", "vCBS")
            ?? OptionalDecimal(DeepValue(tribIbs, "vTotIBSCBS"))
            ?? SumDeepDecimalsOrNull(tribIbs, "vIBS", "vCBS");

        string serviceDescription = FirstValue(cServ, "xDescServ");
        if (string.IsNullOrWhiteSpace(serviceDescription))
            serviceDescription = Value(serv, "xDescServ");
        if (string.IsNullOrWhiteSpace(serviceDescription))
            throw new InvalidDataException("O XML nacional não contém a descrição do serviço.");

        string destinationDetails = PartyDetails(dest, "DESTINATÁRIO DA OPERAÇÃO NÃO IDENTIFICADO NA NFS-e");
        string intermediaryDetails = PartyDetails(intermediary, "INTERMEDIÁRIO DA OPERAÇÃO NÃO IDENTIFICADO NA NFS-e");
        string purposeCode = FirstValue(infDps, "finNFSe");
        if (string.IsNullOrWhiteSpace(purposeCode))
            purposeCode = FirstValue(ibsBlock, "finNFSe");
        if (string.IsNullOrWhiteSpace(purposeCode))
            throw new InvalidDataException("O XML nacional não informa a finalidade da NFS-e (finNFSe).");
        string purpose = PurposeDescription(purposeCode);
        string taxDetails = ExtractTaxDetails(values, tribMun, tribFed, tribIbs, ibsTotals);
        string serviceLocationCode = FirstValue(locPrest, "xCidade", "cLocPrestacao");
        string serviceLocation = string.IsNullOrWhiteSpace(serviceLocationCode)
            ? ""
            : FormatMunicipality(serviceLocationCode) +
              (string.IsNullOrWhiteSpace(Value(locPrest, "UF")) ? "" : " / " + Value(locPrest, "UF"));
        string additional = string.Join(" · ", new[]
        {
            Labeled("Número DFSe", Value(info, "nDFSe")),
            Labeled("Aplicação geradora", Value(info, "verAplic")),
            Labeled("Tipo de emissão", EmissionType(Value(info, "tpEmis"))),
            Labeled("Processo de emissão da DPS", ProcessType(Value(infDps, "procEmi"))),
            Labeled("Tipo de NFS-e de débito", Value(infDps, "tpNFSeDebito")),
            Labeled("Tipo de NFS-e de crédito", Value(infDps, "tpNFSeCredito")),
            Value(infDps, "xInfComp"),
            Value(serv, "xInfComp"),
            Value(values, "xOutInf"),
            taxDetails
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        string recipientName = FirstValue(toma, "xNome");
        if (toma is null)
            recipientName = "TOMADOR/ADQUIRENTE DA OPERAÇÃO NÃO IDENTIFICADO NA NFS-e";

        return new NfseDocumentSummary(
            sourcePath, accessKey, number, competence, processedAt,
            dpsNumber, dpsSeries, dpsIssuedAt, Required(infDps, "tpEmit"),
            StatusDescription(Required(info, "cStat")), purpose,
            environment, GeneratingEnvironment(Required(info, "ambGer")),
            issuerName, issuerDocument, Value(prest, "IM"), Value(prest, "fone"),
            Value(prest, "email"), prestAddress, prestCity.Name, prestCity.State,
            recipientName, First(toma, "CNPJ", "CPF", "NIF"),
            Value(toma, "fone"), Value(toma, "email"), tomaAddress, tomaCity.Name, tomaCity.State,
            Value(cServ, "cTribNac"), Value(cServ, "cTribMun"), Value(cServ, "cNBS"),
            serviceLocation, serviceDescription, destinationDetails, intermediaryDetails, serviceAmount, unconditionalDiscount, conditionalDiscount,
            netAmount, issqnAmount, federalWithholdings, ibscbsAmount, additional)
        {
            TotalRetentions = OptionalDecimal(Value(values, "vTotalRet"))
        };
    }

    private static string Labeled(string label, string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $"{label}: {value}";

    private static string ExtractTaxDetails(params XElement?[] groups)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            "vServ", "vBC", "vDescIncond", "vDescCond", "vLiq", "xOutInf",
            "vISSQN", "vISSQNapur", "vRetIRRF", "vRetCP", "vRetCSLL", "vRetPIS",
            "vRetCOFINS", "vIBS", "vCBS", "vTotIBSCBS"
        };
        var details = groups
            .Where(group => group is not null)
            .SelectMany(group => group!.Descendants())
            .Where(element => !element.HasElements && !excluded.Contains(element.Name.LocalName))
            .Select(element => $"{element.Name.LocalName}: {element.Value.Trim()}")
            .Where(value => !value.EndsWith(": ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return details.Length == 0 ? "" : "Detalhamento tributário do XML: " + string.Join("; ", details);
    }

    private static string EmissionType(string code) => code switch
    {
        "1" => "Emissão direta no modelo da NFS-e Nacional",
        "2" => "Emissão original em leiaute municipal, transcrita para o modelo nacional",
        _ => code
    };

    private static string ProcessType(string code) => code switch
    {
        "1" => "Aplicativo do contribuinte via Web Service",
        "2" => "Aplicativo disponibilizado pelo fisco (Web)",
        "3" => "Aplicativo disponibilizado pelo fisco (App)",
        _ => code
    };

    private static string PurposeDescription(string code) => code switch
    {
        "0" => "NFS-e regular",
        "1" => "NFS-e de crédito",
        "2" => "NFS-e de débito",
        _ => code
    };

    private static string StatusDescription(string code) => code switch
    {
        "100" => "NFS-e gerada",
        "101" => "NFS-e de substituição gerada",
        "102" => "NFS-e de decisão judicial ou administrativa",
        "103" => "NFS-e avulsa",
        "107" => "NFS-e MEI",
        "108" => "Nota de crédito",
        "109" => "Nota de débito",
        _ => "Situação " + code
    };

    private static string GeneratingEnvironment(string code) => code switch
    {
        "1" => "Sistema próprio do município",
        "2" => "SEFIN Nacional NFS-e",
        _ => code
    };

    private static string PartyDetails(XElement? party, string missingText)
    {
        if (party is null) return missingText;
        var address = ReadAddress(OptionalChild(party, "end"));
        var city = ReadCity(OptionalChild(party, "end"));
        string details = string.Join(" · ", new[]
        {
            First(party, "xNome", "CNPJ", "CPF", "NIF"),
            First(party, "CNPJ", "CPF", "NIF"),
            Value(party, "fone"),
            string.Join(" / ", new[] { city.Name, city.State }.Where(x => !string.IsNullOrWhiteSpace(x))),
            address,
            Value(party, "email")
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return details.Length == 0 ? missingText : details;
    }

    private static void ValidateBrazilianDocument(string document, string role)
    {
        bool cpf = document.Length == 11 && document.All(char.IsAsciiDigit);
        bool cnpj = document.Length == 14 &&
            document.Take(12).All(c => char.IsAsciiLetterOrDigit(c) && !char.IsLower(c)) &&
            document.Skip(12).All(char.IsAsciiDigit);
        if (!cpf && !cnpj)
            throw new InvalidDataException($"O CPF/CNPJ do {role} possui formato inválido no XML nacional.");
    }

    private static XElement Child(XElement? parent, string name) =>
        OptionalChild(parent, name) ?? throw new InvalidDataException($"O XML nacional não contém o campo obrigatório {name}.");

    private static XElement? OptionalChild(XElement? parent, string name) => parent?.Element(Ns + name);
    private static string Value(XElement? parent, string name) => OptionalChild(parent, name)?.Value.Trim() ?? "";
    private static string Required(XElement parent, string name)
    {
        string value = Value(parent, name);
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"O campo {name} está ausente na NFS-e.") : value;
    }
    private static string Attr(XElement element, string name) => ((string?)element.Attribute(name) ?? "").Trim();
    private static string First(XElement? parent, params string[] names) => FirstValue(parent, names);
    private static string FirstValue(XElement? parent, params string[] names)
    {
        foreach (string name in names)
        {
            string value = Value(parent, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }
    private static decimal Decimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        return decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var result)
            ? result : throw new InvalidDataException($"Valor monetário inválido na NFS-e: {value}.");
    }
    private static decimal? OptionalDecimal(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Decimal(value);

    private static decimal? SumDeepDecimalsOrNull(XElement? parent, params string[] names)
    {
        var values = names.Select(name => DeepValue(parent, name))
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return values.Length == 0 ? null : values.Sum(Decimal);
    }

    private static string DeepValue(XElement? parent, string name) =>
        parent?.DescendantsAndSelf().FirstOrDefault(element => element.Name == Ns + name)?.Value.Trim() ?? "";

    private static string FirstDeepValue(XElement? parent, params string[] names)
    {
        foreach (string name in names)
        {
            string value = DeepValue(parent, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }
    private static (string Name, string State) ReadCity(XElement? end)
    {
        var national = OptionalChild(end, "endNac");
        var external = OptionalChild(end, "endExt");
        string city = FirstValue(national, "xMun", "xCidade", "cMun");
        if (string.IsNullOrWhiteSpace(city)) city = Value(external, "xCidade");
        return (FormatMunicipality(city), FirstValue(national, "UF") is { Length: > 0 } uf ? uf : Value(external, "xEstProvReg"));
    }

    private static string FormatMunicipality(string value) =>
        value.Length == 7 && value.All(char.IsAsciiDigit) ? "IBGE " + value : value;
    private static string ReadAddress(XElement? end)
    {
        if (end is null) return "";
        var national = OptionalChild(end, "endNac");
        var external = OptionalChild(end, "endExt");
        var address = national ?? external;
        return string.Join(", ", new[]
        {
            Value(address, "xLgr"), Value(address, "nro"), Value(address, "xCpl"),
            Value(address, "xBairro"), FormatMunicipality(FirstValue(address, "xCidade", "cMun")), Value(address, "UF"),
            Value(address, "CEP")
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}