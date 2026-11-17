using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZXing;
using ZXing.Common;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Renderiza o DANFSe nacional em A4 retrato, seguindo a NT SE/CGNFS-e 008/2026.
/// O XML autorizado é a fonte dos dados; o QR aponta para a consulta pública nacional.
/// </summary>
public static class NfseDanfseGenerator
{
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double Margin = 5.67;
    private const string PublicQueryBase = "https://www.nfse.gov.br/ConsultaPublica/?tpc=1&chave=";

    private sealed record Field(string Label, string Value);

    public static void Generate(NfseDocumentSummary invoice, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (string.IsNullOrWhiteSpace(invoice.SourcePath) || !File.Exists(invoice.SourcePath))
            throw new FileNotFoundException("O XML original da NFS-e não foi encontrado.", invoice.SourcePath);

        string source = Path.GetFullPath(invoice.SourcePath);
        string destination = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, destination, comparison))
            throw new InvalidOperationException("O DANFSe não pode substituir o XML original.");

        var validated = NfseXmlReader.ReadFile(source);
        if (!string.Equals(validated.AccessKey, invoice.AccessKey, StringComparison.Ordinal))
            throw new InvalidDataException("A chave do XML original não corresponde à NFS-e selecionada.");

        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Não foi possível identificar a pasta de destino.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(destination)}-{Guid.NewGuid():N}.tmp.pdf");

        try
        {
            // Usa as fontes incorporadas já utilizadas pelo conversor NF-e,
            // evitando dependência de Arial instalado no Windows ou macOS.
            NfePdfFontResolver.Register();
            using (var document = new PdfDocument())
            {
                document.Info.Title = $"DANFSe Nacional {invoice.Number}";
                document.Info.Subject = "Documento Auxiliar da NFS-e - Padrão Nacional";
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(PageWidth);
                page.Height = XUnit.FromPoint(PageHeight);
                using var graphics = XGraphics.FromPdfPage(page);
                DrawPage(graphics, invoice);
                document.Save(temporary);
            }

            ValidatePdf(temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* Preserve o erro original. */ }
        }
    }

    private static void DrawPage(XGraphics g, NfseDocumentSummary d)
    {
        var black = XBrushes.Black;
        var light = new XSolidBrush(XColor.FromArgb(242, 242, 242));
        var line = new XPen(XColor.FromArgb(70, 70, 70), 0.5);
        var border = new XPen(XColors.Black, 1);
        var title = new XFont("Arial", 7, XFontStyleEx.Bold);
        var label = new XFont("Arial", 6, XFontStyleEx.Bold);
        var value = new XFont("Arial", 7, XFontStyleEx.Regular);
        var header = new XFont("Arial", 9, XFontStyleEx.Bold);
        var small = new XFont("Arial", 6, XFontStyleEx.Regular);

        g.DrawRectangle(border, Margin, Margin, PageWidth - 2 * Margin, PageHeight - 2 * Margin);

        // Cabeçalho nacional (NT 008/2026, item 2.4.3).
        g.DrawRectangle(light, Margin + 1, Margin + 1, PageWidth - 2 * Margin - 2, 51);
        g.DrawString("NFS-e", new XFont("Arial", 17, XFontStyleEx.Bold), black,
            new XRect(18, 20, 105, 22), XStringFormats.CenterLeft);
        g.DrawString("NOTA FISCAL DE SERVIÇO ELETRÔNICA", small, black,
            new XRect(18, 42, 150, 10), XStringFormats.CenterLeft);
        g.DrawString("DANFSe v2.0", header, black,
            new XRect(165, 14, 245, 16), XStringFormats.Center);
        g.DrawString("Documento Auxiliar da NFS-e", header,
            new XRect(165, 30, 245, 14), XStringFormats.Center);
        if (d.Environment == "Homologação")
            g.DrawString("NFS-e SEM VALIDADE JURÍDICA",
                new XFont("Arial", 9, XFontStyleEx.Bold),
                new XSolidBrush(XColor.FromArgb(200, 0, 0)),
                new XRect(165, 43, 245, 13), XStringFormats.Center);
        g.DrawString(Clip($"{d.IssuerCity} / {d.IssuerState}", 38), value,
            new XRect(420, 14, 155, 12), XStringFormats.CenterRight);
        g.DrawString("Ambiente gerador: " + Clip(d.GeneratingEnvironment, 34), small,
            new XRect(420, 28, 155, 10), XStringFormats.CenterRight);
        g.DrawString("Sistema Nacional · " + d.Environment, small,
            new XRect(420, 40, 155, 10), XStringFormats.CenterRight);

        // Identificação e QR Code.
        double y = 57;
        DrawSection(g, "DADOS DA NFS-e", Margin + 2, y, PageWidth - 2 * Margin - 4, 95, title, label, value, light, line);
        g.DrawString("CHAVE DE ACESSO DA NFS-e", label, black,
            new XRect(Margin + 8, y + 16, 405, 7), XStringFormats.TopLeft);
        g.DrawString(d.AccessKey, value, black,
            new XRect(Margin + 8, y + 24, 405, 9), XStringFormats.TopLeft);
        DrawFieldGrid(g, Margin + 8, y + 35, 405, 55, new[]
        {
            new Field("NÚMERO DA NFS-e", d.Number),
            new Field("COMPETÊNCIA", FormatDate(d.Competence)),
            new Field("DATA/HORA DE EMISSÃO", FormatDateTime(d.ProcessedAt)),
            new Field("NÚMERO DA DPS", d.DpsNumber),
            new Field("SÉRIE DA DPS", d.DpsSeries),
            new Field("DATA/HORA DE EMISSÃO DA DPS", FormatDateTime(d.DpsIssuedAt)),
            new Field("EMITENTE DA NFS-e", IssuerType(d.IssuerType)),
            new Field("SITUAÇÃO", d.Status),
            new Field("FINALIDADE", d.Purpose)
        }, 3, label, value, line);
        DrawQr(g, PublicQueryBase + Uri.EscapeDataString(d.AccessKey), 496, 47, 43);
        g.DrawString("A autenticidade desta NFS-e pode ser verificada pela leitura do QR Code ou pela consulta da chave de acesso no portal nacional.",
            small, new XRect(484, 91, 92, 27), XStringFormats.TopCenter);

        y = 157;
        DrawParty(g, "PRESTADOR / FORNECEDOR", Margin + 2, y, PageWidth - 2 * Margin - 4, 112,
            new[]
            {
                new Field("CNPJ / CPF / NIF", d.IssuerDocument),
                new Field("INSCRIÇÃO MUNICIPAL", d.IssuerMunicipalRegistration),
                new Field("TELEFONE", d.IssuerPhone),
                new Field("NOME / NOME EMPRESARIAL", d.IssuerName),
                new Field("MUNICÍPIO / UF", $"{d.IssuerCity} / {d.IssuerState}"),
                new Field("ENDEREÇO", d.IssuerAddress),
                new Field("E-MAIL", d.IssuerEmail)
            }, title, label, value, light, line);

        y = 272;
        DrawParty(g, "TOMADOR / ADQUIRENTE", Margin + 2, y, PageWidth - 2 * Margin - 4, 88,
            new[]
            {
                new Field("CNPJ / CPF / NIF", d.RecipientDocument),
                new Field("NOME / NOME EMPRESARIAL", d.RecipientName),
                new Field("TELEFONE", d.RecipientPhone),
                new Field("MUNICÍPIO / UF", $"{d.RecipientCity} / {d.RecipientState}"),
                new Field("ENDEREÇO", d.RecipientAddress),
                new Field("E-MAIL", d.RecipientEmail)
            }, title, label, value, light, line);

        y = 363;
        DrawNarrativeSection(g, "DESTINATÁRIO DA OPERAÇÃO", d.DestinationDetails, Margin + 2, y, 282, 60, title, label, value, light, line);
        DrawNarrativeSection(g, "INTERMEDIÁRIO DA OPERAÇÃO", d.IntermediaryDetails, Margin + 292, y, 282, 60, title, label, value, light, line);

        y = 426;
        DrawParty(g, "DADOS DO SERVIÇO PRESTADO", Margin + 2, y, PageWidth - 2 * Margin - 4, 110,
            new[]
            {
                new Field("CÓDIGO DE TRIBUTAÇÃO NACIONAL", d.ServiceCodeNational),
                new Field("CÓDIGO DE TRIBUTAÇÃO MUNICIPAL", d.ServiceCodeMunicipal),
                new Field("CÓDIGO NBS", d.NbsCode),
                new Field("LOCAL DA PRESTAÇÃO", d.ServiceLocation),
                new Field("DESCRIÇÃO DO SERVIÇO", d.ServiceDescription)
            }, title, label, value, light, line);

        y = 539;
        DrawParty(g, "TRIBUTAÇÃO MUNICIPAL E FEDERAL", Margin + 2, y, PageWidth - 2 * Margin - 4, 76,
            new[]
            {
                new Field("ISSQN APURADO", Money(d.IssqnAmount)),
                new Field("RETENÇÕES FEDERAIS", Money(d.FederalWithholdings)),
                new Field("TRIBUTAÇÃO MUNICIPAL", d.IssqnAmount.HasValue && d.IssqnAmount.Value > 0
                    ? "ISSQN informado no XML"
                    : "TRIBUTAÇÃO MUNICIPAL (ISSQN) - OPERAÇÃO NÃO SUJEITA AO ISSQN"),
                new Field("IBS / CBS", Money(d.IbscbsAmount))
            }, title, label, value, light, line);

        y = 618;
        DrawParty(g, "VALORES DA NFS-e", Margin + 2, y, PageWidth - 2 * Margin - 4, 76,
            new[]
            {
                new Field("VALOR DO SERVIÇO / OPERAÇÃO", Money(d.ServiceAmount)),
                new Field("DESCONTO INCONDICIONADO", Money(d.UnconditionalDiscount)),
                new Field("DESCONTO CONDICIONADO", Money(d.ConditionalDiscount)),
                new Field("TOTAL RETENÇÕES (ISSQN / FEDERAIS)",
                    Money(d.IssqnAmount.HasValue || d.FederalWithholdings.HasValue
                        ? (d.IssqnAmount ?? 0m) + (d.FederalWithholdings ?? 0m)
                        : null)),
                new Field("VALOR LÍQUIDO DA NFS-e", Money(d.NetAmount)),
                new Field("TOTAL IBS / CBS", Money(d.IbscbsAmount)),
                new Field("VALOR LÍQUIDO + IBS/CBS", Money(d.IbscbsAmount.HasValue ? d.NetAmount + d.IbscbsAmount.Value : d.NetAmount))
            }, title, label, value, light, line);

        y = 697;
        DrawParty(g, "INFORMAÇÕES COMPLEMENTARES", Margin + 2, y, PageWidth - 2 * Margin - 4, 82,
            new[]
            {
                new Field("INFORMAÇÕES DO SERVIÇO / DEMAIS DADOS DO XML", d.AdditionalInformation)
            }, title, label, value, light, line);

        g.DrawString("Documento auxiliar gerado localmente a partir do XML da NFS-e do Padrão Nacional. O XML autorizado é o documento fiscal oficial.",
            small, new XRect(Margin + 7, 812, PageWidth - 2 * Margin - 14, 16), XStringFormats.Center);
    }

    private static void DrawNarrativeSection(XGraphics g, string heading, string text, double x, double y,
        double width, double height, XFont title, XFont label, XFont value, XBrush light, XPen line)
    {
        DrawSection(g, heading, x, y, width, height, title, label, value, light, line);
        var rect = new XRect(x + 5, y + 17, width - 10, height - 20);
        var lines = WrapText(g, text, value, rect.Width);
        double lineHeight = 8;
        if (lines.Count * lineHeight > rect.Height + 0.1)
            throw new InvalidDataException($"O bloco '{heading}' excede o espaço legível do DANFSe. O PDF não foi gerado para evitar perda de informação.");
        for (int i = 0; i < lines.Count; i++)
            g.DrawString(lines[i], value, XBrushes.Black,
                new XRect(rect.X, rect.Y + i * lineHeight, rect.Width, lineHeight), XStringFormats.TopLeft);
    }

    private static void DrawParty(XGraphics g, string heading, double x, double y, double width, double height,
        IReadOnlyList<Field> fields, XFont title, XFont label, XFont value, XBrush light, XPen line)
    {
        DrawSection(g, heading, x, y, width, height, title, label, value, light, line);
        DrawFieldGrid(g, x + 6, y + 15, width - 12, height - 18, fields, 2, label, value, line);
    }

    private static void DrawSection(XGraphics g, string heading, double x, double y, double width, double height,
        XFont title, XFont label, XFont value, XBrush light, XPen line)
    {
        g.DrawRectangle(line, x, y, width, height);
        g.DrawRectangle(light, x + 0.25, y + 0.25, width - 0.5, 13);
        g.DrawString(heading.ToUpperInvariant(), title, XBrushes.Black,
            new XRect(x + 4, y + 1, width - 8, 11), XStringFormats.CenterLeft);
    }

    private static void DrawFieldGrid(XGraphics g, double x, double y, double width, double height,
        IReadOnlyList<Field> fields, int columns, XFont label, XFont value, XPen line)
    {
        if (fields.Count == 0) return;
        double cellWidth = width / columns;
        int rows = (int)Math.Ceiling(fields.Count / (double)columns);
        double cellHeight = height / Math.Max(rows, 1);
        for (int i = 0; i < fields.Count; i++)
        {
            int row = i / columns, col = i % columns;
            double cx = x + col * cellWidth, cy = y + row * cellHeight;
            var field = fields[i];
            g.DrawString(field.Label, label, XBrushes.Black,
                new XRect(cx + 3, cy, cellWidth - 6, 7), XStringFormats.TopLeft);
            var valueRect = new XRect(cx + 3, cy + 7, cellWidth - 6, Math.Max(7.5, cellHeight - 7));
            var lines = WrapText(g, field.Value, value, valueRect.Width);
            double lineHeight = 7.5;
            if (lines.Count * lineHeight > valueRect.Height + 0.1)
                throw new InvalidDataException($"O campo '{field.Label}' excede o espaço legível do DANFSe. O PDF não foi gerado para evitar perda de informação.");
            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                g.DrawString(lines[lineIndex], value, XBrushes.Black,
                    new XRect(valueRect.X, valueRect.Y + lineIndex * lineHeight, valueRect.Width, lineHeight), XStringFormats.TopLeft);
            if (col > 0) g.DrawLine(line, cx, cy, cx, cy + cellHeight);
            if (row > 0 && col == 0) g.DrawLine(line, x, cy, x + width, cy);
        }
    }

    private static void DrawQr(XGraphics g, string payload, double x, double y, double size)
    {
        var matrix = new MultiFormatWriter().encode(payload, BarcodeFormat.QR_CODE, 320, 320,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 1 });
        double module = size / Math.Max(matrix.Width, matrix.Height);
        g.DrawRectangle(XBrushes.White, x, y, size, size);
        for (int row = 0; row < matrix.Height; row++)
        for (int col = 0; col < matrix.Width; col++)
            if (matrix[col, row])
                g.DrawRectangle(XBrushes.Black, x + col * module, y + row * module, module + 0.05, module + 0.05);
    }

    private static List<string> WrapText(XGraphics g, string? text, XFont font, double maxWidth)
    {
        string clean = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0) return new List<string> { "" };
        var result = new List<string>();
        string current = "";
        foreach (string word in clean.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (g.MeasureString(candidate, font).Width <= maxWidth)
            {
                current = candidate;
                continue;
            }
            if (current.Length == 0)
                throw new InvalidDataException("Um campo da NFS-e contém texto indivisível maior que a área de impressão.");
            result.Add(current);
            current = word;
        }
        if (current.Length > 0) result.Add(current);
        return result;
    }

    private static string Money(decimal? amount) =>
        amount.HasValue ? amount.Value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) : "";
    private static string Clip(string? text, int max)
    {
        string clean = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= max ? clean : clean[..Math.Max(0, max - 3)] + "...";
    }
    private static string FormatDate(string value) =>
        DateTime.TryParse(value, out var date) ? date.ToString("dd/MM/yyyy") : value;
    private static string FormatDateTime(string value) =>
        DateTimeOffset.TryParse(value, out var date) ? date.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") : value;
    private static string IssuerType(string value) => value switch
    {
        "1" => "Prestador",
        "2" => "Tomador",
        "3" => "Intermediário",
        _ => value
    };

    private static void ValidatePdf(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 500)
            throw new InvalidDataException("O DANFSe não foi gerado corretamente.");
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[5];
        if (stream.Read(signature) != 5 || !signature.SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("O arquivo gerado não é um PDF válido.");
    }
}