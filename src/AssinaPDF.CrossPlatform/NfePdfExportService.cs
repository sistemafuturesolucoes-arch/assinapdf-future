using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Fonts;
using System.Reflection;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public static class NfePdfExportService
{
    public static void ExportInvoiceSummary(NfeDocumentSummary invoice, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        EnsureFontResolver();
        using var document = new PdfDocument();
        document.Info.Title = $"Espelho NF-e {invoice.InvoiceNumber}";
        document.Info.Subject = $"Chave de acesso {invoice.AccessKey}";

        PdfPage page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        XGraphics graphics = XGraphics.FromPdfPage(page);
        var titleFont = new XFont("Arial", 15, XFontStyleEx.Bold);
        var sectionFont = new XFont("Arial", 10, XFontStyleEx.Bold);
        var bodyFont = new XFont("Arial", 8, XFontStyleEx.Regular);
        var smallFont = new XFont("Arial", 7, XFontStyleEx.Regular);
        double y = 34;
        const double left = 32;
        double width = page.Width.Point - 64;

        void NewPage()
        {
            graphics.Dispose();
            page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            graphics = XGraphics.FromPdfPage(page);
            y = 34;
        }

        void Write(string text, XFont font, double lineHeight = 13)
        {
            if (y + lineHeight > page.Height.Point - 32)
                NewPage();
            graphics.DrawString(text, font, XBrushes.Black,
                new XRect(left, y, width, lineHeight), XStringFormats.TopLeft);
            y += lineHeight;
        }

        void Section(string text)
        {
            y += 5;
            Write(text, sectionFont, 16);
            graphics.DrawLine(XPens.Gray, left, y, page.Width.Point - left, y);
            y += 4;
        }

        void Wrapped(string text, XFont font, double lineHeight = 11)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            double maxWidth = width;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string line = "";
            foreach (string word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && graphics.MeasureString(candidate, font).Width > maxWidth)
                {
                    Write(line, font, lineHeight);
                    line = word;
                }
                else line = candidate;
            }
            if (line.Length > 0) Write(line, font, lineHeight);
        }

        Write("ESPELHO DA NOTA FISCAL ELETRÔNICA", titleFont, 24);
        Write("Documento auxiliar para consulta — não substitui o DANFE oficial.", smallFont, 12);
        Section("DADOS DA NF-e");
        Write($"Número: {invoice.InvoiceNumber}    Série: {invoice.Series}    Ambiente: {invoice.Environment}", bodyFont);
        Write($"Emissão: {FormatDate(invoice.IssueDate)}", bodyFont);
        Wrapped($"Chave de acesso: {invoice.AccessKey}", bodyFont);
        Wrapped($"Protocolo: {invoice.ProtocolNumber}    Autorização: {FormatDate(invoice.AuthorizationDate)}", bodyFont);

        Section("EMITENTE");
        Wrapped(invoice.IssuerName, bodyFont);
        Wrapped($"CPF/CNPJ: {invoice.IssuerDocument}", bodyFont);
        Wrapped(invoice.IssuerAddress, bodyFont);

        Section("DESTINATÁRIO");
        Wrapped(invoice.RecipientName, bodyFont);
        Wrapped($"CPF/CNPJ: {invoice.RecipientDocument}", bodyFont);
        Wrapped(invoice.RecipientAddress, bodyFont);

        Section("ITENS DA NOTA");
        foreach (var item in invoice.Items)
        {
            string line = $"{item.Number}. {item.Description} | Qtd.: {item.Quantity.ToString("N4", CultureInfo.GetCultureInfo("pt-BR"))} {item.Unit} | Unit.: {item.UnitPrice.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))} | Total: {item.Total.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))}";
            Wrapped(line, bodyFont, 12);
        }

        Section("TOTAIS");
        Write($"Total dos produtos: {invoice.ProductTotal.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))}", bodyFont);
        Write($"Valor total da NF-e: {invoice.InvoiceTotal.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))}", sectionFont, 16);
        y += 8;
        Wrapped($"Motivo/retorno da autorização: {invoice.AuthorizationReason}", smallFont);
        y += 8;
        Write($"Arquivo de origem: {Path.GetFileName(invoice.SourcePath)}", smallFont);
        Write("Gerado pelo AssinaPDF | Future Soluções", smallFont);

        graphics.Dispose();
        document.Save(destinationPath);
    }

    private static string FormatDate(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var date))
            return date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
        return string.IsNullOrWhiteSpace(value) ? "Não informado" : value;
    }
    private static readonly object FontResolverLock = new();

    private static void EnsureFontResolver()
    {
        lock (FontResolverLock)
        {
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = new InterFontResolver();
        }
    }

    private sealed class InterFontResolver : IFontResolver
    {
        private const string RegularFace = "FutureInter-Regular";
        private const string BoldFace = "FutureInter-Bold";

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string face = isBold ? BoldFace : RegularFace;
            return new FontResolverInfo(face, false, isItalic);
        }

        public byte[]? GetFont(string faceName)
        {
            string suffix = faceName == BoldFace ? "Inter-Bold.ttf" :
                faceName == RegularFace ? "Inter-Regular.ttf" :
                throw new ArgumentException("Fonte PDF não reconhecida.", nameof(faceName));

            Assembly assembly = typeof(NfePdfExportService).Assembly;
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("Assets.Fonts." + suffix, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("A fonte Inter necessária para gerar o PDF não foi encontrada.");
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Não foi possível carregar a fonte Inter incorporada.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }

}
