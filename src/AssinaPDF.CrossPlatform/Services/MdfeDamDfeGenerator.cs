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

    private static readonly Lazy<XFont> Title = new(() => new XFont("Inter", 11, XFontStyleEx.Bold));
    private static readonly Lazy<XFont> Section = new(() => new XFont("Inter", 9, XFontStyleEx.Bold));
    private static readonly Lazy<XFont> Label = new(() => new XFont("Inter", 6.3, XFontStyleEx.Regular));
    private static readonly Lazy<XFont> Value = new(() => new XFont("Inter", 7.6, XFontStyleEx.Bold));
    private static readonly Lazy<XFont> Small = new(() => new XFont("Inter", 6.2, XFontStyleEx.Regular));

    public static void Generate(MdfeDocumentSummary summary, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        NfePdfFontResolver.Register();
        if (!File.Exists(summary.SourcePath))
            throw new FileNotFoundException("XML original do MDF-e não encontrado.", summary.SourcePath);

        var verified = MdfeXmlReader.ReadFile(summary.SourcePath);
        if (verified.Modal != "1")
            throw new NotSupportedException("Este gerador foi validado para o modelo oficial rodoviário. Os modelos aéreo, aquaviário e ferroviário não serão emitidos com um leiaute genérico.");
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
        if (string.IsNullOrWhiteSpace(qrPayload))
            throw new InvalidDataException("O XML autorizado não contém infMDFeSupl/qrCodMDFe; o DAMDFE oficial não pode ser gerado sem o QR Code.");
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
                const int firstPageDocumentRows = 10;
                int remainingRows = Math.Max(0, rows.Count - firstPageDocumentRows);
                int continuationPages = (int)Math.Ceiling(remainingRows / (double)rowsPerPage);
                int totalPages = 1 + continuationPages;
                DrawOfficialFirstPage(pdf, info, root, summary, qrPayload,
                    rows.Take(firstPageDocumentRows).ToList(), totalPages);

                for (int offset = firstPageDocumentRows; offset < rows.Count; offset += rowsPerPage)
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

    private static void DrawOfficialFirstPage(PdfDocument pdf, XElement info,
        XElement root, MdfeDocumentSummary s, string? qrPayload, IReadOnlyList<string> documentRows, int totalPages)
    {
        var page = NewPage(pdf);
        using var g = XGraphics.FromPdfPage(page);

        // Cabeçalho compacto, próximo à composição do DAMDFE rodoviário do MOC.
        DrawIssuerHeader(g, info, qrPayload);
        DrawHomologationNotice(g, s.Environment);
        g.DrawString("DAMDFE - Documento Auxiliar do Manifesto Eletrônico de Documentos Fiscais",
            Small.Value, XBrushes.Black, new XPoint(Left, 94));

        // Faixa de identificação do MDF-e.
        const double bandY = 108;
        DrawBandCell(g, "Modelo", "58", Left, bandY, 42);
        DrawBandCell(g, "Série", s.Series, Left + 42, bandY, 42);
        DrawBandCell(g, "Número", s.DocumentNumber, Left + 84, bandY, 55);
        DrawBandCell(g, "FL", $"1/{totalPages}", Left + 139, bandY, 40);
        DrawBandCell(g, "Data e hora de emissão", FormatDate(s.IssueDate), Left + 179, bandY, 145);
        DrawBandCell(g, "UF carregamento", s.OriginState, Left + 324, bandY, 72);
        DrawBandCell(g, "UF descarregamento", s.DestinationState, Left + 396, bandY, 143);

        g.DrawString("Modal Rodoviário de Carga", Section.Value, XBrushes.Black, new XPoint(Left, 163));
        DrawSummaryTile(g, "Qtd. CT-e", s.CteCount.ToString(Pt), Left, 171, 55);
        DrawSummaryTile(g, "Qtd. NF-e", s.NfeCount.ToString(Pt), Left + 58, 171, 55);
        DrawSummaryTile(g, "Peso total (" + UnitName(s.CargoUnit) + ")", s.CargoQuantity.ToString("N3", Pt), Left + 116, 171, 92);

        double controlX = Left + 213;
        g.DrawString("CONTROLE DO FISCO", Label.Value, XBrushes.Black,
            new XRect(controlX, 166, Width - 213, 10), XStringFormats.TopCenter);
        DrawBarcode(g, s.AccessKey, controlX, 178, Width - 213);

        // Protocolo e chave na mesma faixa, sem competir com o código de barras.
        double protocolY = 222;
        g.DrawString("Protocolo de autorização", Section.Value, XBrushes.Black, new XPoint(Left, protocolY));
        var protocol = root.Element(Ns + "protMDFe")?.Element(Ns + "infProt");
        string protocolText = string.Join(" - ", new[] { V(protocol, "nProt"), FormatDate(V(protocol, "dhRecbto")) }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        DrawTextFit(g, protocolText, Value.Value, Left, protocolY + 12, 220, 12);
        g.DrawString("Chave de acesso", Section.Value, XBrushes.Black, new XPoint(controlX, protocolY));
        DrawTextFit(g, GroupKey(s.AccessKey), Value.Value, controlX, protocolY + 12, Width - 213, 12);
        g.DrawString("Consulte em https://dfe-portal.svrs.rs.gov.br/MDFe/Consulta",
            Small.Value, XBrushes.Black, new XPoint(controlX, protocolY + 27));

        // Veículo e condutor em duas colunas, como no documento de referência.
        DrawRoadVehicleAndDriver(g, info, s, 280);

        // Dados de carga em faixa própria.
        DrawCargoInfo(g, info, s, 350);

        // Vale-pedágio e documentos fiscais lado a lado.
        DrawTollAndDocuments(g, info, documentRows, 418);

        // Observações ocupam a largura total abaixo das duas colunas.
        DrawObservations(g, info, 603);
        DrawFooter(g, s, 1, totalPages);
    }

    private static void DrawIssuerHeader(XGraphics g, XElement info, string? qrPayload)
    {
        var issuer = info.Element(Ns + "emit");
        var address = issuer?.Element(Ns + "enderEmit");
        double x = Left;
        DrawTextFit(g, V(issuer, "xNome"), Section.Value, x, 27, 385, 12);
        string street = Join(address, "xLgr", "nro", "xCpl", "xBairro");
        string city = Join(address, "xMun", "UF", "CEP");
        DrawTextFit(g, street, Small.Value, x, 41, 385, 9);
        DrawTextFit(g, city, Small.Value, x, 52, 385, 9);
        string registration = $"CNPJ/CPF: {First(issuer, "CNPJ", "CPF")}   IE: {V(issuer, "IE")}";
        var road = info.Element(Ns + "infModal")?.Element(Ns + "rodo");
        string rntrc = V(road?.Element(Ns + "infANTT"), "RNTRC");
        if (rntrc.Length > 0) registration += "   RNTRC: " + rntrc;
        DrawTextFit(g, registration, Small.Value, x, 63, 385, 9);

        if (!string.IsNullOrWhiteSpace(qrPayload))
            DrawQrCode(g, qrPayload, Right - 75, 20, 72);
    }

    private static void DrawBandCell(XGraphics g, string label, string value, double x, double y, double width)
    {
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(220, 235, 246)), x, y, width - 1, 31);
        g.DrawString(label, Small.Value, XBrushes.Black, new XPoint(x + 4, y + 10));
        DrawTextFit(g, value, Value.Value, x + 4, y + 23, width - 8, 10);
    }

    private static void DrawSummaryTile(XGraphics g, string label, string value, double x, double y, double width)
    {
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(235, 237, 239)), x, y, width, 31);
        g.DrawString(label, Small.Value, XBrushes.Black, new XPoint(x + 4, y + 10));
        DrawTextFit(g, value, Value.Value, x + 4, y + 23, width - 8, 10);
    }

    private static void DrawRoadVehicleAndDriver(XGraphics g, XElement info, MdfeDocumentSummary s, double y)
    {
        var road = info.Element(Ns + "infModal")?.Element(Ns + "rodo");
        var traction = road?.Element(Ns + "veicTracao");
        var vehicles = new List<XElement>();
        if (traction is not null) vehicles.Add(traction);
        if (road is not null) vehicles.AddRange(road.Elements(Ns + "veicReboque"));

        g.DrawString("Veículo", Section.Value, XBrushes.Black, new XPoint(Left, y));
        g.DrawString("Condutor", Section.Value, XBrushes.Black, new XPoint(Left + 270, y));
        g.DrawLine(XPens.Gray, Left, y + 5, Left + 245, y + 5);
        g.DrawLine(XPens.Gray, Left + 270, y + 5, Right, y + 5);
        g.DrawString("Placa", Label.Value, XBrushes.Black, new XPoint(Left, y + 17));
        g.DrawString("RENAVAM", Label.Value, XBrushes.Black, new XPoint(Left + 82, y + 17));
        g.DrawString("UF", Label.Value, XBrushes.Black, new XPoint(Left + 165, y + 17));
        g.DrawString("RNTRC", Label.Value, XBrushes.Black, new XPoint(Left + 198, y + 17));
        double rowY = y + 29;
        foreach (var vehicle in vehicles.Take(3))
        {
            DrawTextFit(g, V(vehicle, "placa"), Value.Value, Left, rowY, 76, 11);
            DrawTextFit(g, V(vehicle, "RENAVAM"), Small.Value, Left + 82, rowY, 76, 11);
            DrawTextFit(g, V(vehicle, "UF"), Value.Value, Left + 165, rowY, 25, 11);
            DrawTextFit(g, V(road?.Element(Ns + "infANTT"), "RNTRC"), Small.Value, Left + 198, rowY, 47, 11);
            rowY += 13;
        }
        if (vehicles.Count == 0)
            g.DrawString("Veículo não informado no XML.", Small.Value, XBrushes.Black, new XPoint(Left, rowY));

        g.DrawString("CPF", Label.Value, XBrushes.Black, new XPoint(Left + 270, y + 17));
        g.DrawString("Nome", Label.Value, XBrushes.Black, new XPoint(Left + 355, y + 17));
        double driverY = y + 29;
        var drivers = traction?.Elements(Ns + "condutor") ?? Enumerable.Empty<XElement>();
        foreach (var driver in drivers.Take(3))
        {
            DrawTextFit(g, First(driver, "CPF", "CNPJ"), Small.Value, Left + 270, driverY, 78, 11);
            DrawTextFit(g, V(driver, "xNome"), Value.Value, Left + 355, driverY, 184, 11);
            driverY += 13;
        }
        if (!drivers.Any())
            g.DrawString("Condutor não informado no XML.", Small.Value, XBrushes.Black, new XPoint(Left + 270, driverY));
    }

    private static void DrawCargoInfo(XGraphics g, XElement info, MdfeDocumentSummary s, double y)
    {
        g.DrawString("Informações da carga", Section.Value, XBrushes.Black, new XPoint(Left, y));
        g.DrawLine(XPens.Gray, Left, y + 5, Right, y + 5);
        DrawField(g, "Valor total da carga", s.CargoValue.ToString("C2", Pt), Left, y + 13, 120);
        DrawField(g, "Quantidade total", s.CargoQuantity.ToString("N3", Pt), Left + 140, y + 13, 105);
        DrawField(g, "Unidade", UnitName(s.CargoUnit), Left + 270, y + 13, 75);
        var goods = info.Element(Ns + "prodPred");
        DrawField(g, "Tipo / descrição da carga", Join(goods, "tpCarga", "xProd"), Left + 365, y + 13, 174);
    }

    private static void DrawField(XGraphics g, string label, string value, double x, double y, double width)
    {
        g.DrawString(label, Label.Value, XBrushes.Black, new XPoint(x, y));
        DrawTextFit(g, value, Value.Value, x, y + 10, width, 11);
    }

    private static void DrawTollAndDocuments(XGraphics g, XElement info, IReadOnlyList<string> documentRows, double y)
    {
        double leftWidth = 245;
        double rightX = Left + 260;
        double rightWidth = Right - rightX;

        g.DrawString("Vale Pedágio", Section.Value, XBrushes.Black, new XPoint(Left, y));
        g.DrawLine(XPens.Gray, Left, y + 5, Left + leftWidth, y + 5);
        g.DrawString("Responsável / Fornecedor", Label.Value, XBrushes.Black, new XPoint(Left, y + 15));
        g.DrawString("Comprovante", Label.Value, XBrushes.Black, new XPoint(Left + 175, y + 15));
        var tollGroups = info.Element(Ns + "infModal")?.Descendants(Ns + "valePed") ?? Enumerable.Empty<XElement>();
        var tolls = tollGroups.SelectMany(group => group.Elements(Ns + "disp")).ToList();
        double tollY = y + 27;
        foreach (var toll in tolls.Take(10))
        {
            string parties = $"{First(toll, "CNPJPg", "CPFPg")} / {First(toll, "CNPJForn", "CPFForn")}";
            DrawTextFit(g, parties, Small.Value, Left, tollY, 168, 10);
            DrawTextFit(g, V(toll, "nCompra"), Small.Value, Left + 175, tollY, 70, 10);
            tollY += 12;
        }
        if (tolls.Count == 0)
            g.DrawString("Não informado.", Small.Value, XBrushes.Black, new XPoint(Left, tollY));

        g.DrawString("Documentos fiscais vinculados", Section.Value, XBrushes.Black, new XPoint(rightX, y));
        g.DrawLine(XPens.Gray, rightX, y + 5, Right, y + 5);
        g.DrawString("TIPO", Label.Value, XBrushes.Black, new XPoint(rightX, y + 15));
        g.DrawString("CHAVE DE ACESSO", Label.Value, XBrushes.Black, new XPoint(rightX + 48, y + 15));
        double docY = y + 27;
        foreach (string row in documentRows)
        {
            string[] parts = row.Split('|');
            DrawTextFit(g, parts[0], Small.Value, rightX, docY, 43, 10);
            DrawTextFit(g, parts.Length > 2 ? parts[2] : "", Small.Value, rightX + 48, docY, rightWidth - 48, 10);
            docY += 12;
        }
        if (documentRows.Count == 0)
            g.DrawString("Nenhum documento vinculado.", Small.Value, XBrushes.Black, new XPoint(rightX, docY));
    }

    private static void DrawTollInfo(XGraphics g, XElement info, double y)
    {
        var tollGroups = info.Element(Ns + "infModal")?.Descendants(Ns + "valePed") ?? Enumerable.Empty<XElement>();
        var tolls = tollGroups.SelectMany(group => group.Elements(Ns + "disp")).ToList();
        if (tolls.Count == 0) return;

        g.DrawString("Vale Pedágio", Section.Value, XBrushes.Black, new XPoint(Left, y));
        g.DrawLine(XPens.Gray, Left, y + 5, Left + 260, y + 5);
        g.DrawString("Responsável CNPJ/CPF", Label.Value, XBrushes.Black, new XPoint(Left, y + 16));
        g.DrawString("Fornecedor CNPJ", Label.Value, XBrushes.Black, new XPoint(Left + 100, y + 16));
        g.DrawString("Nº Comprovante", Label.Value, XBrushes.Black, new XPoint(Left + 190, y + 16));
        double rowY = y + 28;
        foreach (var toll in tolls.Take(5))
        {
            DrawTextFit(g, First(toll, "CNPJPg", "CPFPg"), Small.Value, Left, rowY, 95, 10);
            DrawTextFit(g, First(toll, "CNPJForn", "CPFForn"), Small.Value, Left + 100, rowY, 85, 10);
            DrawTextFit(g, V(toll, "nCompra"), Small.Value, Left + 190, rowY, 70, 10);
            rowY += 13;
        }
    }

    private static void DrawObservations(XGraphics g, XElement info, double y)
    {
        var additional = info.Element(Ns + "infAdic");
        string observation = Join(additional, "infAdFisco", "infCpl");
        if (observation.Length == 0) return;
        g.DrawString("Observações", Section.Value, XBrushes.Black, new XPoint(Left, y));
        g.DrawLine(XPens.Gray, Left, y + 5, Right, y + 5);
        if (!DrawWrapped(g, observation, Left, y + 17, Width, FooterY - y - 32, Small.Value))
            throw new InvalidDataException("As observações do MDF-e excedem o espaço disponível no DAMDFE. O PDF não foi exportado para evitar omissão de informações.");
    }

    private static void DrawHomologationNotice(XGraphics g, string environment)
    {
        if (!string.Equals(environment, "Homologação", StringComparison.OrdinalIgnoreCase)) return;
        g.DrawString("EMITIDO EM AMBIENTE DE HOMOLOGAÇÃO - SEM VALOR FISCAL",
            Section.Value, XBrushes.Black, new XRect(Left, 255, Width, 9), XStringFormats.Center);
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
            Title.Value, XBrushes.Black, new XPoint(Left, 38));
        g.DrawString($"MDF-e Nº {s.DocumentNumber}   Série {s.Series}   Chave: {GroupKey(s.AccessKey)}",
            Small.Value, XBrushes.Black, new XPoint(Left, 52));
        g.DrawString($"RELAÇÃO DOS DOCUMENTOS FISCAIS VINCULADOS — PÁGINA {pageNumber}/{totalPages}",
            Section.Value, XBrushes.Black, new XPoint(Left, 78));
        g.DrawLine(XPens.Black, Left, 84, Right, 84);
        g.DrawString("Nº", Label.Value, XBrushes.Black, new XPoint(Left + 2, 96));
        g.DrawString("TIPO", Label.Value, XBrushes.Black, new XPoint(Left + 32, 96));
        g.DrawString("MUNICÍPIO / UF DE DESCARREGAMENTO", Label.Value, XBrushes.Black, new XPoint(Left + 95, 96));
        g.DrawString("CHAVE DE ACESSO", Label.Value, XBrushes.Black, new XPoint(Left + 285, 96));
        double y = 105;
        for (int i = 0; i < rows.Count; i++)
        {
            g.DrawLine(XPens.LightGray, Left, y + 14, Right, y + 14);
            DrawTextFit(g, (firstItem + i).ToString(Pt), Small.Value, Left + 2, y + 2, 24, 10);
            DrawTextFit(g, rows[i].Split('|')[0], Small.Value, Left + 32, y + 2, 58, 10);
            DrawTextFit(g, rows[i].Split('|')[1], Small.Value, Left + 95, y + 2, 185, 10);
            DrawTextFit(g, rows[i].Split('|')[2], Small.Value, Left + 285, y + 2, 252, 10);
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
        DrawTextFit(g, $"Chave de acesso: {s.AccessKey}", Small.Value, Left, FooterY + 5, 420, 10);
        g.DrawString($"Página {page}/{total}", Small.Value, XBrushes.Black,
            new XRect(Left, FooterY + 5, Width, 10), XStringFormats.TopRight);
    }

    private static void DrawTextFit(XGraphics g, string? text, XFont font, double x, double y, double width, double height)
    {
        string value = string.IsNullOrWhiteSpace(text) ? "—" : text.Trim();
        while (value.Length > 1 && g.MeasureString(value, font).Width > width)
            value = value[..^2] + "…";
        g.DrawString(value, font, XBrushes.Black, new XRect(x, y, width, height), XStringFormats.TopLeft);
    }

    private static bool DrawWrapped(XGraphics g, string text, double x, double y, double width, double maxHeight, XFont font)
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
                if (y - startY + lineHeight > maxHeight) return false;
                g.DrawString(line, font, XBrushes.Black, new XPoint(x, y));
                y += lineHeight;
                line = word;
            }
            else line = candidate;
        }
        if (line.Length > 0)
        {
            if (y - startY > maxHeight) return false;
            g.DrawString(line, font, XBrushes.Black, new XPoint(x, y));
        }
        return true;
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
