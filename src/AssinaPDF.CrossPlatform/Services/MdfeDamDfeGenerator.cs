using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Gera o DAMDFE a partir do XML processado e autorizado.
/// O XML original é validado novamente e nunca é alterado.
/// </summary>
public static class MdfeDamDfeGenerator
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/mdfe";
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private const double Left = 28, Right = 567, Width = Right - Left;
    private const double Bottom = 805;

    private static XFont TitleFont => new("Liberation Serif", 15, XFontStyleEx.Bold);
    private static XFont SectionFont => new("Liberation Serif", 9, XFontStyleEx.Bold);
    private static XFont LabelFont => new("Liberation Serif", 6.5, XFontStyleEx.Bold);
    private static XFont ValueFont => new("Liberation Serif", 7.5, XFontStyleEx.Regular);
    private static XFont SmallFont => new("Liberation Serif", 6.5, XFontStyleEx.Regular);

    public static void Generate(MdfeDocumentSummary summary, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        NfcePdfFontResolverBootstrap.EnsureConfigured();
        if (!File.Exists(summary.SourcePath))
            throw new FileNotFoundException("XML original do MDF-e não encontrado.", summary.SourcePath);

        var verified = MdfeXmlReader.ReadFile(summary.SourcePath);
        if (!string.Equals(verified.AccessKey, summary.AccessKey, StringComparison.Ordinal))
            throw new InvalidDataException("A chave do XML não corresponde ao MDF-e selecionado.");

        string source = Path.GetFullPath(summary.SourcePath);
        string target = Path.GetFullPath(outputPath);
        if (string.Equals(source, target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("O PDF não pode substituir o XML original.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 20_000_000
        };
        XDocument doc;
        using (var stream = File.OpenRead(source))
        using (var reader = XmlReader.Create(stream, settings))
            doc = XDocument.Load(reader);

        var root = doc.Root ?? throw new InvalidDataException("Raiz do XML não encontrada.");
        var info = root.Element(Ns + "MDFe")?.Element(Ns + "infMDFe")
            ?? throw new InvalidDataException("Estrutura infMDFe não encontrada.");
        string? folder = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("Pasta de destino inválida.");
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(target) + "-" + Guid.NewGuid().ToString("N") + ".tmp.pdf");

        try
        {
            using (var pdf = new PdfDocument())
            {
                pdf.Info.Title = $"DAMDFE {summary.DocumentNumber}";
                pdf.Info.Subject = "Documento Auxiliar do Manifesto Eletrônico de Documentos Fiscais";
                DrawFirstPage(pdf, info, root, summary);
                DrawDocumentPages(pdf, info, summary);
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
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void DrawFirstPage(PdfDocument pdf, XElement info, XElement root, MdfeDocumentSummary s)
    {
        var page = NewPage(pdf);
        using var g = XGraphics.FromPdfPage(page);
        double y = DrawHeader(g, s, 1, 1);

        var emit = info.Element(Ns + "emit");
        var ide = info.Element(Ns + "ide");
        Section(g, "IDENTIFICAÇÃO DO EMITENTE", ref y);
        Box(g, y, 44);
        Field(g, "RAZÃO SOCIAL", V(emit, "xNome"), Left + 5, y + 3, 315);
        Field(g, "CNPJ / CPF", s.IssuerDocument, Left + 325, y + 3, 105);
        Field(g, "INSCRIÇÃO ESTADUAL", V(emit, "IE"), Left + 435, y + 3, 99);
        Field(g, "ENDEREÇO", Join(emit?.Element(Ns + "enderEmit"), "xLgr", "nro", "xCpl", "xBairro", "xMun", "UF", "CEP"), Left + 5, y + 23, 529);
        y += 48;

        Section(g, "DADOS DO MDF-e", ref y);
        Box(g, y, 54);
        Field(g, "CHAVE DE ACESSO", s.AccessKey, Left + 5, y + 3, 365);
        Field(g, "MODELO", "58", Left + 375, y + 3, 45);
        Field(g, "SÉRIE / NÚMERO", $"{s.Series} / {s.DocumentNumber}", Left + 425, y + 3, 109);
        Field(g, "DATA E HORA DE EMISSÃO", s.IssueDate, Left + 5, y + 27, 190);
        Field(g, "MODAL", ModalName(s.Modal), Left + 200, y + 27, 115);
        Field(g, "TIPO EMITENTE", V(ide, "tpEmit"), Left + 320, y + 27, 95);
        Field(g, "UF INÍCIO / FIM", $"{s.OriginState} / {s.DestinationState}", Left + 420, y + 27, 114);
        y += 58;

        Section(g, "PERCURSO", ref y);
        Box(g, y, 34);
        Field(g, "UF DE CARREGAMENTO", s.OriginState, Left + 5, y + 5, 165);
        Field(g, "UF DE DESCARREGAMENTO", s.DestinationState, Left + 180, y + 5, 165);
        Field(g, "UF(s) DO PERCURSO", RouteStates(ide), Left + 355, y + 5, 179);
        y += 38;

        DrawModalSection(g, info, s, ref y);

        Section(g, "INFORMAÇÕES DA CARGA", ref y);
        var totals = info.Element(Ns + "tot");
        var prod = info.Element(Ns + "prodPred");
        Box(g, y, 44);
        Field(g, "VALOR TOTAL DA CARGA", s.CargoValue.ToString("N2", Pt), Left + 5, y + 4, 145);
        Field(g, "QUANTIDADE TOTAL", s.CargoQuantity.ToString("N4", Pt), Left + 160, y + 4, 130);
        Field(g, "UNIDADE", UnitName(s.CargoUnit), Left + 300, y + 4, 75);
        Field(g, "TIPO DE CARGA", V(prod, "tpCarga"), Left + 385, y + 4, 65);
        Field(g, "PRODUTO PREDOMINANTE", V(prod, "xProd"), Left + 5, y + 24, 529);
        y += 48;

        Section(g, "RESUMO DOS DOCUMENTOS FISCAIS VINCULADOS", ref y);
        Box(g, y, 31);
        Field(g, "CT-e", s.CteCount.ToString(Pt), Left + 5, y + 5, 110);
        Field(g, "NF-e", s.NfeCount.ToString(Pt), Left + 125, y + 5, 110);
        Field(g, "TOTAL DE DOCUMENTOS", (s.CteCount + s.NfeCount).ToString(Pt), Left + 245, y + 5, 180);
        y += 35;

        Section(g, "PROTOCOLO DE AUTORIZAÇÃO", ref y);
        var protocol = root.Element(Ns + "protMDFe")?.Element(Ns + "infProt");
        Box(g, y, 37);
        Field(g, "PROTOCOLO", V(protocol, "nProt"), Left + 5, y + 4, 170);
        Field(g, "DATA / HORA", V(protocol, "dhRecbto"), Left + 185, y + 4, 180);
        Field(g, "STATUS", $"{V(protocol, "cStat")} - {V(protocol, "xMotivo")}", Left + 375, y + 4, 159);
        y += 41;

        var observation = V(info.Element(Ns + "infAdic"), "infCpl");
        if (!string.IsNullOrWhiteSpace(observation) && y < Bottom - 30)
        {
            Section(g, "OBSERVAÇÕES", ref y);
            DrawWrapped(g, observation, Left + 4, y, Width - 8, Math.Min(55, Bottom - y - 20), ValueFont);
        }
        DrawFooter(g, s, 1, 1);
    }

    private static void DrawModalSection(XGraphics g, XElement info, MdfeDocumentSummary s, ref double y)
    {
        string modal = s.Modal;
        if (modal == "1")
        {
            Section(g, "VEÍCULO, TRAÇÃO E CONDUTOR — MODAL RODOVIÁRIO", ref y);
            var road = info.Element(Ns + "infModal")?.Element(Ns + "rodo");
            var vehicle = road?.Element(Ns + "veicTracao");
            Box(g, y, 53);
            Field(g, "PLACA", V(vehicle, "placa"), Left + 5, y + 3, 82);
            Field(g, "RENAVAM", V(vehicle, "RENAVAM"), Left + 94, y + 3, 100);
            Field(g, "UF", V(vehicle, "UF"), Left + 200, y + 3, 45);
            Field(g, "RNTRC", V(road, "RNTRC"), Left + 250, y + 3, 100);
            Field(g, "TIPO RODADO / CARROCERIA", $"{V(vehicle, "tpRod")} / {V(vehicle, "tpCar")}", Left + 355, y + 3, 179);
            string drivers = string.Join("; ", road?.Elements(Ns + "condutor").Select(d => $"{V(d, "xNome")} — {First(d, "CPF", "CNPJ")}") ?? Enumerable.Empty<string>());
            Field(g, "CONDUTOR(ES)", drivers, Left + 5, y + 28, 529);
            y += 57;
        }
        else
        {
            Section(g, $"DADOS DO MODAL — {ModalName(modal).ToUpperInvariant()}", ref y);
            var modalData = info.Element(Ns + "infModal");
            Box(g, y, 42);
            Field(g, "INFORMAÇÕES DO MODAL", ModalDescription(modalData), Left + 5, y + 4, 529);
            y += 46;
        }
    }

    private static void DrawDocumentPages(PdfDocument pdf, XElement info, MdfeDocumentSummary s)
    {
        var rows = new List<string>();
        var groups = info.Element(Ns + "infDoc")?.Elements(Ns + "infMunDescarga") ?? Enumerable.Empty<XElement>();
        foreach (var city in groups)
        {
            string destination = Join(city, "xMunDescarga", "UF");
            foreach (var item in city.Elements(Ns + "infCTe"))
                rows.Add($"CT-e     {destination}     {V(item, "chCTe")}");
            foreach (var item in city.Elements(Ns + "infNFe"))
                rows.Add($"NF-e     {destination}     {V(item, "chNFe")}");
            foreach (var item in city.Elements(Ns + "infMDFe"))
                rows.Add($"MDF-e    {destination}     {V(item, "chMDFe")}");
        }
        if (rows.Count == 0) return;

        const int rowsPerPage = 42;
        int pageCount = (int)Math.Ceiling(rows.Count / (double)rowsPerPage) + 1;
        for (int offset = 0; offset < rows.Count; offset += rowsPerPage)
        {
            int pageNumber = pdf.PageCount + 1;
            var page = NewPage(pdf);
            using var g = XGraphics.FromPdfPage(page);
            double y = DrawHeader(g, s, pageNumber, pageCount);
            Section(g, "RELAÇÃO COMPLETA DOS DOCUMENTOS FISCAIS VINCULADOS", ref y);
            Box(g, y, 20);
            g.DrawString("TIPO       MUNICÍPIO DE DESCARREGAMENTO / UF       CHAVE DE ACESSO", LabelFont, XBrushes.Black,
                new XRect(Left + 5, y + 5, Width - 10, 12), XStringFormats.TopLeft);
            y += 22;
            foreach (string row in rows.Skip(offset).Take(rowsPerPage))
            {
                g.DrawLine(XPens.LightGray, Left, y + 11, Right, y + 11);
                g.DrawString(row, SmallFont, XBrushes.Black,
                    new XRect(Left + 5, y, Width - 10, 12), XStringFormats.TopLeft);
                y += 15;
            }
            DrawFooter(g, s, pageNumber, pageCount);
        }
    }

    private static PdfPage NewPage(PdfDocument pdf)
    {
        var page = pdf.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        page.Orientation = PdfSharp.PageOrientation.Portrait;
        return page;
    }

    private static double DrawHeader(XGraphics g, MdfeDocumentSummary s, int page, int total)
    {
        g.DrawRectangle(XPens.Black, Left, 22, Width, 54);
        g.DrawString("DAMDFE", TitleFont, XBrushes.Black, new XRect(Left + 8, 27, 150, 23), XStringFormats.TopLeft);
        g.DrawString("DOCUMENTO AUXILIAR DO MANIFESTO ELETRÔNICO DE DOCUMENTOS FISCAIS",
            SmallFont, XBrushes.Black, new XRect(Left + 8, 53, 350, 12), XStringFormats.TopLeft);
        g.DrawString($"MDF-e Nº {s.DocumentNumber}   SÉRIE {s.Series}",
            SectionFont, XBrushes.Black, new XRect(Left + 350, 31, 180, 18), XStringFormats.TopRight);
        g.DrawString($"PÁGINA {page} DE {total}", SmallFont, XBrushes.Black,
            new XRect(Left + 350, 54, 180, 12), XStringFormats.TopRight);
        if (s.Environment == "Homologação")
            g.DrawString("EMITIDO EM AMBIENTE DE HOMOLOGAÇÃO - SEM VALOR FISCAL",
                SectionFont, XBrushes.Black, new XRect(Left, 82, Width, 13), XStringFormats.Center);
        return s.Environment == "Homologação" ? 103 : 88;
    }

    private static void DrawFooter(XGraphics g, MdfeDocumentSummary s, int page, int total)
    {
        g.DrawLine(XPens.Black, Left, 814, Right, 814);
        g.DrawString($"Chave de acesso: {s.AccessKey}", SmallFont, XBrushes.Black, new XPoint(Left, 825));
        g.DrawString($"Página {page}/{total}", SmallFont, XBrushes.Black, new XRect(Left, 825, Width, 10), XStringFormats.TopRight);
    }

    private static void Section(XGraphics g, string title, ref double y)
    {
        g.DrawString(title, SectionFont, XBrushes.Black, new XPoint(Left, y + 8));
        y += 13;
    }

    private static void Box(XGraphics g, double y, double height) =>
        g.DrawRectangle(XPens.Black, Left, y, Width, height);

    private static void Field(XGraphics g, string label, string? value, double x, double y, double width)
    {
        g.DrawString(label, LabelFont, XBrushes.Gray,
            new XRect(x, y, width, 8), XStringFormats.TopLeft);
        string clean = string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        g.DrawString(clean, ValueFont, XBrushes.Black,
            new XRect(x, y + 8, width, 12), XStringFormats.TopLeft);
    }

    private static void DrawWrapped(XGraphics g, string text, double x, double y, double width, double maxHeight, XFont font)
    {
        double lineHeight = 10;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string line = "";
        foreach (string word in words)
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (g.MeasureString(candidate, font).Width > width && line.Length > 0)
            {
                if (y + lineHeight > Bottom) return;
                g.DrawString(line, font, XBrushes.Black, new XPoint(x, y));
                y += lineHeight;
                line = word;
                if (y > Bottom) return;
            }
            else line = candidate;
        }
        if (line.Length > 0 && y <= Bottom)
            g.DrawString(line, font, XBrushes.Black, new XPoint(x, y));
    }

    private static string V(XElement? e, string n) => e?.Element(Ns + n)?.Value.Trim() ?? "";
    private static string First(XElement? e, params string[] names)
    {
        foreach (var name in names) { var value = V(e, name); if (value.Length > 0) return value; }
        return "";
    }
    private static string Join(XElement? e, params string[] names) =>
        string.Join(", ", names.Select(n => V(e, n)).Where(v => v.Length > 0));
    private static string RouteStates(XElement? ide) =>
        string.Join(" · ", ide?.Element(Ns + "infPercurso")?.Elements(Ns + "UFPer").Select(x => x.Value) ?? Enumerable.Empty<string>());
    private static string ModalName(string v) => v switch
    {
        "1" => "Rodoviário", "2" => "Aéreo", "3" => "Aquaviário", "4" => "Ferroviário", _ => "Não identificado"
    };
    private static string ModalDescription(XElement? modal)
    {
        if (modal is null) return "Dados específicos do modal não encontrados no XML.";
        return string.Join(" · ", modal.Descendants().Where(x => !x.HasElements)
            .Select(x => $"{x.Name.LocalName}: {x.Value.Trim()}")
            .Where(x => !x.EndsWith(": ", StringComparison.Ordinal))
            .Take(8));
    }
    private static string UnitName(string v) => v switch
    {
        "01" => "KG", "02" => "TON", "03" => "UNIDADE", "04" => "LITROS", "05" => "M³", _ => v
    };
}
