using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZXing;
using ZXing.Common;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Gera uma representação auxiliar em PDF da NF-e autorizada.
/// O XML autorizado continua sendo o documento fiscal de origem.
/// </summary>
public static class NfeDanfeGenerator
{
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double Margin = 24;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static void Generate(NfeDocumentSummary invoice, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (invoice.AccessKey.Length != 44 || invoice.AccessKey.Any(c => !char.IsAsciiDigit(c)))
            throw new InvalidDataException("A chave de acesso não é válida para gerar o DANFE.");

        using var pdf = new PdfDocument();
        int rowsPerPage = 17;
        int pageCount = Math.Max(1, (int)Math.Ceiling(invoice.Items.Count / (double)rowsPerPage));

        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var page = pdf.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);
            DrawPage(gfx, invoice, pageIndex, pageCount, rowsPerPage);
        }

        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        pdf.Save(fullPath);
    }

    private static void DrawPage(XGraphics gfx, NfeDocumentSummary invoice, int pageIndex, int pageCount, int rowsPerPage)
    {
        var regular = new XFont("Arial", 7, XFontStyleEx.Regular);
        var small = new XFont("Arial", 6, XFontStyleEx.Regular);
        var bold = new XFont("Arial", 8, XFontStyleEx.Bold);
        var title = new XFont("Arial", 14, XFontStyleEx.Bold);
        var pen = new XPen(XColors.Black, 0.55);
        double x = Margin;
        double width = PageWidth - Margin * 2;
        double y = 22;

        gfx.DrawRectangle(pen, x, y, width, 86);
        gfx.DrawString("DANFE", title, XBrushes.Black, new XRect(x + 8, y + 7, 125, 20));
        gfx.DrawString("Documento Auxiliar da Nota Fiscal Eletrônica", small, XBrushes.Black,
            new XRect(x + 8, y + 27, 145, 20));
        gfx.DrawString(pageIndex == 0 ? "0 - ENTRADA   1 - SAÍDA" : "CONTINUAÇÃO",
            bold, XBrushes.Black, new XRect(x + 8, y + 48, 145, 14));
        gfx.DrawString($"Nº {invoice.InvoiceNumber}   Série {invoice.Series}",
            bold, XBrushes.Black, new XRect(x + 8, y + 64, 145, 14));

        gfx.DrawString(invoice.IssuerName, bold, XBrushes.Black,
            new XRect(x + 160, y + 7, 175, 18));
        gfx.DrawString("CHAVE DE ACESSO", small, XBrushes.Black,
            new XRect(x + 345, y + 6, 210, 12));
        DrawBarcode(gfx, invoice.AccessKey, x + 345, y + 20, 205, 37);
        gfx.DrawString(FormatAccessKey(invoice.AccessKey), small, XBrushes.Black,
            new XRect(x + 345, y + 58, 210, 12));
        gfx.DrawString($"Página {pageIndex + 1} de {pageCount}", small, XBrushes.Black,
            new XRect(x + 345, y + 72, 210, 10));

        y += 92;
        DrawField(gfx, pen, x, y, 270, 34, "NATUREZA DA OPERAÇÃO", "Conforme XML fiscal");
        DrawField(gfx, pen, x + 270, y, 285, 34, "PROTOCOLO DE AUTORIZAÇÃO",
            $"{invoice.ProtocolNumber}  {invoice.AuthorizationDate}");
        y += 38;
        DrawField(gfx, pen, x, y, 270, 42, "EMITENTE", invoice.IssuerName);
        DrawField(gfx, pen, x + 270, y, 285, 42, "CNPJ / CPF", invoice.IssuerDocument);
        y += 46;
        DrawField(gfx, pen, x, y, 270, 42, "ENDEREÇO DO EMITENTE", invoice.IssuerAddress);
        DrawField(gfx, pen, x + 270, y, 285, 42, "DESTINATÁRIO", invoice.RecipientName);
        y += 46;
        DrawField(gfx, pen, x, y, 270, 42, "DOCUMENTO DO DESTINATÁRIO", invoice.RecipientDocument);
        DrawField(gfx, pen, x + 270, y, 285, 42, "ENDEREÇO DO DESTINATÁRIO", invoice.RecipientAddress);
        y += 46;
        DrawField(gfx, pen, x, y, 185, 30, "DATA DE EMISSÃO", invoice.IssueDate);
        DrawField(gfx, pen, x + 185, y, 185, 30, "AMBIENTE", invoice.Environment);
        DrawField(gfx, pen, x + 370, y, 185, 30, "SITUAÇÃO", invoice.AuthorizationReason);
        y += 38;

        gfx.DrawRectangle(pen, x, y, width, 18);
        gfx.DrawString("DADOS DOS PRODUTOS / SERVIÇOS", bold, XBrushes.Black,
            new XRect(x + 5, y + 3, width - 10, 12));
        y += 20;

        double[] columns = { x, x + 30, x + 260, x + 305, x + 350, x + 395, x + 445, x + 500, x + width };
        string[] headers = { "ITEM", "DESCRIÇÃO", "NCM", "CFOP", "UN", "QUANT.", "V. UNIT.", "V. TOTAL" };
        for (int i = 0; i < headers.Length; i++)
        {
            gfx.DrawRectangle(pen, columns[i], y, columns[i + 1] - columns[i], 18);
            gfx.DrawString(headers[i], small, XBrushes.Black,
                new XRect(columns[i] + 2, y + 4, columns[i + 1] - columns[i] - 4, 11));
        }
        y += 18;

        var items = invoice.Items.Skip(pageIndex * rowsPerPage).Take(rowsPerPage).ToArray();
        foreach (var item in items)
        {
            gfx.DrawRectangle(pen, x, y, width, 19);
            string[] values =
            {
                item.Number.ToString(CultureInfo.InvariantCulture),
                Clip(item.Description, 42),
                item.Ncm,
                item.Cfop,
                item.Unit,
                item.Quantity.ToString("N2", PtBr),
                item.UnitPrice.ToString("N2", PtBr),
                item.Total.ToString("N2", PtBr)
            };
            for (int i = 0; i < values.Length; i++)
                gfx.DrawString(values[i], regular, XBrushes.Black,
                    new XRect(columns[i] + 2, y + 4, columns[i + 1] - columns[i] - 4, 12));
            y += 19;
        }

        if (pageIndex == pageCount - 1)
        {
            y += 8;
            DrawField(gfx, pen, x, y, 185, 36, "VALOR DOS PRODUTOS", invoice.ProductTotal.ToString("C", PtBr));
            DrawField(gfx, pen, x + 185, y, 185, 36, "VALOR TOTAL DA NF-e", invoice.InvoiceTotal.ToString("C", PtBr));
            DrawField(gfx, pen, x + 370, y, 185, 36, "PROTOCOLO", invoice.ProtocolNumber);
            y += 43;
            gfx.DrawRectangle(pen, x, y, width, 42);
            gfx.DrawString("INFORMAÇÕES COMPLEMENTARES", bold, XBrushes.Black,
                new XRect(x + 5, y + 4, width - 10, 12));
            gfx.DrawString("Documento gerado localmente a partir do XML autorizado. Consulte o XML para os dados fiscais completos.",
                small, XBrushes.Black, new XRect(x + 5, y + 19, width - 10, 18));
        }
        else
        {
            gfx.DrawString("Continua na próxima página", regular, XBrushes.Black,
                new XRect(x, PageHeight - 45, width, 14));
        }

        gfx.DrawString("Representação auxiliar. O XML autorizado é a fonte fiscal original.",
            small, XBrushes.Black, new XRect(x, PageHeight - 24, width, 12));
    }

    private static void DrawField(XGraphics gfx, XPen pen, double x, double y, double width, double height,
        string label, string value)
    {
        gfx.DrawRectangle(pen, x, y, width, height);
        gfx.DrawString(label, new XFont("Arial", 5.5, XFontStyleEx.Bold), XBrushes.Black,
            new XRect(x + 4, y + 3, width - 8, 9));
        gfx.DrawString(Clip(value, Math.Max(12, (int)(width / 4.2))),
            new XFont("Arial", 7, XFontStyleEx.Regular), XBrushes.Black,
            new XRect(x + 4, y + 14, width - 8, height - 16));
    }

    private static void DrawBarcode(XGraphics gfx, string accessKey, double x, double y, double width, double height)
    {
        var matrix = new MultiFormatWriter().encode(accessKey, BarcodeFormat.CODE_128,
            1000, 140, new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = 10,
                [EncodeHintType.CHARACTER_SET] = "UTF-8"
            });
        double moduleWidth = width / matrix.Width;
        int start = -1;
        for (int col = 0; col <= matrix.Width; col++)
        {
            bool black = col < matrix.Width && matrix[col, 0];
            if (black && start < 0) start = col;
            if (!black && start >= 0)
            {
                gfx.DrawRectangle(XBrushes.Black, x + start * moduleWidth,
                    y, (col - start) * moduleWidth, height);
                start = -1;
            }
        }
    }

    private static string FormatAccessKey(string key) =>
        string.Join(" ", Enumerable.Range(0, 4).Select(i => key.Substring(i * 11, 11)));

    private static string Clip(string value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "-" :
        value.Length <= max ? value : value[..Math.Max(1, max - 1)] + "…";
}
