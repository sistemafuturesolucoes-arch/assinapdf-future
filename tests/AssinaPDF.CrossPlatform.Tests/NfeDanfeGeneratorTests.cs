using AssinaPDF.MacPrototype.Services;
using Xunit;

namespace AssinaPDF.CrossPlatform.Tests;

public sealed class NfeDanfeGeneratorTests
{
    [Fact]
    public void Generate_CreatesPdfForAuthorizedInvoice()
    {
        var invoice = new NfeDocumentSummary(
            SourcePath: "",
            AccessKey: "35261112345678000195550010000000011000000010",
            InvoiceNumber: "1",
            Series: "1",
            IssueDate: "2026-11-10T10:00:00-03:00",
            IssuerName: "EMPRESA TESTE LTDA",
            IssuerDocument: "12345678000195",
            IssuerAddress: "Rua de Teste, 100 - Centro - Passo Fundo/RS",
            RecipientName: "CLIENTE TESTE",
            RecipientDocument: "12345678901",
            RecipientAddress: "Rua Cliente, 20 - Centro - Passo Fundo/RS",
            ProductTotal: 100m,
            InvoiceTotal: 100m,
            ProtocolNumber: "135260000000001",
            AuthorizationDate: "2026-11-10T10:01:00-03:00",
            AuthorizationReason: "Autorizado o uso da NF-e",
            Environment: "Homologação",
            Items: new[]
            {
                new NfeItemSummary(1, "P001", "Produto de teste", "12345678", "5102",
                    2m, "UN", 50m, 100m)
            });

        string path = Path.Combine(Path.GetTempPath(), $"danfe-{Guid.NewGuid():N}.pdf");
        try
        {
            NfeDanfeGenerator.Generate(invoice, path);

            Assert.True(File.Exists(path));
            byte[] header = File.ReadAllBytes(path).Take(5).ToArray();
            Assert.Equal("%PDF-"u8.ToArray(), header);
            Assert.True(new FileInfo(path).Length > 1000);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Generate_RejectsInvalidAccessKey()
    {
        var invoice = new NfeDocumentSummary(
            "", "123", "1", "1", "", "EMITENTE", "123", "", "DESTINATARIO", "456", "",
            10m, 10m, "123", "", "Autorizado", "Homologação",
            Array.Empty<NfeItemSummary>());

        Assert.Throws<InvalidDataException>(() =>
            NfeDanfeGenerator.Generate(invoice, Path.Combine(Path.GetTempPath(), "invalid-danfe.pdf")));
    }
}
