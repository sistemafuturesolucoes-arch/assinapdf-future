using System.IO.Compression;
using AssinaPDF.MacPrototype.Services;
using PdfSharp.Pdf;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public class CteBatchZipExporterTests
{
    [Fact]
    public void Create_MixedBatch_PackagesOriginalsPdfsErrorsAndDuplicatesWithoutChangingSources()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AssinaPDF-CteZip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string xmlPath = Path.Combine(directory, "cte.xml");
        string duplicatePath = Path.Combine(directory, "cte-duplicado.xml");
        string invalidPath = Path.Combine(directory, "cte-invalido.xml");
        string pdfPath = Path.Combine(directory, "dacte.pdf");
        string zipPath = Path.Combine(directory, "resultado.zip");
        byte[] original = System.Text.Encoding.UTF8.GetBytes("<cteProc>original-preservado</cteProc>");

        try
        {
            File.WriteAllBytes(xmlPath, original);
            File.WriteAllText(duplicatePath, "<cteProc>duplicado</cteProc>");
            File.WriteAllText(invalidPath, "<xml>invalido</xml>");
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage();
                pdf.Save(pdfPath);
            }

            var summary = new CteDocumentSummary(
                xmlPath, "43261012345678000195570010000000011000000001",
                "1", "1", "2026-10-22T10:00:00-03:00",
                "TRANSPORTADORA TESTE LTDA", "12345678000195",
                "", "", "", "", "", "", "", "",
                "01", "Transporte", 100m, 25m,
                "142260000123456", "2026-10-22T10:00:01-03:00",
                "Autorizado", "Produção", "57");

            var docs = new[]
            {
                new CteZipDocument(xmlPath, summary, pdfPath, null),
                new CteZipDocument(duplicatePath, summary with { SourcePath = duplicatePath }, null,
                    "Chave já importada.", true, "Chave já importada."),
                new CteZipDocument(invalidPath, null, null, "XML sem autorização.")
            };

            CteBatchZipExporter.Create(zipPath, docs);

            using var archive = ZipFile.OpenRead(zipPath);
            var names = archive.Entries.Select(x => x.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains(names, x => x.Contains("/XML_ORIGINAIS/cte.xml", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, x => x.Contains("/DACTE/DACTE-CTe57-", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, x => x.EndsWith("/DACTE-CONSOLIDADO.pdf", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, x => x.StartsWith("DUPLICADOS/", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, x => x.StartsWith("XML_COM_ERRO/", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("relatorio.csv", names);
            Assert.Contains("LEIA-ME.txt", names);
            Assert.Equal(original, File.ReadAllBytes(xmlPath));
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
        }
    }
}
