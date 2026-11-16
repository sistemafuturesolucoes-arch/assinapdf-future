using System.IO.Compression;
using AssinaPDF.MacPrototype.Services;
using PdfSharp.Pdf;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public class NfceBatchZipExporterTests
{
    [Fact]
    public void Create_MixedBatch_PackagesDanfceOriginalsErrorsAndDuplicatesWithoutChangingSources()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AssinaPDF-NfceZip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string xmlPath = Path.Combine(directory, "nfce.xml");
        string duplicatePath = Path.Combine(directory, "nfce-duplicada.xml");
        string invalidPath = Path.Combine(directory, "nfce-invalida.xml");
        string pdfPath = Path.Combine(directory, "danfce.pdf");
        string zipPath = Path.Combine(directory, "resultado.zip");
        byte[] original = System.Text.Encoding.UTF8.GetBytes("<nfeProc>original-preservado</nfeProc>");

        try
        {
            File.WriteAllBytes(xmlPath, original);
            File.WriteAllText(duplicatePath, "<nfeProc>duplicada</nfeProc>");
            File.WriteAllText(invalidPath, "<xml>invalido</xml>");
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage();
                pdf.Save(pdfPath);
            }

            var summary = new NfceDocumentSummary(
                xmlPath, "43261012345678000195650010000000011000000001",
                "1", "1", "2026-10-22T10:00:00-03:00",
                "COMERCIO TESTE LTDA", "12345678000195", "Rua Teste, 10",
                "", "", 25m, 25m, "242260000123456",
                "2026-10-22T10:00:01-03:00", "Autorizado",
                "Produção", "https://sefaz.example/consulta", Array.Empty<NfceItemSummary>());

            var docs = new[]
            {
                new NfceZipDocument(xmlPath, summary, pdfPath, null),
                new NfceZipDocument(duplicatePath, summary with { SourcePath = duplicatePath }, null,
                    "Chave já importada.", true, "Chave já importada."),
                new NfceZipDocument(invalidPath, null, null, "XML inválido.")
            };

            NfceBatchZipExporter.Create(zipPath, docs);

            using var archive = ZipFile.OpenRead(zipPath);
            var names = archive.Entries.Select(x => x.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains(names, x => x.Contains("/XML_ORIGINAIS/nfce.xml", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, x => x.Contains("/DANFCE/DANFCE-NFCe-", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, x => x.EndsWith("/DANFCE-CONSOLIDADO.pdf", StringComparison.OrdinalIgnoreCase));
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
