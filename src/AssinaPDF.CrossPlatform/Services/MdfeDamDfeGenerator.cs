using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Gera uma representação PDF do DAMDFE a partir do XML processado e autorizado.
/// O XML é revalidado antes da emissão e nunca é alterado.
/// </summary>
public static class MdfeDamDfeGenerator
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/mdfe";
    // Use Arial, the standard sans-serif family supported by PDFsharp's platform
    // resolver and the application's DANFE font setup. Lazy creation ensures
    // the shared resolver is configured before PDFsharp creates any XFont.
    private static XFont TitleFont => new("Arial", 15, XFontStyleEx.Bold);
    private static XFont SectionFont => new("Arial", 9, XFontStyleEx.Bold);
    private static XFont LabelFont => new("Arial", 7, XFontStyleEx.Bold);
    private static XFont ValueFont => new("Arial", 8, XFontStyleEx.Regular);
    private static XFont SmallFont => new("Arial", 6.5, XFontStyleEx.Regular);

    public static void Generate(MdfeDocumentSummary summary, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // PDFsharp uses a process-wide resolver initialized by the app bootstrap.
        NfcePdfFontResolverBootstrap.EnsureConfigured();
        if (!File.Exists(summary.SourcePath)) throw new FileNotFoundException("XML original do MDF-e não encontrado.", summary.SourcePath);
        var verified = MdfeXmlReader.ReadFile(summary.SourcePath);
        if (!string.Equals(verified.AccessKey, summary.AccessKey, StringComparison.Ordinal))
            throw new InvalidDataException("A chave do XML não corresponde ao MDF-e selecionado.");
        string source = Path.GetFullPath(summary.SourcePath), target = Path.GetFullPath(outputPath);
        if (string.Equals(source, target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("O PDF não pode substituir o XML original.");

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 };
        XDocument doc;
        using (var stream = File.OpenRead(source))
        using (var reader = XmlReader.Create(stream, settings)) doc = XDocument.Load(reader);
        var info = doc.Root?.Element(Ns + "MDFe")?.Element(Ns + "infMDFe")
            ?? throw new InvalidDataException("Estrutura infMDFe não encontrada.");
        string? folder = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Pasta de destino inválida.");
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(target) + "-" + Guid.NewGuid().ToString("N") + ".tmp.pdf");
        try
        {
            using (var pdf = new PdfDocument())
            {
                pdf.Info.Title = $"DAMDFE {summary.DocumentNumber}";
                pdf.Info.Subject = "Documento Auxiliar do Manifesto Eletrônico de Documentos Fiscais";
                var page = pdf.AddPage(); page.Size = PdfSharp.PageSize.A4;
                using var g = XGraphics.FromPdfPage(page);
                DrawPage(g, info, doc.Root!, summary);
                pdf.Save(temp);
            }
            using (var check = File.OpenRead(temp))
            {
                Span<byte> signature = stackalloc byte[5];
                if (check.Read(signature) != 5 || !signature.SequenceEqual("%PDF-"u8))
                    throw new InvalidDataException("O arquivo gerado não é um PDF válido.");
            }
            File.Move(temp, target, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static void DrawPage(XGraphics g, XElement info, XElement root, MdfeDocumentSummary s)
    {
        const double left = 25, right = 570, width = right - left;
        double y = 22;
        g.DrawRectangle(XPens.Black, left, y, width, 52);
        g.DrawString("DAMDFE", TitleFont, XBrushes.Black, new XRect(left + 10, y + 6, 180, 22), XStringFormats.TopLeft);
        g.DrawString("DOCUMENTO AUXILIAR DO MANIFESTO ELETRÔNICO DE DOCUMENTOS FISCAIS", SmallFont, XBrushes.Black, new XRect(left + 10, y + 29, 330, 12), XStringFormats.TopLeft);
        g.DrawString($"MDF-e Nº {s.DocumentNumber}   SÉRIE {s.Series}", SectionFont, XBrushes.Black, new XRect(left + 350, y + 9, 190, 18), XStringFormats.TopRight);
        g.DrawString(s.Environment == "Homologação" ? "EMITIDO EM AMBIENTE DE HOMOLOGAÇÃO - SEM VALOR FISCAL" : "DOCUMENTO AUXILIAR DO MDF-e", SmallFont, XBrushes.Black, new XRect(left + 250, y + 31, 290, 12), XStringFormats.TopRight);
        y += 58;

        var ide = info.Element(Ns + "ide");
        var emit = info.Element(Ns + "emit");
        Section(g, "IDENTIFICAÇÃO DO EMITENTE", left, ref y, width);
        Box(g, left, y, width, 43);
        Text(g, "RAZÃO SOCIAL", emit?.Element(Ns + "xNome")?.Value, left + 5, y + 3, 320, 17);
        Text(g, "CNPJ / CPF", s.IssuerDocument, left + 335, y + 3, 105, 17);
        Text(g, "INSCRIÇÃO ESTADUAL", V(emit, "IE"), left + 445, y + 3, 95, 17);
        Text(g, "ENDEREÇO", Join(emit?.Element(Ns + "enderEmit"), "xLgr", "nro", "xCpl", "xBairro", "xMun", "UF", "CEP"), left + 5, y + 22, 535, 17);
        y += 47;

        Section(g, "DADOS DO MDF-e", left, ref y, width);
        Box(g, left, y, width, 48);
        Text(g, "CHAVE DE ACESSO", s.AccessKey, left + 5, y + 3, 365, 18);
        Text(g, "MODELO", "58", left + 375, y + 3, 45, 18);
        Text(g, "SÉRIE / NÚMERO", $"{s.Series} / {s.DocumentNumber}", left + 425, y + 3, 115, 18);
        Text(g, "DATA E HORA DE EMISSÃO", s.IssueDate, left + 5, y + 25, 190, 18);
        Text(g, "MODAL", ModalName(s.Modal), left + 200, y + 25, 130, 18);
        Text(g, "TIPO EMITENTE", V(ide, "tpEmit"), left + 335, y + 25, 95, 18);
        Text(g, "UF INÍCIO / FIM", $"{s.OriginState} / {s.DestinationState}", left + 435, y + 25, 105, 18);
        y += 52;

        Section(g, "PERCURSO", left, ref y, width);
        Box(g, left, y, width, 30);
        Text(g, "UF DE CARREGAMENTO", s.OriginState, left + 5, y + 4, 170, 20);
        Text(g, "UF DE DESCARREGAMENTO", s.DestinationState, left + 185, y + 4, 170, 20);
        Text(g, "PERCURSO / ESTADOS", RouteStates(ide), left + 365, y + 4, 175, 20);
        y += 34;

        Section(g, "VEÍCULO, TRAÇÃO E CONDUTOR", left, ref y, width);
        Box(g, left, y, width, 48);
        var road = info.Element(Ns + "infModal")?.Element(Ns + "rodo");
        var vehicle = road?.Element(Ns + "veicTracao");
        Text(g, "PLACA", V(vehicle, "placa"), left + 5, y + 3, 90, 18);
        Text(g, "RENAVAM", V(vehicle, "RENAVAM"), left + 100, y + 3, 120, 18);
        Text(g, "UF", V(vehicle, "UF"), left + 225, y + 3, 55, 18);
        Text(g, "RNTRC", V(road, "RNTRC"), left + 285, y + 3, 100, 18);
        Text(g, "TIPO RODADO / CARROCERIA", $"{V(vehicle, "tpRod")} / {V(vehicle, "tpCar")}", left + 390, y + 3, 150, 18);
        var drivers = road?.Elements(Ns + "condutor").Select(d => $"{V(d, "xNome")} - {First(d, "CPF", "CNPJ")}").ToArray() ?? Array.Empty<string>();
        Text(g, "CONDUTOR(ES)", drivers.Length == 0 ? "Não informado" : string.Join("; ", drivers), left + 5, y + 25, 535, 18);
        y += 52;

        Section(g, "INFORMAÇÕES DA CARGA", left, ref y, width);
        var totals = info.Element(Ns + "tot");
        Box(g, left, y, width, 35);
        Text(g, "VALOR TOTAL DA CARGA", s.CargoValue.ToString("N2", Pt), left + 5, y + 4, 175, 22);
        Text(g, "QUANTIDADE", s.CargoQuantity.ToString("N4", Pt), left + 190, y + 4, 135, 22);
        Text(g, "UNIDADE", UnitName(s.CargoUnit), left + 335, y + 4, 100, 22);
        Text(g, "PRODUTO PREDOMINANTE", V(totals, "xProd"), left + 440, y + 4, 100, 22);
        y += 39;

        Section(g, "DOCUMENTOS FISCAIS VINCULADOS", left, ref y, width);
        var groups = info.Element(Ns + "infDoc")?.Elements(Ns + "infMunDescarga").ToArray() ?? Array.Empty<XElement>();
        int docs = 0;
        foreach (var city in groups)
        {
            string cityName = $"{V(city, "xMunDescarga")} / {V(city, "UF")}";
            var ctes = city.Elements(Ns + "infCTe").ToArray();
            var nfes = city.Elements(Ns + "infNFe").ToArray();
            foreach (var item in ctes) { if (y > 710) { g.DrawString("Demais documentos vinculados constam no XML autorizado.", SmallFont, XBrushes.Black, new XPoint(left + 5, y)); y += 10; break; } g.DrawString($"CT-e · {cityName} · {V(item, "chCTe")}", SmallFont, XBrushes.Black, new XPoint(left + 5, y)); y += 10; docs++; }
            foreach (var item in nfes) { if (y > 710) { g.DrawString("Demais documentos vinculados constam no XML autorizado.", SmallFont, XBrushes.Black, new XPoint(left + 5, y)); y += 10; break; } g.DrawString($"NF-e · {cityName} · {V(item, "chNFe")}", SmallFont, XBrushes.Black, new XPoint(left + 5, y)); y += 10; docs++; }
        }
        if (docs == 0) { g.DrawString("Nenhum documento individual listado no grupo infDoc.", ValueFont, XBrushes.Black, new XPoint(left + 5, y)); y += 13; }
        y += 4;

        if (y > 745) y = 745;
        Section(g, "PROTOCOLO DE AUTORIZAÇÃO", left, ref y, width);
        var protocol = root.Element(Ns + "protMDFe")?.Element(Ns + "infProt");
        Box(g, left, y, width, 35);
        Text(g, "PROTOCOLO", V(protocol, "nProt"), left + 5, y + 3, 180, 22);
        Text(g, "DATA / HORA", V(protocol, "dhRecbto"), left + 190, y + 3, 180, 22);
        Text(g, "STATUS", V(protocol, "cStat") + " - " + V(protocol, "xMotivo"), left + 375, y + 3, 165, 22);
        g.DrawString("Documento gerado localmente a partir do XML autorizado. Confira o leiaute e os dados antes de utilizar.", SmallFont, XBrushes.Gray, new XPoint(left, 825));
        g.DrawString("Chave de acesso: " + s.AccessKey, SmallFont, XBrushes.Black, new XPoint(left, 837));
    }

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static void Section(XGraphics g, string title, double x, ref double y, double w)
    { g.DrawString(title, SectionFont, XBrushes.Black, new XPoint(x, y + 8)); y += 12; }
    private static void Box(XGraphics g, double x, double y, double w, double h) => g.DrawRectangle(XPens.Black, x, y, w, h);
    private static void Text(XGraphics g, string label, string? value, double x, double y, double w, double h)
    {
        g.DrawString(label, LabelFont, XBrushes.Gray, new XRect(x, y, w, 8), XStringFormats.TopLeft);
        string clean = string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        if (clean.Length > Math.Max(12, (int)(w / 4.1))) clean = clean[..Math.Max(12, (int)(w / 4.1) - 1)] + "…";
        g.DrawString(clean, ValueFont, XBrushes.Black, new XRect(x, y + 8, w, h - 8), XStringFormats.TopLeft);
    }
    private static string V(XElement? e, string n) => e?.Element(Ns + n)?.Value.Trim() ?? "";
    private static string First(XElement? e, params string[] names) { foreach (var n in names) { var v = V(e, n); if (v.Length > 0) return v; } return ""; }
    private static string Join(XElement? e, params string[] names) => string.Join(", ", names.Select(n => V(e, n)).Where(v => v.Length > 0));
    private static string RouteStates(XElement? ide) => string.Join(" · ", ide?.Element(Ns + "infPercurso")?.Elements(Ns + "UFPer").Select(x => x.Value) ?? Enumerable.Empty<string>());
    private static string ModalName(string v) => v switch { "1" => "Rodoviário", "2" => "Aéreo", "3" => "Aquaviário", "4" => "Ferroviário", _ => v };
    private static string UnitName(string v) => v switch { "01" => "KG", "02" => "TON", "03" => "UNIDADE", "04" => "LITROS", "05" => "M³", _ => v };
}
