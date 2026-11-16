using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZXing;
using ZXing.Common;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Gera o DAMDFE seguindo a organização visual dos modelos do MOC 3.00b,
/// Anexo II. Os dados impressos são lidos exclusivamente do XML do MDF-e.
/// </summary>
public static class MdfeDamDfeGenerator
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/mdfe";
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private const double Left = 28, Right = 567, Width = Right - Left;
    private const double FooterY = 814;

    private static readonly XFont Title = new("Liberation Serif", 11, XFontStyleEx.Bold);
    private static readonly XFont Section = new("Liberation Serif", 9, XFontStyleEx.Bold);
    private static readonly XFont Label = new("Liberation Serif", 6.3, XFontStyleEx.Regular);
    private static readonly XFont Value = new("Liberation Serif", 7.6, XFontStyleEx.Bold);
    private static readonly XFont Small = new("Liberation Serif", 6.2, XFontStyleEx.Regular);

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
        XDocument document;
        using (var stream = File.OpenRead(source))
        using (var reader = XmlReader.Create(stream, settings))
            document = XDocument.Load(reader);

        var root = document.Root ?? throw new InvalidDataException("Raiz do XML não encontrada.");
        var mdfe = root.Element(Ns + "MDFe") ?? throw new InvalidDataException("Elemento MDFe não encontrado.");
        var info = mdfe.Element(Ns + "infMDFe") ?? throw new InvalidDataException("Elemento infMDFe não encontrado.");
        string? qrPayload = V(mdfe.Element(Ns + "infMDFeSupl"), "qrCodMDFe");
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

                var rows = CollectDocumentRows(info);
                const int rowsPerPage = 32;
                int continuationPages = (int)Math.Ceiling(rows.Count / (double)rowsPerPage);
                int totalPages = 1 + continuationPages;
                DrawOfficialFirstPage(pdf, mdfe, info, root, summary, qrPayload, totalPages);

                for (int offset = 0; offset < rows.Count; offset += rowsPerPage)
                    DrawDocumentContinuation(pdf, summary, rows.Skip(offset).Take(rowsPerPage).ToList(),
                        pdf.PageCount + 1, totalPages, offset + 1);

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

    private static void DrawOfficialFirstPage(PdfDocument pdf, XElement mdfe, XElement info,
        XElement root, MdfeDocumentSummary s, string? qrPayload, int totalPages)
    {
        var page = NewPage(pdf);
        using var g = XGraphics.FromPdfPage(page);
        DrawIssuerHeader(g, info, qrPayload);

        g.DrawString("DAMDFE - Documento Auxiliar do Manifesto de Documentos Fiscais Eletrônicos",
            Small, XBrushes.Black, new XPoint(Left, 91));

        double bandY = 103;
        string issue = FormatDate(s.IssueDate);
        DrawBandCell(g, "Modelo", "58", Left, bandY, 42);
        DrawBandCell(g, "Série", s.Series, Left + 42, bandY, 42);
        DrawBandCell(g, "Número", s.DocumentNumber, Left + 84, bandY, 48);
        DrawBandCell(g, "FL", $"1/{totalPages}", Left + 132, bandY, 45);
        DrawBandCell(g, "Data e hora de Emissão", issue, Left + 177, bandY, 118);
        DrawBandCell(g, "UF Carreg.", s.OriginState, Left + 295, bandY, 65);
        DrawBandCell(g, "UF Descarreg.", s.DestinationState, Left + 360, bandY, 75);

        double y = 161;
        string modalTitle = $"Modelo {ModalName(s.Modal)} de Carga";
        g.DrawString(modalTitle, Section, XBrushes.Black, new XPoint(Left, y + 10));

        // Totais à esquerda e controle fiscal à direita, conforme o modelo oficial.
        double totalsY = y + 20;
        DrawSummaryTile(g, "Qtd. CT-e", s.CteCount.ToString(Pt), Left, totalsY, 66);
        DrawSummaryTile(g, "Qtd. NF-e", s.NfeCount.ToString(Pt), Left + 69, totalsY, 66);
        DrawSummaryTile(g, "Peso total (" + UnitName(s.CargoUnit) + ")", s.CargoQuantity.ToString("N3", Pt), Left + 138, totalsY, 94);

        double controlX = Left + 244;
        g.DrawString("CONTROLE DO FISCO", Label, XBrushes.Black,
            new XRect(controlX, y + 1, Width - 244, 10), XStringFormats.TopCenter);
        DrawBarcode(g, s.AccessKey, controlX, totalsY - 1, Width - 244, 42);

        double protocolY = 233;
        g.DrawString("Protocolo de autorização", Section, XBrushes.Black, new XPoint(Left, protocolY));
        var protocol = root.Element(Ns + "protMDFe")?.Element(Ns + "infProt");
        string protocolText = string.Join(" - ", new[] { V(protocol, "nProt"), FormatDate(V(protocol, "dhRecbto")) }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        DrawTextFit(g, protocolText, Value, Left, protocolY + 12, 220, 12);

        g.DrawString("Chave de Acesso", Section, XBrushes.Black, new XPoint(controlX, protocolY));
        DrawTextFit(g, GroupKey(s.AccessKey), Value, controlX, protocolY + 12, Width - 244, 12);
        g.DrawString("Consulte em https://dfe-portal.svrs.rs.gov.br/MDFe/Consulta",
            Small, XBrushes.Black, new XPoint(controlX, protocolY + 27));

        double bodyY = 282;
        DrawRoadVehicleAndDriver(g, info, s, bodyY);
        DrawTollInfo(g, info, bodyY + 83);
        DrawObservations(g, info, bodyY + 148);
        DrawHomologationNotice(g, s.Environment);
        DrawFooter(g, s, 1, totalPages);
    }

    private static void DrawIssuerHeader(XGraphics g, XElement info, string? qrPayload)
    {
        var issuer = info.Element(Ns + "emit");
        var address = issuer?.Element(Ns + "enderEmit");
        g.DrawRectangle(XPens.LightGray, Left, 25, 68, 45);
        g.DrawString("LOGO", Section, XBrushes.Gray, new XRect(Left, 37, 68, 12), XStringFormats.Center);
        g.DrawString("EMPRESA", Small, XBrushes.Gray, new XRect(Left, 50, 68, 10), XStringFormats.Center);

        double x = Left + 75;
        g.DrawString(V(issuer, "xNome"), Section, XBrushes.Black, new XPoint(x, 34));
        string street = Join(address, "xLgr", "nro", "xCpl", "xBairro");
        string city = Join(address, "xMun", "UF", "CEP");
        g.DrawString(street, Small, XBrushes.Black, new XPoint(x, 45));
        g.DrawString(city, Small, XBrushes.Black, new XPoint(x, 55));
        string registration = $"CNPJ/CPF: {First(issuer, "CNPJ", "CPF")}   IE: {V(issuer, "IE")}";
        string rntrc = V(info.Element(Ns + "infModal")?.Element(Ns + "rodo"), "RNTRC");
        if (rntrc.Length > 0) registration += "   RNTRC: " + rntrc;
        g.DrawString(registration, Small, XBrushes.Black, new XPoint(x, 65));

        if (!string.IsNullOrWhiteSpace(qrPayload))
            DrawQrCode(g, qrPayload, Right - 58, 24, 54);
    }

    private static void DrawBandCell(XGraphics g, string label, string value, double x, double y, double width)
    {
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(220, 235, 246)), x, y, width - 1, 31);
        g.DrawString(label, Small, XBrushes.Black, new XPoint(x + 4, y + 10));
        DrawTextFit(g, value, Value, x + 4, y + 23, width - 8, 10);
    }

    private static void DrawSummaryTile(XGraphics g, string label, string value, double x, double y, double width)
    {
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(235, 237, 239)), x, y, width, 31);
        g.DrawString(label, Small, XBrushes.Black, new XPoint(x + 4, y + 10));
        DrawTextFit(g, value, Value, x + 4, y + 23, width - 8, 10);
    }

    private static void DrawRoadVehicleAndDriver(XGraphics g, XElement info, MdfeDocumentSummary s, double y)
    {
        if (s.Modal != "1")
        {
            DrawOtherModal(g, info, s, y);
            return;
        }

        var road = info.Element(Ns + "infModal")?.Element(Ns + "rodo");
        var vehicles = new List<XElement>();
        if (road?.Element(Ns + "veicTracao") is XElement traction) vehicles.Add(traction);
        if (road is not null) vehicles.AddRange(road.Elements(Ns + "veicReboque"));

        g.DrawString("Veículo", Section, XBrushes.Black, new XPoint(Left, y));
        g.DrawString("Condutor", Section, XBrushes.Black, new XPoint(Left + 244, y));
        g.DrawLine(XPens.Gray, Left, y + 5, Left + 220, y + 5);
        g.DrawLine(XPens.Gray, Left + 244, y + 5, Right, y + 5);

        g.DrawString("Placa", Label, XBrushes.Black, new XPoint(Left, y + 17));
        g.DrawString("RNTRC", Label, XBrushes.Black, new XPoint(Left + 75, y + 17));
        double rowY = y + 29;
        foreach (var vehicle in vehicles.Take(4))
        {
            g.DrawString(V(vehicle, "placa"), Value, XBrushes.Black, new XPoint(Left, rowY));
            g.DrawString(V(road, "RNTRC"), Value, XBrushes.Black, new XPoint(Left + 75, rowY));
            rowY += 15;
        }
        if (vehicles.Count == 0)
            g.DrawString("Dados de veículo não informados no XML.", Small, XBrushes.Black, new XPoint(Left, rowY));

        g.DrawString("CPF", Label, XBrushes.Black, new XPoint(Left + 244, y + 17));
        g.DrawString("Nome", Label, XBrushes.Black, new XPoint(Left + 325, y + 17));
        double driverY = y + 29;
        var drivers = road?.Elements(Ns + "condutor") ?? Enumerable.Empty<XElement>();
        foreach (var driver in drivers.Take(6))
        {
            DrawTextFit(g, First(driver, "CPF", "CNPJ"), Value, Left + 244, driverY, 75, 11);
            DrawTextFit(g, V(driver, "xNome"), Value, Left + 325, driverY, 218, 11);
            g.DrawLine(XPens.LightGray, Left + 244, driverY + 4, Right, driverY + 4);
            driverY += 15;
        }
        if (!drivers.Any())
            g.DrawString("Condutor não informado no XML.", Small, XBrushes.Black, new XPoint(Left + 244, driverY));
    }

    private static void DrawOtherModal(XGraphics g, XElement info, MdfeDocumentSummary s, double y)
    {
        g.DrawString("Informações do modal", Section, XBrushes.Black, new XPoint(Left, y));
        g.DrawLine(XPens.Gray, Left, y + 5, Right, y + 5);
        var modal = info.Element(Ns + "infModal");
        var specific = modal?.Elements().FirstOrDefault();
        var values = specific?.DescendantsAndSelf().Where(x => !x.HasElements)
            .Select(x => $"{x.Name.LocalName}: {x.Value.Trim()}")
            .Where(x => !x.EndsWith(": ", StringComparison.Ordinal)).Take(18).ToList() ?? new List<string>();
        double rowY = y + 19;
        foreach (var value in values)
        {
            DrawTextFit(g, value, Small, Left, rowY, Width, 10);
            rowY += 12;
        }
        if (values.Count == 0)
            g.DrawString($"Dados específicos do modal {ModalName(s.Modal)} não localizados.", Small, XBrushes.Black, new XPoint(Left, rowY));
    }

    private static void DrawTollInfo(XGraphics g, XElement info, double y)
    {
        var tolls = info.Element(Ns + "infModal")?.Descendants(Ns + "valePed").ToList() ?? new List<XElement>();
        if (tolls.Count == 0) return;

        g.DrawString("Vale Pedágio", Section, XBrushes.Black, new XPoint(Left, y));
        g.DrawLine(XPens.Gray, Left, y + 5, Left + 260, y + 5);
        g.DrawString("Responsável CNPJ/CPF", Label, XBrushes.Black, new XPoint(Left, y + 16));
        g.DrawString("Fornecedor CNPJ", Label, XBrushes.Black, new XPoint(Left + 100, y + 16));
        g.DrawString("Nº Comprovante", Label, XBrushes.Black, new XPoint(Left + 190, y + 16));
        double rowY = y + 28;
        foreach (var toll in tolls.Take(5))
        {
            DrawTextFit(g, First(toll, "CNPJPg", "CPFPg"), Small, Left, rowY, 95, 10);
            DrawTextFit(g, First(toll, "CNPJForn", "CPFForn"), Small, Left + 100, rowY, 85, 10);
            DrawTextFit(g, V(toll, "nCompra"), Small, Left + 190, rowY, 70, 10);
            rowY += 13;
        }
    }

    private static void DrawObservations(XGraphics g, XElement info, double y)
    {
        g.DrawString("Observações", Section, XBrushes.Black, new XPoint(Left, y));
        g.DrawLine(XPens.Gray, Left, y + 5, Right, y + 5);
        string observation = V(info.Element(Ns + "infAdic"), "infCpl");
        if (observation.Length == 0) return;
        DrawWrapped(g, observation, Left, y + 17, Width, 75, Small);
    }

    private static void DrawHomologationNotice(XGraphics g, string environment)
    {
        if (!string.Equals(environment, "Homologação", StringComparison.OrdinalIgnoreCase)) return;
        g.DrawString("EMITIDO EM AMBIENTE DE HOMOLOGAÇÃO - SEM VALOR FISCAL",
            Section, XBrushes.Black, new XRect(Left, 786, Width, 12), XStringFormats.Center);
    }

    private static void DrawBarcode(XGraphics g, string key, double x, double y, double availableWidth)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var matrix = new MultiFormatWriter().encode(key, BarcodeFormat.CODE_128, 900, 100);
        // 11.5 cm = 326.0 pt, as required by MOC. The official control area spans
        // the available right-hand half; preserve the full width without cropping.
        double width = Math.Max(availableWidth, 326);
        width = Math.Min(width, Right - x);
        DrawMatrix(g, matrix, x, y, width, 34);
    }

    private static void DrawQrCode(XGraphics g, string payload, double x, double y, double size)
    {
        var matrix = new MultiFormatWriter().encode(payload, BarcodeFormat.QR_CODE, 300, 300);
        // QR minimum 25 mm, including the quiet zone.
        DrawMatrix(g, matrix, x, y, size, size);
    }

    private static void DrawMatrix(XGraphics g, BitMatrix matrix, double x, double y, double width, double height)
    {
        g.DrawRectangle(XBrushes.White, x, y, width, height);
        double cellW = width / matrix.Width;
        double cellH = height / matrix.Height;
        for (int row = 0; row < matrix.Height; row++)
        {
            int start = -1;
            for (int col = 0; col <= matrix.Width; col++)
            {
                bool black = col < matrix.Width && matrix[col, row];
                if (black && start < 0) start = col;
                if (!black && start >= 0)
                {
                    g.DrawRectangle(XBrushes.Black, x + start * cellW, y + row * cellH,
                        (col - start) * cellW + 0.05, cellH + 0.05);
                    start = -1;
                }
            }
        }
    }

    private static void DrawDocumentContinuation(PdfDocument pdf, MdfeDocumentSummary s,
        IReadOnlyList<string> rows, int pageNumber, int totalPages, int firstItem)
    {
        var page = NewPage(pdf);
        using var g = XGraphics.FromPdfPage(page);
        g.DrawString("DAMDFE - Documento Auxiliar do Manifesto de Documentos Fiscais Eletrônicos",
            Title, XBrushes.Black, new XPoint(Left, 38));
        g.DrawString($"MDF-e Nº {s.DocumentNumber}   Série {s.Series}   Chave: {GroupKey(s.AccessKey)}",
            Small, XBrushes.Black, new XPoint(Left, 52));
        g.DrawString($"RELAÇÃO DOS DOCUMENTOS FISCAIS VINCULADOS — PÁGINA {pageNumber}/{totalPages}",
            Section, XBrushes.Black, new XPoint(Left, 78));
        g.DrawLine(XPens.Black, Left, 84, Right, 84);
        g.DrawString("Nº", Label, XBrushes.Black, new XPoint(Left + 2, 96));
        g.DrawString("TIPO", Label, XBrushes.Black, new XPoint(Left + 32, 96));
        g.DrawString("MUNICÍPIO / UF DE DESCARREGAMENTO", Label, XBrushes.Black, new XPoint(Left + 95, 96));
        g.DrawString("CHAVE DE ACESSO", Label, XBrushes.Black, new XPoint(Left + 285, 96));
        double y = 105;
        for (int i = 0; i < rows.Count; i++)
        {
            g.DrawLine(XPens.LightGray, Left, y + 14, Right, y + 14);
            DrawTextFit(g, (firstItem + i).ToString(Pt), Small, Left + 2, y + 2, 24, 10);
            DrawTextFit(g, rows[i].Split('|')[0], Small, Left + 32, y + 2, 58, 10);
            DrawTextFit(g, rows[i].Split('|')[1], Small, Left + 95, y + 2, 185, 10);
            DrawTextFit(g, rows[i].Split('|')[2], Small, Left + 285, y + 2, 252, 10);
            y += 16;
        }
        DrawFooter(g, s, pageNumber, totalPages);
    }

    private static List<string> CollectDocumentRows(XElement info)
    {
        var rows = new List<string>();
        var groups = info.Element(Ns + "infDoc")?.Elements(Ns + "infMunDescarga") ?? Enumerable.Empty<XElement>();
        foreach (var city in groups)
        {
            string destination = Join(city, "xMunDescarga", "UF");
            foreach (var item in city.Elements(Ns + "infCTe"))
                rows.Add($"CT-e|{destination}|{V(item, "chCTe")}");
            foreach (var item in city.Elements(Ns + "infNFe"))
                rows.Add($"NF-e|{destination}|{V(item, "chNFe")}");
            foreach (var item in city.Elements(Ns + "infMDFe"))
                rows.Add($"MDF-e|{destination}|{V(item, "chMDFe")}");
        }
        return rows;
    }

    private static PdfPage NewPage(PdfDocument pdf)
    {
        var page = pdf.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        page.Orientation = PdfSharp.PageOrientation.Portrait;
        return page;
    }

    private static void DrawFooter(XGraphics g, MdfeDocumentSummary s, int page, int total)
    {
        g.DrawLine(XPens.Black, Left, FooterY, Right, FooterY);
        DrawTextFit(g, $"Chave de acesso: {s.AccessKey}", Small, Left, FooterY + 5, 420, 10);
        g.DrawString($"Página {page}/{total}", Small, XBrushes.Black,
            new XRect(Left, FooterY + 5, Width, 10), XStringFormats.TopRight);
    }

    private static void DrawTextFit(XGraphics g, string? text, XFont font, double x, double y, double width, double height)
    {
        string value = string.IsNullOrWhiteSpace(text) ? "—" : text.Trim();
        while (value.Length > 1 && g.MeasureString(value, font).Width > width)
            value = value[..^2] + "…";
        g.DrawString(value, font, XBrushes.Black, new XRect(x, y, width, height), XStringFormats.TopLeft);
    }

    private static void DrawWrapped(XGraphics g, string text, double x, double y, double width, double maxHeight, XFont font)
    {
        double lineHeight = 9;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string line = "";
        double startY = y;
        foreach (string word in words)
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (g.MeasureString(candidate, font).Width > width && line.Length > 0)
            {
                if (y - startY + lineHeight > maxHeight) return;
                g.DrawString(line, font, XBrushes.Black, new XPoint(x, y));
                y += lineHeight;
                line = word;
            }
            else line = candidate;
        }
        if (line.Length > 0 && y - startY <= maxHeight)
            g.DrawString(line, font, XBrushes.Black, new XPoint(x, y));
    }

    private static string V(XElement? element, string name) =>
        element?.Element(Ns + name)?.Value.Trim() ?? "";

    private static string First(XElement? element, params string[] names)
    {
        foreach (string name in names)
        {
            string value = V(element, name);
            if (value.Length > 0) return value;
        }
        return "";
    }

    private static string Join(XElement? element, params string[] names) =>
        string.Join(", ", names.Select(name => V(element, name)).Where(value => value.Length > 0));

    private static string ModalName(string value) => value switch
    {
        "1" => "Rodoviário", "2" => "Aéreo", "3" => "Aquaviário", "4" => "Ferroviário",
        _ => "Não identificado"
    };

    private static string UnitName(string value) => value switch
    {
        "01" => "KG", "02" => "TON", "03" => "UNIDADE", "04" => "LITROS", "05" => "M³",
        _ => value
    };

    private static string GroupKey(string key) =>
        string.Join(" ", Enumerable.Range(0, (key.Length + 3) / 4)
            .Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));

    private static string FormatDate(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("dd/MM/yyyy HH:mm:ss zzz", Pt);
        return value;
    }
}
