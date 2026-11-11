using System.IO.Compression;
using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class NfeBatchZipExporterTests
{
    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "NfeAuthorized.Synthetic.xml");

    [Fact]
    public void Create_ValidInvoice_WritesOriginalXmlDanfeConsolidatedPdfAndReports()
    {
        string directory = CreateTempDirectory();
        string xmlPath = Path.Combine(directory, "entrada.xml");
        string pdfPath = Path.Combine(directory, "danfe.pdf");
        string zipPath = Path.Combine(directory, "resultado.zip");

        try
        {
            File.Copy(FixturePath, xmlPath);
            var summary = NfeXmlReader.ReadFile(xmlPath);
            NfeDanfeGenerator.Generate(summary, pdfPath);

            NfeBatchZipExporter.Create(
                zipPath,
                new[] { new NfeZipDocument(xmlPath, summary, pdfPath, null) },
                NfeZipGroupingMode.Recipient);

            using var archive = ZipFile.OpenRead(zipPath);
            string root = "EMPRESAS/99888777000100-CLIENTE FICTICIO DE TESTE/2026-10/";

            Assert.Contains(archive.Entries, e => e.FullName == root + "XML_ORIGINAIS/entrada.xml");
            Assert.Contains(archive.Entries, e => e.FullName.StartsWith(root + "DANFES/", StringComparison.Ordinal));
            Assert.Contains(archive.Entries, e => e.FullName == root + "DANFE-CONSOLIDADO-2026-10.pdf");
            Assert.Contains(archive.Entries, e => e.FullName == "relatorio.csv");
            Assert.Contains(archive.Entries, e => e.FullName == "LEIA-ME.txt");

            using var original = archive.GetEntry(root + "XML_ORIGINAIS/entrada.xml")!.Open();
            using var expected = File.OpenRead(xmlPath);
            using var actualBytes = new MemoryStream();
            original.CopyTo(actualBytes);
            Assert.Equal(File.ReadAllBytes(xmlPath), actualBytes.ToArray());

            string csv;
            using (var reader = new StreamReader(archive.GetEntry("relatorio.csv")!.Open()))
                csv = reader.ReadToEnd();
            Assert.Contains("PROCESSADO", csv);
            Assert.Contains(summary.AccessKey, csv);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Create_InvalidAndDuplicateRecords_AreSeparatedAndExplained()
    {
        string directory = CreateTempDirectory();
        string invalidPath = Path.Combine(directory, "invalido.xml");
        string duplicatePath = Path.Combine(directory, "duplicado.xml");
        string zipPath = Path.Combine(directory, "ocorrencias.zip");

        try
        {
            File.WriteAllText(invalidPath, "<xml>inválido</xml>");
            File.WriteAllText(duplicatePath, "<xml>duplicado</xml>");

            var records = new[]
            {
                new NfeZipDocument(invalidPath, null, null, "XML sem estrutura NF-e"),
                new NfeZipDocument(duplicatePath, null, null, null, true, "Chave já importada")
            };

            NfeBatchZipExporter.Create(zipPath, records, NfeZipGroupingMode.Issuer);

            using var archive = ZipFile.OpenRead(zipPath);
            Assert.Contains(archive.Entries, e => e.FullName == "XML_COM_ERRO/0001-invalido.xml");
            Assert.Contains(archive.Entries, e => e.FullName == "XML_COM_ERRO/0001-invalido.xml.motivo.txt");
            Assert.Contains(archive.Entries, e => e.FullName == "DUPLICADOS/0001-duplicado.xml");
            Assert.Contains(archive.Entries, e => e.FullName == "DUPLICADOS/0001-duplicado.xml.motivo.txt");

            string csv;
            using (var reader = new StreamReader(archive.GetEntry("relatorio.csv")!.Open()))
                csv = reader.ReadToEnd();
            Assert.Contains("INVALIDO", csv);
            Assert.Contains("DUPLICADO", csv);
            Assert.Contains("XML sem estrutura NF-e", csv);
            Assert.Contains("Chave já importada", csv);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Create_EmptyInput_RejectsExport()
    {
        string directory = CreateTempDirectory();
        try
        {
            string zipPath = Path.Combine(directory, "vazio.zip");
            Assert.Throws<InvalidOperationException>(() =>
                NfeBatchZipExporter.Create(zipPath, Array.Empty<NfeZipDocument>(), NfeZipGroupingMode.Recipient));
            Assert.False(File.Exists(zipPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Create_DoesNotOverwriteOriginalXml()
    {
        string directory = CreateTempDirectory();
        string xmlPath = Path.Combine(directory, "original.xml");
        try
        {
            File.WriteAllText(xmlPath, "<xml />");
            byte[] original = File.ReadAllBytes(xmlPath);

            Assert.Throws<InvalidOperationException>(() =>
                NfeBatchZipExporter.Create(
                    xmlPath,
                    new[] { new NfeZipDocument(xmlPath, null, null, "inválido") },
                    NfeZipGroupingMode.Recipient));

            Assert.Equal(original, File.ReadAllBytes(xmlPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }


    [Fact]
    public void Create_DoesNotOverwriteOriginalDanfe()
    {
        string directory = CreateTempDirectory();
        string xmlPath = Path.Combine(directory, "original.xml");
        string pdfPath = Path.Combine(directory, "original.pdf");
        try
        {
            File.WriteAllText(xmlPath, "<xml />");
            File.WriteAllBytes(pdfPath, new byte[] { 1, 2, 3 });
            byte[] original = File.ReadAllBytes(pdfPath);

            Assert.Throws<InvalidOperationException>(() =>
                NfeBatchZipExporter.Create(
                    pdfPath,
                    new[] { new NfeZipDocument(xmlPath, null, pdfPath, "inválido") },
                    NfeZipGroupingMode.Recipient));

            Assert.Equal(original, File.ReadAllBytes(pdfPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "nfe-zip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}